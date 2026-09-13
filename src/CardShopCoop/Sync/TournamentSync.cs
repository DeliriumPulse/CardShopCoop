using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors tournament DATA and scheduling host->client (MsgType.TournamentState).
    /// The customer bracket only exists in the host simulation, so the joiner gets
    /// CPlayerData.m_TournamentData (schedule, fee, sign-ups, round, prize catalog)
    /// plus a per-customer digest of CustomerTournamentData - enough for the phone
    /// app (HostTournamentScreen reads m_TournamentData live on open) and for the
    /// physical pairing board, which we drive directly through TournamentPairingScreen
    /// because RefreshAllCustomerData wants live Customer objects the joiner never has.
    /// There are NO client ops: scheduling is host-only, so the joiner's confirm/cancel
    /// buttons are blocked with a "the host schedules tournaments" toast instead of
    /// being forwarded. Prize shelf CONTENTS are synced elsewhere (CardShelfSync).
    /// </summary>
    public class TournamentSync : TickableCoopModule
    {
        /// <summary>TournamentPrizeShelf.m_ScreenMesh (the shelf's little tournament display) is
        /// absent from the Game Pass Assembly-CSharp, which made a direct field access fail to
        /// COMPILE against that build - one cosmetic toggle taking the whole universal DLL down
        /// with it. Resolved once through reflection instead, so a missing or renamed field just
        /// disables the show/hide. Null when the field isn't there; every use is null-guarded.
        /// (Reflection change contributed by Jburne10 for the Game Pass build.)</summary>
        private static readonly FieldInfo FiScreenMesh =
            AccessTools.Field(typeof(TournamentPrizeShelf), "m_ScreenMesh");

        /// <summary>Set by CoopCore: host -> clients state broadcast.</summary>
        public Action<INetMessage> BroadcastState;

        /// <summary>True while ClientApplyState writes CPlayerData.m_TournamentData, so
        /// no patch mistakes the authoritative copy for a local scheduling action.</summary>
        public static bool ApplyingRemote;

        private readonly SnapshotGate _gate = new SnapshotGate(1.5f, 15f, -6.1f);
        private int _clientHash;

        // NEVER CSingleton<CustomerManager>.Instance: touched while no real manager
        // exists (client reload loading screen, host mid-session save load) the getter
        // fabricates a fake empty DontDestroyOnLoad manager that shadows the real one
        // for the rest of the run (see WorldSync.ResolveShelfManager). Static because
        // the wire writer and hash are static; fake-null re-resolves after scene loads.
        private static CustomerManager _cm;

        private static CustomerManager Cm()
        {
            if (_cm == null)
                _cm = UnityEngine.Object.FindObjectOfType<CustomerManager>();
            return _cm;
        }

        public override string Name => "tournament";

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public override void Dispose()
        {
            base.Dispose();
            ApplyingRemote = false;
            _cm = null;
        }

        public override void Reset()
        {
            _gate.Reset(-6.1f);
            _clientHash = 0;
            _cm = null;
        }

        public override void ForceResend()
        {
            _gate.Force();
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // Scheduling, cancelling and prize setup all mutate m_TournamentData and the
            // prize shelf plan - host-only decisions, since only the host's customer sim
            // can actually run the event. The joiner's phone screen stays readable (it
            // renders the synced data) but its buttons do nothing except explain why.
            Try(h, typeof(HostTournamentScreen), "OnPressConfirm",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
            Try(h, typeof(HostTournamentScreen), "OnPressCancel",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
            Try(h, typeof(HostTournamentScreen), "ConfirmCancelTournament",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
            Try(h, typeof(HostTournamentScreen), "OnPressPrizeSetup",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
        }

        public static bool ScheduleBlockPrefix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "the host schedules tournaments";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
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

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            if (!_gate.Due(dt))
                return;
            Guarded("host", () =>
            {
                var td = CPlayerData.m_TournamentData;
                if (td == null)
                    return;
                int hash = ComputeHash(td);
                if (!_gate.ShouldSend(hash))
                    return;
                BroadcastState?.Invoke(BuildState(td));
            });
        }

        // No HostApplyOp / SendOp: the joiner never sends tournament ops - scheduling
        // is blocked client-side with a toast rather than forwarded.

        // ---------------- client ----------------

        public void ClientApplyState(TournamentStateMessage message)
        {
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(TournamentStateMessage message)
        {
            var td = CPlayerData.m_TournamentData;
            if (td == null)
            {
                CPlayerData.m_TournamentData = td = new TournamentData();
            }

            byte flags = message.Flags;
            td.m_IsHostingTournament = (flags & 1) != 0;
            bool wasDay = td.m_IsTournamentDay;
            bool wasOver = td.m_IsTournamentDayOver;
            td.m_IsTournamentDay = (flags & 2) != 0;
            td.m_IsTournamentDayOver = (flags & 4) != 0;
            td.m_TournamentMaxPlayerCount = message.MaxPlayerCount;
            td.m_TournamentSignedUpCustomerCount = message.SignedUpCustomerCount;
            td.m_TournamentFinishedCurrentRoundCustomerCount = message.FinishedCurrentRoundCustomerCount;
            td.m_TournamentCurrentRound = message.CurrentRound;
            td.m_TournamentMaxRound = message.MaxRound;
            td.m_TournamentFee = message.Fee;
            td.m_TournamentTotalValue = message.TotalValue;

            // prize catalog: mutate the vanilla 4-slot list in place so screens that
            // index m_PrizeDataList[i] never see it shorter than they expect
            if (td.m_PrizeDataList == null)
                td.m_PrizeDataList = new List<TournamentPrizeDataList>();
            int lists = message.PrizeSlots.Count;
            while (td.m_PrizeDataList.Count < lists)
                td.m_PrizeDataList.Add(new TournamentPrizeDataList { m_PrizeDataList = new List<TournamentPrizeData>() });
            for (int i = 0; i < lists; i++)
            {
                var slot = td.m_PrizeDataList[i];
                if (slot.m_PrizeDataList == null)
                    slot.m_PrizeDataList = new List<TournamentPrizeData>();
                slot.m_PrizeDataList.Clear();
                var dtoSlot = message.PrizeSlots[i];
                for (int j = 0; j < dtoSlot.Prizes.Count; j++)
                {
                    var pe = dtoSlot.Prizes[j];
                    var p = new TournamentPrizeData();
                    if (pe.HasCard)
                        p.m_CardData = pe.Card;
                    // host id -> ours (already translated by the DTO deserialize); a prize
                    // from a pack only the host has becomes EItemType.None and the prize slot
                    // just shows nothing, which is what an unresolvable prize did before
                    // translation existed
                    p.m_ItemType = pe.ItemType;
                    p.m_Count = pe.Count;
                    slot.m_PrizeDataList.Add(p);
                }
            }

            // bracket digest
            int n = message.Bracket.Count;
            var digest = new List<PairingEntry>(n);
            for (int i = 0; i < n; i++)
            {
                var be = message.Bracket[i];
                var e = new PairingEntry();
                e.SortedIndex = be.SortedIndex;
                e.ModelIndex = be.ModelIndex;
                byte f = be.Flags;
                e.IsFemale = (f & 1) != 0;
                e.IsWin = (f & 2) != 0;
                e.HasResult = (f & 4) != 0;
                e.WinCount = be.WinCount;
                e.WinPoints = be.WinPoints;
                e.OMW = be.OMW;
                e.OOMW = be.OOMW;
                digest.Add(e);
            }

            // the heal broadcast repeats unchanged state every 15s; skip the UI churn
            // (ShowPairingScreen resets every panel) when nothing actually moved
            int hash = ComputeHash(td);
            for (int i = 0; i < digest.Count; i++)
            {
                var e = digest[i];
                hash = hash * 31 + e.SortedIndex;
                hash = hash * 31 + e.ModelIndex;
                hash = hash * 31 + ((e.IsFemale ? 1 : 0) | (e.IsWin ? 2 : 0) | (e.HasResult ? 4 : 0));
                hash = hash * 31 + e.WinCount;
                hash = hash * 31 + e.WinPoints;
                hash = hash * 31 + e.OMW;
                hash = hash * 31 + e.OOMW;
            }
            if (hash == _clientHash)
                return;
            _clientHash = hash;

            RefreshBoards(td, digest, wasDay != td.m_IsTournamentDay || wasOver != td.m_IsTournamentDayOver);
        }

        /// <summary>Client: the pairing board and shelf screen mesh are normally driven
        /// by day-start events, which the mod suppresses on the joiner - so we gate them
        /// here, exactly the way TournamentPrizeShelf.CheckTournamentScreenVisibility does.</summary>
        private void RefreshBoards(TournamentData td, List<PairingEntry> digest, bool visibilityChanged)
        {
            var cm = Cm();
            if (cm == null || cm.m_TournamentPairingScreen == null)
                return;
            var screen = cm.m_TournamentPairingScreen;
            bool showBoard = td.m_IsTournamentDay || td.m_IsTournamentDayOver;

            if (visibilityChanged)
            {
                try
                {
                    screen.gameObject.SetActive(showBoard);
                    var shelves = ShelfManager.GetTournamentPrizeShelfList();
                    for (int i = 0; i < shelves.Count; i++)
                    {
                        if (shelves[i] == null || FiScreenMesh == null)
                            continue;
                        var mesh = FiScreenMesh.GetValue(shelves[i]) as GameObject;
                        if (mesh != null)
                            mesh.SetActive(showBoard);
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("TournamentSync board vis: " + e.Message); }
            }
            if (!showBoard)
            {
                screen.ShowPairingScreen(isShow: false, 0);
                return;
            }

            // full repaint: ShowPairingScreen resets the panels, then we repopulate from
            // the digest with fabricated CustomerTournamentData - UpdateCustomerData only
            // reads the scalar fields we carry
            screen.ShowPairingScreen(isShow: true, td.m_TournamentMaxPlayerCount);
            screen.UpdateCurrentRound(td.m_TournamentCurrentRound, td.m_TournamentMaxRound);
            int panels = screen.m_TournamentPairingUIGrpList != null ? screen.m_TournamentPairingUIGrpList.Count : 0;
            for (int i = 0; i < digest.Count; i++)
            {
                var e = digest[i];
                if (e.SortedIndex / 2 >= panels)
                    continue;
                screen.OnCustomerRegisterStart(e.SortedIndex, e.ModelIndex, e.IsFemale);
                var ctd = new CustomerTournamentData
                {
                    m_TournamentCustomerSortedIndex = e.SortedIndex,
                    m_IsTournamentWin = e.IsWin,
                    m_HasRegisteredTournamentResult = e.HasResult,
                    m_TournamentWinCount = e.WinCount,
                    m_TournamentWinPoints = e.WinPoints,
                    m_TournamentOMW = e.OMW,
                    m_TournamentOOMW = e.OOMW,
                };
                screen.m_TournamentPairingUIGrpList[e.SortedIndex / 2].UpdateCustomerData(ctd);
            }
        }

        private struct PairingEntry
        {
            public int SortedIndex;
            public int ModelIndex;
            public bool IsFemale;
            public bool IsWin;
            public bool HasResult;
            public int WinCount;
            public int WinPoints;
            public int OMW;
            public int OOMW;
        }

        // ---------------- wire / hash ----------------

        private static TournamentStateMessage BuildState(TournamentData td)
        {
            var msg = new TournamentStateMessage
            {
                Flags = (byte)((td.m_IsHostingTournament ? 1 : 0)
                             | (td.m_IsTournamentDay ? 2 : 0)
                             | (td.m_IsTournamentDayOver ? 4 : 0)),
                MaxPlayerCount = td.m_TournamentMaxPlayerCount,
                SignedUpCustomerCount = td.m_TournamentSignedUpCustomerCount,
                FinishedCurrentRoundCustomerCount = td.m_TournamentFinishedCurrentRoundCustomerCount,
                CurrentRound = td.m_TournamentCurrentRound,
                MaxRound = td.m_TournamentMaxRound,
                Fee = td.m_TournamentFee,
                TotalValue = td.m_TournamentTotalValue,
            };

            var lists = td.m_PrizeDataList;
            int lc = lists != null ? Mathf.Min(lists.Count, 8) : 0;
            for (int i = 0; i < lc; i++)
            {
                var slot = new TournamentPrizeSlot();
                var inner = lists[i] != null ? lists[i].m_PrizeDataList : null;
                int ec = inner != null ? Mathf.Min(inner.Count, 64) : 0;
                for (int j = 0; j < ec; j++)
                {
                    var p = inner[j];
                    bool hasCard = p != null && p.m_CardData != null;
                    slot.Prizes.Add(new TournamentPrizeEntry
                    {
                        HasCard = hasCard,
                        Card = hasCard ? p.m_CardData : null,
                        // item prizes are EItemTypes (a modded id space) - the card above
                        // already goes through the WriteCard chokepoint
                        ItemType = p != null ? p.m_ItemType : (EItemType)0,
                        Count = p != null ? p.m_Count : 0,
                    });
                }
                msg.PrizeSlots.Add(slot);
            }

            // bracket digest straight from the host's live sorted list (the same list
            // the vanilla pairing board renders from)
            var cm = Cm();
            var sorted = cm != null ? cm.m_TournamentSortedCustomerList : null;
            int n = sorted != null ? Mathf.Min(sorted.Count, 64) : 0;
            for (int i = 0; i < n; i++)
            {
                var c = sorted[i];
                var ctd = c != null ? c.GetCustomerTournamentData() : null;
                if (ctd == null)
                {
                    msg.Bracket.Add(new TournamentBracketEntry());
                    continue;
                }
                msg.Bracket.Add(new TournamentBracketEntry
                {
                    SortedIndex = (byte)Mathf.Clamp(ctd.m_TournamentCustomerSortedIndex, 0, 255),
                    ModelIndex = c.GetCustomerModelIndex(),
                    Flags = (byte)((c.m_IsFemale ? 1 : 0)
                                 | (ctd.m_IsTournamentWin ? 2 : 0)
                                 | (ctd.m_HasRegisteredTournamentResult ? 4 : 0)),
                    WinCount = ctd.m_TournamentWinCount,
                    WinPoints = ctd.m_TournamentWinPoints,
                    OMW = ctd.m_TournamentOMW,
                    OOMW = ctd.m_TournamentOOMW,
                });
            }
            return msg;
        }

        /// <summary>Change detector over everything BuildState sends. The host also folds
        /// in the live bracket; the client re-derives the same shape from the payload.</summary>
        private static int ComputeHash(TournamentData td)
        {
            int hash = 17;
            hash = hash * 31 + ((td.m_IsHostingTournament ? 1 : 0)
                              | (td.m_IsTournamentDay ? 2 : 0)
                              | (td.m_IsTournamentDayOver ? 4 : 0));
            hash = hash * 31 + td.m_TournamentMaxPlayerCount;
            hash = hash * 31 + td.m_TournamentSignedUpCustomerCount;
            hash = hash * 31 + td.m_TournamentFinishedCurrentRoundCustomerCount;
            hash = hash * 31 + td.m_TournamentCurrentRound;
            hash = hash * 31 + td.m_TournamentMaxRound;
            hash = hash * 31 + (int)(td.m_TournamentFee * 100f);
            hash = hash * 31 + (int)(td.m_TournamentTotalValue * 100f);
            var lists = td.m_PrizeDataList;
            if (lists != null)
            {
                for (int i = 0; i < lists.Count; i++)
                {
                    var inner = lists[i] != null ? lists[i].m_PrizeDataList : null;
                    if (inner == null)
                        continue;
                    for (int j = 0; j < inner.Count; j++)
                    {
                        var p = inner[j];
                        if (p == null)
                            continue;
                        hash = hash * 31 + (int)p.m_ItemType;
                        hash = hash * 31 + p.m_Count;
                        if (p.m_CardData != null)
                        {
                            hash = hash * 31 + (int)p.m_CardData.expansionType;
                            hash = hash * 31 + (int)p.m_CardData.monsterType;
                            hash = hash * 31 + (int)p.m_CardData.borderType;
                            hash = hash * 31 + ((p.m_CardData.isFoil ? 1 : 0) | (p.m_CardData.isDestiny ? 2 : 0));
                        }
                    }
                }
            }
            // host side only: fold the live bracket so round results retrigger a send
            if (CoopCore.Role == CoopRole.Host)
            {
                var cm = Cm();
                var sorted = cm != null ? cm.m_TournamentSortedCustomerList : null;
                if (sorted != null)
                {
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        var ctd = sorted[i] != null ? sorted[i].GetCustomerTournamentData() : null;
                        if (ctd == null)
                            continue;
                        hash = hash * 31 + ctd.m_TournamentCustomerSortedIndex;
                        hash = hash * 31 + (sorted[i] != null ? sorted[i].GetCustomerModelIndex() : 0);
                        hash = hash * 31 + (((sorted[i] != null && sorted[i].m_IsFemale) ? 1 : 0)
                                          | (ctd.m_IsTournamentWin ? 2 : 0)
                                          | (ctd.m_HasRegisteredTournamentResult ? 4 : 0));
                        hash = hash * 31 + ctd.m_TournamentWinCount;
                        hash = hash * 31 + ctd.m_TournamentWinPoints;
                        hash = hash * 31 + ctd.m_TournamentOMW;
                        hash = hash * 31 + ctd.m_TournamentOOMW;
                    }
                }
            }
            return hash;
        }
    }
}
