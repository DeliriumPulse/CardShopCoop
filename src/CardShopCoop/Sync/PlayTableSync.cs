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
    public class PlayTableSync
    {
        private const float Cadence = 1.5f;
        private const float HealInterval = 12f;
        private const int MaxTables = 250;         // wire: table count is a byte
        private const int MaxSeats = 8;            // vanilla tables have 2; hard cap
        private const int MaxTableBytes = 250;     // per-table budget (fixed format stays ~28B)

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
        public Action<Action<BinaryWriter>> BroadcastState;

        private float _timer;
        private int _lastHash;
        private float _heal;
        private bool _loggedDrop;   // budget overflow warned once, not every tick
        private ShelfManager _sm;

        // client: last state applied per (tableIdx<<8 | seat), so a heal broadcast
        // does not re-run SpecificSetup (it re-randomizes deckbox/comic positions
        // every call - reapplying unchanged data would make the props jump around)
        private readonly Dictionary<int, SeatState> _applied = new Dictionary<int, SeatState>();

        // no SendOp / HostApplyOp: TableState is host->client only, the joiner never
        // edits a customer match. ApplyPatches is a no-op kept for the module
        // contract: pure visuals need no vanilla paths blocked and charge no money.
        public static void ApplyPatches(Harmony h)
        {
        }

        public void Reset()
        {
            ClearMirrors();
            _applied.Clear();
            _timer = -7.6f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            _loggedDrop = false;
            _sm = null;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = 999f; // beats the hash gate even if the real hash is 0
        }

        private ShelfManager Sm()
        {
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame) return;
            _timer += dt;
            if (_timer < Cadence) return;
            _timer -= Cadence;
            try
            {
                var sm = Sm();
                if (sm == null) return;
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
                    if (table == null) continue;
                    var sets = table.m_TableGameItemSetList;
                    int seats = sets != null ? Mathf.Min(sets.Count, MaxSeats) : 0;
                    for (int s = 0; s < seats; s++)
                    {
                        var st = HostSeat(sets[s]);
                        hash = hash * 31 + (st.Active ? 1 : 0);
                        if (!st.Active) continue;
                        hash = hash * 31 + st.PlayMat;
                        hash = hash * 31 + st.DeckBox;
                        hash = hash * 31 + st.Comic;
                    }
                }

                _heal += Cadence;
                if (hash == _lastHash && _heal < HealInterval) return;
                _lastHash = hash;
                _heal = 0f;
                BroadcastState?.Invoke(bw => WriteState(bw, tables, count));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PlayTableSync host: " + e.Message); }
        }

        private static SeatState HostSeat(TableGameItemSet set)
        {
            if (set == null || !set.gameObject.activeSelf) return default;
            var data = set.m_TableGameItemSetData;
            return new SeatState
            {
                Active = true,
                PlayMat = data != null ? (int)data.playMatType : 0,
                DeckBox = data != null ? (int)data.deckBoxType : 0,
                Comic = data != null ? (int)data.comicBookType : 0,
            };
        }

        private static void WriteState(BinaryWriter bw, List<InteractablePlayTable> tables, int count)
        {
            bw.Write((byte)count);
            for (int i = 0; i < count; i++)
            {
                var table = tables[i];
                var sets = table != null ? table.m_TableGameItemSetList : null;
                int seats = sets != null ? Mathf.Min(sets.Count, MaxSeats) : 0;
                // fixed format: 2 + seats*(1|13) bytes - a vanilla 2-seat table is at
                // most 28 bytes, far under the MaxTableBytes budget by construction
                bw.Write((byte)i);
                bw.Write((byte)seats);
                for (int s = 0; s < seats; s++)
                {
                    var st = HostSeat(sets[s]);
                    bw.Write(st.Active);
                    if (!st.Active) continue;
                    bw.Write(st.PlayMat);
                    bw.Write(st.DeckBox);
                    bw.Write(st.Comic);
                }
            }
        }

        // ---------------- client ----------------

        public void ClientApplyState(BinaryReader br)
        {
            try { ClientApplyInner(br); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PlayTableSync apply: " + e.Message); }
        }

        private void ClientApplyInner(BinaryReader br)
        {
            var sm = Sm();
            var tables = sm != null ? sm.m_PlayTableList : null;
            int count = br.ReadByte();
            for (int i = 0; i < count; i++)
            {
                int tableIdx = br.ReadByte();
                int seats = br.ReadByte();
                InteractablePlayTable table =
                    (tables != null && tableIdx < tables.Count) ? tables[tableIdx] : null;
                // the JOINER may be playing the minigame at this table right now -
                // never stomp their own session's props (covers m_IsPlayerOccupied too)
                bool skipTable = table == null || table.GetHasStartPlayerPlayCard();
                var sets = (!skipTable) ? table.m_TableGameItemSetList : null;
                for (int s = 0; s < seats; s++)
                {
                    var st = new SeatState { Active = br.ReadBoolean() };
                    if (st.Active)
                    {
                        st.PlayMat = br.ReadInt32();
                        st.DeckBox = br.ReadInt32();
                        st.Comic = br.ReadInt32();
                    }
                    if (skipTable || sets == null || s >= sets.Count) continue;
                    ApplySeat(tableIdx, s, sets[s], st);
                }
            }
        }

        private void ApplySeat(int tableIdx, int seat, TableGameItemSet set, SeatState want)
        {
            if (set == null) return;
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
            if (_applied.Count == 0) return;
            var sm = _sm; // cached only - never FindObjectOfType during teardown
            var tables = sm != null ? sm.m_PlayTableList : null;
            if (tables != null)
            {
                foreach (var kv in _applied)
                {
                    if (!kv.Value.Active) continue;
                    int tableIdx = kv.Key >> 8;
                    int seat = kv.Key & 0xFF;
                    try
                    {
                        if (tableIdx >= tables.Count || tables[tableIdx] == null) continue;
                        var sets = tables[tableIdx].m_TableGameItemSetList;
                        if (sets != null && seat < sets.Count && sets[seat] != null)
                            sets[seat].gameObject.SetActive(false);
                    }
                    catch { }
                }
            }
            _applied.Clear();
        }
    }
}
