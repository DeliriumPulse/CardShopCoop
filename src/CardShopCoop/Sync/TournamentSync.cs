using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using CardShopCoop.Net;

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
    public class TournamentSync
    {
        /// <summary>Set by CoopCore: host -> clients state broadcast.</summary>
        public Action<Action<BinaryWriter>> BroadcastState;

        /// <summary>True while ClientApplyState writes CPlayerData.m_TournamentData, so
        /// no patch mistakes the authoritative copy for a local scheduling action.</summary>
        public static bool ApplyingRemote;

        private float _timer;
        private int _lastHash;      // host: last broadcast / client: last applied
        private float _heal;

        public void Reset()
        {
            _timer = -6.1f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = 999f; // beats the hash gate even if the real hash is 0
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
            if (CoopCore.Role != CoopRole.Client) return true;
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
            if (!inGame) return;
            _timer += dt;
            if (_timer < 1.5f) return;
            _timer -= 1.5f;
            try
            {
                var td = CPlayerData.m_TournamentData;
                if (td == null) return;
                int hash = ComputeHash(td);
                _heal += 1.5f;
                if (hash == _lastHash && _heal < 15f) return;
                _lastHash = hash;
                _heal = 0f;
                BroadcastState?.Invoke(bw => WriteState(bw, td));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TournamentSync host: " + e.Message); }
        }

        // No HostApplyOp / SendOp: the joiner never sends tournament ops - scheduling
        // is blocked client-side with a toast rather than forwarded.

        // ---------------- client ----------------

        public void ClientApplyState(BinaryReader br)
        {
            ApplyingRemote = true;
            try { ClientApplyInner(br); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TournamentSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(BinaryReader br)
        {
            var td = CPlayerData.m_TournamentData;
            if (td == null) { CPlayerData.m_TournamentData = td = new TournamentData(); }

            byte flags = br.ReadByte();
            td.m_IsHostingTournament = (flags & 1) != 0;
            bool wasDay = td.m_IsTournamentDay;
            bool wasOver = td.m_IsTournamentDayOver;
            td.m_IsTournamentDay = (flags & 2) != 0;
            td.m_IsTournamentDayOver = (flags & 4) != 0;
            td.m_TournamentMaxPlayerCount = br.ReadInt32();
            td.m_TournamentSignedUpCustomerCount = br.ReadInt32();
            td.m_TournamentFinishedCurrentRoundCustomerCount = br.ReadInt32();
            td.m_TournamentCurrentRound = br.ReadInt32();
            td.m_TournamentMaxRound = br.ReadInt32();
            td.m_TournamentFee = br.ReadSingle();
            td.m_TournamentTotalValue = br.ReadSingle();

            // prize catalog: mutate the vanilla 4-slot list in place so screens that
            // index m_PrizeDataList[i] never see it shorter than they expect
            if (td.m_PrizeDataList == null) td.m_PrizeDataList = new List<TournamentPrizeDataList>();
            int lists = br.ReadByte();
            while (td.m_PrizeDataList.Count < lists)
                td.m_PrizeDataList.Add(new TournamentPrizeDataList { m_PrizeDataList = new List<TournamentPrizeData>() });
            for (int i = 0; i < lists; i++)
            {
                var slot = td.m_PrizeDataList[i];
                if (slot.m_PrizeDataList == null) slot.m_PrizeDataList = new List<TournamentPrizeData>();
                slot.m_PrizeDataList.Clear();
                int entries = br.ReadByte();
                for (int j = 0; j < entries; j++)
                {
                    var p = new TournamentPrizeData();
                    if (br.ReadBoolean()) p.m_CardData = Msg.ReadCard(br);
                    p.m_ItemType = (EItemType)br.ReadInt32();
                    p.m_Count = br.ReadInt32();
                    slot.m_PrizeDataList.Add(p);
                }
            }

            // bracket digest
            int n = br.ReadByte();
            var digest = new List<PairingEntry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new PairingEntry();
                e.SortedIndex = br.ReadByte();
                e.ModelIndex = br.ReadInt32();
                byte f = br.ReadByte();
                e.IsFemale = (f & 1) != 0;
                e.IsWin = (f & 2) != 0;
                e.HasResult = (f & 4) != 0;
                e.WinCount = br.ReadInt32();
                e.WinPoints = br.ReadInt32();
                e.OMW = br.ReadInt32();
                e.OOMW = br.ReadInt32();
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
            if (hash == _lastHash) return;
            _lastHash = hash;

            RefreshBoards(td, digest, wasDay != td.m_IsTournamentDay || wasOver != td.m_IsTournamentDayOver);
        }

        /// <summary>Client: the pairing board and shelf screen mesh are normally driven
        /// by day-start events, which the mod suppresses on the joiner - so we gate them
        /// here, exactly the way TournamentPrizeShelf.CheckTournamentScreenVisibility does.</summary>
        private void RefreshBoards(TournamentData td, List<PairingEntry> digest, bool visibilityChanged)
        {
            var cm = CSingleton<CustomerManager>.Instance;
            if (cm == null || cm.m_TournamentPairingScreen == null) return;
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
                        if (shelves[i] != null && shelves[i].m_ScreenMesh != null)
                            shelves[i].m_ScreenMesh.SetActive(showBoard);
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
                if (e.SortedIndex / 2 >= panels) continue;
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

        private static void WriteState(BinaryWriter bw, TournamentData td)
        {
            bw.Write((byte)((td.m_IsHostingTournament ? 1 : 0)
                          | (td.m_IsTournamentDay ? 2 : 0)
                          | (td.m_IsTournamentDayOver ? 4 : 0)));
            bw.Write(td.m_TournamentMaxPlayerCount);
            bw.Write(td.m_TournamentSignedUpCustomerCount);
            bw.Write(td.m_TournamentFinishedCurrentRoundCustomerCount);
            bw.Write(td.m_TournamentCurrentRound);
            bw.Write(td.m_TournamentMaxRound);
            bw.Write(td.m_TournamentFee);
            bw.Write(td.m_TournamentTotalValue);

            var lists = td.m_PrizeDataList;
            int lc = lists != null ? Mathf.Min(lists.Count, 8) : 0;
            bw.Write((byte)lc);
            for (int i = 0; i < lc; i++)
            {
                var inner = lists[i] != null ? lists[i].m_PrizeDataList : null;
                int ec = inner != null ? Mathf.Min(inner.Count, 64) : 0;
                bw.Write((byte)ec);
                for (int j = 0; j < ec; j++)
                {
                    var p = inner[j];
                    bool hasCard = p != null && p.m_CardData != null;
                    bw.Write(hasCard);
                    if (hasCard) Msg.WriteCard(bw, p.m_CardData);
                    bw.Write(p != null ? (int)p.m_ItemType : 0);
                    bw.Write(p != null ? p.m_Count : 0);
                }
            }

            // bracket digest straight from the host's live sorted list (the same list
            // the vanilla pairing board renders from)
            var cm = CSingleton<CustomerManager>.Instance;
            var sorted = cm != null ? cm.m_TournamentSortedCustomerList : null;
            int n = sorted != null ? Mathf.Min(sorted.Count, 64) : 0;
            bw.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                var c = sorted[i];
                var ctd = c != null ? c.GetCustomerTournamentData() : null;
                if (ctd == null)
                {
                    bw.Write((byte)0); bw.Write(0); bw.Write((byte)0);
                    bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                    continue;
                }
                bw.Write((byte)Mathf.Clamp(ctd.m_TournamentCustomerSortedIndex, 0, 255));
                bw.Write(c.GetCustomerModelIndex());
                bw.Write((byte)((c.m_IsFemale ? 1 : 0)
                              | (ctd.m_IsTournamentWin ? 2 : 0)
                              | (ctd.m_HasRegisteredTournamentResult ? 4 : 0)));
                bw.Write(ctd.m_TournamentWinCount);
                bw.Write(ctd.m_TournamentWinPoints);
                bw.Write(ctd.m_TournamentOMW);
                bw.Write(ctd.m_TournamentOOMW);
            }
        }

        /// <summary>Change detector over everything WriteState sends. The host also folds
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
                    if (inner == null) continue;
                    for (int j = 0; j < inner.Count; j++)
                    {
                        var p = inner[j];
                        if (p == null) continue;
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
                var cm = CSingleton<CustomerManager>.Instance;
                var sorted = cm != null ? cm.m_TournamentSortedCustomerList : null;
                if (sorted != null)
                {
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        var ctd = sorted[i] != null ? sorted[i].GetCustomerTournamentData() : null;
                        if (ctd == null) continue;
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
