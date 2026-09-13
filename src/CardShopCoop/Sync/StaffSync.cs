using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Staff (worker) sync. The hire screen lives on the phone, so the joiner can press
    /// Hire freely - but WorkerManager.ActivateWorker is blocked client-side, so vanilla
    /// would charge the shared wallet for a worker that never exists in the real
    /// simulation. We block the client's hire BEFORE it charges and forward a StaffOp
    /// the host applies through the vanilla path (fee, roster flag, activation), so the
    /// wallet is charged exactly once, host-side.
    ///
    /// Fire / give-bonus / task-assignment screens are NOT forwarded: they only open via
    /// Worker.OnMousePress on a live Worker, and on the joiner real workers are swept
    /// inactive while puppet clones are stripped of colliders and scripts - those screens
    /// are physically unreachable, so ops for them would be dead code.
    ///
    /// Host broadcasts the hired roster + per-worker save-data essentials, hash-gated
    /// (on change + a slow heal), into the client's CPlayerData mirrors so the joiner's
    /// phone shows the truth and salary-derived numbers (bills) agree.
    /// </summary>
    public class StaffSync : TickableCoopModule
    {
        private const byte OpHire = 1;
        private const byte OpUpdate = 2;
        private const byte OpBonus = 3;
        private const byte OpFire = 4;
        private const byte OpBeginInteract = 5;
        private const byte OpEndInteract = 6;
        private const int MaxWorkers = 32;

        /// <summary>Patches are static but ops need the wired instance; CoopCore
        /// constructs exactly one StaffSync, so the constructor self-registers.</summary>
        public static StaffSync Instance;

        /// <summary>True while ClientApplyState writes the mirrors, so no patch of ours
        /// (present or future) mistakes an echo for a local action.</summary>
        public static bool ApplyingRemote;

        public Action<INetMessage> SendOp;         // set by CoopCore: client->host
        public Action<INetMessage> BroadcastState; // set by CoopCore: host->clients
        public Action<int, INetMessage> SendToClient;
        public Action<INetMessage> BroadcastInteraction;

        // HireWorkerPanelUI keeps its identity and guards private; read them instead of
        // duplicating fee/level math that a game update could drift away from
        private static readonly System.Reflection.FieldInfo FiPanelIsHired =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_IsHired");
        private static readonly System.Reflection.FieldInfo FiPanelIndex =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_Index");
        private static readonly System.Reflection.FieldInfo FiPanelLevelRequired =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_LevelRequired");
        private static readonly System.Reflection.FieldInfo FiPanelHireFee =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_TotalHireFee");
        private static readonly System.Reflection.FieldInfo FiPanelScreen =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_HireWorkerScreen");
        private static readonly System.Reflection.MethodInfo MiPanelEvaluateHired =
            AccessTools.Method(typeof(HireWorkerPanelUI), "EvaluateHired");
        private static readonly System.Reflection.FieldInfo FiInteractWorker =
            AccessTools.Field(typeof(WorkerInteractUIScreen), "m_Worker");
        private static readonly System.Reflection.FieldInfo FiOptionWorker =
            AccessTools.Field(typeof(WorkerOptionUIScreen), "m_Worker");
        private static readonly System.Reflection.FieldInfo FiPriceWorker =
            AccessTools.Field(typeof(WorkerOptionSetPriceUIScreen), "m_Worker");
        private static readonly System.Reflection.FieldInfo FiPackWorker =
            AccessTools.Field(typeof(WorkerSetPackOpenerTypeOptionScreen), "m_Worker");

        private WorkerManager _wm;
        private HireWorkerScreen _hireScreen;
        private bool _hireScreenSearched; // the screen may legitimately not exist yet
        private readonly SnapshotGate _gate = new SnapshotGate(1.0f, 15f, -0.7f);
        private readonly List<Entry> _buf = new List<Entry>(MaxWorkers);
        private readonly Dictionary<int, int> _workerLeaseOwner = new Dictionary<int, int>();
        private static readonly Dictionary<int, bool> ClientWorkerBusy = new Dictionary<int, bool>();
        private static readonly HashSet<int> ClientWorkerLease = new HashSet<int>();
        private static bool _allowClientWorkerOpen;
        private static readonly System.Reflection.FieldInfo FiWorkerTargetRotation =
            AccessTools.Field(typeof(Worker), "m_TargetLerpRotation");

        public StaffSync()
        {
            Instance = this;
        }

        public override void Start() => Instance = this;

        public override string Name => nameof(StaffSync);

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        private struct Entry
        {
            public bool Hired;
            public bool HasData;
            public byte PrimaryTask;
            public byte SecondaryTask;
            public byte WorkerTask;
            public byte CurrentState;
            public bool GoingHome;
            public byte BonusCount;
            public bool BonusBoosted;
            public bool FillNoLabel;
            public bool RoundUpPrice;
            public bool RoundUpCardPrice;
            public bool AvoidSetCardPrice;
            public bool AvoidSetCardPriceRestock;
            public float PriceMult;
            public float CardPriceMult;
            public List<bool> PackTypes; // reference to the game's list; read-only here
            public List<int> ExpList;
        }

        public override void Reset()
        {
            _wm = null;
            _hireScreen = null;
            _hireScreenSearched = false;
            _gate.Reset(-0.7f);
            _workerLeaseOwner.Clear();
            ClientWorkerBusy.Clear();
            ClientWorkerLease.Clear();
            _allowClientWorkerOpen = false;
        }

        public override void ForceResend()
        {
            _gate.Force();
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(Instance, this))
                Instance = null;
            ApplyingRemote = false;
        }

        private WorkerManager Wm()
        {
            // NEVER CSingleton<WorkerManager>.Instance: resolved while no real manager
            // exists (host mid-session save load) the getter fabricates a fake empty
            // DontDestroyOnLoad manager that shadows the real one for the rest of the
            // run (see WorldSync.ResolveShelfManager)
            if (_wm == null)
                _wm = UnityEngine.Object.FindObjectOfType<WorkerManager>();
            return _wm;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(HireWorkerPanelUI), "OnPressHireButton",
                prefix: new HarmonyMethod(typeof(StaffSync), nameof(HirePrefix)));
            Try(h, typeof(WorkerInteractUIScreen), "SetTaskAsPrimaryOrSecondary",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(TaskChangedPostfix)));
            Try(h, typeof(WorkerOptionUIScreen), "OnPressRestockShelfWithNoLabel",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(OptionChangedPostfix)));
            Try(h, typeof(WorkerOptionSetPriceUIScreen), "OnPressConfirm",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(PriceOptionPostfix)));
            Try(h, typeof(WorkerSetPackOpenerTypeOptionScreen), "OnPressConfirm",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(PackOptionPostfix)));
            Try(h, typeof(WorkerInteractUIScreen), "OnPressGiveBonus",
                prefix: new HarmonyMethod(typeof(StaffSync), nameof(BonusPrefix)));
            Try(h, typeof(WorkerInteractUIScreen), "OnPressFire",
                prefix: new HarmonyMethod(typeof(StaffSync), nameof(FirePrefix)));
            Try(h, typeof(Worker), "OnMousePress",
                prefix: new HarmonyMethod(typeof(StaffSync), nameof(WorkerMousePressPrefix)));
            Try(h, typeof(Worker), "OnPressStopInteract",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(WorkerStopInteractPostfix)));
        }

        /// <summary>Client: block the vanilla hire BEFORE it charges the (forwarded)
        /// wallet or flips the local roster, and ask the host to run the real thing.
        /// The vanilla UX guards are re-checked locally so the button still talks back.</summary>
        public static bool HirePrefix(HireWorkerPanelUI __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            try
            {
                if ((bool)FiPanelIsHired.GetValue(__instance))
                    return false;
                int index = (int)FiPanelIndex.GetValue(__instance);
                int levelRequired = (int)FiPanelLevelRequired.GetValue(__instance);
                float fee = (float)FiPanelHireFee.GetValue(__instance);
                if (CPlayerData.m_ShopLevel + 1 < levelRequired)
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.ShopLevelNotEnough);
                    return false;
                }
                // the panel was Init'd when the screen opened; the roster may have
                // echoed a hire (ours or the host's) since then
                if (index < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(index))
                    return false;
                if (CPlayerData.m_CoinAmountDouble < (double)fee)
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.Money);
                    return false;
                }
                var self = Instance;
                if (self?.SendOp == null)
                {
                    // wired sessions always set SendOp; letting vanilla run here would
                    // charge the shared wallet for a worker that never exists
                    CoopPlugin.Log.LogWarning("StaffSync: hire pressed but SendOp not wired - ignored");
                    return false;
                }
                self.SendOp(new StaffOpMessage { Op = OpHire, Index = index });
                SoundManager.GenericConfirm();
                if (CoopCore.Instance != null)
                {
                    CoopCore.Instance.RegisterLine = "hired - starting work at the host's shop";
                    CoopCore.Instance.RegisterLineTimer = 4f;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("StaffSync hire prefix: " + e.Message); }
            return false;
        }

        private static Worker WorkerFrom(System.Reflection.FieldInfo field, object instance)
        {
            try
            {
                return field?.GetValue(instance) as Worker;
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        private static void SendUpdate(Worker worker)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote || worker == null || Instance?.SendOp == null)
                return;
            try
            {
                var d = worker.GetWorkerSaveData();
                Instance.SendOp(new StaffOpMessage
                {
                    Op = OpUpdate,
                    Index = worker.m_WorkerIndex,
                    PrimaryTask = (byte)d.primaryTask,
                    SecondaryTask = (byte)d.secondaryTask,
                    WorkerTask = (byte)d.workerTask,
                    FillNoLabel = d.isFillShelfWithoutLabel,
                    RoundUpPrice = d.isRoundUpPrice,
                    AvoidSetCardPrice = d.isAvoidSetCardPrice,
                    RoundUpCardPrice = d.isRoundUpCardPrice,
                    AvoidSetCardPriceRestock = d.isAvoidSetCardPriceWhileRestock,
                    PriceMult = d.setPriceMultiplier,
                    CardPriceMult = d.setCardPriceMultiplier,
                    PackTypes = d.cardPackItemTypeEnabledList,
                });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("StaffSync update send: " + e.Message); }
        }

        public static void TaskChangedPostfix(WorkerInteractUIScreen __instance)
        {
            if (CoopCore.Role == CoopRole.Client)
                SendUpdate(WorkerFrom(FiInteractWorker, __instance));
        }
        public static void OptionChangedPostfix(WorkerOptionUIScreen __instance)
        {
            if (CoopCore.Role == CoopRole.Client)
                SendUpdate(WorkerFrom(FiOptionWorker, __instance));
        }
        public static void PriceOptionPostfix(WorkerOptionSetPriceUIScreen __instance)
        {
            if (CoopCore.Role == CoopRole.Client)
                SendUpdate(WorkerFrom(FiPriceWorker, __instance));
        }
        public static void PackOptionPostfix(WorkerSetPackOpenerTypeOptionScreen __instance)
        {
            if (CoopCore.Role == CoopRole.Client)
                SendUpdate(WorkerFrom(FiPackWorker, __instance));
        }

        public static bool BonusPrefix(WorkerInteractUIScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            var worker = WorkerFrom(FiInteractWorker, __instance);
            if (worker == null || Instance?.SendOp == null)
                return false;
            Instance.SendOp(new StaffOpMessage { Op = OpBonus, Index = worker.m_WorkerIndex });
            return false;
        }

        public static bool FirePrefix(WorkerInteractUIScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            var worker = WorkerFrom(FiInteractWorker, __instance);
            if (worker == null || Instance?.SendOp == null)
                return false;
            Instance.SendOp(new StaffOpMessage { Op = OpFire, Index = worker.m_WorkerIndex });
            try
            {
                worker.OnPressStopInteract();
                __instance.CloseScreen();
            }
            catch (System.Exception caught) { Swallow.Log(caught); }
            return false;
        }

        public static bool WorkerMousePressPrefix(Worker __instance)
        {
            if (__instance == null)
                return false;
            int index = __instance.m_WorkerIndex;
            if (_allowClientWorkerOpen)
                return true;
            if (CoopCore.Role == CoopRole.Host)
                return Instance != null && Instance.HostBeginInteraction(index, 0, default(Vector3));
            if (CoopCore.Role != CoopRole.Client)
                return true;
            if (ClientWorkerLease.Contains(index))
                return false;
            if (ClientWorkerBusy.TryGetValue(index, out bool busy) && busy)
                return false;
            if (Instance?.SendOp == null)
                return false;
            Vector3 pos;
            if (!CoopCore.TryGetLocalPlayerPosition(out pos))
                return false;
            Instance.SendOp(new StaffOpMessage { Op = OpBeginInteract, Index = index, Position = pos });
            return false;
        }

        public static void WorkerStopInteractPostfix(Worker __instance)
        {
            if (__instance == null)
                return;
            int index = __instance.m_WorkerIndex;
            if (CoopCore.Role == CoopRole.Host)
                Instance?.HostEndInteraction(index, 0);
            else if (CoopCore.Role == CoopRole.Client && ClientWorkerLease.Contains(index))
            {
                // Vanilla calls OnPressStopInteract before the enclosing screen method's
                // postfix. Delay the release one frame so that a committed task/option
                // update is sent while this client still owns the lease.
                if (CoopCore.Instance != null)
                    CoopCore.Instance.StartCoroutine(ReleaseClientWorkerLater(index));
                else
                    ReleaseClientWorker(index);
            }
        }

        private static IEnumerator ReleaseClientWorkerLater(int index)
        {
            yield return null;
            ReleaseClientWorker(index);
        }

        private static void ReleaseClientWorker(int index)
        {
            if (!ClientWorkerLease.Remove(index))
                return;
            Instance?.SendOp?.Invoke(new StaffOpMessage { Op = OpEndInteract, Index = index });
        }

        public static void ClientInteractionMessage(StaffInteractMessage message)
        {
            int index = message.Index;
            bool granted = message.Granted;
            bool occupied = message.Occupied;
            ClientWorkerBusy[index] = occupied;
            if (!occupied)
                ClientWorkerLease.Remove(index);
            if (!granted)
                return;
            ClientWorkerLease.Add(index);
            var worker = NpcSync.GetWorkerPuppet(index);
            if (worker == null)
                return;
            _allowClientWorkerOpen = true;
            try
            {
                worker.OnMousePress();
            }
            finally { _allowClientWorkerOpen = false; }
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }

        // ---------------- host ----------------

        public void HostApplyOp(StaffOpMessage message, int connId)
        {
            byte op = message.Op;
            Guarded("apply", () =>
            {
                switch (op)
                {
                    case OpHire:
                        HostHire(message.Index);
                        break;
                    case OpUpdate:
                        HostUpdate(message, connId);
                        break;
                    case OpBonus:
                        HostBonus(message.Index, connId);
                        break;
                    case OpFire:
                        HostFire(message.Index, connId);
                        break;
                    case OpBeginInteract:
                        HostBeginInteraction(message.Index, connId, message.Position);
                        break;
                    case OpEndInteract:
                        HostEndInteraction(message.Index, connId);
                        break;
                    default:
                        CoopPlugin.Log.LogWarning("StaffSync: unknown op " + op);
                        break;
                }
            });
        }

        private bool ValidWorkerIndex(int index)
        {
            var workers = WorkerManager.GetWorkerList();
            return workers != null && index >= 0 && index < workers.Count && workers[index] != null
                && index < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(index);
        }

        private void SendInteraction(int connId, int index, bool granted, bool occupied)
        {
            if (connId <= 0)
                return;
            SendToClient?.Invoke(connId, new StaffInteractMessage { Index = index, Granted = granted, Occupied = occupied });
        }

        private void BroadcastInteractionState(int index, bool occupied)
        {
            BroadcastInteraction?.Invoke(new StaffInteractMessage { Index = index, Granted = false, Occupied = occupied });
        }

        private bool HostBeginInteraction(int index, int connId, Vector3 playerPosition)
        {
            if (!ValidWorkerIndex(index))
                return false;
            if (_workerLeaseOwner.TryGetValue(index, out int owner))
            {
                if (owner == connId)
                    return true;
                SendInteraction(connId, index, false, true);
                return false;
            }
            var worker = WorkerManager.GetWorkerList()[index];
            if (connId != 0)
            {
                Vector3 toward = playerPosition - worker.transform.position;
                toward.y = 0f;
                if (toward.sqrMagnitude > 0.0001f)
                {
                    try
                    {
                        FiWorkerTargetRotation?.SetValue(worker, Quaternion.LookRotation(toward, Vector3.up));
                    }
                    catch (System.Exception caught) { Swallow.Log(caught); }
                }
            }
            worker.m_IsPausingAction = true;
            _workerLeaseOwner[index] = connId;
            SendInteraction(connId, index, true, true);
            BroadcastInteractionState(index, true);
            return true;
        }

        private void HostEndInteraction(int index, int connId)
        {
            if (!_workerLeaseOwner.TryGetValue(index, out int owner) || owner != connId)
                return;
            var workers = WorkerManager.GetWorkerList();
            if (workers != null && index >= 0 && index < workers.Count && workers[index] != null)
                workers[index].m_IsPausingAction = false;
            _workerLeaseOwner.Remove(index);
            BroadcastInteractionState(index, false);
        }

        public void HostReleaseConn(int connId)
        {
            var release = new List<int>();
            foreach (var kv in _workerLeaseOwner)
                if (kv.Value == connId)
                    release.Add(kv.Key);
            foreach (int index in release)
                HostEndInteraction(index, connId);
        }

        private void HostUpdate(StaffOpMessage message, int connId)
        {
            int index = message.Index;
            if (!_workerLeaseOwner.TryGetValue(index, out int leaseOwner) || leaseOwner != connId)
                return;
            var wm = Wm();
            if (wm == null || index < 0 || index >= wm.m_WorkerDataList.Count)
                return;
            var workers = WorkerManager.GetWorkerList();
            if (workers == null || index >= workers.Count || workers[index] == null)
                return;
            var w = workers[index];
            if (!CPlayerData.GetIsWorkerHired(index))
                return;
            var primary = (EWorkerTask)message.PrimaryTask;
            var secondary = (EWorkerTask)message.SecondaryTask;
            var task = (EWorkerTask)message.WorkerTask;
            bool fill = message.FillNoLabel, round = message.RoundUpPrice, avoid = message.AvoidSetCardPrice;
            bool cardRound = message.RoundUpCardPrice, cardAvoid = message.AvoidSetCardPriceRestock;
            float mult = Mathf.Clamp(message.PriceMult, 0f, 10f);
            float cardMult = Mathf.Clamp(message.CardPriceMult, 0f, 10f);
            int pn = message.PackTypes == null ? 0 : Math.Min(message.PackTypes.Count, 255);
            var packs = new bool[pn];
            for (int i = 0; i < pn; i++)
                packs[i] = message.PackTypes[i];
            if ((int)primary < 0 || ((int)primary > 6 && primary != EWorkerTask.GoBackHome))
                return;
            if ((int)secondary < 0 || ((int)secondary > 6 && secondary != EWorkerTask.GoBackHome))
                return;
            w.SetRestockShelfWithNoLabel(fill);
            w.UpdateSetPriceOption(round, avoid, mult);
            w.UpdateSetCardPriceOption(cardRound, cardAvoid, cardMult);
            for (int i = 0; i < packs.Length && i < w.GetCardPackItemTypeEnabledList().Count; i++)
                w.SetCardPackItemTypeEnabled(i, packs[i]);
            w.SetTask(primary);
            w.SetLastTask(task);
            w.SetSecondaryTask(secondary);
            ForceResend();
        }

        private void HostBonus(int index, int connId)
        {
            var wm = Wm();
            var workers = WorkerManager.GetWorkerList();
            if (wm == null || workers == null || index < 0 || index >= workers.Count || workers[index] == null
                || !CPlayerData.GetIsWorkerHired(index))
                return;
            if (!_workerLeaseOwner.TryGetValue(index, out int owner) || owner != connId)
                return;
            var w = workers[index];
            if (w.GetBonusBoostedCount() >= 3)
                return;
            float fee = w.GetWorkerData().costPerDay;
            if (CPlayerData.m_CoinAmountDouble < fee)
                return;
            PriceChangeManager.AddTransaction(-fee, ETransactionType.WorkerSalary, 0);
            CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(fee));
            CPlayerData.m_GameReportDataCollect.employeeCost -= fee;
            CPlayerData.m_GameReportDataCollectPermanent.employeeCost -= fee;
            w.GiveSalaryBonus();
            ForceResend();
        }

        private void HostFire(int index, int connId)
        {
            var workers = WorkerManager.GetWorkerList();
            if (workers == null || index < 0 || index >= workers.Count || workers[index] == null
                || !CPlayerData.GetIsWorkerHired(index))
                return;
            if (!_workerLeaseOwner.TryGetValue(index, out int owner) || owner != connId)
                return;
            workers[index].FireWorker();
            HostEndInteraction(index, connId);
            ForceResend();
        }

        /// <summary>Host: run HireWorkerPanelUI.OnPressHireButton's happy path minus its
        /// panel UI, so a joiner's hire is indistinguishable from the host's own. The
        /// wallet is charged HERE and only here - the client path was blocked before
        /// its ReduceCoin could fire.</summary>
        private void HostHire(int index)
        {
            try
            {
                var wm = Wm();
                if (wm == null || wm.m_WorkerDataList == null)
                    return;
                if (index < 0 || index >= wm.m_WorkerDataList.Count || index >= CPlayerData.m_IsWorkerHired.Count)
                {
                    CoopPlugin.Log.LogWarning("StaffSync: hire op for unknown worker " + index);
                    return;
                }
                // double-hire guard: duplicate ops, or both players racing the same panel
                if (CPlayerData.GetIsWorkerHired(index))
                    return;
                WorkerData workerData = WorkerManager.GetWorkerData(index);
                if (CPlayerData.m_ShopLevel + 1 < workerData.shopLevelRequired)
                    return;
                var gm = CSingleton<CGameManager>.Instance;
                if (gm != null && gm.m_IsPrologue && !workerData.prologueShow)
                    return;
                if (CPlayerData.m_CoinAmountDouble < (double)workerData.hiringCost)
                {
                    // the client pre-checked its mirror; losing this race is rare and the
                    // roster echo (still unhired) is the correction
                    CoopPlugin.Log.LogInfo("StaffSync: hire refused, not enough money for worker " + index);
                    return;
                }
                // vanilla hire path, faithfully (including the report counters and the
                // achievement check the panel does)
                PriceChangeManager.AddTransaction(0f - workerData.hiringCost, ETransactionType.HireWorker, index);
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(workerData.hiringCost));
                CPlayerData.SetIsWorkerHired(index, isHired: true);
                wm.ActivateWorker(index, resetTask: true);
                CPlayerData.m_GameReportDataCollect.employeeCost -= workerData.hiringCost;
                CPlayerData.m_GameReportDataCollectPermanent.employeeCost -= workerData.hiringCost;
                int hiredCount = 0;
                for (int i = 0; i < CPlayerData.m_IsWorkerHired.Count; i++)
                {
                    if (CPlayerData.m_IsWorkerHired[i])
                        hiredCount++;
                }
                AchievementManager.OnStaffHired(hiredCount);
                SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
                CoopPlugin.Log.LogInfo("StaffSync: joiner hired worker " + index);
                ForceResend(); // the confirming echo rides the next tick
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("StaffSync host hire: " + e.Message); }
        }

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            if (!_gate.Due(dt))
                return;
            Guarded("host", () =>
            {
                var wm = Wm();
                if (wm == null || wm.m_WorkerDataList == null)
                    return;
                Collect(wm, _buf);
                int hash = HashEntries(_buf);
                if (!_gate.ShouldSend(hash))
                    return;
                var list = _buf; // serialized synchronously by Msg.Build; safe to close over
                var entries = new List<StaffEntry>(list.Count);
                for (int i = 0; i < list.Count; i++)
                    entries.Add(ToStaffEntry(list[i]));
                BroadcastState?.Invoke(new StaffStateMessage { Entries = entries });
            });
        }

        /// <summary>Essentials come from the LIVE Worker when it's active (the save-data
        /// list only refreshes on save), falling back to the saved copy for workers who
        /// are hired but home for the night.</summary>
        private static void Collect(WorkerManager wm, List<Entry> outList)
        {
            outList.Clear();
            int n = Mathf.Min(wm.m_WorkerDataList.Count, MaxWorkers);
            var workers = WorkerManager.GetWorkerList();
            var saved = CPlayerData.m_WorkerSaveDataList;
            for (int i = 0; i < n; i++)
            {
                var e = new Entry
                {
                    Hired = i < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(i),
                };
                WorkerSaveData d = null;
                var w = workers != null && i < workers.Count ? workers[i] : null;
                if (w != null && w.m_IsActive)
                {
                    try
                    {
                        d = w.GetWorkerSaveData();
                    }
                    catch (System.Exception caught) { Swallow.Log(caught); }
                }
                if (d == null && saved != null && i < saved.Count)
                    d = saved[i];
                if (d != null)
                {
                    e.HasData = true;
                    e.PrimaryTask = (byte)d.primaryTask;
                    e.SecondaryTask = (byte)d.secondaryTask;
                    e.WorkerTask = (byte)d.workerTask;
                    e.CurrentState = (byte)d.currentState;
                    e.GoingHome = d.isGoingHome;
                    e.BonusCount = (byte)Mathf.Clamp(d.bonusBoostedCount, 0, 255);
                    e.BonusBoosted = d.isBonusBoosted;
                    e.FillNoLabel = d.isFillShelfWithoutLabel;
                    e.RoundUpPrice = d.isRoundUpPrice;
                    e.RoundUpCardPrice = d.isRoundUpCardPrice;
                    e.AvoidSetCardPrice = d.isAvoidSetCardPrice;
                    e.AvoidSetCardPriceRestock = d.isAvoidSetCardPriceWhileRestock;
                    e.PriceMult = d.setPriceMultiplier;
                    e.CardPriceMult = d.setCardPriceMultiplier;
                    e.PackTypes = d.cardPackItemTypeEnabledList;
                    e.ExpList = d.expList;
                }
                outList.Add(e);
            }
        }

        private static int HashEntries(List<Entry> list)
        {
            int hash = 17;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                hash = hash * 31 + (e.Hired ? 1 : 0);
                if (!e.HasData)
                {
                    hash = hash * 31;
                    continue;
                }
                hash = hash * 31 + e.PrimaryTask;
                hash = hash * 31 + e.SecondaryTask;
                hash = hash * 31 + e.WorkerTask;
                hash = hash * 31 + e.CurrentState;
                hash = hash * 31 + (e.GoingHome ? 1 : 0);
                hash = hash * 31 + e.BonusCount;
                hash = hash * 31 + PackFlags(e);
                hash = hash * 31 + (int)(e.PriceMult * 100f);
                hash = hash * 31 + (int)(e.CardPriceMult * 100f);
                if (e.PackTypes != null)
                {
                    for (int k = 0; k < e.PackTypes.Count; k++)
                        hash = hash * 31 + (e.PackTypes[k] ? 1 : 0);
                }
                if (e.ExpList != null)
                {
                    for (int k = 0; k < e.ExpList.Count; k++)
                        hash = hash * 31 + e.ExpList[k];
                }
            }
            return hash;
        }

        // ---------------- client ----------------

        public void ClientApplyState(StaffStateMessage message)
        {
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(StaffStateMessage message)
        {
            int n = message.Entries.Count;
            bool rosterChanged = false;
            var saved = CPlayerData.m_WorkerSaveDataList;
            // One scene lookup per state apply, not per worker: the interaction screen caches
            // the bonus count when it opens, so an open screen must be refreshed from the mirror.
            var interactScreen = UnityEngine.Object.FindObjectOfType<WorkerInteractUIScreen>(true);
            for (int i = 0; i < n; i++)
            {
                var e = message.Entries[i];
                if (i < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(i) != e.Hired)
                {
                    // roster only - no ActivateWorker: real workers stay suppressed on the
                    // client, puppets carry the visuals; this flag is what the hire screen
                    // and the salary totals (bills) read
                    CPlayerData.SetIsWorkerHired(i, e.Hired);
                    rosterChanged = true;
                }
                if (!e.HasData || saved == null)
                    continue;
                // WorkerManager.m_WorkerSaveDataList aliases this list after load, so
                // writing entries in place updates both mirrors
                while (saved.Count <= i)
                    saved.Add(new WorkerSaveData());
                var d = saved[i];
                if (d == null)
                {
                    d = new WorkerSaveData();
                    saved[i] = d;
                }
                d.primaryTask = (EWorkerTask)e.PrimaryTask;
                d.secondaryTask = (EWorkerTask)e.SecondaryTask;
                d.workerTask = (EWorkerTask)e.WorkerTask;
                d.currentState = (EWorkerState)e.CurrentState;
                d.isGoingHome = e.GoingHome;
                d.bonusBoostedCount = e.BonusCount;
                d.isBonusBoosted = e.BonusBoosted;
                d.isFillShelfWithoutLabel = e.FillNoLabel;
                d.isRoundUpPrice = e.RoundUpPrice;
                d.isRoundUpCardPrice = e.RoundUpCardPrice;
                d.isAvoidSetCardPrice = e.AvoidSetCardPrice;
                d.isAvoidSetCardPriceWhileRestock = e.AvoidSetCardPriceRestock;
                d.setPriceMultiplier = e.PriceMult;
                d.setCardPriceMultiplier = e.CardPriceMult;
                if (e.PackTypes != null)
                    d.cardPackItemTypeEnabledList = e.PackTypes;
                if (e.ExpList != null)
                    d.expList = e.ExpList;
                NpcSync.RefreshWorkerUi(i, d);
                RefreshInteractScreen(interactScreen, i);
            }
            if (rosterChanged)
                RefreshHirePanels();
        }

        /// <summary>The hire screen Init()s its panels on every open, but an echo that
        /// lands while the joiner is LOOKING at the screen (the case right after they
        /// press Hire) must flip the panel to "Hired" without a reopen.</summary>
        private void RefreshHirePanels()
        {
            if (!_hireScreenSearched)
            {
                _hireScreenSearched = true;
                _hireScreen = UnityEngine.Object.FindObjectOfType<HireWorkerScreen>(true);
            }
            if (_hireScreen == null || _hireScreen.m_HireWorkerPanelUIList == null
                || MiPanelEvaluateHired == null || FiPanelScreen == null)
                return;
            for (int i = 0; i < _hireScreen.m_HireWorkerPanelUIList.Count; i++)
            {
                var panel = _hireScreen.m_HireWorkerPanelUIList[i];
                if (panel == null)
                    continue;
                // a panel that was never Init'd has index 0 and no screen ref; skip it -
                // the screen's own OnOpenScreen -> Init covers the first open
                if (FiPanelScreen.GetValue(panel) == null)
                    continue;
                try
                {
                    MiPanelEvaluateHired.Invoke(panel, null);
                }
                catch (System.Exception caught) { Swallow.Log(caught); }
            }
        }

        /// <summary>The interaction screen caches the bonus count when it opens, so refresh
        /// its controls when a mirrored worker update lands while the screen is open.</summary>
        private static void RefreshInteractScreen(WorkerInteractUIScreen screen, int index)
        {
            if (screen == null || screen.m_ScreenGrp == null || !screen.m_ScreenGrp.activeSelf)
                return;
            var worker = FiInteractWorker.GetValue(screen) as Worker;
            if (worker == null || worker.m_WorkerIndex != index)
                return;
            int count = worker.GetBonusBoostedCount();
            screen.m_GiveBonusBtn.interactable = count < 3;
            if (count > 0)
            {
                screen.m_BonusAddAmountText.text = "+" + count;
                screen.m_BonusAddAmountGrp.SetActive(true);
            }
            else
            {
                screen.m_BonusAddAmountGrp.SetActive(false);
            }
        }

        // ---------------- wire ----------------

        private static byte PackFlags(Entry e)
        {
            return (byte)((e.Hired ? 1 : 0)
                | (e.HasData ? 2 : 0)
                | (e.BonusBoosted ? 4 : 0)
                | (e.FillNoLabel ? 8 : 0)
                | (e.RoundUpPrice ? 16 : 0)
                | (e.RoundUpCardPrice ? 32 : 0)
                | (e.AvoidSetCardPrice ? 64 : 0)
                | (e.AvoidSetCardPriceRestock ? 128 : 0));
        }

        /// <summary>Map the internal entry snapshot onto the wire DTO's entry shape.
        /// The two structs carry the same fields; only the type name differs.</summary>
        private static StaffEntry ToStaffEntry(Entry e)
        {
            return new StaffEntry
            {
                Hired = e.Hired,
                HasData = e.HasData,
                PrimaryTask = e.PrimaryTask,
                SecondaryTask = e.SecondaryTask,
                WorkerTask = e.WorkerTask,
                CurrentState = e.CurrentState,
                GoingHome = e.GoingHome,
                BonusCount = e.BonusCount,
                BonusBoosted = e.BonusBoosted,
                FillNoLabel = e.FillNoLabel,
                RoundUpPrice = e.RoundUpPrice,
                RoundUpCardPrice = e.RoundUpCardPrice,
                AvoidSetCardPrice = e.AvoidSetCardPrice,
                AvoidSetCardPriceRestock = e.AvoidSetCardPriceRestock,
                PriceMult = e.PriceMult,
                CardPriceMult = e.CardPriceMult,
                PackTypes = e.PackTypes,
                ExpList = e.ExpList,
            };
        }
    }
}
