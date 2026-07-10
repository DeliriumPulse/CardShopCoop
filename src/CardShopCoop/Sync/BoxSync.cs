using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors loose delivery/packaging boxes (item boxes) between host and client.
    /// The host's RestockManager list is the single source of truth, broadcast every
    /// 1.5s; the client reconciles its own live list to match, spawning via the
    /// game's own RestockManager.SpawnPackageBoxItem (the exact save-load recipe) and
    /// despawning via the box's own OnDestroyed. Client-side changes (dispensing to a
    /// shelf, carrying, trashing) are detected against the last applied state and sent
    /// as requests the host applies and echoes. The joiner's restock ORDERS are forwarded
    /// separately (GamePatches) so deliveries always spawn host-side, officially.
    ///
    /// Every box carries a host-assigned STABLE ID. Identity-by-list-index looked fine
    /// until any single removal shifted every later index: the client's reconcile then
    /// destroyed/respawned the whole shifted tail - including the box in a player's
    /// HANDS (second field report). IDs make removals surgical.
    /// </summary>
    public class BoxSync
    {
        public struct Entry
        {
            public ushort Id;    // host-assigned, stable for the box's lifetime
            public int Type;
            public int Count;
            public bool IsBig;
            public bool IsOpen;
            public bool Carried; // in someone's hands: position is transient, don't apply
            public bool Settled; // physics at rest: only settled poses are applied
            public bool Stored;  // on a warehouse rack slot: the compartment owns pose+contents
            public byte StoreShelf; // WarehouseShelf.GetIndex() while stored
            public byte StoreComp;  // compartment index within that shelf while stored
            public Vector3 Pos;
            public float Yaw;
        }

        /// <summary>Set by CoopCore: is this box currently in the LOCAL player's hands?</summary>
        public static Func<InteractablePackagingBox_Item, bool> IsLocallyCarried = _ => false;

        private static readonly System.Reflection.MethodInfo MiSetOpenClose =
            AccessTools.Method(typeof(InteractablePackagingBox_Item), "SetOpenCloseBox");
        private static readonly System.Reflection.FieldInfo FiAmountToSpawn =
            AccessTools.Field(typeof(InteractablePackagingBox_Item), "m_ItemAmountToSpawn");
        private static readonly System.Reflection.FieldInfo FiStoredList =
            AccessTools.Field(typeof(ShelfCompartment), "m_StoredItemList");
        // protected on InteractableObject; true while ANY holder (player or WORKER)
        // carries the box - the player-only IsLocallyCarried guard left worker-held
        // boxes unprotected, so guest reports teleported boxes out of the restocker's
        // hands and broke its bring-boxes-inside loop (field report)
        private static readonly System.Reflection.FieldInfo FiBeingHold =
            AccessTools.Field(typeof(InteractableObject), "m_IsBeingHold");
        // private on InteractablePackagingBox_Item (NOT InteractableObject): gates the
        // worker restock candidate filters via CanWorkerTakeBox() (= !m_PreventWorkerTakeBox,
        // decompiled InteractablePackagingBox_Item ~337-340). The worker filters
        // (Worker.cs ~567/580/1023...) check only IsValidObject()+CanWorkerTakeBox(),
        // never activeSelf, so a box a GUEST is carrying (hidden but not worker-locked)
        // is still a valid restock candidate and a worker will drain/take it out of the
        // guest's hands (field report). We flip this true while a box id is in
        // _remoteCarried and clear it when the id leaves. NOT m_IsBeingHold: that field
        // drives IsValidObject / hold state on other host paths and stomping it here
        // would fight them.
        private static readonly System.Reflection.FieldInfo FiPreventWorkerTake =
            AccessTools.Field(typeof(InteractablePackagingBox_Item), "m_PreventWorkerTakeBox");

        private static bool IsBeingHeld(InteractablePackagingBox_Item box)
        {
            try { return FiBeingHold?.GetValue(box) is bool b && b; } catch { return false; }
        }

        /// <summary>Host: mark/unmark a box worker-untouchable while a GUEST carries it,
        /// so the restocker's candidate filters (CanWorkerTakeBox) skip it. Direct field
        /// write via the cached private FieldInfo; per-call try/catch so one bad box never
        /// aborts the caller's loop.</summary>
        private static void SetHostWorkerLock(InteractablePackagingBox_Item box, bool locked)
        {
            if (box == null) return;
            try { FiPreventWorkerTake?.SetValue(box, locked); } catch { }
        }

        /// <summary>Host: a peer disconnected. A box still marked client-carried would stay
        /// hidden AND worker-locked FOREVER - the set-down request is never coming, and a
        /// rejoining guest starts with an empty carry state so it never sends one either.
        /// Release every client-carried box: worker lock off, visible again, physics on; the
        /// next snapshot then broadcasts Carried=false so every guest un-hides it too.
        /// _remoteCarried isn't keyed by connection, so this releases everything - correct
        /// for 2-player, and self-healing in 3-player (a SURVIVING guest still carrying a
        /// box re-asserts its carry on its next ~0.5s report and it re-hides/locks).</summary>
        public void HostReleaseRemoteCarried()
        {
            if (_remoteCarried.Count == 0) return;
            int released = 0;
            foreach (var id in _remoteCarried)
            {
                if (!_hostById.TryGetValue(id, out var box) || box == null) continue;
                SetHostWorkerLock(box, false);
                // C-e: a box that was carried can be parked under the floor (hide spot / a
                // pose that slipped below the map). Re-showing it there drops it through the
                // world unrecoverable, so lift any under-floor box back to a sane pose
                // (keep x/z, y=0.5) BEFORE it becomes visible again.
                try
                {
                    if (box.transform.position.y < -2f)
                    {
                        var p = box.transform.position;
                        box.transform.position = new Vector3(p.x, 0.5f, p.z);
                    }
                }
                catch { }
                try { if (!box.gameObject.activeSelf) box.gameObject.SetActive(true); } catch { }
                try { box.SetPhysicsEnabled(true); } catch { }
                released++;
            }
            _remoteCarried.Clear();
            if (released > 0)
            {
                CoopPlugin.Log.LogInfo($"BoxSync host: released {released} client-carried box(es) after a disconnect");
                ForceBroadcastNextTick();
            }
        }

        // client: host truth + id<->box maps (boxes spawned by our apply, or adopted
        // from the save-load population by type/order at first snapshot)
        private readonly List<Entry> _lastApplied = new List<Entry>();
        private readonly Dictionary<ushort, InteractablePackagingBox_Item> _byId =
            new Dictionary<ushort, InteractablePackagingBox_Item>();
        private readonly Dictionary<InteractablePackagingBox_Item, ushort> _idOf =
            new Dictionary<InteractablePackagingBox_Item, ushort>();
        private readonly HashSet<ushort> _carriedLastTick = new HashSet<ushort>();
        private readonly Dictionary<ushort, double> _recentlyReleased = new Dictionary<ushort, double>(); // client: ignore stale carried echoes
        private readonly Dictionary<ushort, double> _locallyTouched = new Dictionary<ushort, double>();   // client: my recent edits beat stale echoes
        private readonly HashSet<ushort> _snapshotIds = new HashSet<ushort>();      // scratch
        private readonly List<ushort> _removeScratch = new List<ushort>();          // scratch
        private readonly List<InteractablePackagingBox_Item> _orphanScratch =
            new List<InteractablePackagingBox_Item>();                              // scratch
        // client: id -> host's latest entry for the snapshot currently being applied,
        // so the store-retry ghost eviction (B2) can ask where the host claims a tracked
        // occupant lives without threading hostList through the static ApplyToBox
        private readonly Dictionary<ushort, Entry> _hostWhereScratch = new Dictionary<ushort, Entry>();
        // rate limits for the 1000-box-cap warnings (host oversize / client sweep-skip)
        private double _lastCapWarn;
        private double _lastCapSkipLog;

        // host: id assignment + per-client state
        private readonly Dictionary<InteractablePackagingBox_Item, ushort> _hostIds =
            new Dictionary<InteractablePackagingBox_Item, ushort>();
        private readonly Dictionary<ushort, InteractablePackagingBox_Item> _hostById =
            new Dictionary<ushort, InteractablePackagingBox_Item>();
        private ushort _nextId = 1; // 0 = "unassigned"
        private readonly HashSet<ushort> _remoteCarried = new HashSet<ushort>();    // host: client-held boxes
        private readonly HashSet<ushort> _hostCarriedLastTick = new HashSet<ushort>();
        private readonly Dictionary<ushort, double> _hostRecentlyReleased = new Dictionary<ushort, double>(); // host: just set it down; stale client reports must not stomp it

        private float _timer;
        private int _lastHostHash;
        private float _hostHeal;
        private readonly List<Entry> _reportBuf = new List<Entry>();
        private RestockManager _rm;

        public Action<List<Entry>> OnHostSnapshot;   // host: broadcast
        public Action<List<Entry>> OnClientChanges;  // client: request
        public Action<int, int> OnLocalRemoved;      // client: (id, type) I trashed a box

        /// <summary>Wired by CoopCore to the OnDestroyed patch: a box was destroyed by
        /// LOCAL gameplay (trash bin, storage) - not by sync reconciliation.</summary>
        public static Action<InteractablePackagingBox_Item> LocalBoxDestroyed;

        /// <summary>True while sync code itself destroys/spawns boxes, so the OnDestroyed
        /// patch doesn't mistake reconciliation for a player throwing boxes away.</summary>
        public static bool ApplyingRemote;

        public void Reset()
        {
            _lastApplied.Clear();
            _byId.Clear();
            _idOf.Clear();
            _carriedLastTick.Clear();
            _recentlyReleased.Clear();
            _locallyTouched.Clear();
            // clear the worker-untouchable lock on every box we still hold marked as
            // guest-carried BEFORE dropping the maps (B2): a session teardown must not
            // strand a box worker-locked into the next session. Best-effort - dead/
            // fake-null boxes just skip.
            foreach (var id in _remoteCarried)
                if (_hostById.TryGetValue(id, out var carried) && carried != null)
                    SetHostWorkerLock(carried, false);
            _hostIds.Clear();
            _hostById.Clear();
            _nextId = 1;
            _remoteCarried.Clear();
            _hostCarriedLastTick.Clear();
            _hostRecentlyReleased.Clear();
            _remWindowStart.Clear();
            _remWindowCount.Clear();
            _timer = -0.6f; // staggered phase vs the other snapshot engines
            _lastHostHash = 0;
            _hostHeal = 0f;
            _rm = null;
            // static store-retry state leaks across sessions; a reused box id could inherit
            // a prior session's "gave up storing" and never get placed on the rack
            _storeFails.Clear();
            _storeGaveUp.Clear();
            _underMapLogged.Clear(); // C-e: don't carry an under-map warning suppression across sessions
            _sm = null;
            _lastResolveWarn = 0;
            _hostWhereScratch.Clear();
            // drop any closure over a prior session's id maps so the ghost-eviction probe
            // can't fire against stale state before the next ClientApply re-wires it
            HostLocationOf = _ => default(HostBoxWhere);
        }

        /// <summary>Force the next HostTick to broadcast the loose-box population immediately,
        /// bypassing the unchanged-hash gate and the 1.5s cadence. Used when another system
        /// (e.g. an empty-box dispense) spawns a box that must reach the guest promptly.</summary>
        public void ForceBroadcastNextTick()
        {
            _lastHostHash = 0;
            _timer = 1.5f;
        }

        private RestockManager Rm()
        {
            if (_rm == null) _rm = UnityEngine.Object.FindObjectOfType<RestockManager>();
            return _rm;
        }

        private static List<InteractablePackagingBox_Item> LiveBoxes()
        {
            return RestockManager.GetItemPackagingBoxList();
        }

        private static Entry Snapshot(InteractablePackagingBox_Item box)
        {
            // mid-tumble poses must never be broadcast: applying them teleports the
            // other side's copy through the air (and a teleported SLEEPING body
            // freezes there) - only settled poses cross the wire
            bool settled = true;
            try
            {
                // use the box's REAL physics body, not GetComponentInChildren<Rigidbody>():
                // the prefab's open/close rig mesh (m_RigMeshGrp) carries its own KINEMATIC
                // child body that depth-first search returns first, so we'd read "settled"
                // while the real body is still mid-fall -> the host broadcasts a mid-air pose
                // and the box hangs frozen on the guest (the "floating boxes" report)
                var rb = box.m_Rigidbody;
                settled = rb == null || rb.isKinematic || rb.IsSleeping()
                    || rb.velocity.sqrMagnitude < 0.04f;
            }
            catch { }
            // warehouse-rack storage: the slot transform owns the pose, so a stored
            // box reports its slot address instead of a physics pose
            bool stored = false; int sShelf = 0, sComp = 0;
            try
            {
                if (box.m_IsStored)
                {
                    var sc = box.GetBoxStoredCompartment();
                    if (sc != null)
                    {
                        stored = true;
                        sShelf = sc.GetWarehouseIndex();
                        sComp = sc.GetIndex();
                    }
                }
            }
            catch { }
            return new Entry
            {
                Type = (int)box.m_ItemCompartment.GetItemType(),
                Count = box.m_ItemCompartment.GetItemCount(),
                IsBig = box.m_IsBigBox,
                IsOpen = box.IsBoxOpened(),
                // worker-held counts as carried: mirrors hide it while the restocker
                // walks it (instead of dragging a copy along the floor) and never
                // position-stomp the original out of its hands
                Carried = !stored && (IsLocallyCarried(box) || IsBeingHeld(box)),
                Settled = stored || settled,
                Stored = stored,
                StoreShelf = (byte)Mathf.Clamp(sShelf, 0, 255),
                StoreComp = (byte)Mathf.Clamp(sComp, 0, 255),
                Pos = box.transform.position,
                Yaw = box.transform.eulerAngles.y,
            };
        }

        private static bool Differs(Entry a, Entry b)
        {
            if (a.Type != b.Type || a.Count != b.Count || a.IsBig != b.IsBig || a.IsOpen != b.IsOpen) return true;
            if (a.Stored != b.Stored) return true;
            // both stored: the rack slot owns the pose - comparing transforms would
            // report phantom "drift" every tick and fight the game's arrangement
            if (a.Stored) return a.StoreShelf != b.StoreShelf || a.StoreComp != b.StoreComp;
            return (a.Pos - b.Pos).sqrMagnitude > 0.01f || Mathf.Abs(Mathf.DeltaAngle(a.Yaw, b.Yaw)) > 3f;
        }

        /// <summary>Seed an EMPTY box's compartment so it can actually hold the wanted
        /// item type (B1). When a guest pulls the first item off a shelf into an empty
        /// open box, the box adopts the shelf's type on the guest side; the host box is
        /// still type None with an empty pos list, so PreSpawnItemUpdate/SpawnItem clamp
        /// every count to 0 (both CLAMP to m_ItemPosList.Count, ShelfCompartment ~498/507)
        /// and the gain evaporates. Initialize the type + pos list exactly the way
        /// FillBoxWithItem does (decompiled InteractablePackagingBox_Item ~100-118:
        /// SetItemType on the box, SetCompartmentItemType then CalculatePositionList on
        /// the compartment) so a subsequent count can land. Only touches EMPTY boxes
        /// (GetItemCount() <= 0): a box with real contents keeps its type, and an OPEN box
        /// with spawned Item objects positioned against this same pos list is never rebuilt
        /// under them. Returns true if it seeded (or the type already matched with a pos
        /// list), false if it declined (empty/None want, or non-empty box of another type).</summary>
        private static bool EnsureCompartmentType(InteractablePackagingBox_Item box, int wantType)
        {
            try
            {
                if (wantType == (int)EItemType.None) return false; // nothing to seed
                var comp = box.m_ItemCompartment;
                int curType = (int)comp.GetItemType();
                bool hasPosList = comp.GetItemPosListCount() > 0;
                if (curType == wantType && hasPosList) return true; // already usable
                // only adopt into a genuinely EMPTY box - never restyle one holding items
                if (comp.GetItemCount() > 0) return curType == wantType;
                var et = (EItemType)wantType;
                box.SetItemType(et);                    // mirror FillBoxWithItem's box-side type
                comp.SetCompartmentItemType(et);
                comp.CalculatePositionList();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Data-only count apply via the closed-box path (stored boxes are
        /// always closed): safe before OR after the box is slotted into a rack.</summary>
        private static void ApplyClosedCount(InteractablePackagingBox_Item box, int count)
        {
            try
            {
                var comp = box.m_ItemCompartment;
                if (comp.GetItemCount() == count) return;
                // PreSpawnItemUpdate CLAMPS to m_ItemPosList.Count (ShelfCompartment.cs
                // ~496-501). An adopted/orphan-paired box that never ran FillBoxWithItem
                // has an EMPTY pos list, so every count clamps to 0 - the stored box's
                // contents pin to zero and never converge with the host (contents
                // divergence report). Initialize the pos list the way FillBoxWithItem
                // does (InteractablePackagingBox_Item.cs ~100-118: SetCompartmentItemType
                // then CalculatePositionList) so the count can actually apply. Guard to
                // stored/closed boxes only: an OPEN box mid-display has real, spawned
                // Item objects positioned against this same list, and rebuilding it under
                // them would shuffle the visible stack.
                bool storedOrClosed = true;
                try { storedOrClosed = box.m_IsStored || !box.IsBoxOpened(); } catch { }
                if (storedOrClosed && count > 0 && comp.GetItemPosListCount() <= 0)
                {
                    try
                    {
                        comp.SetCompartmentItemType(comp.GetItemType());
                        comp.CalculatePositionList();
                    }
                    catch { }
                }
                comp.PreSpawnItemUpdate(count);
                FiAmountToSpawn?.SetValue(box, count);
            }
            catch { }
        }

        /// <summary>A stored box destroyed without unhooking leaks the compartment's
        /// box count and leaves a dangling slot reference.</summary>
        private static void UnhookIfStored(InteractablePackagingBox_Item box)
        {
            try
            {
                if (box == null || !box.m_IsStored) return;
                var comp = box.GetBoxStoredCompartment();
                if (comp != null) comp.RemoveBox(box);
                box.m_IsStored = false;
            }
            catch { }
        }

        // NEVER CSingleton<ShelfManager>.Instance here: box snapshots arrive during
        // the client's LOADING SCREEN, and touching it then creates a fake empty
        // manager that shadows the real one all session - the 1.0.11 store mirror
        // silently found zero racks forever (second storage field report)
        private static ShelfManager _sm;
        private static double _lastResolveWarn;
        // storage-rack give-up tracking: after a few failed store attempts for a box
        // (full/mismatched slot) we stop retrying and leave it loose
        private static readonly Dictionary<ushort, int> _storeFails = new Dictionary<ushort, int>();
        private static readonly HashSet<ushort> _storeGaveUp = new HashSet<ushort>();

        /// <summary>What the host's CURRENT snapshot says about an occupant box we already
        /// track. Wired per-apply by ClientApplyInner (closure over _idOf + the incoming
        /// hostList) because ApplyToBox is static and can't see the instance maps. Used by
        /// the store-retry ghost eviction (B2): a compartment occupant that WE track but
        /// whom the host places somewhere ELSE (or not stored at all) is a stale ghost from
        /// rack-index divergence, and evicting it frees the slot the real store needs.</summary>
        public struct HostBoxWhere
        {
            public bool Tracked;   // this occupant is a box in our id maps (host knows it)
            public ushort Id;      // its stable id (0 if untracked)
            public bool Stored;    // host's latest: is it stored at all?
            public int Shelf;      // host's latest StoreShelf (valid only if Stored)
            public int Comp;       // host's latest StoreComp (valid only if Stored)
        }
        /// <summary>Default: nothing is tracked (host path / not wired) - eviction no-ops.</summary>
        public static Func<InteractablePackagingBox_Item, HostBoxWhere> HostLocationOf =
            _ => default(HostBoxWhere);

        private static ShelfCompartment ResolveWarehouseCompartment(int shelfIdx, int compIdx)
        {
            try
            {
                if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
                var sm = _sm;
                if (sm == null) return null;
                var list = sm.m_WarehouseShelfList;
                for (int i = 0; i < list.Count; i++)
                {
                    var ws = list[i];
                    if (ws == null || ws.GetIndex() != shelfIdx) continue;
                    return ws.GetWarehouseCompartment(compIdx);
                }
                // no silent skips: an unresolvable rack means the store mirror is
                // stuck retrying - say so (throttled) instead of shrugging
                double nowT = Time.realtimeSinceStartupAsDouble;
                if (nowT - _lastResolveWarn > 30.0)
                {
                    _lastResolveWarn = nowT;
                    CoopPlugin.Log.LogWarning($"BoxSync store: no warehouse rack with index {shelfIdx} (have {list.Count}) - retrying on next snapshot");
                }
            }
            catch { }
            return null;
        }

        /// <summary>B2: a stored-apply retry keeps getting rejected. Inspect the target
        /// compartment's box occupants; if any is a box WE track whose CURRENT host snapshot
        /// places it at a DIFFERENT shelf/comp (or not stored at all), it is a stale GHOST
        /// left over from rack-index divergence occupying the slot. Evict it (the game's own
        /// RemoveBox via UnhookIfStored) and retry the store in the same pass. Only evicts on
        /// POSITIVE identification (tracked box + host disagrees about its location) - an
        /// untracked box, or one the host agrees belongs here, is left alone and the caller
        /// falls through to fail-counting. Always logs a diagnostic that proves-or-kills the
        /// index-divergence theory: the resolved rack's GetIndex/GetWarehouseIndex, the
        /// occupant ids found, and where the host claims each occupant lives.</summary>
        private static bool TryEvictGhostAndRetryStore(
            InteractablePackagingBox_Item box, ShelfCompartment rackComp, Entry want)
        {
            try
            {
                var occupants = rackComp.GetInteractablePackagingBoxList();
                // rack address as the compartment itself reports it - if these diverge from
                // want.StoreShelf/want.StoreComp the peers disagree on rack ordering
                int rackWarehouseIdx = -1, rackCompIdx = -1;
                try { rackWarehouseIdx = rackComp.GetWarehouseIndex(); } catch { }
                try { rackCompIdx = rackComp.GetIndex(); } catch { }

                var diag = new System.Text.StringBuilder();
                diag.Append($"BoxSync store DIAG: box id {want.Id} rejected by rack want={want.StoreShelf}/{want.StoreComp} ")
                    .Append($"resolved warehouseIdx={rackWarehouseIdx} compIdx={rackCompIdx} ")
                    .Append($"occupants={(occupants == null ? 0 : occupants.Count)}: ");

                bool evictedAny = false;
                if (occupants != null)
                {
                    // snapshot the list: UnhookIfStored -> RemoveBox mutates it under us
                    var snap = new List<InteractablePackagingBox_Item>(occupants);
                    for (int i = 0; i < snap.Count; i++)
                    {
                        var occ = snap[i];
                        if (occ == null) { diag.Append("[null] "); continue; }
                        var where = HostLocationOf(occ);
                        if (!where.Tracked)
                        {
                            diag.Append("[untracked] ");
                            continue;
                        }
                        // where does the host say this occupant lives?
                        string hostSays = where.Stored ? $"{where.Shelf}/{where.Comp}" : "not-stored";
                        bool hostDisagrees = !where.Stored
                            || where.Shelf != want.StoreShelf || where.Comp != want.StoreComp;
                        diag.Append($"[id {where.Id} host={hostSays}{(hostDisagrees ? " GHOST" : "")}] ");
                        // never evict the box we're trying to store, and never evict one the
                        // host agrees belongs in THIS slot (that's a legitimately-full slot)
                        if (hostDisagrees && !ReferenceEquals(occ, box))
                        {
                            // full take-recipe, not just RemoveBox: a stored box has physics
                            // OFF and is parented to the slot, so bare unhook would leave the
                            // evicted ghost a frozen kinematic husk at the old slot. Unhook +
                            // unparent + physics on = a normal loose box its own next apply
                            // pass repositions (or re-stores at the host's real slot).
                            UnhookIfStored(occ); // game's RemoveBox + clears m_IsStored
                            try { occ.transform.SetParent(null); } catch { }
                            try { occ.SetPhysicsEnabled(true); } catch { }
                            try { if (occ.m_MoveStateValidArea != null) occ.m_MoveStateValidArea.gameObject.SetActive(true); } catch { }
                            try { occ.m_ItemCompartment.SetPriceTagVisibility(occ.gameObject.activeSelf); } catch { }
                            evictedAny = true;
                        }
                    }
                }
                CoopPlugin.Log.LogWarning(diag.ToString());

                if (!evictedAny) return false;
                // slot freed: retry the store in this same pass (physics already off from the
                // caller's attempt). DispenseItem returns void; caller re-checks box.m_IsStored.
                try { box.DispenseItem(isPlayer: false, rackComp); }
                catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync store evict-retry: " + e.Message); }
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("BoxSync store evict: " + e.Message);
                return false;
            }
        }

        // ---------------- host ----------------

        private ushort HostIdFor(InteractablePackagingBox_Item box)
        {
            if (_hostIds.TryGetValue(box, out ushort id)) return id;
            do { id = _nextId++; if (_nextId == 0) _nextId = 1; }
            while (id == 0 || _hostById.ContainsKey(id));
            _hostIds[box] = id;
            _hostById[id] = box;
            return id;
        }

        private void HostPruneDead()
        {
            _removeScratch.Clear();
            foreach (var kv in _hostById)
                if (kv.Value == null) _removeScratch.Add(kv.Key);
            for (int i = 0; i < _removeScratch.Count; i++)
            {
                ushort id = _removeScratch[i];
                if (_hostById.TryGetValue(id, out var dead) && !ReferenceEquals(dead, null))
                    _hostIds.Remove(dead); // Unity fake-null: reference still hashes
                _hostById.Remove(id);
                _remoteCarried.Remove(id);
                _hostCarriedLastTick.Remove(id);
                _hostRecentlyReleased.Remove(id);
            }
        }

        public void HostTick(float dt, bool active)
        {
            if (!active || Rm() == null) return;
            // carry transitions broadcast IMMEDIATELY - a box that still looks
            // on-the-floor invites another player to grab it too
            bool force = false;
            try
            {
                var scan = LiveBoxes();
                for (int i = 0; i < scan.Count; i++)
                {
                    if (scan[i] == null) continue;
                    ushort id = HostIdFor(scan[i]);
                    if (IsLocallyCarried(scan[i]))
                    {
                        if (_hostCarriedLastTick.Add(id)) force = true;
                    }
                    else if (_hostCarriedLastTick.Remove(id))
                    {
                        force = true;
                        _hostRecentlyReleased[id] = Time.realtimeSinceStartupAsDouble;
                    }
                }
            }
            catch { }
            _timer += dt;
            if (!force && _timer < 1.5f) return;
            if (_timer >= 1.5f) _timer -= 1.5f;
            if (force) _lastHostHash = 0; // transitions bypass the unchanged-gate
            try
            {
                var boxes = LiveBoxes();
                // cap raised 250 -> 1000 (the wire count is now a ushort): a Day-100+ shop's
                // warehouse holds well over 250 boxes, and the old byte-capped snapshot
                // silently dropped everything past #250 - which is exactly where the game
                // APPENDS new delivery boxes. Guests then (a) never saw fresh deliveries
                // ("when guest orders items, the boxes only show on host") and (b) DESTROYED
                // their mapped copies of every box past the cap via the absent-id sweep -
                // permanent, compounding divergence ("items in boxes on storage shelf are
                // not the same"). 1000 entries * ~27B = ~27KB per changed snapshot: trivial
                // for the reliable channel.
                if (boxes.Count > 1000 && Time.realtimeSinceStartupAsDouble - _lastCapWarn > 60.0)
                {
                    _lastCapWarn = Time.realtimeSinceStartupAsDouble;
                    CoopPlugin.Log.LogWarning($"BoxSync host: {boxes.Count} live boxes exceed the 1000-box sync cap - boxes past the cap will not sync to guests");
                }
                var list = new List<Entry>(Mathf.Min(boxes.Count, 1000));
                for (int i = 0; i < boxes.Count && list.Count < 1000; i++)
                {
                    if (boxes[i] == null) continue;
                    var e = Snapshot(boxes[i]);
                    e.Id = HostIdFor(boxes[i]);
                    // a client holds it: mark carried AND not-stored, so a not-yet-processed
                    // m_IsStored=true on our side can't broadcast a Stored=true echo that
                    // re-pins the taker's report back to stored (the rack-take desync)
                    if (_remoteCarried.Contains(e.Id))
                    {
                        e.Carried = true; e.Stored = false;
                        // re-assert the worker lock every tick: a game path can clear
                        // m_PreventWorkerTakeBox out from under us (e.g. SetOpenCloseBox
                        // resets it false on close, decompiled ~370) while the guest still
                        // carries the box, which would re-open it to worker theft (B2)
                        SetHostWorkerLock(boxes[i], true);
                    }
                    list.Add(e);
                }
                // skip identical snapshots (boxes sit still most of the time); a slow
                // heal broadcast still repairs any client that missed one
                int hash = 17;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    hash = hash * 31 + e.Id;
                    hash = hash * 31 + e.Type;
                    hash = hash * 31 + e.Count;
                    hash = hash * 31 + ((e.IsBig ? 1 : 0) | (e.IsOpen ? 2 : 0) | (e.Carried ? 4 : 0) | (e.Settled ? 8 : 0) | (e.Stored ? 16 : 0));
                    hash = hash * 31 + e.StoreShelf * 311 + e.StoreComp;
                    hash = hash * 31 + (int)(e.Pos.x * 8f);
                    hash = hash * 31 + (int)(e.Pos.y * 8f); // include height: a box that settled to a corrected Y must re-broadcast
                    hash = hash * 31 + (int)(e.Pos.z * 8f);
                }
                _hostHeal += 1.5f;
                if (hash == _lastHostHash && _hostHeal < 10f) return;
                _lastHostHash = hash;
                if (_hostHeal >= 10f) HostPruneDead(); // slow housekeeping on the heal beat
                _hostHeal = 0f;
                OnHostSnapshot?.Invoke(list);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync host: " + e.Message); }
        }

        /// <summary>Host: a client asked for box states (their local edits).</summary>
        public void HostApplyRequest(List<Entry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!_hostById.TryGetValue(e.Id, out var box) || box == null) continue;
                // type sanity: a mangled or ancient request must not RESTYLE a box that
                // already holds a different item. But an EMPTY host box (type None, or a
                // stale different type with zero contents) legitimately adopts the request's
                // type: the guest pulled an item off a shelf into an empty open box, and the
                // box's compartment took on the shelf's type (decompiled
                // RemoveItemFromShelf ~280-297 -> ShelfCompartment.CheckItemType ~242-247
                // sets the type when m_ItemAmount<=0). Rejecting that dropped the box's gain
                // while the shelf decrement synced through - the item was destroyed host-side
                // and healed away on the guest (B1). Only reject when the host box is
                // genuinely occupied with a CONFLICTING type. EItemType.None == -1.
                int htype = (int)box.m_ItemCompartment.GetItemType();
                if (htype != e.Type && htype != (int)EItemType.None
                    && box.m_ItemCompartment.GetItemCount() > 0) continue;
                // never stomp a box in the host's or a WORKER's hands - AND (C-d) never a box
                // the HOST is currently moving/dragging in move-mode. GetIsMovingObject() is
                // the ObjMoveSync-style guard (InteractablePackagingBox_Item inherits it from
                // InteractableObject); the guest's report carries a stale pose that would snap
                // the box back out from under the host's drag. Skipping the whole request drops
                // its pose/store fields (which the hostAuthoritative apply below would ignore
                // anyway) plus its content, which a mid-move host box owns just like held ones.
                if (IsLocallyCarried(box) || IsBeingHeld(box) || box.GetIsMovingObject()) continue;
                // just set down: a report the client built while we still carried it is
                // stale by definition - the race that teleported boxes mid-restock
                if (_hostRecentlyReleased.TryGetValue(e.Id, out double rel)
                    && Time.realtimeSinceStartupAsDouble - rel < 6.0) continue;
                // enter/leave the client-carried set, and pair the worker-untouchable
                // lock with it: while a guest carries a box no worker may drain or take
                // it (B2). Set on the exact box the request resolved to; cleared the
                // instant the guest sets it down (the else branch below).
                if (e.Carried) { _remoteCarried.Add(e.Id); SetHostWorkerLock(box, true); }
                else { _remoteCarried.Remove(e.Id); SetHostWorkerLock(box, false); }
                // C-c belt+braces (HOST side): skip CONTENT (count/open) when the host box is
                // OPEN, NOT client-carried, AND already holds items - a NON-empty open box on
                // the host is being dispensed from by the host/workers and its contents are
                // host-authoritative. The guest's ClientTick used to echo EVERY box's lagging
                // mirror count every cycle, and applying that stale count to a box the host was
                // actively draining refilled it endlessly ("boxes get unlimited items"). A box
                // the guest carries is theirs, so let its content through. DEVIATION from the
                // literal spec (which skipped ALL open non-carried boxes): an EMPTY open box
                // (count 0) is exempt, because that is the guest pulling the FIRST item into an
                // open box (B1) - a legitimate gain the host must accept or the item is
                // destroyed host-side and healed away on the guest. A refill source is always
                // non-empty, so the count>0 carve-out keeps the refill fix intact while
                // preserving B1. C-a: hostAuthoritative=true also drops pose/store regardless.
                bool hostOpen = false; int hostCount = 0;
                try { hostOpen = box.IsBoxOpened(); } catch { }
                try { hostCount = box.m_ItemCompartment.GetItemCount(); } catch { }
                // ...and a count INCREASE is exempt too: a guest reclaiming ANOTHER item into
                // an already-non-empty open box (RemoveItemFromShelf supports multiples) is a
                // legitimate gain the host must accept - the refill BUG was the guest's stale
                // echo of untouched boxes, which the touched-only report gating already kills
                // at the source; this host-side guard is belt-and-braces against the residual
                // (host + guest working the SAME box within the touch window), where blocking
                // equal/lower counts suffices.
                bool applyContent = !(hostOpen && hostCount > 0 && !_remoteCarried.Contains(e.Id)
                                      && e.Count <= hostCount);
                ApplyToBox(box, e, hostAuthoritative: true, applyContent: applyContent);
            }
            // fan the change out to everyone NOW - with 3+ players the other
            // clients otherwise wait out the periodic tick and the hash gate.
            // Exactly one period: a larger sentinel made the keep-the-phase
            // decrement fire EVERY frame for seconds (the post-throw jitter)
            _timer = 1.5f;
            _lastHostHash = 0;
        }

        private static readonly Dictionary<int, double> _remWindowStart = new Dictionary<int, double>();
        private static readonly Dictionary<int, int> _remWindowCount = new Dictionary<int, int>();

        /// <summary>Shared by ALL box-family modules (item/card/furniture): true if this
        /// client's removal budget for the current 2s window is spent. A human trashing a
        /// warehouse won't exceed 16 boxes in 2 seconds (C-b raised the cap from 4) - but a
        /// client whose game is re-running its world-load teardown echoes its ENTIRE box
        /// population, all three lists, as "trashed" (first field incident: 250 boxes in
        /// 10ms). Per-connection, so one player's flood never eats another's legitimate
        /// trash. refusedId (optional) names the box in the throttled over-budget warning.</summary>
        public static bool RemovalFlooded(int connId, string channel, int refusedId = -1)
        {
            double nowT = Time.realtimeSinceStartupAsDouble;
            if (!_remWindowStart.TryGetValue(connId, out double start) || nowT - start > 2.0)
            {
                _remWindowStart[connId] = nowT;
                _remWindowCount[connId] = 0;
            }
            int c = _remWindowCount[connId] = _remWindowCount[connId] + 1;
            // C-b: cap raised 4 -> 16 (4x). This is friends-coop, not anti-grief-critical:
            // the old cap of 4/2s stranded host-side boxes during a legitimate warehouse
            // cleanup spree, and the guest - believing them trashed - watched them 'respawn
            // next day' and clog shelving (field report). A reload teardown echo still floods
            // hundreds in milliseconds, so the flood protection stays; only the ceiling moves.
            if (c <= 16) return false;
            // C-b: a refused removal must NOT be silent - name the box id and say 'removal
            // budget' so a stranded/respawning box is traceable. Throttled (first over-cap
            // removal of the window, then every 100th) so the reload-echo flood can't spam.
            if (c == 17 || c % 100 == 0)
                CoopPlugin.Log.LogWarning($"ignoring {channel} removal"
                    + (refusedId >= 0 ? $" of box id {refusedId}" : "")
                    + $" from client {connId} - removal budget spent for this 2s window (reload echo, not gameplay)");
            return true;
        }

        /// <summary>Host: a client trashed a box - destroy the real one so the next
        /// broadcast doesn't resurrect it at its old spot.</summary>
        public void HostApplyRemoval(int id, int type, int connId)
        {
            if (RemovalFlooded(connId, "item-box", id)) return;
            if (!_hostById.TryGetValue((ushort)id, out var box) || box == null) return;
            if ((int)box.m_ItemCompartment.GetItemType() != type) return;
            if (IsLocallyCarried(box) || IsBeingHeld(box)) return; // player's OR worker's hands
            ApplyingRemote = true;
            try { UnhookIfStored(box); box.OnDestroyed(); } // OnDestroyed alone leaks the rack slot
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync removal: " + e.Message); }
            finally { ApplyingRemote = false; }
            _hostIds.Remove(box);
            _hostById.Remove((ushort)id);
            _remoteCarried.Remove((ushort)id);
            _hostCarriedLastTick.Remove((ushort)id);
            _hostRecentlyReleased.Remove((ushort)id);
        }

        /// <summary>Host: the host player destroyed a box locally - drop its id now
        /// rather than waiting for the heal-beat prune.</summary>
        public void HostNotifyLocalDestroyed()
        {
            HostPruneDead();
        }

        /// <summary>Client: the local player destroyed a box (trash bin etc.). Tell the
        /// host to remove the real one and stop tracking it, so no ghost report or stale
        /// echo brings it back.</summary>
        public void NotifyLocalDestroyed(InteractablePackagingBox_Item box)
        {
            if (!_idOf.TryGetValue(box, out ushort id)) return; // never synced; host doesn't know it
            int type = 0;
            try { type = (int)box.m_ItemCompartment.GetItemType(); } catch { }
            _idOf.Remove(box);
            _byId.Remove(id);
            _carriedLastTick.Remove(id);
            _locallyTouched.Remove(id);
            _recentlyReleased.Remove(id);
            for (int i = 0; i < _lastApplied.Count; i++)
            {
                if (_lastApplied[i].Id != id) continue;
                _lastApplied.RemoveAt(i);
                break;
            }
            OnLocalRemoved?.Invoke(id, type);
        }

        // ---------------- client ----------------

        /// <summary>Client: reconcile the live box population to the host's snapshot.</summary>
        public void ClientApply(List<Entry> hostList)
        {
            ApplyingRemote = true;
            // publish "where does the host say this tracked occupant lives" for the store-
            // retry ghost eviction (B2). Rebuild the id->entry index for THIS snapshot, then
            // hand ApplyToBox a closure over our id maps. Reset in finally so a stale closure
            // never fires during the host's own apply path (HostApplyRequest).
            _hostWhereScratch.Clear();
            for (int i = 0; i < hostList.Count; i++) _hostWhereScratch[hostList[i].Id] = hostList[i];
            HostLocationOf = occupant =>
            {
                var r = default(HostBoxWhere);
                try
                {
                    if (occupant == null || !_idOf.TryGetValue(occupant, out ushort oid)) return r;
                    r.Tracked = true;
                    r.Id = oid;
                    if (_hostWhereScratch.TryGetValue(oid, out var he))
                    {
                        r.Stored = he.Stored;
                        r.Shelf = he.StoreShelf;
                        r.Comp = he.StoreComp;
                    }
                    // not in this snapshot at all: host no longer lists it stored -> not stored
                }
                catch { }
                return r;
            };
            try { ClientApplyInner(hostList); }
            finally { ApplyingRemote = false; HostLocationOf = _ => default(HostBoxWhere); }
        }

        private void ClientApplyInner(List<Entry> hostList)
        {
            // drop map entries whose box died locally (reconcile destroys, scene churn)
            _removeScratch.Clear();
            foreach (var kv in _byId)
                if (kv.Value == null) _removeScratch.Add(kv.Key);
            for (int i = 0; i < _removeScratch.Count; i++)
            {
                ushort id = _removeScratch[i];
                if (_byId.TryGetValue(id, out var dead) && !ReferenceEquals(dead, null))
                    _idOf.Remove(dead);
                _byId.Remove(id);
            }

            // ADOPT unmapped local boxes (spawned by the save-load or the game's own
            // post-load drip spawner, so they exist on both sides): pair them with
            // unmapped snapshot entries by type+size in list order - the client's
            // load order mirrors the host list the save was written from
            _orphanScratch.Clear();
            var live = LiveBoxes();
            for (int i = 0; i < live.Count; i++)
            {
                var b = live[i];
                if (b != null && !_idOf.ContainsKey(b)) _orphanScratch.Add(b);
            }
            if (_orphanScratch.Count > 0)
            {
                for (int i = 0; i < hostList.Count; i++)
                {
                    var want = hostList[i];
                    if (_byId.TryGetValue(want.Id, out var mapped) && mapped != null) continue;
                    for (int j = 0; j < _orphanScratch.Count; j++)
                    {
                        var cand = _orphanScratch[j];
                        if (cand == null) continue;
                        if ((int)cand.m_ItemCompartment.GetItemType() != want.Type
                            || cand.m_IsBigBox != want.IsBig) continue;
                        _byId[want.Id] = cand;
                        _idOf[cand] = want.Id;
                        _orphanScratch[j] = null;
                        break;
                    }
                }
            }

            _snapshotIds.Clear();
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < hostList.Count; i++)
            {
                var want = hostList[i];
                _snapshotIds.Add(want.Id);
                _byId.TryGetValue(want.Id, out var box);
                if (box != null && (box.m_ItemCompartment.GetItemType() != (EItemType)want.Type
                                    || box.m_IsBigBox != want.IsBig))
                {
                    // shouldn't happen with stable ids - but never rebuild a box in
                    // someone's HANDS; wait for the set-down
                    if (IsLocallyCarried(box)) continue;
                    _idOf.Remove(box);
                    UnhookIfStored(box);
                    try { box.OnDestroyed(); } catch { }
                    box = null;
                }
                if (box == null)
                {
                    try
                    {
                        // field diagnostic for the "guest orders show only on host" report:
                        // EPL modded ids live in [200000,500000] and their spawn path was
                        // suspected - log the attempt so success is as visible as failure
                        if (want.Type >= 200000)
                            CoopPlugin.Log.LogInfo($"BoxSync client: spawning modded-item box id {want.Id} type {want.Type} (EPL virtual id)");
                        box = RestockManager.SpawnPackageBoxItem((EItemType)want.Type, want.Count, want.IsBig);
                    }
                    catch (Exception e)
                    {
                        CoopPlugin.Log.LogWarning("BoxSync spawn: " + e.Message);
                        continue;
                    }
                    if (box == null) continue;
                    _byId[want.Id] = box;
                    _idOf[box] = want.Id;
                }
                // a box in MY hands is mine until I put it down; a box in the HOST's
                // hands has a transient position we don't copy
                if (IsLocallyCarried(box)) continue;
                // a stale "carried" echo about a box I JUST released must not hide it
                if (want.Carried && _recentlyReleased.TryGetValue(want.Id, out double t) && now - t < 6.0)
                    continue;
                // my own recent edits (took an item, kicked it) win over stale echoes;
                // my report reaches the host and the next echo agrees. VISIBILITY is
                // exempt: someone else's pickup/set-down must show here immediately,
                // or their set-down box stays invisible to me for the whole window
                if (_locallyTouched.TryGetValue(want.Id, out double touched) && now - touched < 6.0)
                {
                    if (!want.Carried && !box.gameObject.activeSelf)
                    {
                        box.gameObject.SetActive(true);
                        try { box.m_ItemCompartment.SetPriceTagVisibility(true); } catch { }
                    }
                    else if (want.Carried && box.gameObject.activeSelf)
                    {
                        try { box.m_ItemCompartment.SetPriceTagVisibility(false); } catch { }
                        box.gameObject.SetActive(false);
                    }
                    continue;
                }
                ApplyToBox(box, want, applyPosition: !want.Carried);
            }

            // remove local boxes whose id the host no longer lists: with stable ids
            // this is surgical - ONLY the genuinely-destroyed box dies, never an
            // index-shifted neighbor (the "vanished out of my hands" bug).
            // EXCEPT when the snapshot rode the cap: a truncated list proves nothing
            // about absence (the box may simply be past the cap, not destroyed), and
            // sweeping on it is exactly how guests permanently lost real warehouse
            // boxes back when the cap was 250. Destroys just defer to the next
            // un-capped snapshot.
            bool truncated = hostList.Count >= 1000;
            if (truncated && Time.realtimeSinceStartupAsDouble - _lastCapSkipLog > 60.0)
            {
                _lastCapSkipLog = Time.realtimeSinceStartupAsDouble;
                CoopPlugin.Log.LogInfo("BoxSync client: snapshot rode the 1000-box cap - skipping the absent-box sweep (can't tell destroyed from truncated)");
            }
            if (!truncated)
            {
                _removeScratch.Clear();
                foreach (var kv in _byId)
                    if (!_snapshotIds.Contains(kv.Key)) _removeScratch.Add(kv.Key);
                for (int i = 0; i < _removeScratch.Count; i++)
                {
                    ushort id = _removeScratch[i];
                    if (!_byId.TryGetValue(id, out var box)) continue;
                    if (box != null)
                    {
                        if (IsLocallyCarried(box))
                        {
                            CoopPlugin.Log.LogWarning($"host removed the box in your hands (id {id}, {(EItemType)(int)box.m_ItemCompartment.GetItemType()}) - it was consumed host-side");
                            // CRITICAL: OnDestroyed does NOT exit hold-box mode, so destroying a
                            // box the guest is holding strands the controller in HoldingBoxState
                            // forever (soft-lock: can't interact with anything, not even the trash).
                            // Release hold-box mode FIRST via the game's own exit.
                            try { CoopCore.ForceExitHoldBox(box); } catch { }
                        }
                        _idOf.Remove(box);
                        UnhookIfStored(box);
                        try { box.OnDestroyed(); } catch { }
                    }
                    _byId.Remove(id);
                    _carriedLastTick.Remove(id);
                    _locallyTouched.Remove(id);
                    _recentlyReleased.Remove(id);
                }
            }

            // remember the applied truth for local-change detection
            _lastApplied.Clear();
            _lastApplied.AddRange(hostList);
        }

        /// <summary>Client: detect the local player's own box edits and request them.</summary>
        public void ClientTick(float dt, bool active)
        {
            if (!active || Rm() == null || _lastApplied.Count == 0) return;
            // carry transitions are detected EVERY FRAME and reported immediately -
            // the periodic diff alone left pickups/set-downs invisible for seconds,
            // long enough for someone else to try grabbing the same box
            bool force = false;
            try
            {
                foreach (var kv in _idOf)
                {
                    if (kv.Key == null) continue;
                    if (IsLocallyCarried(kv.Key))
                    {
                        if (_carriedLastTick.Add(kv.Value)) force = true; // pickup transition
                    }
                    else if (_carriedLastTick.Remove(kv.Value))
                    {
                        force = true; // set-down transition
                        _recentlyReleased[kv.Value] = Time.realtimeSinceStartupAsDouble;
                    }
                }
            }
            catch { }
            _timer += dt;
            if (!force && _timer < 1.5f) return;
            if (_timer >= 1.5f) _timer -= 1.5f;
            try
            {
                bool changed = force;
                _reportBuf.Clear(); // serialized synchronously by the callback; safe to reuse
                var list = _reportBuf;
                double nowT = Time.realtimeSinceStartupAsDouble;
                for (int i = 0; i < _lastApplied.Count; i++)
                {
                    var truth = _lastApplied[i];
                    _byId.TryGetValue(truth.Id, out var box);
                    // C-c (guest side): the guest must only echo a box it has a REASON to
                    // report. The old loop parroted EVERY box every cycle - dead ones verbatim,
                    // stored/other-carried verbatim, and every live box's Snapshot - so a box's
                    // LAGGING mirror count was re-reported forever, and the host applied that
                    // stale count to a box it was actively dispensing from, refilling it
                    // endlessly ("boxes get unlimited items"). Reasons to report: I'm carrying
                    // it (carry transition), I just set it down, or its live snapshot DIFFERS
                    // from the applied truth / I touched it within the window. An untouched box
                    // is left out entirely; the host's own snapshot stays its authority.
                    if (box == null)
                        continue; // dead/unmapped locally: nothing local to report; a trash is settled by the separate BoxRemoved message

                    bool touchedRecently = _locallyTouched.TryGetValue(truth.Id, out double tch) && nowT - tch < 6.0;
                    bool justReleased = _recentlyReleased.TryGetValue(truth.Id, out double rr) && nowT - rr < 6.0;

                    // while I'M carrying it: tell the host (so everyone else hides their copy)
                    // but keep reporting the last settled position. Carry IS a report reason;
                    // the pickup/set-down transitions themselves force a send (see above).
                    if (IsLocallyCarried(box))
                    {
                        var held = truth;
                        held.Carried = true;
                        held.Stored = false; // just took it off a rack: a stale stored flag would keep the host's copy slotted
                        list.Add(held);
                        continue;
                    }
                    // host says STORED: the rack slot is host-authoritative. Do NOT echo an
                    // untouched stored box - that is exactly the stale echo C-c removes, and
                    // reporting a not-stored state for a box the host legitimately shelved would
                    // command it to yank the box off the rack (revert war). Only report when I
                    // actively touched it recently. EXCEPTION: if WE physically took this exact
                    // box off the rack (no longer stored locally AND recently carried+released),
                    // fall through to Snapshot so the take mirrors even if the transient Carried
                    // frame was missed and a stale host Stored=true echo re-pinned us.
                    if (truth.Stored)
                    {
                        bool reallyStored = true;
                        try { reallyStored = box.m_IsStored; } catch { }
                        bool weTookItOff = !reallyStored && justReleased;
                        if (!weTookItOff)
                        {
                            if (touchedRecently) { list.Add(truth); changed = true; }
                            continue;
                        }
                        // else fall through to Snapshot(box): reports Stored=false + real pose
                    }
                    // hidden because ANOTHER player carries it: nothing local to report UNLESS I
                    // just set it down myself (that report IS the set-down, and skipping it
                    // deadlocks the box as carried-forever on the other screen)
                    if (truth.Carried && !justReleased)
                        continue;

                    var now = Snapshot(box);
                    now.Id = truth.Id;
                    bool differs = Differs(now, truth);
                    if (differs)
                    {
                        changed = true;
                        _locallyTouched[truth.Id] = nowT;
                    }
                    // C-c: echo this box only with a genuine local reason - a fresh diff this
                    // tick, a recent local edit still in the window, or a set-down/take we're
                    // confirming. An untouched box whose mirror already matches the applied
                    // truth is dropped (no lagging-count re-report -> no host refill).
                    if (differs || touchedRecently || justReleased)
                    {
                        list.Add(now);
                        if (justReleased) changed = true; // ensure the set-down/take actually sends
                    }
                }
                if (changed) OnClientChanges?.Invoke(list);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync client: " + e.Message); }
        }

        // ---------------- shared apply ----------------

        // C-e: box ids we've already warned about for an under-map pose, so the "refusing
        // under-map pose" warning fires ONCE per box id (a stuck under-floor host pose would
        // otherwise spam it every tick). Cleared in Reset() with the rest of the session state.
        private static readonly HashSet<ushort> _underMapLogged = new HashSet<ushort>();

        /// <summary>C-e: an under-map pose (y &lt; -2) must never be applied - a box written
        /// there falls through the world and is unrecoverable (a corrupt/late snapshot or a
        /// box that slipped below the floor host-side). Returns true (and logs ONCE per box id)
        /// when the wanted pose is under the floor, so pose-apply callers skip the position
        /// write and leave the box where it is.</summary>
        private static bool UnderMapPose(Entry want)
        {
            if (want.Pos.y >= -2f) return false;
            if (_underMapLogged.Add(want.Id))
                CoopPlugin.Log.LogWarning($"BoxSync: refusing under-map pose (y={want.Pos.y:F2}) for box id {want.Id} - keeping its current position");
            return true;
        }

        private static void ApplyToBox(InteractablePackagingBox_Item box, Entry want, bool applyPosition = true, bool hostAuthoritative = false, bool applyContent = true)
        {
            try
            {
                // C-a: on the HOST this runs for a guest's REQUEST (hostAuthoritative=true).
                // The host owns rack placement AND pose for every box it tracks - only guest-
                // authoritative transitions may cross: carried enter/leave (worker lock paired
                // in HostApplyRequest), open/close, CONTENT changes that pass the C-c guard
                // (applyContent), and the STORE-ON-SETDOWN below. Skip the client's stored/rack
                // mirror machinery and the pose write; the host's own next snapshot rebroadcasts
                // its truth and heals the guest. This is exactly what stops the 1.0.29 give-up
                // PIN (kinematic freeze at want.Pos) from running HOST-side and freezing
                // authoritative boxes at the guest's reported pose.
                //
                // STORE-ON-SETDOWN (the one stored-transition the host MUST record): a guest
                // racking a box is as guest-authoritative as carrying it off one - BoxSync's
                // Stored field is the ONLY carrier (no dedicated store message exists), so if
                // the host never stores its copy the box stays LOOSE in the authoritative
                // world, the guest's report re-differs forever, and the storage is silently
                // LOST on save. Run the game's own store recipe ONCE per report (no retry
                // loop, no give-up pin, no eviction - those are client-mirror machinery); if
                // the host's rack genuinely rejects it (slot truly full), the box stays a
                // normal loose box and the host's truth rebroadcast pops it back off the
                // guest's rack too - correct, visible, self-consistent.
                if (hostAuthoritative && want.Stored && !want.Carried)
                {
                    bool hostStored = false;
                    try { hostStored = box.m_IsStored; } catch { }
                    if (!hostStored)
                    {
                        var hostRack = ResolveWarehouseCompartment(want.StoreShelf, want.StoreComp);
                        if (hostRack != null)
                        {
                            try
                            {
                                // reactivate FIRST (a guest-carried box is hidden host-side):
                                // DispenseItem on an OPEN box runs SetOpenCloseBox, whose
                                // StartCoroutine throws on an inactive GameObject - the throw
                                // landed before m_IsStored and silently lost the store
                                if (!box.gameObject.activeSelf) box.gameObject.SetActive(true);
                                // seed contents only when the HOST box is genuinely empty
                                // (all DispenseItem's empty-check needs) - an unconditional
                                // count write here would bypass the applyContent authority
                                // gate and let a stale carried-mirror count shrink/inflate
                                // the host's authoritative contents at store time
                                if (box.m_ItemCompartment.GetItemCount() <= 0)
                                    ApplyClosedCount(box, Mathf.Max(want.Count, 1));
                                box.SetPhysicsEnabled(false);
                                box.DispenseItem(isPlayer: false, hostRack);
                                bool ok = false;
                                try { ok = box.m_IsStored; } catch { }
                                if (!ok) box.SetPhysicsEnabled(true); // rejected: stay a normal loose box
                            }
                            catch (Exception e)
                            {
                                CoopPlugin.Log.LogWarning("BoxSync host store: " + e.Message);
                                try { box.SetPhysicsEnabled(true); } catch { }
                            }
                        }
                    }
                }
                if (!hostAuthoritative)
                {
                    // warehouse-rack storage FIRST: a stored box is parented to a rack slot
                    // the game owns. Syncing it as a loose box yanked stored boxes off the
                    // rack on BOTH sides ("boxes all over the place" report) and left them
                    // uninteractable - the store/unstore transition must go through the
                    // game's own methods so the compartment registration mirrors too
                    bool locallyStored = false;
                    try { locallyStored = box.m_IsStored; } catch { }
                    if (want.Stored)
                    {
                        // contents FIRST: DispenseItem rejects "empty" boxes, and a mirror
                        // whose count went stale while the box was carried would otherwise
                        // live-lock the store forever (closed-box path is data-only, safe
                        // whether stored already or about to be)
                        ApplyClosedCount(box, want.Count);
                        if (locallyStored) return;              // already stored; slot owns it
                        // give-up guard: a genuinely full/mismatched rack slot rejects the
                        // store on EVERY tick, and the old code retried forever (field log:
                        // "rejected box id 252 ... retrying" every 30s all session). Once we
                        // give up we STOP dispensing - but instead of the old fall-through to a
                        // loose pose (which rendered the box floating at the elevated rack-slot
                        // world position want.Pos with physics on - the "storage boxes floating
                        // in the air" report), we KINEMATICALLY PIN it at the host pose so it
                        // reads as shelved (B1). A later snapshot retries the store on change.
                        if (!_storeGaveUp.Contains(want.Id))
                        {
                            if (!box.gameObject.activeSelf) box.gameObject.SetActive(true);
                            var rackComp = ResolveWarehouseCompartment(want.StoreShelf, want.StoreComp);
                            if (rackComp != null)
                            {
                                try
                                {
                                    // the game's OWN restore recipe (ShelfManager.DelayLoad):
                                    // physics off, then DispenseItem. In the player flow
                                    // StartHoldBox already disabled physics; a live loose box
                                    // here still has gravity and would fall off the rack
                                    box.SetPhysicsEnabled(false);
                                    box.DispenseItem(isPlayer: false, rackComp);
                                }
                                catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync store: " + e.Message); }
                                if (box.m_IsStored)
                                {
                                    _storeFails.Remove(want.Id);
                                    return; // stored successfully; slot owns pose
                                }
                                // DispenseItem returns void and eats failures. On the 2nd+ attempt,
                                // before we count another fail, check whether the slot is blocked by
                                // a GHOST occupant - a box WE track that the host places somewhere
                                // else (or not stored) - and if so evict it and retry in THIS pass
                                // (B2). This is also our field probe for the rack-index-divergence
                                // theory: we log the resolved rack's indices, the occupant ids, and
                                // where the host claims each occupant lives.
                                _storeFails.TryGetValue(want.Id, out int fails);
                                if (fails >= 1 && TryEvictGhostAndRetryStore(box, rackComp, want))
                                {
                                    if (box.m_IsStored)
                                    {
                                        _storeFails.Remove(want.Id);
                                        return; // ghost gone, store succeeded; slot owns pose
                                    }
                                }
                                box.SetPhysicsEnabled(true); // don't leave a loose box frozen mid-retry
                                _storeFails[want.Id] = ++fails;
                                if (fails < 4)
                                    return; // still trying; don't apply a loose pose mid-attempt
                                _storeGaveUp.Add(want.Id);
                                CoopPlugin.Log.LogWarning($"BoxSync store: rack {want.StoreShelf}/{want.StoreComp} keeps rejecting box id {want.Id} (slot full or size/type mismatch) - pinning it shelved at the host pose");
                                // fall through to the B1 pin below (do NOT return here)
                            }
                            else
                            {
                                return; // no rack yet; don't apply a loose pose mid-attempt
                            }
                        }
                        // GAVE UP (this pass or a prior one): the give-up branch OWNS the final
                        // physics/pose so the generic loose-apply further down can't fight it.
                        // Kinematic pin at the host pose = sits AT the rack, matching the host,
                        // instead of floating loose. SetPhysicsEnabled(false) => isKinematic +
                        // collider off (InteractablePackagingBox.cs ~163-175). Once the host
                        // takes it off the rack, want.Stored flips false and the else-branch
                        // below re-enables physics so it behaves normally again.
                        try
                        {
                            if (!box.gameObject.activeSelf)
                            {
                                box.gameObject.SetActive(true);
                                try { box.m_ItemCompartment.SetPriceTagVisibility(true); } catch { }
                            }
                            box.SetPhysicsEnabled(false);
                            // C-e: never write an under-map pose (y < -2) - a box pinned there
                            // falls through the world unrecoverable; keep its current position.
                            if (!UnderMapPose(want))
                            {
                                box.transform.SetPositionAndRotation(want.Pos, Quaternion.Euler(0f, want.Yaw, 0f));
                                ObjMoveSync.SyncTagGroup(box.transform); // box price tags ride in their own group
                            }
                        }
                        catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync store pin: " + e.Message); }
                        return;
                    }
                    else
                    {
                        // host says NOT stored: clear any give-up state so a future store
                        // (rack slot freed up, box re-placed) is attempted fresh
                        bool wasPinned = _storeGaveUp.Count > 0 && _storeGaveUp.Remove(want.Id);
                        if (_storeFails.Count > 0) _storeFails.Remove(want.Id);
                        // a give-up box was pinned KINEMATIC (physics off) at the rack pose. Now
                        // the host has taken it off the rack, so it must behave as a normal loose
                        // box again - re-enable physics here (the locallyStored take-recipe below
                        // won't fire for it, because the pin never actually stored it: m_IsStored
                        // is false). Without this the box stays an unclickable frozen ghost.
                        if (wasPinned && !locallyStored)
                        {
                            try { box.SetPhysicsEnabled(true); } catch { }
                        }
                    }
                    if (locallyStored)
                    {
                        // remote took it off the rack: replicate the take recipe, not just
                        // the bookkeeping - a stored box has physics DISABLED, and skipping
                        // the re-enable left an unclickable kinematic ghost at the drop spot
                        UnhookIfStored(box);
                        try { box.transform.SetParent(null); } catch { }
                        try { box.SetPhysicsEnabled(true); } catch { }
                        try { if (box.m_MoveStateValidArea != null) box.m_MoveStateValidArea.gameObject.SetActive(true); } catch { }
                        try { box.m_ItemCompartment.SetPriceTagVisibility(box.gameObject.activeSelf); } catch { }
                    }
                }
                else if (want.Carried)
                {
                    // C-a (host): the host ignores guest store/pose noise for tracked boxes,
                    // but a guest now CARRYING this box has physically taken it off the rack.
                    // Mirror THAT unstore via the game's own take recipe - a Carried-enter is a
                    // guest-authoritative transition, and for a stored box it means "off the
                    // rack." Skipping it leaves the host box registered on the slot: it
                    // rebroadcasts Stored=true when the guest sets it down and teleports the box
                    // back onto the rack on the guest (rack-take desync). Bare not-stored noise
                    // without a carry is still ignored (that's what C-a set out to stop).
                    bool hostStored = false;
                    try { hostStored = box.m_IsStored; } catch { }
                    if (hostStored)
                    {
                        UnhookIfStored(box);
                        try { box.transform.SetParent(null); } catch { }
                        try { box.SetPhysicsEnabled(true); } catch { }
                        try { if (box.m_MoveStateValidArea != null) box.m_MoveStateValidArea.gameObject.SetActive(true); } catch { }
                        try { box.m_ItemCompartment.SetPriceTagVisibility(box.gameObject.activeSelf); } catch { }
                    }
                }

                // someone (remote) is carrying it: their avatar shows the box in hand,
                // so the world copy disappears until it's set down - tags included
                // (box price tags live in a separate canvas group)
                if (want.Carried)
                {
                    if (box.gameObject.activeSelf)
                    {
                        try { box.m_ItemCompartment.SetPriceTagVisibility(false); } catch { }
                        box.gameObject.SetActive(false);
                    }
                    return;
                }
                if (!box.gameObject.activeSelf)
                {
                    box.gameObject.SetActive(true);
                    try { box.m_ItemCompartment.SetPriceTagVisibility(true); } catch { }
                }

                // open/close FIRST: content semantics depend on the resulting state.
                // C-c belt+braces: applyContent is false when the HOST box is open and NOT
                // remote-carried (an open box on the host is being worked by host/workers and
                // its contents are host-authoritative) - so a guest's stale count/open never
                // stomps it. That guest echo of an open box's lagging count was the endless
                // item-refill ("boxes get unlimited items"). On the client applyContent is
                // always true, so nothing changes there.
                if (applyContent && box.IsBoxOpened() != want.IsOpen && MiSetOpenClose != null)
                {
                    try { MiSetOpenClose.Invoke(box, null); } catch { }
                }

                var comp = box.m_ItemCompartment;
                int cur = comp.GetItemCount();
                if (applyContent && cur != want.Count)
                {
                    // EMPTY box gaining its first item: adopt the wanted type + build the
                    // pos list (B1). Without this, an empty box whose host compartment is
                    // still type None (the guest pulled a shelf item into it) can't spawn
                    // anything - SpawnItem/PreSpawnItemUpdate both clamp to the empty pos
                    // list. EnsureCompartmentType only acts on genuinely empty compartments,
                    // so an open box mid-display with real items is left untouched.
                    if (want.Count > 0) EnsureCompartmentType(box, want.Type);
                    if (box.IsBoxOpened())
                    {
                        // atomic clear-and-rebuild: SpawnItem is a LOADER (sets the count
                        // and appends fresh items), so incremental use duplicates objects
                        if (FiStoredList?.GetValue(comp) is List<Item> stored && stored.Count > 0)
                        {
                            foreach (var it in new List<Item>(stored))
                            {
                                if (it == null) continue;
                                comp.RemoveItem(it);
                                ItemSpawnManager.DisableItem(it);
                            }
                            stored.Clear();
                        }
                        if (want.Count > 0) comp.SpawnItem(want.Count, spawnFromFront: true);
                        else comp.PreSpawnItemUpdate(0);
                    }
                    else
                    {
                        // closed box: items are LAZY - the visible count and the amount
                        // spawned on first open must both track the synced count
                        comp.PreSpawnItemUpdate(want.Count);
                        FiAmountToSpawn?.SetValue(box, want.Count);
                    }
                }
                // C-a + relocation: apply the guest's settled/drop pose so a box the guest
                // carried and SET DOWN (or nudged loose) actually moves on the host too. This
                // is NOT the C-a bug - that was the STORE give-up PIN (kinematic freeze at
                // want.Pos) running host-side, which the skipped store block above now
                // prevents. Only boxes the guest actually touched reach here (its C-c report
                // gate drops untouched boxes), and a box the HOST is move-dragging never gets
                // this far (C-d drops it in HostApplyRequest) - so this is exactly a guest
                // relocation, not a stale echo or a snap-back. Skip when the box is (still)
                // STORED: the host owns rack placement and want.Pos for a stored box is the
                // elevated rack-slot pose, which as a loose pose would drop it off the rack.
                // C-e: never write an under-map pose (logged once per id inside UnderMapPose).
                bool boxStoredNow = false;
                try { boxStoredNow = box.m_IsStored; } catch { }
                if (applyPosition && want.Settled && !want.Stored && !boxStoredNow && !UnderMapPose(want))
                {
                    var t = box.transform;
                    if ((t.position - want.Pos).sqrMagnitude > 0.01f
                        || Mathf.Abs(Mathf.DeltaAngle(t.eulerAngles.y, want.Yaw)) > 3f)
                    {
                        t.SetPositionAndRotation(want.Pos, Quaternion.Euler(0f, want.Yaw, 0f));
                        ObjMoveSync.SyncTagGroup(t); // box price tags ride in their own group
                        try
                        {
                            // kill local tumble and WAKE the body: a sleeping rigidbody
                            // teleported mid-air hangs there frozen until poked. Use the
                            // REAL body (m_Rigidbody), not GetComponentInChildren which can
                            // return the kinematic rig-mesh child and skip the wake entirely.
                            var rb = box.m_Rigidbody;
                            if (rb != null && !rb.isKinematic)
                            {
                                rb.velocity = Vector3.zero;
                                rb.angularVelocity = Vector3.zero;
                                rb.WakeUp();
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync apply: " + e.Message); }
        }

        // ---------------- wire ----------------

        public static void WriteEntries(BinaryWriter bw, List<Entry> entries)
        {
            // ushort count (was byte): the byte ceiling forced the old 250-box snapshot
            // cap, which silently dropped late-list boxes - see the HostTick comment.
            // WIRE CHANGE: peers must both be on this build (the version handshake
            // already rejects mixed versions, so this is safe).
            bw.Write((ushort)Mathf.Min(entries.Count, 1000));
            for (int i = 0; i < entries.Count && i < 1000; i++)
            {
                var e = entries[i];
                bw.Write(e.Id);
                bw.Write(e.Type);
                bw.Write((ushort)Mathf.Clamp(e.Count, 0, ushort.MaxValue));
                bw.Write((byte)((e.IsBig ? 1 : 0) | (e.IsOpen ? 2 : 0) | (e.Carried ? 4 : 0) | (e.Settled ? 8 : 0) | (e.Stored ? 16 : 0)));
                bw.Write(e.StoreShelf);
                bw.Write(e.StoreComp);
                bw.Write(e.Pos.x); bw.Write(e.Pos.y); bw.Write(e.Pos.z);
                bw.Write(e.Yaw);
            }
        }

        public static List<Entry> ReadEntries(BinaryReader br)
        {
            int n = br.ReadUInt16(); // widened with WriteEntries (was a byte)
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new Entry { Id = br.ReadUInt16(), Type = br.ReadInt32(), Count = br.ReadUInt16() };
                byte f = br.ReadByte();
                e.IsBig = (f & 1) != 0;
                e.IsOpen = (f & 2) != 0;
                e.Carried = (f & 4) != 0;
                e.Settled = (f & 8) != 0;
                e.Stored = (f & 16) != 0;
                e.StoreShelf = br.ReadByte();
                e.StoreComp = br.ReadByte();
                e.Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                e.Yaw = br.ReadSingle();
                list.Add(e);
            }
            return list;
        }
    }
}
