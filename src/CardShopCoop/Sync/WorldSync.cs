using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Shelf-stock synchronization by snapshot diffing. Both sides load identical saves,
    /// so a compartment is identified by (shelfKind, shelfIndex, compartmentIndex) into
    /// ShelfManager's lists. Every 0.75s the world is snapshotted; whatever changed since
    /// the last snapshot is reported. On the host those diffs are authoritative broadcasts
    /// (they capture player actions, customers, workers - every mutation source, with no
    /// per-interaction patches). On the client, diffs against the last host-applied state
    /// are the local player's own actions and are sent to the host as requests.
    /// </summary>
    public class WorldSync
    {
        public struct Entry
        {
            public int Key;   // kind<<24 | shelfIdx<<8 | compIdx
            public int Type;  // EItemType
            public int Count;
        }

        private struct CompState { public int Type; public int Count; }

        private readonly Dictionary<int, CompState> _last = new Dictionary<int, CompState>();
        private readonly Dictionary<WarehouseShelf, List<ShelfCompartment>> _whComps
            = new Dictionary<WarehouseShelf, List<ShelfCompartment>>();
        private float _timer;

        /// <summary>Fired with locally-originated changes (host: broadcast; client: request).</summary>
        public Action<List<Entry>> OnLocalChanges;

        private static readonly FieldInfo FiWarehouseComps =
            AccessTools.Field(typeof(WarehouseShelf), "m_ItemCompartmentList");

        // NEVER CSingleton<ShelfManager>.Instance: if touched before the game scene
        // exists (e.g. deltas arriving during the client's loading screen) it silently
        // creates and caches an empty fake manager that then shadows the real one for
        // the whole session, breaking the game's own shelf loading.
        private ShelfManager _sm;

        private ShelfManager ResolveShelfManager()
        {
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        private static int Key(int kind, int shelf, int comp)
        {
            return (kind << 24) | ((shelf & 0xFFFF) << 8) | (comp & 0xFF);
        }

        public void Reset()
        {
            _last.Clear();
            _whComps.Clear();
            _timer = 0.35f; // staggered phase: engines must not all walk on the same frame
            _sm = null;
        }

        public void Tick(float dt, bool inGame)
        {
            if (!inGame) return;
            _timer += dt;
            if (_timer < 0.75f) return;
            _timer -= 0.75f; // keep the phase; reset-to-zero drifts back into alignment

            List<Entry> changes = null;
            try
            {
                var sm = ResolveShelfManager();
                if (sm == null) return;

                for (int i = 0; i < sm.m_ShelfList.Count; i++)
                {
                    var shelf = sm.m_ShelfList[i];
                    if (shelf == null) continue;
                    var comps = shelf.GetItemCompartmentList();
                    for (int j = 0; j < comps.Count; j++)
                        Visit(Key(0, i, j), comps[j], ref changes);
                }
                for (int i = 0; i < sm.m_WarehouseShelfList.Count; i++)
                {
                    var wh = sm.m_WarehouseShelfList[i];
                    if (wh == null) continue;
                    // the list object never changes identity - cache it per shelf instead
                    // of a reflection GetValue inside the hottest recurring walk
                    if (!_whComps.TryGetValue(wh, out var comps) || comps == null)
                        _whComps[wh] = comps = FiWarehouseComps?.GetValue(wh) as List<ShelfCompartment>;
                    if (comps == null) continue;
                    for (int j = 0; j < comps.Count; j++)
                        Visit(Key(1, i, j), comps[j], ref changes);
                }
                // combi card shelves and tournament prize shelves carry ITEM compartments
                // too (the bottom half) - they live in their own manager lists, so the
                // walks above never saw them
                for (int i = 0; i < sm.m_CardItemCombiShelfList.Count; i++)
                {
                    var combi = sm.m_CardItemCombiShelfList[i];
                    if (combi == null) continue;
                    var comps = combi.GetItemCompartmentList();
                    for (int j = 0; j < comps.Count; j++)
                        Visit(Key(3, i, j), comps[j], ref changes);
                }
                for (int i = 0; i < sm.m_TournamentPrizeShelfList.Count; i++)
                {
                    var prize = sm.m_TournamentPrizeShelfList[i];
                    if (prize == null) continue;
                    var comps = prize.GetItemCompartmentList();
                    for (int j = 0; j < comps.Count; j++)
                        Visit(Key(14, i, j), comps[j], ref changes);
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("WorldSync snapshot: " + e.Message);
                return;
            }

            if (changes != null && changes.Count > 0)
                OnLocalChanges?.Invoke(changes);
        }

        private void Visit(int key, ShelfCompartment comp, ref List<Entry> changes)
        {
            if (comp == null) return;
            int type = (int)comp.GetItemType();
            int count = comp.GetItemCount();
            if (_last.TryGetValue(key, out var st) && st.Type == type && st.Count == count)
                return;
            if (changes == null) changes = new List<Entry>();
            if (changes.Count >= 512) return; // leave un-recorded; picked up next tick
            _last[key] = new CompState { Type = type, Count = count };
            changes.Add(new Entry { Key = key, Type = type, Count = count });
        }

        /// <summary>Apply authoritative states (client) or requested states (host).</summary>
        public void ApplyRemote(List<Entry> entries)
        {
            var sm = ResolveShelfManager();
            if (sm == null) return;
            foreach (var e in entries)
            {
                try
                {
                    var comp = Resolve(sm, e.Key);
                    if (comp == null) continue;
                    ApplyCompartment(comp, e.Type, e.Count);
                    _last[e.Key] = new CompState { Type = e.Type, Count = e.Count };
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning($"WorldSync apply {e.Key:X}: {ex.Message}");
                }
            }
        }

        private static ShelfCompartment Resolve(ShelfManager sm, int key)
        {
            int kind = key >> 24;
            int shelfIdx = (key >> 8) & 0xFFFF;
            int compIdx = key & 0xFF;
            if (kind == 0)
            {
                if (shelfIdx >= sm.m_ShelfList.Count) return null;
                var comps = sm.m_ShelfList[shelfIdx]?.GetItemCompartmentList();
                return comps != null && compIdx < comps.Count ? comps[compIdx] : null;
            }
            if (kind == 3)
            {
                if (shelfIdx >= sm.m_CardItemCombiShelfList.Count) return null;
                var comps = sm.m_CardItemCombiShelfList[shelfIdx]?.GetItemCompartmentList();
                return comps != null && compIdx < comps.Count ? comps[compIdx] : null;
            }
            if (kind == 14)
            {
                if (shelfIdx >= sm.m_TournamentPrizeShelfList.Count) return null;
                var comps = sm.m_TournamentPrizeShelfList[shelfIdx]?.GetItemCompartmentList();
                return comps != null && compIdx < comps.Count ? comps[compIdx] : null;
            }
            if (shelfIdx >= sm.m_WarehouseShelfList.Count) return null;
            var whComps = FiWarehouseComps?.GetValue(sm.m_WarehouseShelfList[shelfIdx]) as List<ShelfCompartment>;
            return whComps != null && compIdx < whComps.Count ? whComps[compIdx] : null;
        }

        private static readonly FieldInfo FiStoredItemList =
            AccessTools.Field(typeof(ShelfCompartment), "m_StoredItemList");

        /// <summary>
        /// Set a compartment to exactly (type, count). IMPORTANT: ShelfCompartment.SpawnItem
        /// is a LOADER, not an adder - it sets m_ItemAmount = amount and appends `amount`
        /// fresh items, assuming an empty compartment (that's how Shelf.LoadItemCompartment
        /// uses it at save load). Calling it incrementally corrupts the count (the
        /// "shelf wiped down to one item" bug). So every apply is an atomic
        /// clear-and-rebuild: synchronous within the frame, no flicker, and it self-heals
        /// compartments whose m_ItemAmount already disagrees with their real item list.
        /// </summary>
        private static void ApplyCompartment(ShelfCompartment comp, int type, int count)
        {
            int curType = (int)comp.GetItemType();
            int cur = comp.GetItemCount();
            if (cur == count && (curType == type || count == 0)) return;

            // same product, fewer items (a customer bought some): remove exactly the
            // difference - the full teardown/respawn for a 1-item sale was constant
            // visible churn on the client every 0.75s during open hours
            if (curType == type && count < cur && count > 0)
            {
                for (int k = cur - count; k > 0; k--)
                {
                    var item = comp.GetLastItem();
                    if (item == null) break;
                    comp.RemoveItem(item);
                    ItemSpawnManager.DisableItem(item);
                }
                if (comp.GetItemCount() == count) return;
                // count disagrees (corrupted m_ItemAmount): fall through and self-heal
            }

            Clear(comp);
            if (count > 0)
            {
                comp.SetCompartmentItemType((EItemType)type);
                comp.CalculatePositionList();
                comp.SpawnItem(count, spawnFromFront: true);
            }
        }

        private static void Clear(ShelfCompartment comp)
        {
            // Drain the REAL stored list (not m_ItemAmount, which may be corrupted).
            if (FiStoredItemList?.GetValue(comp) is List<Item> stored && stored.Count > 0)
            {
                foreach (var item in new List<Item>(stored))
                {
                    if (item == null) continue;
                    comp.RemoveItem(item);
                    ItemSpawnManager.DisableItem(item);
                }
                stored.Clear();
            }
            else
            {
                for (int guard = 0; guard < 4096; guard++)
                {
                    var item = comp.GetLastItem();
                    if (item == null) break;
                    comp.RemoveItem(item);
                    ItemSpawnManager.DisableItem(item);
                }
            }
        }

        // ---- wire format ----

        public static void WriteEntries(BinaryWriter bw, List<Entry> entries)
        {
            bw.Write((ushort)entries.Count);
            foreach (var e in entries)
            {
                bw.Write(e.Key);
                bw.Write(e.Type);
                bw.Write((ushort)Math.Max(0, Math.Min(e.Count, ushort.MaxValue)));
            }
        }

        public static List<Entry> ReadEntries(BinaryReader br)
        {
            int n = br.ReadUInt16();
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
                list.Add(new Entry { Key = br.ReadInt32(), Type = br.ReadInt32(), Count = br.ReadUInt16() });
            return list;
        }
    }
}
