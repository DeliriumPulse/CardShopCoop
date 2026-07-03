using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Keeps the PLACED-OBJECT POPULATION identical on every machine. All other syncs key
    /// objects by their index in ShelfManager's lists, so a host buying (or selling) a
    /// shelf mid-session used to desync every index after it - moves landing on the wrong
    /// shelves, "he sees two shelves, I see one". The host broadcasts each list's
    /// (objectType, transform) roster every 3s; clients reconcile using the game's own
    /// save-load recipe (SpawnInteractableObject self-registers in list order) and the
    /// object's own OnDestroyed removal.
    /// </summary>
    public class PopulationSync
    {
        public const int KindCount = 15;

        /// <summary>Shared list resolver used by PopulationSync and ObjMoveSync so both
        /// always agree on what "kind 3, index 7" means.</summary>
        public static IList GetList(ShelfManager sm, int kind)
        {
            switch (kind)
            {
                case 0: return sm.m_ShelfList;
                case 1: return sm.m_WarehouseShelfList;
                case 2: return sm.m_CardShelfList;
                case 3: return sm.m_CardItemCombiShelfList;
                case 4: return sm.m_CashierCounterList;
                case 5: return sm.m_DecoObjectList;
                case 6: return sm.m_PlayTableList;
                case 7: return sm.m_WorkbenchList;
                case 8: return sm.m_TrashBinList;
                case 9: return sm.m_CardStorageShelfList;
                case 10: return sm.m_AutoCleanserList;
                case 11: return sm.m_AutoPackOpenerList;
                case 12: return sm.m_EmptyBoxStorageList;
                case 13: return sm.m_BulkDonationBoxList;
                case 14: return sm.m_TournamentPrizeShelfList;
                default: return null;
            }
        }

        public struct Entry
        {
            public int ObjType;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private ShelfManager _sm;
        private float _timer;
        private int _lastHash;
        private float _heal;

        public Action<List<List<Entry>>> OnHostSnapshot;

        /// <summary>Fired when reconciliation destroys or spawns an object of a kind -
        /// other syncs' index baselines for that kind are stale from this moment.</summary>
        public static Action<int> OnClientStructureChanged;

        public void Reset()
        {
            _sm = null;
            _timer = -1.1f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
        }

        private ShelfManager Sm()
        {
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        public void HostTick(float dt, bool active)
        {
            if (!active) return;
            _timer += dt;
            if (_timer < 3f) return;
            _timer -= 3f;
            try
            {
                var sm = Sm();
                if (sm == null) return;
                // population changes a handful of times per session; hash the cheap
                // identity (counts + types) and skip the heavy build when unchanged,
                // with a slow heal so a client that missed one still converges
                int hash = 17;
                for (int kind = 0; kind < KindCount; kind++)
                {
                    var list = GetList(sm, kind);
                    int n = list?.Count ?? 0;
                    hash = hash * 31 + n;
                    if (list != null)
                        for (int i = 0; i < n && i < 250; i++)
                            if (list[i] is InteractableObject obj)
                                hash = hash * 31 + (int)obj.m_ObjectType;
                }
                _heal += 3f;
                if (hash == _lastHash && _heal < 30f) return;
                _lastHash = hash;
                _heal = 0f;
                var all = new List<List<Entry>>(KindCount);
                for (int kind = 0; kind < KindCount; kind++)
                {
                    var list = GetList(sm, kind);
                    var entries = new List<Entry>(list?.Count ?? 0);
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count && i < 250; i++)
                        {
                            var obj = list[i] as InteractableObject;
                            if (obj == null) continue;
                            entries.Add(new Entry
                            {
                                ObjType = (int)obj.m_ObjectType,
                                Pos = obj.transform.position,
                                Rot = obj.transform.rotation,
                            });
                        }
                    }
                    all.Add(entries);
                }
                OnHostSnapshot?.Invoke(all);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PopulationSync host: " + e.Message); }
        }

        /// <summary>Client: make each object list match the host's roster.</summary>
        public void ClientApply(List<List<Entry>> hostLists)
        {
            var sm = Sm();
            if (sm == null) return;
            for (int kind = 0; kind < KindCount && kind < hostLists.Count; kind++)
            {
                try { ReconcileKind(sm, kind, hostLists[kind]); }
                catch (Exception e) { CoopPlugin.Log.LogWarning($"PopulationSync kind {kind}: {e.Message}"); }
            }
        }

        private static void ReconcileKind(ShelfManager sm, int kind, List<Entry> want)
        {
            var list = GetList(sm, kind);
            if (list == null) return;

            // extras beyond the host's roster: remove from the end (game removal shifts lists)
            int guard = 8;
            while (list.Count > want.Count && guard-- > 0)
            {
                var extra = list[list.Count - 1] as InteractableObject;
                if (extra == null) { list.RemoveAt(list.Count - 1); continue; }
                CoopPlugin.Log.LogInfo($"population: removing extra {extra.m_ObjectType} (kind {kind})");
                extra.OnDestroyed();
                OnClientStructureChanged?.Invoke(kind);
                list = GetList(sm, kind);
            }

            // type mismatches mid-list: repair ONE per tick (each removal shifts indices;
            // converges across ticks without ever mass-deleting on a glitch)
            for (int i = 0; i < list.Count && i < want.Count; i++)
            {
                var obj = list[i] as InteractableObject;
                if (obj == null) continue;
                if ((int)obj.m_ObjectType != want[i].ObjType)
                {
                    CoopPlugin.Log.LogInfo($"population: repairing index {i} (kind {kind}): {obj.m_ObjectType} -> {(EObjectType)want[i].ObjType}");
                    obj.OnDestroyed();
                    OnClientStructureChanged?.Invoke(kind);
                    return; // re-align next tick
                }
            }

            // missing objects: spawn with the game's own save-load recipe (self-registers
            // at the end of the list, keeping order identical to the host's)
            guard = 8;
            while (list.Count < want.Count && guard-- > 0)
            {
                var e = want[list.Count];
                var spawned = ShelfManager.SpawnInteractableObject((EObjectType)e.ObjType);
                if (spawned == null) break;
                spawned.transform.SetPositionAndRotation(e.Pos, e.Rot);
                CoopPlugin.Log.LogInfo($"population: spawned {(EObjectType)e.ObjType} (kind {kind})");
                OnClientStructureChanged?.Invoke(kind);
                list = GetList(sm, kind);
            }
        }

        // ---- wire ----

        public static void Write(BinaryWriter bw, List<List<Entry>> all)
        {
            bw.Write((byte)all.Count);
            foreach (var entries in all)
            {
                bw.Write((byte)Mathf.Min(entries.Count, 250));
                for (int i = 0; i < entries.Count && i < 250; i++)
                {
                    var e = entries[i];
                    bw.Write(e.ObjType);
                    bw.Write(e.Pos.x); bw.Write(e.Pos.y); bw.Write(e.Pos.z);
                    bw.Write(e.Rot.x); bw.Write(e.Rot.y); bw.Write(e.Rot.z); bw.Write(e.Rot.w);
                }
            }
        }

        public static List<List<Entry>> Read(BinaryReader br)
        {
            int kinds = br.ReadByte();
            var all = new List<List<Entry>>(kinds);
            for (int k = 0; k < kinds; k++)
            {
                int n = br.ReadByte();
                var entries = new List<Entry>(n);
                for (int i = 0; i < n; i++)
                {
                    entries.Add(new Entry
                    {
                        ObjType = br.ReadInt32(),
                        Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                        Rot = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    });
                }
                all.Add(entries);
            }
            return all;
        }
    }
}
