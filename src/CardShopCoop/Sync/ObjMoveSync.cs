using CardShopCoop.Net;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors the positions of placed objects (shelves, warehouse racks, card displays,
    /// combi shelves, cashier counters, decorations) between host and client. Identity is
    /// the host-assigned object id - the same identity used by the stock syncs.
    /// A move is only broadcast once it SETTLES (same pose two ticks in a row), so a
    /// boxed-up shelf being carried around doesn't stream; it pops to its new spot on the
    /// other side when placed. Children (compartments, items, price tags) ride along.
    /// </summary>
    public class ObjMoveSync : CoopModule
    {
        public struct Entry
        {
            public int Key;      // kind<<24 | stableObjectId
            public int Type;     // identity: (int)m_ObjectType, or (int)m_DecoObjectType for
                                 // kind-5 decos (whose m_ObjectType is None) - same accessor
                                 // PopulationSync serializes. Carried so the receiver can
                                 // REJECT an entry whose (kind,index) now resolves to a
                                 // DIFFERENT object (a stale index from a fresh/lagging peer
                                 // that never moves the wrong furniture). NoType = couldn't read.
            /// <summary>Client only: the host sent a MODDED type id whose name does not exist on
            /// this PC (a content pack only he has). Type is then the enum's None sentinel and
            /// means NOTHING, so the identity guard must not be handed it - EDecoObject.None is
            /// 0, a perfectly comparable real value, so an unmappable deco move could MATCH a
            /// local object whose type genuinely reads 0 and teleport the wrong decoration.
            /// Same flag, same reason, as PopulationSync.Entry.Unresolved. Always false on the
            /// host (which never translates) and for every vanilla id.</summary>
            public bool Unresolved;
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
        private float _heal;
        private const float HealInterval = 10f;
        private bool _forceImmediate;
        private float _lastRejectLog = -999f; // throttle the identity-reject spam to ~1/5s
        private float _lastAcceptedLog = -999f;

        // Time-sliced scan state: the O(all-objects) walk is spread over frames.
        private readonly ListScanCursor _cursor = new ListScanCursor { Budget = 32 };
        private System.Collections.IList[] _groups;
        private bool _scanning;
        private List<Entry> _scanChanges;
        private bool _scanImmediate;
        private bool _scanHeal;

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
            if (io == null)
                return NoType;
            return (kind == 5) ? (int)io.m_DecoObjectType : (int)io.m_ObjectType;
        }

        public override string Name => "object-moves";

        public override void ForceResend() => ForceNextTick();

        public override void Reset()
        {
            _sent.Clear();
            _candidate.Clear();
            _sm = null;
            _timer = -0.25f; // staggered phase vs the other snapshot engines
            _heal = 0f;
            _forceImmediate = false;
            _lastAcceptedLog = -999f;
            _scanning = false;
            _groups = null;
            _scanChanges = null;
            _cursor.Reset();
        }

        private ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        public void Tick(float dt, bool active)
        {
            if (!active)
                return;
            _timer += dt;
            if (!_scanning)
            {
                if (_timer < 1.0f)
                    return;
                _timer -= 1.0f;
                _scanImmediate = _forceImmediate;
                _forceImmediate = false;
                _heal += 1.0f;
                _scanHeal = false;
                if (_heal >= HealInterval)
                {
                    _heal -= HealInterval;
                    _scanHeal = true;
                }
                var sm = Sm();
                if (sm == null)
                    return;
                if (_groups == null || _groups.Length != PopulationSync.KindCount)
                    _groups = new System.Collections.IList[PopulationSync.KindCount];
                for (int k = 0; k < _groups.Length; k++)
                    _groups[k] = PopulationSync.GetList(sm, k);
                _cursor.Reset();
                _scanning = true;
                _scanChanges = null;
            }
            try
            {
                _cursor.Scan(_groups, VisitObjMove);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("ObjMoveSync snapshot: " + e.Message);
                _scanning = false;
                return;
            }
            if (_cursor.Done)
            {
                _scanning = false;
                if (_scanChanges != null && _scanChanges.Count > 0)
                    OnLocalChanges?.Invoke(_scanChanges);
            }
        }

        private void VisitObjMove(object item, int kind, int index)
        {
            var obj = item as Component;
            if (obj == null || !obj.gameObject.activeInHierarchy)
                return; // boxed/carried
            if (kind == 5 && CardShopCoop.Patches.GamePatches.IsPendingDeco(obj as InteractableObject))
                return;
            if (!PlacedObjectIdentity.TryMakeObjectKey(kind, obj as InteractableObject, out int key))
                return;
            // Never author a move for an object the game is actively moving (a drag in
            // progress). On the guest there is nothing legitimate to report mid-drag, and
            // reporting the pre-settle pose is exactly the packet that races the host's
            // settle-delta and starts the snap-back / off-grid-rotation echo war.
            if (obj is InteractableObject moving && moving.GetIsMovingObject())
            {
                _candidate.Remove(key);
                return;
            }
            var p = obj.transform.position;
            var r = obj.transform.rotation;

            bool knownSent = _sent.TryGetValue(key, out var sent);
            // Only the host emits periodic authoritative heals. A client must not turn a
            // heal into a request for every placed object.
            bool forceHeal = _scanHeal && !IsClientRole;
            if (knownSent && sent.Same(p, r) && !forceHeal)
            {
                _candidate.Remove(key);
                return;
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
                return;
            }
            // An explicit completed mutation is already settled by vanilla. Keep the
            // two-sample gate for ordinary recovery polling.
            if (forceHeal || _scanImmediate || (_candidate.TryGetValue(key, out var cand) && cand.Same(p, r)))
            {
                if (_scanChanges == null)
                    _scanChanges = new List<Entry>();
                // Do not advance the sent baseline until this entry is actually queued;
                // otherwise the 65th move in a batch is lost forever.
                if (_scanChanges.Count >= 64)
                    return;
                _sent[key] = new Pose { P = p, R = r, Valid = true };
                _candidate.Remove(key);
                _scanChanges.Add(new Entry { Key = key, Type = TypeIdOf(obj, kind), Pos = p, Rot = r });
            }
            else
            {
                _candidate[key] = new Pose { P = p, R = r, Valid = true };
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
        public List<Entry> ApplyRemote(List<Entry> entries, bool dropIfHostMoving = false)
        {
            var sm = Sm();
            var accepted = new List<Entry>();
            if (sm == null)
                return accepted;
            foreach (var e in entries)
            {
                try
                {
                    // Unmappable modded type: e.Type is a None sentinel and carries no identity
                    // at all, so it can never be compared against a live object (see Entry.
                    // Unresolved). We do not have the object the host moved, so there is nothing
                    // here to move - drop the entry before Resolve can hand us a stand-in.
                    if (e.Unresolved)
                        continue;
                    var comp = Resolve(sm, e.Key);
                    if (comp == null)
                        continue;
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
                    var oldPos = t.position;
                    t.SetPositionAndRotation(e.Pos, e.Rot);
                    SyncTagGroup(t);
                    if (io is InteractableAutoPackOpener)
                    {
                        try
                        {
                            _miOpenerSetUI?.Invoke(io, null);
                        }
                        catch (System.Exception caught) { Swallow.Log(caught); }
                    }
                    _sent[e.Key] = new Pose { P = e.Pos, R = e.Rot, Valid = true };
                    _candidate.Remove(e.Key);
                    accepted.Add(e);
                    if (dropIfHostMoving && Time.realtimeSinceStartup - _lastAcceptedLog > 0.25f)
                    {
                        _lastAcceptedLog = Time.realtimeSinceStartup;
                        CoopPlugin.Log.LogInfo($"ObjMoveSync: accepted guest move {e.Key:X} "
                            + $"({oldPos.x:F2},{oldPos.y:F2},{oldPos.z:F2}) -> "
                            + $"({e.Pos.x:F2},{e.Pos.y:F2},{e.Pos.z:F2})");
                    }
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning($"ObjMoveSync apply {e.Key:X}: {ex.Message}");
                }
            }
            return accepted;
        }

        /// <summary>Run the settle check on the next co-op frame instead of waiting for
        /// the one-second recovery poll. Objects still moving remain protected by Walk.</summary>
        public void ForceNextTick()
        {
            _timer = 1.0f;
            _forceImmediate = true;
        }

        // Price tags live in a SEPARATE canvas group (m_Shelf_WorldUIGrp) that the game
        // only drags along during its own move mode - a remote transform set leaves the
        // tags floating at the old spot unless we move the group too.
        private static readonly Dictionary<Type, System.Reflection.FieldInfo> _tagGrpFields
            = new Dictionary<Type, System.Reflection.FieldInfo>();

        public static void SyncTagGroup(Transform objTransform)
        {
            var comp = objTransform.GetComponent<InteractableObject>();
            if (comp == null)
                return;
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
            ushort id = PlacedObjectIdentity.ObjectIdFromObjectKey(key);
            if (PopulationSync.GetList(sm, kind) == null)
                return null;
            return PlacedObjectIdentity.TryResolve(sm, kind, id, out var obj) ? obj : null;
        }

        /// <summary>Resolves a placed-object wire key for the transient movement preview.
        /// The returned object is only used as a local visual source; callers must not
        /// mutate its gameplay state.</summary>
        public static Component ResolveObjectByKey(int key)
        {
            var sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return sm == null ? null : Resolve(sm, key);
        }


        // ---- wire ----

        /// <summary>Which modded id space Entry.Type lives in for a given kind - the same
        /// split TypeIdOf makes: kind 5 carries an EDecoObject, everything else an
        /// EObjectType. Used in BOTH directions because these two helpers serve both
        /// messages (client->host requests and host->client deltas); the host's half of
        /// each is the identity function, which is what keeps the wire speaking host ids.
        /// NoType (int.MinValue) is far below the modded floor, so it passes through
        /// untranslated and the "can't verify identity" path keeps working.</summary>
        private static Util.EnumKind KindOf(int kind)
        {
            return kind == 5 ? Util.EnumKind.DecoObject : Util.EnumKind.ObjectType;
        }
    }
}
