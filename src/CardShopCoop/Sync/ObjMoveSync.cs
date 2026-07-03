using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors the positions of placed objects (shelves, warehouse racks, card displays,
    /// combi shelves, cashier counters, decorations) between host and client. Identity is
    /// the object's index in its ShelfManager list - the same scheme the stock syncs use.
    /// A move is only broadcast once it SETTLES (same pose two ticks in a row), so a
    /// boxed-up shelf being carried around doesn't stream; it pops to its new spot on the
    /// other side when placed. Children (compartments, items, price tags) ride along.
    /// </summary>
    public class ObjMoveSync
    {
        public struct Entry
        {
            public int Key;      // kind<<24 | index
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private struct Pose
        {
            public Vector3 P;
            public Quaternion R;
            public bool Valid;

            public bool Same(Vector3 p, Quaternion r)
            {
                // dot-product threshold ~= angle < 0.5deg, without Quaternion.Angle's
                // acos - this runs for every placed object every tick
                return Valid && (P - p).sqrMagnitude < 0.0004f
                    && Mathf.Abs(Quaternion.Dot(R, r)) > 0.99999f;
            }
        }

        private readonly Dictionary<int, Pose> _sent = new Dictionary<int, Pose>();      // last broadcast
        private readonly Dictionary<int, Pose> _candidate = new Dictionary<int, Pose>(); // settle window
        private ShelfManager _sm;
        private float _timer;

        public Action<List<Entry>> OnLocalChanges;

        public void Reset()
        {
            _sent.Clear();
            _candidate.Clear();
            _sm = null;
            _timer = -0.25f; // staggered phase vs the other snapshot engines
        }

        private ShelfManager Sm()
        {
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        public void Tick(float dt, bool active)
        {
            if (!active) return;
            _timer += dt;
            if (_timer < 1.0f) return;
            _timer -= 1.0f;

            List<Entry> changes = null;
            try
            {
                var sm = Sm();
                if (sm == null) return;
                for (int kind = 0; kind < PopulationSync.KindCount; kind++)
                    Walk(PopulationSync.GetList(sm, kind), kind, ref changes);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("ObjMoveSync snapshot: " + e.Message);
                return;
            }
            if (changes != null && changes.Count > 0)
                OnLocalChanges?.Invoke(changes);
        }

        private void Walk(System.Collections.IList list, int kind, ref List<Entry> changes)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var obj = list[i] as Component;
                if (obj == null || !obj.gameObject.activeInHierarchy) continue; // boxed/carried
                int key = (kind << 24) | (i & 0xFFFF);
                var p = obj.transform.position;
                var r = obj.transform.rotation;

                if (_sent.TryGetValue(key, out var sent) && sent.Same(p, r))
                {
                    _candidate.Remove(key);
                    continue;
                }
                // settle gate: only report once the pose repeats across two ticks
                if (_candidate.TryGetValue(key, out var cand) && cand.Same(p, r))
                {
                    _sent[key] = new Pose { P = p, R = r, Valid = true };
                    _candidate.Remove(key);
                    if (changes == null) changes = new List<Entry>();
                    if (changes.Count >= 64) return;
                    changes.Add(new Entry { Key = key, Pos = p, Rot = r });
                }
                else
                {
                    _candidate[key] = new Pose { P = p, R = r, Valid = true };
                }
            }
        }

        public void ApplyRemote(List<Entry> entries)
        {
            var sm = Sm();
            if (sm == null) return;
            foreach (var e in entries)
            {
                try
                {
                    var t = Resolve(sm, e.Key);
                    if (t == null) continue;
                    t.SetPositionAndRotation(e.Pos, e.Rot);
                    SyncTagGroup(t);
                    _sent[e.Key] = new Pose { P = e.Pos, R = e.Rot, Valid = true };
                    _candidate.Remove(e.Key);
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning($"ObjMoveSync apply {e.Key:X}: {ex.Message}");
                }
            }
        }

        // Price tags live in a SEPARATE canvas group (m_Shelf_WorldUIGrp) that the game
        // only drags along during its own move mode - a remote transform set leaves the
        // tags floating at the old spot unless we move the group too.
        private static readonly Dictionary<Type, System.Reflection.FieldInfo> _tagGrpFields
            = new Dictionary<Type, System.Reflection.FieldInfo>();

        public static void SyncTagGroup(Transform objTransform)
        {
            var comp = objTransform.GetComponent<InteractableObject>();
            if (comp == null) return;
            var type = comp.GetType();
            if (!_tagGrpFields.TryGetValue(type, out var fi))
            {
                fi = HarmonyLib.AccessTools.Field(type, "m_Shelf_WorldUIGrp");
                _tagGrpFields[type] = fi; // may be null for kinds without tags
            }
            if (fi?.GetValue(comp) is Transform grp && grp != null)
                grp.SetPositionAndRotation(objTransform.position, objTransform.rotation);
        }

        private static Transform Resolve(ShelfManager sm, int key)
        {
            int kind = key >> 24;
            int idx = key & 0xFFFF;
            var list = PopulationSync.GetList(sm, kind);
            if (list == null || idx >= list.Count) return null;
            return (list[idx] as Component)?.transform;
        }

        // ---- wire ----

        public static void WriteEntries(BinaryWriter bw, List<Entry> entries)
        {
            bw.Write((byte)entries.Count);
            foreach (var e in entries)
            {
                bw.Write(e.Key);
                bw.Write(e.Pos.x); bw.Write(e.Pos.y); bw.Write(e.Pos.z);
                bw.Write(e.Rot.x); bw.Write(e.Rot.y); bw.Write(e.Rot.z); bw.Write(e.Rot.w);
            }
        }

        public static List<Entry> ReadEntries(BinaryReader br)
        {
            int n = br.ReadByte();
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new Entry
                {
                    Key = br.ReadInt32(),
                    Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    Rot = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                });
            }
            return list;
        }
    }
}
