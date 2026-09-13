using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors the on-table visuals of customer card matches host->client
    /// (MsgType.TableState, host->client ONLY - there are no ops).
    ///
    /// RESEARCH NOTE (decompiled/InteractablePlayTable.cs, TableGameItemSet.cs,
    /// Customer.cs): a customer match does NOT lay out real CardData cards on the
    /// table. The visible layout is exactly two things:
    ///   1. per-seat TableGameItemSet child objects on the table prefab
    ///      (m_TableGameItemSetList[seat]): a playmat, a deck box and a comic book
    ///      mesh, configured from TableGameItemSetData (3 EItemTypes) via
    ///      SpecificSetup(...) and toggled with gameObject.SetActive - see
    ///      InteractablePlayTable.CustomerHasReached / LoadData / StopTableGame;
    ///   2. the card fan / single card props on the CUSTOMER prefab
    ///      (Customer.m_GameCardFanOut / m_GameCardSingle) - those already ride
    ///      NpcSync's IsPlaying flag on the puppets, so they are out of scope here.
    /// There are no per-slot Card3dUI groups, dice or coins on the table, so the
    /// digest is per table: per seat (active?, playmat/deckbox/comic EItemTypes) -
    /// no Msg.WriteCard / Card3dUISpawner needed at all. Applying uses the game's
    /// own SpecificSetup recipe on the client's SAME table prefab children, so
    /// nothing is spawned, pooled or registered: pure visuals on existing objects.
    /// </summary>
    public class PlayTableSync : TickableCoopModule
    {
        private const float Cadence = 1.5f;
        private const float HealInterval = 12f;
        private const int MaxTables = 250;         // wire: table count is a byte
        private const int MaxSeats = 8;            // vanilla tables have 2; hard cap
        private const int MaxTableBytes = 250;     // per-table budget (fixed format stays ~28B)
        private const byte IntentKindPlayTable = 1;
        private const byte IntentKickTable = 1;

        private struct SeatState
        {
            public bool Active;
            public int PlayMat, DeckBox, Comic; // EItemType values

            public bool Same(SeatState o)
            {
                return Active == o.Active && PlayMat == o.PlayMat
                    && DeckBox == o.DeckBox && Comic == o.Comic;
            }
        }

        /// <summary>Set by CoopCore: host -> clients state broadcast (MsgType.TableState).</summary>
        public Action<INetMessage> BroadcastState;
        public static PlayTableSync Active;
        private static PlayerIntentBus _intents;

        private readonly SnapshotGate _gate = new SnapshotGate(1.5f, 12f, -7.6f);
        private bool _loggedDrop;   // budget overflow warned once, not every tick
        private ShelfManager _sm;

        // client: last state applied per (tableIdx<<8 | seat), so a heal broadcast
        // does not re-run SpecificSetup (it re-randomizes deckbox/comic positions
        // every call - reapplying unchanged data would make the props jump around)
        private readonly Dictionary<int, SeatState> _applied = new Dictionary<int, SeatState>();
        private readonly Dictionary<int, bool> _occupied = new Dictionary<int, bool>();

        // TableState remains host->client-only. The kick is a separate single-shot intent;
        // the joiner never edits the local mirror or charges money.
        public PlayTableSync()
        {
            Active = this;
        }

        public override string Name => nameof(PlayTableSync);

        public override void Start() => Active = this;
        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public void RegisterIntents(PlayerIntentBus bus)
        {
            if (bus == null)
                throw new ArgumentNullException("bus");
            _intents = bus;
            bus.Register(IntentKindPlayTable, IntentKickTable, HostKickTable);
        }

        public static void ApplyPatches(Harmony h)
        {
            var original = AccessTools.Method(typeof(InteractablePlayTable), "StartMoveObject");
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("PlayTableSync patch target missing: InteractablePlayTable.StartMoveObject");
                return;
            }
            h.Patch(original, prefix: new HarmonyMethod(typeof(PlayTableSync), nameof(StartMoveObjectPrefix)));
        }

        public override void Reset()
        {
            ClearMirrors();
            _applied.Clear();
            _occupied.Clear();
            _gate.Reset(-7.6f);
            _loggedDrop = false;
            _sm = null;
        }

        public override void ForceResend()
        {
            _gate.Force();
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(Active, this))
                Active = null;
            _intents = null;
        }

        private ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
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
                var sm = Sm();
                if (sm == null)
                    return;
                var tables = sm.m_PlayTableList;
                // every table rides every broadcast (per-seat inactive flags clear the
                // client), so match end / table move heal without a tombstone protocol
                int hash = 17;
                int count = Mathf.Min(tables.Count, MaxTables);
                if (tables.Count > MaxTables && !_loggedDrop)
                {
                    _loggedDrop = true;
                    CoopPlugin.Log.LogWarning($"PlayTableSync: {tables.Count - MaxTables} play tables beyond the {MaxTables} cap are not mirrored");
                }
                for (int i = 0; i < count; i++)
                {
                    var table = tables[i];
                    hash = hash * 31 + (table == null ? 0 : 1);
                    if (table == null)
                        continue;
                    hash = hash * 31 + (table.GetCurrentPlayerCount() > 0 ? 1 : 0);
                    var sets = table.m_TableGameItemSetList;
                    int seats = sets != null ? Mathf.Min(sets.Count, MaxSeats) : 0;
                    for (int s = 0; s < seats; s++)
                    {
                        var st = HostSeat(sets[s]);
                        hash = hash * 31 + (st.Active ? 1 : 0);
                        if (!st.Active)
                            continue;
                        hash = hash * 31 + st.PlayMat;
                        hash = hash * 31 + st.DeckBox;
                        hash = hash * 31 + st.Comic;
                    }
                }

                if (!_gate.ShouldSend(hash))
                    return;
                BroadcastState?.Invoke(BuildState(tables, count));
            });
        }

        private static SeatState HostSeat(TableGameItemSet set)
        {
            if (set == null || !set.gameObject.activeSelf)
                return default;
            var data = set.m_TableGameItemSetData;
            return new SeatState
            {
                Active = true,
                PlayMat = data != null ? (int)data.playMatType : 0,
                DeckBox = data != null ? (int)data.deckBoxType : 0,
                Comic = data != null ? (int)data.comicBookType : 0,
            };
        }

        private static TableStateMessage BuildState(List<InteractablePlayTable> tables, int count)
        {
            var msg = new TableStateMessage();
            for (int i = 0; i < count; i++)
            {
                var table = tables[i];
                var sets = table != null ? table.m_TableGameItemSetList : null;
                int seats = sets != null ? Mathf.Min(sets.Count, MaxSeats) : 0;
                // fixed format: 2 + seats*(1|13) bytes - a vanilla 2-seat table is at
                // most 28 bytes, far under the MaxTableBytes budget by construction
                var entry = new TableEntry { Index = (byte)i };
                entry.Occupied = table != null && table.GetCurrentPlayerCount() > 0;
                for (int s = 0; s < seats; s++)
                {
                    var st = HostSeat(sets[s]);
                    var seat = new TableSeatEntry { Active = st.Active };
                    if (st.Active)
                    {
                        // the three set pieces are EItemTypes, one of the id spaces
                        // EnhancedPrefabLoader mints custom ids into - so they travel as
                        // HOST ids like every other modded id (identity below the modded
                        // floor, and identity here anyway: only the host writes this)
                        seat.PlayMat = (EItemType)st.PlayMat;
                        seat.DeckBox = (EItemType)st.DeckBox;
                        seat.Comic = (EItemType)st.Comic;
                    }
                    entry.Seats.Add(seat);
                }
                msg.Tables.Add(entry);
            }
            return msg;
        }

        // ---------------- client ----------------

        public void ClientApplyState(TableStateMessage message)
        {
            Guarded("apply", () => ClientApplyInner(message));
        }

        private void ClientApplyInner(TableStateMessage message)
        {
            var sm = Sm();
            var tables = sm != null ? sm.m_PlayTableList : null;
            for (int i = 0; i < message.Tables.Count; i++)
            {
                var entry = message.Tables[i];
                int tableIdx = entry.Index;
                _occupied[tableIdx] = entry.Occupied;
                var seats = entry.Seats;
                InteractablePlayTable table =
                    (tables != null && tableIdx < tables.Count) ? tables[tableIdx] : null;
                // the JOINER may be playing the minigame at this table right now -
                // never stomp their own session's props (covers m_IsPlayerOccupied too)
                bool skipTable = table == null || table.GetHasStartPlayerPlayCard();
                var sets = (!skipTable) ? table.m_TableGameItemSetList : null;
                for (int s = 0; s < seats.Count; s++)
                {
                    var se = seats[s];
                    var st = new SeatState { Active = se.Active };
                    if (st.Active)
                    {
                        // host ids -> ours (already translated by the DTO deserialize), so
                        // ApplySeat below can cast straight to a LOCAL EItemType. A set piece
                        // from a pack only the host has resolves to EItemType.None and simply
                        // paints no mesh - the seat is still shown, one-sided packs are allowed
                        st.PlayMat = (int)se.PlayMat;
                        st.DeckBox = (int)se.DeckBox;
                        st.Comic = (int)se.Comic;
                    }
                    if (skipTable || sets == null || s >= sets.Count)
                        continue;
                    ApplySeat(tableIdx, s, sets[s], st);
                }
            }
        }

        public static bool StartMoveObjectPrefix(InteractablePlayTable __instance)
        {
            if (CoopCore.Role != CoopRole.Client || __instance == null)
                return true;
            if (__instance.GetHasStartPlayerPlayCard())
                return true;
            if (!__instance.GetIsTournamentPlayTable() && Active != null
                && Active.IsOccupied(__instance))
            {
                var sm = Active.Sm();
                int index = sm != null && sm.m_PlayTableList != null
                    ? sm.m_PlayTableList.IndexOf(__instance) : -1;
                if (index >= 0 && index <= 255)
                {
                    if (_intents != null && _intents.TrySend(IntentKindPlayTable, IntentKickTable, (byte)index))
                        return false;
                }
            }
            return true;
        }

        private bool IsOccupied(InteractablePlayTable table)
        {
            Sm();
            int index = _sm != null && _sm.m_PlayTableList != null
                ? _sm.m_PlayTableList.IndexOf(table) : -1;
            if (index >= 0 && _occupied.TryGetValue(index, out var occupied))
                return occupied;
            return false;
        }

        private void HostKickTable(PlayerIntentMessage message)
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            var sm = Sm();
            if (sm == null || sm.m_PlayTableList == null || message.Target >= sm.m_PlayTableList.Count)
                return;
            var table = sm.m_PlayTableList[message.Target];
            if (table == null || table.GetIsTournamentPlayTable() || table.GetCurrentPlayerCount() <= 0)
                return;
            if (StopTableGame == null)
            {
                CoopPlugin.Log.LogError("PlayTableSync: InteractablePlayTable.StopTableGame was not found");
                return;
            }
            CoopPlugin.Log.LogInfo($"PlayTableSync: client requested kick for table {message.Target}");
            StopTableGame.Invoke(table, null);
        }

        private static readonly System.Reflection.MethodInfo StopTableGame =
            AccessTools.Method(typeof(InteractablePlayTable), "StopTableGame");

        private void ApplySeat(int tableIdx, int seat, TableGameItemSet set, SeatState want)
        {
            if (set == null)
                return;
            int key = (tableIdx << 8) | seat;
            if (_applied.TryGetValue(key, out var have) && have.Same(want)
                && set.gameObject.activeSelf == want.Active)
                return;
            try
            {
                if (want.Active)
                {
                    // the game's own recipe (InteractablePlayTable.CustomerHasReached):
                    // SpecificSetup paints the playmat/deckbox/comic meshes, then the
                    // set is activated. Purely cosmetic on the table's own children -
                    // m_HasStartPlay et al stay false, so no sim state is touched and
                    // nothing is spawned or registered anywhere.
                    var data = new TableGameItemSetData
                    {
                        playMatType = (EItemType)want.PlayMat,
                        deckBoxType = (EItemType)want.DeckBox,
                        comicBookType = (EItemType)want.Comic,
                    };
                    set.SpecificSetup(data);
                    set.gameObject.SetActive(true);
                }
                else
                {
                    set.gameObject.SetActive(false);
                }
                _applied[key] = want;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"PlayTableSync seat {tableIdx}/{seat}: " + e.Message);
            }
        }

        /// <summary>Client: hide every mirror we activated (disconnect / scene reset).
        /// Only seats we tracked are touched, and only to deactivate - a joiner's own
        /// live minigame table is never in _applied (skipped at apply time).</summary>
        private void ClearMirrors()
        {
            if (_applied.Count == 0)
                return;
            var sm = _sm; // cached only - never FindObjectOfType during teardown
            var tables = sm != null ? sm.m_PlayTableList : null;
            if (tables != null)
            {
                foreach (var kv in _applied)
                {
                    if (!kv.Value.Active)
                        continue;
                    int tableIdx = kv.Key >> 8;
                    int seat = kv.Key & 0xFF;
                    try
                    {
                        if (tableIdx >= tables.Count || tables[tableIdx] == null)
                            continue;
                        var sets = tables[tableIdx].m_TableGameItemSetList;
                        if (sets != null && seat < sets.Count && sets[seat] != null)
                            sets[seat].gameObject.SetActive(false);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                }
            }
            _applied.Clear();
        }
    }
}
