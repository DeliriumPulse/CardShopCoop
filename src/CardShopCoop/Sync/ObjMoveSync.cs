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
            public int Type;     // identity: (int)m_ObjectType, or (int)m_DecoObjectType for
                                 // kind-5 decos (whose m_ObjectType is None) - same accessor
                                 // PopulationSync serializes. Carried so the receiver can
                                 // REJECT an entry whose (kind,index) now resolves to a
                                 // DIFFERENT object (a stale index from a fresh/lagging peer
                                 // that never moves the wrong furniture). NoType = couldn't read.
            public Vector3 Pos;
            public Quaternion Rot;
        }

        // identity we can't read (the list element isn't an InteractableObject) - the guard
        // treats NoType on either side as "can't verify" and lets the apply through, so a
        // non-IO object is never spuriously rejected. Real EObjectType values are never this.
        private const int NoType = int.MinValue;

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
        private float _lastRejectLog = -999f; // throttle the identity-reject spam to ~1/5s

        public Action<List<Entry>> OnLocalChanges;

        // Client role: a fresh joiner must ADOPT every object's loaded pose as its silent
        // baseline instead of re-reporting it (see Walk). Read straight off the static role
        // the way the other Sync engines read CoopCore.ClientReloading - no per-tick wiring
        // to inject, and the host (role != Client) simply never adopts.
        private static bool IsClientRole => CoopCore.Role == CoopRole.Client;

        // The object's identity for the wire: decorations (kind 5) carry m_ObjectType == None,
        // so their real identity is m_DecoObjectType - exactly PopulationSync's split.
        private static int TypeIdOf(Component obj, int kind)
        {
            var io = obj as InteractableObject;
            if (io == null) return NoType;
            return (kind == 5) ? (int)io.m_DecoObjectType : (int)io.m_ObjectType;
        }

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
                // Never author a move for an object the game is actively moving (a drag in
                // progress). On the guest there is nothing legitimate to report mid-drag, and
                // reporting the pre-settle pose is exactly the packet that races the host's
                // settle-delta and starts the snap-back / off-grid-rotation echo war.
                if (obj is InteractableObject moving && moving.GetIsMovingObject())
                {
                    _candidate.Remove(key);
                    continue;
                }
                var p = obj.transform.position;
                var r = obj.transform.rotation;

                bool knownSent = _sent.TryGetValue(key, out var sent);
                if (knownSent && sent.Same(p, r))
                {
                    _candidate.Remove(key);
                    continue;
                }
                // FRESH-JOINER ADOPTION (client only): the first time we see an object after
                // Reset, its pose is the loaded/host pose - NOT a move this joiner made. Walk
                // re-reporting every settled object's pose to the host was D-c (the joiner's
                // stale index teleporting the host's fresh furniture). So on the client, an
                // object unknown to both _sent and _candidate is adopted straight into the
                // sent-baseline with no ObjMoveRequest. Only genuine subsequent movement (a
                // pose that later diverges from this baseline) flows through the settle gate.
                if (!knownSent && IsClientRole && !_candidate.ContainsKey(key))
                {
                    _sent[key] = new Pose { P = p, R = r, Valid = true };
                    continue;
                }
                // settle gate: only report once the pose repeats across two ticks
                if (_candidate.TryGetValue(key, out var cand) && cand.Same(p, r))
                {
                    _sent[key] = new Pose { P = p, R = r, Valid = true };
                    _candidate.Remove(key);
                    if (changes == null) changes = new List<Entry>();
                    if (changes.Count >= 64) return;
                    changes.Add(new Entry { Key = key, Type = TypeIdOf(obj, kind), Pos = p, Rot = r });
                }
                else
                {
                    _candidate[key] = new Pose { P = p, R = r, Valid = true };
                }
            }
        }

        // the auto pack opener's world-space UI panel is NOT a child of the machine, so a
        // bare transform set leaves it stranded at the old spot; SetUITransform re-anchors it
        // (private - runs on OnPlacedMovedObject, which a headless remote move never triggers).
        private static readonly System.Reflection.MethodInfo _miOpenerSetUI =
            HarmonyLib.AccessTools.Method(typeof(InteractableAutoPackOpener), "SetUITransform");

        /// <summary>Apply remote object poses. <paramref name="dropIfHostMoving"/> is set on
        /// the HOST when applying a client's move-request: an object the host is currently
        /// dragging must NOT be yanked to the client's stale pose (the snap-back echo war) -
        /// the baseline is refreshed so Walk won't re-echo, but the object is left alone.</summary>
        public void ApplyRemote(List<Entry> entries, bool dropIfHostMoving = false)
        {
            var sm = Sm();
            if (sm == null) return;
            foreach (var e in entries)
            {
                try
                {
                    var comp = Resolve(sm, e.Key);
                    if (comp == null) continue;
                    // IDENTITY GUARD: the (kind,index) may resolve to a DIFFERENT object than
                    // the sender meant (a stale index from a fresh/lagging peer, or a
                    // population that shifted under us before repair catches up). Applying it
                    // would teleport the wrong furniture - D-c. Reject unless the carried type
                    // matches. NoType on either side = can't read identity, so let it through
                    // (never a false reject); real types differing = hard skip, no baseline touch.
                    int actualType = TypeIdOf(comp, e.Key >> 24);
                    if (actualType != NoType && e.Type != NoType && actualType != e.Type)
                    {
                        if (Time.realtimeSinceStartup - _lastRejectLog > 5f)
                        {
                            _lastRejectLog = Time.realtimeSinceStartup;
                            CoopPlugin.Log.LogInfo($"ObjMoveSync: skipped stale move {e.Key:X} " +
                                $"(wire type {e.Type} != resolved {actualType})");
                        }
                        continue;
                    }
                    var t = comp.transform;
                    var io = comp as InteractableObject ?? t.GetComponent<InteractableObject>();
                    if (dropIfHostMoving && io != null && io.GetIsMovingObject())
                    {
                        // the game's move machinery owns this transform right now; writing a
                        // stale incoming pose fights the drag and re-asserts the old pose.
                        // Just refresh the baseline so our own Walk won't re-report it.
                        _sent[e.Key] = new Pose { P = t.position, R = t.rotation, Valid = true };
                        _candidate.Remove(e.Key);
                        continue;
                    }
                    t.SetPositionAndRotation(e.Pos, e.Rot);
                    SyncTagGroup(t);
                    if (io is InteractableAutoPackOpener) { try { _miOpenerSetUI?.Invoke(io, null); } catch { } }
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

        // Returns the placed object as a Component (was: its Transform) so ApplyRemote can
        // read the object's identity for the guard. Callers take .transform off it.
        private static Component Resolve(ShelfManager sm, int key)
        {
            int kind = key >> 24;
            int idx = key & 0xFFFF;
            var list = PopulationSync.GetList(sm, kind);
            if (list == null || idx >= list.Count) return null;
            return list[idx] as Component;
        }

        // ---- wire ----

        public static void WriteEntries(BinaryWriter bw, List<Entry> entries)
        {
            bw.Write((byte)entries.Count);
            foreach (var e in entries)
            {
                bw.Write(e.Key);
                bw.Write(e.Type); // identity guard - append-only, safe on the 1.0.30-only wire
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
                    Type = br.ReadInt32(), // matches WriteEntries order (both peers 1.0.30)
                    Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    Rot = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                });
            }
            return list;
        }
    }
}
