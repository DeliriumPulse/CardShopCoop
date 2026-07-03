using System;
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
    public class StaffSync
    {
        private const byte OpHire = 1;
        private const float SendInterval = 1.0f;
        private const float HealInterval = 15f;
        private const int MaxWorkers = 32;

        /// <summary>Patches are static but ops need the wired instance; CoopCore
        /// constructs exactly one StaffSync, so the constructor self-registers.</summary>
        public static StaffSync Instance;

        /// <summary>True while ClientApplyState writes the mirrors, so no patch of ours
        /// (present or future) mistakes an echo for a local action.</summary>
        public static bool ApplyingRemote;

        public Action<Action<BinaryWriter>> SendOp;         // set by CoopCore: client->host
        public Action<Action<BinaryWriter>> BroadcastState; // set by CoopCore: host->clients

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

        private WorkerManager _wm;
        private HireWorkerScreen _hireScreen;
        private bool _hireScreenSearched; // the screen may legitimately not exist yet
        private float _timer;
        private int _lastHash;
        private float _heal;
        private bool _force;
        private readonly List<Entry> _buf = new List<Entry>(MaxWorkers);

        public StaffSync()
        {
            Instance = this;
        }

        private struct Entry
        {
            public bool Hired;
            public bool HasData;
            public byte PrimaryTask;
            public byte SecondaryTask;
            public byte WorkerTask;
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
        }

        public void Reset()
        {
            _wm = null;
            _hireScreen = null;
            _hireScreenSearched = false;
            _timer = -0.7f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            _force = false;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = 0f;
            _force = true;
        }

        private WorkerManager Wm()
        {
            if (_wm == null) _wm = CSingleton<WorkerManager>.Instance;
            return _wm;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(HireWorkerPanelUI), "OnPressHireButton",
                prefix: new HarmonyMethod(typeof(StaffSync), nameof(HirePrefix)));
        }

        /// <summary>Client: block the vanilla hire BEFORE it charges the (forwarded)
        /// wallet or flips the local roster, and ask the host to run the real thing.
        /// The vanilla UX guards are re-checked locally so the button still talks back.</summary>
        public static bool HirePrefix(HireWorkerPanelUI __instance)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            try
            {
                if ((bool)FiPanelIsHired.GetValue(__instance)) return false;
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
                self.SendOp(bw => { bw.Write(OpHire); bw.Write(index); });
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

        public void HostApplyOp(BinaryReader br)
        {
            byte op = br.ReadByte();
            switch (op)
            {
                case OpHire:
                    HostHire(br.ReadInt32());
                    break;
                default:
                    CoopPlugin.Log.LogWarning("StaffSync: unknown op " + op);
                    break;
            }
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
                if (wm == null || wm.m_WorkerDataList == null) return;
                if (index < 0 || index >= wm.m_WorkerDataList.Count || index >= CPlayerData.m_IsWorkerHired.Count)
                {
                    CoopPlugin.Log.LogWarning("StaffSync: hire op for unknown worker " + index);
                    return;
                }
                // double-hire guard: duplicate ops, or both players racing the same panel
                if (CPlayerData.GetIsWorkerHired(index)) return;
                WorkerData workerData = WorkerManager.GetWorkerData(index);
                if (CPlayerData.m_ShopLevel + 1 < workerData.shopLevelRequired) return;
                var gm = CSingleton<CGameManager>.Instance;
                if (gm != null && gm.m_IsPrologue && !workerData.prologueShow) return;
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
                    if (CPlayerData.m_IsWorkerHired[i]) hiredCount++;
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
            if (!inGame) return;
            _timer += dt;
            if (_timer < SendInterval) return;
            _timer -= SendInterval;
            try
            {
                var wm = Wm();
                if (wm == null || wm.m_WorkerDataList == null) return;
                Collect(wm, _buf);
                int hash = HashEntries(_buf);
                _heal += SendInterval;
                if (!_force && hash == _lastHash && _heal < HealInterval) return;
                _force = false;
                _lastHash = hash;
                _heal = 0f;
                var list = _buf; // serialized synchronously by Msg.Build; safe to close over
                BroadcastState?.Invoke(bw => WriteState(bw, list));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("StaffSync host: " + e.Message); }
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
                    try { d = w.GetWorkerSaveData(); } catch { }
                }
                if (d == null && saved != null && i < saved.Count) d = saved[i];
                if (d != null)
                {
                    e.HasData = true;
                    e.PrimaryTask = (byte)d.primaryTask;
                    e.SecondaryTask = (byte)d.secondaryTask;
                    e.WorkerTask = (byte)d.workerTask;
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
                if (!e.HasData) { hash = hash * 31; continue; }
                hash = hash * 31 + e.PrimaryTask;
                hash = hash * 31 + e.SecondaryTask;
                hash = hash * 31 + e.WorkerTask;
                hash = hash * 31 + e.BonusCount;
                hash = hash * 31 + PackFlags(e);
                hash = hash * 31 + (int)(e.PriceMult * 100f);
                hash = hash * 31 + (int)(e.CardPriceMult * 100f);
                if (e.PackTypes != null)
                {
                    for (int k = 0; k < e.PackTypes.Count; k++)
                        hash = hash * 31 + (e.PackTypes[k] ? 1 : 0);
                }
            }
            return hash;
        }

        // ---------------- client ----------------

        public void ClientApplyState(BinaryReader br)
        {
            ApplyingRemote = true;
            try { ClientApplyInner(br); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("StaffSync client: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(BinaryReader br)
        {
            int n = br.ReadByte();
            bool rosterChanged = false;
            var saved = CPlayerData.m_WorkerSaveDataList;
            for (int i = 0; i < n; i++)
            {
                var e = ReadEntry(br);
                if (i < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(i) != e.Hired)
                {
                    // roster only - no ActivateWorker: real workers stay suppressed on the
                    // client, puppets carry the visuals; this flag is what the hire screen
                    // and the salary totals (bills) read
                    CPlayerData.SetIsWorkerHired(i, e.Hired);
                    rosterChanged = true;
                }
                if (!e.HasData || saved == null) continue;
                // WorkerManager.m_WorkerSaveDataList aliases this list after load, so
                // writing entries in place updates both mirrors
                while (saved.Count <= i) saved.Add(new WorkerSaveData());
                var d = saved[i];
                if (d == null) { d = new WorkerSaveData(); saved[i] = d; }
                d.primaryTask = (EWorkerTask)e.PrimaryTask;
                d.secondaryTask = (EWorkerTask)e.SecondaryTask;
                d.workerTask = (EWorkerTask)e.WorkerTask;
                d.bonusBoostedCount = e.BonusCount;
                d.isBonusBoosted = e.BonusBoosted;
                d.isFillShelfWithoutLabel = e.FillNoLabel;
                d.isRoundUpPrice = e.RoundUpPrice;
                d.isRoundUpCardPrice = e.RoundUpCardPrice;
                d.isAvoidSetCardPrice = e.AvoidSetCardPrice;
                d.isAvoidSetCardPriceWhileRestock = e.AvoidSetCardPriceRestock;
                d.setPriceMultiplier = e.PriceMult;
                d.setCardPriceMultiplier = e.CardPriceMult;
                if (e.PackTypes != null) d.cardPackItemTypeEnabledList = e.PackTypes;
            }
            if (rosterChanged) RefreshHirePanels();
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
                || MiPanelEvaluateHired == null || FiPanelScreen == null) return;
            for (int i = 0; i < _hireScreen.m_HireWorkerPanelUIList.Count; i++)
            {
                var panel = _hireScreen.m_HireWorkerPanelUIList[i];
                if (panel == null) continue;
                // a panel that was never Init'd has index 0 and no screen ref; skip it -
                // the screen's own OnOpenScreen -> Init covers the first open
                if (FiPanelScreen.GetValue(panel) == null) continue;
                try { MiPanelEvaluateHired.Invoke(panel, null); } catch { }
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

        private static void WriteState(BinaryWriter bw, List<Entry> list)
        {
            bw.Write((byte)Mathf.Min(list.Count, MaxWorkers));
            for (int i = 0; i < list.Count && i < MaxWorkers; i++)
            {
                var e = list[i];
                bw.Write(PackFlags(e));
                if (!e.HasData) continue;
                bw.Write(e.PrimaryTask);
                bw.Write(e.SecondaryTask);
                bw.Write(e.WorkerTask);
                bw.Write(e.BonusCount);
                bw.Write(e.PriceMult);
                bw.Write(e.CardPriceMult);
                int pn = e.PackTypes != null ? Mathf.Min(e.PackTypes.Count, 255) : 0;
                bw.Write((byte)pn);
                for (int k = 0; k < pn; k += 8)
                {
                    byte b = 0;
                    for (int bit = 0; bit < 8 && k + bit < pn; bit++)
                        if (e.PackTypes[k + bit]) b |= (byte)(1 << bit);
                    bw.Write(b);
                }
            }
        }

        private static Entry ReadEntry(BinaryReader br)
        {
            byte f = br.ReadByte();
            var e = new Entry
            {
                Hired = (f & 1) != 0,
                HasData = (f & 2) != 0,
                BonusBoosted = (f & 4) != 0,
                FillNoLabel = (f & 8) != 0,
                RoundUpPrice = (f & 16) != 0,
                RoundUpCardPrice = (f & 32) != 0,
                AvoidSetCardPrice = (f & 64) != 0,
                AvoidSetCardPriceRestock = (f & 128) != 0,
            };
            if (!e.HasData) return e;
            e.PrimaryTask = br.ReadByte();
            e.SecondaryTask = br.ReadByte();
            e.WorkerTask = br.ReadByte();
            e.BonusCount = br.ReadByte();
            e.PriceMult = br.ReadSingle();
            e.CardPriceMult = br.ReadSingle();
            int pn = br.ReadByte();
            var packs = new List<bool>(pn);
            for (int k = 0; k < pn; k += 8)
            {
                byte b = br.ReadByte();
                for (int bit = 0; bit < 8 && k + bit < pn; bit++)
                    packs.Add((b & (1 << bit)) != 0);
            }
            e.PackTypes = packs;
            return e;
        }
    }
}
