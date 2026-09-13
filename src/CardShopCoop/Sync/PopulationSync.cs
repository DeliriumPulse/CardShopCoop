using CardShopCoop.Net;
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
    /// (stable object id, objectType, transform) roster every 3s; clients reconcile using
    /// the game's own save-load recipe and bind the host id to the resulting object.
    /// </summary>
    public class PopulationSync : CoopModule
    {
        // 15 is the generic interactable-object list. It must be included because
        // generic furniture is still a real placed object and is referenced by the
        // furniture-box and move mirrors.
        public const int KindCount = 16;

        /// <summary>Shared list resolver used by PopulationSync and ObjMoveSync so both
        /// always agree on what "kind 3, index 7" means.</summary>
        public static IList GetList(ShelfManager sm, int kind)
        {
            switch (kind)
            {
                case 0:
                    return sm.m_ShelfList;
                case 1:
                    return sm.m_WarehouseShelfList;
                case 2:
                    return sm.m_CardShelfList;
                case 3:
                    return sm.m_CardItemCombiShelfList;
                case 4:
                    return sm.m_CashierCounterList;
                case 5:
                    return sm.m_DecoObjectList;
                case 6:
                    return sm.m_PlayTableList;
                case 7:
                    return sm.m_WorkbenchList;
                case 8:
                    return sm.m_TrashBinList;
                case 9:
                    return sm.m_CardStorageShelfList;
                case 10:
                    return sm.m_AutoCleanserList;
                case 11:
                    return sm.m_AutoPackOpenerList;
                case 12:
                    return sm.m_EmptyBoxStorageList;
                case 13:
                    return sm.m_BulkDonationBoxList;
                case 14:
                    return sm.m_TournamentPrizeShelfList;
                case 15:
                    return sm.m_InteractableObjectList;
                default:
                    return null;
            }
        }

        public struct Entry
        {
            public ushort Id;
            public int ObjType;
            /// <summary>Client only: the host sent a MODDED object id whose name does not
            /// exist on this PC (a content pack only he has). ObjType is then the enum's
            /// None sentinel and means nothing - so this entry may never be compared
            /// against a local object or spawned. Always false on the host, which never
            /// translates, and false for every vanilla id, which never needs to.</summary>
            public bool Unresolved;
            public Vector3 Pos;
            public Quaternion Rot;
            // A furniture delivery is represented by a real object in the population
            // list, but the object's transform remains at its construction position
            // (normally the origin) while the delivery box is moved to its spawn pose.
            // Carry the game's separate boxed state so population reconciliation never
            // briefly creates an unboxed object at the origin.
            public bool IsBoxed;
            public Vector3 BoxedPos;
            public Quaternion BoxedRot;
        }

        private ShelfManager _sm;
        private readonly Dictionary<InteractableObject, int> _idsThisTick
            = new Dictionary<InteractableObject, int>();
        private float _timer;
        private readonly HashSet<string> _snapshotErrors = new HashSet<string>();
        private int _lastHash;
        private float _heal;

        // Time-sliced scan state: the roster is built in two passes (hash, then build),
        // each spread over frames so a large shop never walks everything in one frame.
        private readonly ListScanCursor _cursor = new ListScanCursor { Budget = 48 };
        private IList[] _groups;
        private bool _scanning;
        private bool _building;
        private int _scanHash;
        private bool _sawError;
        private List<List<Entry>> _all;

        public Action<List<List<Entry>>> OnHostSnapshot;

        /// <summary>Fired when reconciliation destroys or spawns an object of a kind -
        /// other syncs' index baselines for that kind are stale from this moment.</summary>
        public static Action<int> OnClientStructureChanged;
        private static readonly HashSet<int> s_ambiguousWarnings = new HashSet<int>();

        public override string Name => "population";

        public override void ForceResend() => ForceNextTick();

        public override void Reset()
        {
            PlacedObjectIdentity.Reset();
            s_ambiguousWarnings.Clear();
            _sm = null;
            _snapshotErrors.Clear();
            _timer = -1.1f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            _scanning = false;
            _building = false;
            _groups = null;
            _all = null;
            _cursor.Reset();
        }

        public void ForceNextTick()
        {
            _timer = 3f;
            _lastHash = 0;
            _idsThisTick.Clear();
            _scanning = false;
        }

        private ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        public void HostTick(float dt, bool active)
        {
            if (!active)
                return;
            _timer += dt;
            if (!_scanning)
            {
                if (_timer < 3f)
                    return;
                _timer -= 3f;
                var sm = Sm();
                if (sm == null)
                    return;
                if (_groups == null || _groups.Length != KindCount)
                    _groups = new IList[KindCount];
                for (int k = 0; k < KindCount; k++)
                    _groups[k] = GetList(sm, k);
                _idsThisTick.Clear();
                _scanHash = 17;
                _sawError = false;
                _building = false;
                _cursor.Reset();
                _scanning = true;
            }
            try
            {
                if (!_building)
                {
                    // Pass 1: cheap identity hash + id assignment. No heavy build unless it changed.
                    _cursor.Scan(_groups, HashVisit);
                    if (!_cursor.Done)
                        return;
                    if (_sawError)
                    {
                        _scanning = false; // retry the complete roster on the next cadence
                        return;
                    }
                    _heal += 3f;
                    if (_scanHash == _lastHash && _heal < 30f)
                    {
                        _scanning = false;
                        return;
                    }
                    _lastHash = _scanHash;
                    _heal = 0f;
                    _all = new List<List<Entry>>(KindCount);
                    for (int k = 0; k < KindCount; k++)
                        _all.Add(new List<Entry>(_groups[k]?.Count ?? 0));
                    _building = true;
                    _cursor.Reset();
                }
                // Pass 2: build the roster, one slice per frame.
                _cursor.Scan(_groups, BuildVisit);
                if (_cursor.Done)
                {
                    _scanning = false;
                    if (!_sawError)
                        OnHostSnapshot?.Invoke(_all);
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("PopulationSync host: " + e.Message);
                _scanning = false;
            }
        }

        private void HashVisit(object item, int kind, int index)
        {
            var obj = item as InteractableObject;
            if (obj == null)
                return;
            try
            {
                int id = PlacedObjectIdentity.AssignHost(obj);
                _idsThisTick[obj] = id;
                bool boxed = IsBoxed(obj);
                _scanHash = _scanHash * 31 + id
                    + ((kind == 5) ? (int)obj.m_DecoObjectType : (int)obj.m_ObjectType)
                    + (boxed ? 1 : 0)
                    + (boxed ? Mathf.RoundToInt(ObjectPose(obj).x * 8f) : 0)
                    + (boxed ? Mathf.RoundToInt(ObjectPose(obj).z * 8f) : 0);
            }
            catch (Exception e) { _sawError = true; LogSnapshotError(kind + ":" + index, e); }
        }

        private void BuildVisit(object item, int kind, int index)
        {
            var obj = item as InteractableObject;
            if (obj == null)
                return;
            try
            {
                var entry = new Entry
                {
                    Id = (ushort)(_idsThisTick.TryGetValue(obj, out int id) ? id : PlacedObjectIdentity.AssignHost(obj)),
                    ObjType = (kind == 5) ? (int)obj.m_DecoObjectType : (int)obj.m_ObjectType,
                    Pos = obj.transform.position,
                    Rot = obj.transform.rotation,
                };
                if (obj.GetIsBoxedUp() && obj.GetPackagingBoxShelf() != null)
                {
                    entry.IsBoxed = true;
                    entry.BoxedPos = obj.GetPackagingBoxShelf().transform.position;
                    entry.BoxedRot = obj.GetPackagingBoxShelf().transform.rotation;
                }
                _all[kind].Add(entry);
            }
            catch (Exception e) { _sawError = true; LogSnapshotError(kind + ":" + index, e); }
        }

        private void LogSnapshotError(string item, Exception e)
        {
            if (_snapshotErrors.Add(item))
                CoopPlugin.Log.LogWarning("PopulationSync snapshot item " + item + ": " + e.Message);
        }

        /// <summary>Client: make each object list match the host's roster.</summary>
        public void ClientApply(List<List<Entry>> hostLists)
        {
            var sm = Sm();
            if (sm == null)
                return;
            for (int kind = 0; kind < KindCount && kind < hostLists.Count; kind++)
            {
                try
                {
                    ReconcileKind(sm, kind, hostLists[kind]);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning($"PopulationSync kind {kind}: {e.Message}"); }
            }
        }

        private static void ReconcileKind(ShelfManager sm, int kind, List<Entry> want)
        {
            var list = GetList(sm, kind);
            if (list == null)
                return;

            // Snapshot the live client objects once (non-null only, matching how the host
            // serializes its roster).
            var clientObjs = new List<InteractableObject>(list.Count);
            for (int i = 0; i < list.Count; i++)
                if (list[i] is InteractableObject o)
                    clientObjs.Add(o);

            bool identityMismatch = clientObjs.Count != want.Count;
            if (!identityMismatch)
            {
                for (int i = 0; i < want.Count; i++)
                {
                    if (!PlacedObjectIdentity.TryGet(clientObjs[i], out ushort id) || id != want[i].Id)
                    {
                        identityMismatch = true;
                        break;
                    }
                }
            }

            // A roster identity differs = a real add/remove/replacement. Use type + nearest position to
            // identify WHICH object is extra/missing: deleting a shelf out of the MIDDLE
            // re-indexes every object after it, so "remove extras from the end" pairs the
            // WRONG objects whenever shelves share a type - the client keeps the wrong shelf
            // and deletes the right one, and every index-keyed content sync then repaints
            // the wrong shelves forever. Positions are authoritative (both peers load the
            // same save and ObjMoveSync keeps poses in step), so type + nearest position
            // finds the true counterpart even after a middle removal.
            if (identityMismatch)
            {
                ReconcileByPose(sm, kind, want, clientObjs);
                return;
            }

            // Same count: NO structural change. Verify identities the way the old code
            // did (repair one genuine type mismatch per tick) and never touch positions -
            // the periodic heal and a mid-move object must not churn shelves merely because
            // a pose momentarily differs.
            for (int i = 0; i < list.Count && i < want.Count; i++)
            {
                var obj = list[i] as InteractableObject;
                if (obj == null)
                    continue;
                // an object from a content pack only the HOST has: his id carries no name
                // we know, so want[i].ObjType is a None sentinel that would mismatch
                // whatever stands here. Leave the slot alone - one-sided packs are allowed,
                // and a mirror we cannot build is not a licence to delete the joiner's furniture.
                if (want[i].Unresolved)
                    continue;
                // compare on the correct identity per kind, or a wrong deco variant (whose
                // m_ObjectType is always -1) could never be detected and repaired
                int cur = (kind == 5) ? (int)obj.m_DecoObjectType : (int)obj.m_ObjectType;
                if (cur != want[i].ObjType)
                {
                    BoxShared.DebugLog("population", $"population: repairing index {i} (kind {kind}): {cur} -> {want[i].ObjType}");
                    obj.OnDestroyed();
                    OnClientStructureChanged?.Invoke(kind);
                    return; // re-align next tick
                }
                // Same count and type, but boxed<->placed differs: the host placed a boxed
                // object (or boxed a placed one). That is not a structural change, so apply
                // the transition explicitly or the client keeps the stale boxed mirror.
                if (IsBoxed(obj) != want[i].IsBoxed)
                {
                    BoxShared.DebugLog("population", $"population: boxed state index {i} (kind {kind}) {IsBoxed(obj)} -> {want[i].IsBoxed}");
                    if (want[i].IsBoxed)
                        FurnitureBoxOps.BoxUpPlacedObject(obj);
                    else
                        FurnitureBoxOps.PlaceBoxedObject(obj, want[i].Pos, want[i].Rot);
                    OnClientStructureChanged?.Invoke(kind);
                    return; // re-align next tick
                }
            }
        }

        /// <summary>Reconcile a kind whose roster count differs from the host's - a real
        /// insert or (more importantly) a MIDDLE deletion that re-indexed the list. Matches
        /// host entries to client objects by (objectType, nearest position) so the extra
        /// object is the one actually removed, not merely "the last one".</summary>
        private static void ReconcileByPose(ShelfManager sm, int kind, List<Entry> want,
            List<InteractableObject> clientObjs)
        {
            var matchedClient = new bool[clientObjs.Count];
            var matchedHost = new bool[want.Count];

            // nearest-position matching among same-type peers: a true counterpart sits at
            // ~identical position (d ~ 0), so a removed shelf's neighbour never steals its
            // partner. TolSq is generous enough for float/save drift, tight enough that a
            // genuinely-missing object (no counterpart) is still left unmatched.
            const float TolSq = 1.0f;
            for (int w = 0; w < want.Count; w++)
            {
                // an object from a content pack only the HOST has: his id carries no name
                // we know, so ObjType is a None sentinel that would mismatch whatever stands
                // here. One-sided packs are allowed; a mirror we cannot build is not a
                // licence to delete the joiner's furniture - so protect the local object at
                // this position by pose alone and neither spawn nor remove for it.
                if (want[w].Unresolved)
                {
                    int best = FindUniquePoseMatch(kind, clientObjs, matchedClient, want[w], -1);
                    if (best == -2)
                    {
                        AbortAmbiguous(kind);
                        return;
                    }
                    float bestDs = best >= 0
                        ? (ObjectPose(clientObjs[best]) - MatchPose(want[w])).sqrMagnitude
                        : float.MaxValue;
                    if (best >= 0 && bestDs <= TolSq)
                    {
                        matchedClient[best] = true;
                        matchedHost[w] = true;
                        PlacedObjectIdentity.Bind(clientObjs[best], want[w].Id);
                    }
                    continue;
                }
                int b = -1;
                for (int c = 0; c < clientObjs.Count; c++)
                {
                    if (matchedClient[c])
                        continue;
                    if (PlacedObjectIdentity.TryGet(clientObjs[c], out ushort id) && id == want[w].Id)
                    {
                        b = c;
                        break;
                    }
                }
                if (b < 0)
                {
                    b = FindUniquePoseMatch(kind, clientObjs, matchedClient, want[w], want[w].ObjType);
                    if (b == -2)
                    {
                        AbortAmbiguous(kind);
                        return;
                    }
                }
                // An existing stable identity wins even if the object is being moved.
                float bDs = b >= 0 && PlacedObjectIdentity.TryGet(clientObjs[b], out ushort matchedId)
                    && matchedId == want[w].Id
                    ? 0f
                    : b >= 0 ? (ObjectPose(clientObjs[b]) - MatchPose(want[w])).sqrMagnitude : float.MaxValue;
                if (b >= 0 && bDs <= TolSq)
                {
                    matchedClient[b] = true;
                    matchedHost[w] = true;
                    PlacedObjectIdentity.Bind(clientObjs[b], want[w].Id);
                    if (kind == 5)
                        CardShopCoop.Patches.GamePatches.AdoptPendingDeco(clientObjs[b]);
                    if (IsBoxed(clientObjs[b]) != want[w].IsBoxed)
                    {
                        BoxShared.DebugLog("population", $"population: boxed transition id {want[w].Id} (kind {kind}) {IsBoxed(clientObjs[b])} -> {want[w].IsBoxed}");
                        if (want[w].IsBoxed)
                            FurnitureBoxOps.BoxUpPlacedObject(clientObjs[b]);
                        else
                            FurnitureBoxOps.PlaceBoxedObject(clientObjs[b], want[w].Pos, want[w].Rot);
                        OnClientStructureChanged?.Invoke(kind);
                        return; // re-align next tick
                    }
                }
            }

            // client objects with no host counterpart are extras: remove them (bounded per
            // tick so a glitch can't mass-delete in one frame; the next roster pass
            // converges the remainder). Removing shifts the list, so re-resolve after each
            // removal - but never destroy an object already pulled from the list, and NEVER
            // yank an object the local player is actively dragging (its pose is transient,
            // so it legitimately won't match a fixed host pose right now).
            int guard = 8;
            for (int c = clientObjs.Count - 1; c >= 0 && guard > 0; c--)
            {
                if (matchedClient[c])
                    continue;
                var obj = clientObjs[c];
                if (obj == null || !GetList(sm, kind).Contains(obj))
                    continue;
                if (obj.GetIsMovingObject())
                    continue;
                // PopulationSync and the box engine both materialize furniture delivery
                // entries. If the box engine already tracks this object's box, it is NOT an
                // orphan: destroying it here makes the engine re-adopt a fresh spawn next
                // pass (two boxes for one purchase). Let the engine's own absent-id sweep
                // retire it instead.
                if (IsBoxed(obj) && IsBoxEngineOwned(obj))
                    continue;
                guard--;
                BoxShared.DebugLog("population", $"population: removing unmatched {TypeName(kind, obj)} (kind {kind})");
                obj.OnDestroyed();
                OnClientStructureChanged?.Invoke(kind);
            }

            // host entries with no local counterpart are missing: spawn with the game's own
            // save-load recipe (self-registers at the end of the list, keeping order
            // identical to the host's).
            guard = 8;
            for (int w = 0; w < want.Count && guard > 0; w++)
            {
                var e = want[w];
                if (e.Unresolved || matchedHost[w])
                    continue;
                // if a same-type client object is being dragged (matched nothing above
                // because its pose is transient), it is almost certainly this entry's
                // counterpart - consume it as the match instead of spawning a duplicate.
                if (TryClaimDragged(kind, clientObjs, matchedClient, e.ObjType, e.Id, e.IsBoxed))
                    continue;
                // A boxed entry the box engine already materialized must be REBOUND, never
                // respawned. Spawning a second delivery box here is what produced more boxes
                // than purchases when the roster id binding was briefly missing.
                if (e.IsBoxed && kind != 5
                    && TryClaimBoxEngineBox(kind, clientObjs, matchedClient, e))
                    continue;
                // nothing to spawn for content we don't have installed: the id resolved to
                // None, whose prefab lookup would fail anyway. Skip - the slot IS the
                // identity every other sync keys on, so it cannot be filled by the next
                // object along.
                // decorations self-register into m_DecoObjectList via SpawnDecoObject; the
                // generic SpawnInteractableObject would land them in the wrong list (and
                // resolve a null prefab from EObjectType.None), so they never appeared
                // Furniture delivery entries must be created with the game's own
                // boxed-delivery recipe. SpawnInteractableObject alone creates a
                // visible object at Vector3.zero; the real game moves only the box
                // after BoxUpObject, so reproducing that sequence avoids the
                // appear-at-origin -> disappear race with the furniture box ops.
                InteractableObject spawned;
                if (e.IsBoxed && kind != 5)
                {
                    CardShopCoop.Patches.GamePatches.ApplyingMirrorPurchase = true;
                    try
                    {
                        ShelfManager.SpawnInteractableObjectInPackageBox(
                            (EObjectType)e.ObjType, e.BoxedPos, e.BoxedRot);
                    }
                    finally { CardShopCoop.Patches.GamePatches.ApplyingMirrorPurchase = false; }
                    spawned = FindBoxedObjectAtPose(kind, e.ObjType, e.BoxedPos);
                }
                else
                {
                    spawned = (kind == 5)
                        ? ShelfManager.SpawnDecoObject((EDecoObject)e.ObjType)
                        : ShelfManager.SpawnInteractableObject((EObjectType)e.ObjType);
                }
                if (spawned == null)
                    continue;
                guard--;
                if (!e.IsBoxed)
                    spawned.transform.SetPositionAndRotation(e.Pos, e.Rot);
                PlacedObjectIdentity.Bind(spawned, e.Id);
                BoxShared.DebugLog("population", $"population: spawned {TypeName(kind, e.ObjType)} (kind {kind})");
                OnClientStructureChanged?.Invoke(kind);
            }
        }

        /// <summary>If an unmatched client object of this type is being dragged by the
        /// local player, mark it as this host entry's counterpart so we neither remove it
        /// (the removal pass skips moving objects) nor spawn a duplicate for the same
        /// physical object. Returns true if one was claimed.</summary>
        private static bool TryClaimDragged(int kind, List<InteractableObject> clientObjs,
            bool[] matchedClient, int objType, ushort id, bool expectedBoxed)
        {
            if (clientObjs == null || matchedClient == null)
                return false;
            for (int c = 0; c < clientObjs.Count; c++)
            {
                if (matchedClient[c])
                    continue;
                var o = clientObjs[c];
                if (o == null || !o.GetIsMovingObject())
                    continue;
                if (IsBoxed(o) != expectedBoxed)
                    continue;
                int t = (kind == 5) ? (int)o.m_DecoObjectType : (int)o.m_ObjectType;
                if (t == objType)
                {
                    matchedClient[c] = true;
                    PlacedObjectIdentity.Bind(o, id);
                    return true;
                }
            }
            return false;
        }

        private static bool IsBoxed(InteractableObject obj)
        {
            return obj != null && obj.GetIsBoxedUp();
        }

        /// <summary>True when the box engine already tracks this object's delivery box (it has
        /// a client id for it). Keeps PopulationSync from destroying or duplicating a furniture
        /// box the engine owns.</summary>
        private static bool IsBoxEngineOwned(InteractableObject obj)
        {
            try
            {
                var core = CoopCore.Instance;
                if (core == null || obj == null)
                    return false;
                var box = obj.GetPackagingBoxShelf();
                return box != null && core.Boxes != null && core.Boxes.TryGetClientId(box, out _);
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        /// <summary>Before spawning a boxed furniture entry, bind an existing boxed object the
        /// box engine already owns (nearest same-type candidate) instead of creating another
        /// delivery box for the same purchase.</summary>
        private static bool TryClaimBoxEngineBox(int kind, List<InteractableObject> clientObjs,
            bool[] matchedClient, Entry want)
        {
            int best = -1;
            float bestDs = float.MaxValue;
            for (int c = 0; c < clientObjs.Count; c++)
            {
                if (matchedClient[c])
                    continue;
                var o = clientObjs[c];
                if (o == null || !IsBoxed(o) || !IsBoxEngineOwned(o))
                    continue;
                int t = (kind == 5) ? (int)o.m_DecoObjectType : (int)o.m_ObjectType;
                if (t != want.ObjType)
                    continue;
                float ds = (ObjectPose(o) - MatchPose(want)).sqrMagnitude;
                if (ds < bestDs)
                {
                    bestDs = ds;
                    best = c;
                }
            }
            if (best < 0)
                return false;
            matchedClient[best] = true;
            PlacedObjectIdentity.Bind(clientObjs[best], want.Id);
            return true;
        }

        private static Vector3 ObjectPose(InteractableObject obj)
        {
            if (IsBoxed(obj))
            {
                try
                {
                    var box = obj.GetPackagingBoxShelf();
                    if (box != null)
                        return box.transform.position;
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            return obj != null ? obj.transform.position : Vector3.zero;
        }

        // Returns -2 when two candidates are equally near.  Greedy tie-breaking is unsafe:
        // the subsequent unmatched pass could destroy the twin that the host meant to keep.
        private static int FindUniquePoseMatch(int kind, List<InteractableObject> clientObjs,
            bool[] matchedClient, Entry want, int requiredType)
        {
            const float TieEpsilon = 0.0001f;
            int best = -1;
            float bestDs = float.MaxValue;
            bool tied = false;
            for (int c = 0; c < clientObjs.Count; c++)
            {
                if (matchedClient[c])
                    continue;
                var obj = clientObjs[c];
                if (obj == null || IsBoxed(obj) != want.IsBoxed)
                    continue;
                if (requiredType >= 0)
                {
                    int curType = kind == 5 ? (int)obj.m_DecoObjectType : (int)obj.m_ObjectType;
                    if (curType != requiredType)
                        continue;
                }
                float ds = (ObjectPose(obj) - MatchPose(want)).sqrMagnitude;
                if (ds < bestDs - TieEpsilon)
                {
                    best = c;
                    bestDs = ds;
                    tied = false;
                }
                else if (Mathf.Abs(ds - bestDs) <= TieEpsilon)
                {
                    tied = true;
                }
            }
            if (tied && best >= 0 && bestDs <= 1.0f)
                return -2;
            return best;
        }

        private static void AbortAmbiguous(int kind)
        {
            if (s_ambiguousWarnings.Add(kind))
                CoopPlugin.Log.LogWarning($"population: ambiguous kind {kind} position match; preserving objects and re-baselining index mirrors");
            // Re-baseline index consumers, but do not destroy or bind anything until a fresh
            // roster disambiguates the candidates.
            OnClientStructureChanged?.Invoke(kind);
        }

        private static Vector3 MatchPose(Entry entry)
        {
            return entry.IsBoxed ? entry.BoxedPos : entry.Pos;
        }

        private static InteractableObject FindBoxedObjectAtPose(int kind, int objType, Vector3 boxPos)
        {
            // SpawnInteractableObjectInPackageBox does not return the object. Resolve
            // the newly registered object by its type and delivery-box pose; the
            // nearest match is deterministic because the host entry was just missing.
            var sm = CSingleton<ShelfManager>.Instance;
            var list = GetList(sm, kind);
            InteractableObject found = null;
            float best = float.MaxValue;
            if (list == null)
                return null;
            for (int i = 0; i < list.Count; i++)
            {
                var obj = list[i] as InteractableObject;
                if (obj == null || (int)obj.m_ObjectType != objType || !IsBoxed(obj))
                    continue;
                var box = obj.GetPackagingBoxShelf();
                if (box == null)
                    continue;
                float ds = (box.transform.position - boxPos).sqrMagnitude;
                if (ds < best)
                {
                    best = ds;
                    found = obj;
                }
            }
            return found;
        }

        /// <summary>A placed object's identity as a string, for the reconcile logs. Kind 5
        /// (decorations) carries m_DecoObjectType (m_ObjectType is always None there);
        /// every other kind carries m_ObjectType.</summary>
        private static string TypeName(int kind, InteractableObject obj)
            => kind == 5 ? ((EDecoObject)obj.m_DecoObjectType).ToString() : ((EObjectType)obj.m_ObjectType).ToString();

        private static string TypeName(int kind, int objType)
            => kind == 5 ? ((EDecoObject)objType).ToString() : ((EObjectType)objType).ToString();

        // ---- wire ----

        /// <summary>Which modded id space a kind's ObjType lives in: kind 5 serializes
        /// m_DecoObjectType (EDecoObject), every other kind m_ObjectType (EObjectType) -
        /// the same split the snapshot and ReconcileKind use. Both are enums EPL mints
        /// custom ids into, so both translate; the two are NOT interchangeable.</summary>
        private static Util.EnumKind KindOf(int kind)
        {
            return kind == 5 ? Util.EnumKind.DecoObject : Util.EnumKind.ObjectType;
        }
    }
}
