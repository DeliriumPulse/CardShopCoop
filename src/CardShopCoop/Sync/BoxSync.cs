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

        private static bool IsBeingHeld(InteractablePackagingBox_Item box)
        {
            try { return FiBeingHold?.GetValue(box) is bool b && b; } catch { return false; }
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
                var rb = box.GetComponentInChildren<Rigidbody>();
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

        /// <summary>Data-only count apply via the closed-box path (stored boxes are
        /// always closed): safe before OR after the box is slotted into a rack.</summary>
        private static void ApplyClosedCount(InteractablePackagingBox_Item box, int count)
        {
            try
            {
                var comp = box.m_ItemCompartment;
                if (comp.GetItemCount() == count) return;
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
                var list = new List<Entry>(Mathf.Min(boxes.Count, 250));
                for (int i = 0; i < boxes.Count && list.Count < 250; i++)
                {
                    if (boxes[i] == null) continue;
                    var e = Snapshot(boxes[i]);
                    e.Id = HostIdFor(boxes[i]);
                    if (_remoteCarried.Contains(e.Id)) e.Carried = true; // a client holds it
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
                // type sanity: a mangled or ancient request must not restyle a box
                if ((int)box.m_ItemCompartment.GetItemType() != e.Type) continue;
                if (IsLocallyCarried(box) || IsBeingHeld(box)) continue; // never stomp a box in the host's or a WORKER's hands
                // just set down: a report the client built while we still carried it is
                // stale by definition - the race that teleported boxes mid-restock
                if (_hostRecentlyReleased.TryGetValue(e.Id, out double rel)
                    && Time.realtimeSinceStartupAsDouble - rel < 6.0) continue;
                if (e.Carried) _remoteCarried.Add(e.Id);
                else _remoteCarried.Remove(e.Id);
                ApplyToBox(box, e);
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
        /// client's removal budget for the current 2s window is spent. No human trashes
        /// 5 boxes in 2 seconds - but a client whose game is re-running its world-load
        /// teardown echoes its ENTIRE box population, all three lists, as "trashed"
        /// (first field incident: 250 boxes in 10ms). Per-connection, so one player's
        /// flood never eats another player's legitimate trash.</summary>
        public static bool RemovalFlooded(int connId, string channel)
        {
            double nowT = Time.realtimeSinceStartupAsDouble;
            if (!_remWindowStart.TryGetValue(connId, out double start) || nowT - start > 2.0)
            {
                _remWindowStart[connId] = nowT;
                _remWindowCount[connId] = 0;
            }
            int c = _remWindowCount[connId] = _remWindowCount[connId] + 1;
            if (c <= 4) return false;
            if (c == 5 || c % 100 == 0)
                CoopPlugin.Log.LogWarning($"ignoring {channel} removal flood from client {connId} (reload echo, not gameplay)");
            return true;
        }

        /// <summary>Host: a client trashed a box - destroy the real one so the next
        /// broadcast doesn't resurrect it at its old spot.</summary>
        public void HostApplyRemoval(int id, int type, int connId)
        {
            if (RemovalFlooded(connId, "item-box")) return;
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
            try { ClientApplyInner(hostList); }
            finally { ApplyingRemote = false; }
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
            // index-shifted neighbor (the "vanished out of my hands" bug)
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
                        CoopPlugin.Log.LogWarning($"host removed the box in your hands (id {id}, {(EItemType)(int)box.m_ItemCompartment.GetItemType()}) - it was consumed host-side");
                    _idOf.Remove(box);
                    UnhookIfStored(box);
                    try { box.OnDestroyed(); } catch { }
                }
                _byId.Remove(id);
                _carriedLastTick.Remove(id);
                _locallyTouched.Remove(id);
                _recentlyReleased.Remove(id);
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
                    if (box == null)
                    {
                        // dead or unmapped locally: parrot the host truth; if we trashed
                        // it, the BoxRemoved message (sent separately) settles it
                        list.Add(truth);
                        continue;
                    }
                    // while I'M carrying it: tell the host (so everyone else hides
                    // their copy) but keep reporting the last settled position
                    if (IsLocallyCarried(box))
                    {
                        var held = truth;
                        held.Carried = true;
                        held.Stored = false; // just took it off a rack: a stale stored
                        list.Add(held);      // flag would keep the host's copy slotted
                        continue;
                    }
                    // host says STORED: the rack slot is host-authoritative. Report the
                    // truth VERBATIM - if our local store mirror was rejected (full slot,
                    // stale type), reporting our not-stored state would command the host
                    // to yank its legitimately-stored box off the rack (revert war). The
                    // carried branch above is the one legitimate exit: the player took it
                    if (truth.Stored)
                    {
                        list.Add(truth);
                        continue;
                    }
                    // hidden because ANOTHER player carries it: no local truth to
                    // report (its transform is parked at the hide spot, contents
                    // stale) - UNLESS I just set it down myself: that report IS the
                    // set-down, and skipping it deadlocks the box as carried-forever
                    if (truth.Carried
                        && !(_recentlyReleased.TryGetValue(truth.Id, out double rr) && nowT - rr < 6.0))
                    {
                        list.Add(truth);
                        continue;
                    }
                    var now = Snapshot(box);
                    now.Id = truth.Id;
                    if (Differs(now, truth))
                    {
                        changed = true;
                        _locallyTouched[truth.Id] = nowT;
                    }
                    list.Add(now);
                }
                if (changed) OnClientChanges?.Invoke(list);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync client: " + e.Message); }
        }

        // ---------------- shared apply ----------------

        private static void ApplyToBox(InteractablePackagingBox_Item box, Entry want, bool applyPosition = true)
        {
            try
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
                    // give up, fall through to the loose apply below - visible and
                    // grabbable beats eternal churn (a later snapshot retries on change)
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
                            // DispenseItem returns void and eats failures: count and give up
                            box.SetPhysicsEnabled(true); // don't leave a loose box frozen
                            _storeFails.TryGetValue(want.Id, out int fails);
                            _storeFails[want.Id] = ++fails;
                            if (fails >= 4)
                            {
                                _storeGaveUp.Add(want.Id);
                                CoopPlugin.Log.LogWarning($"BoxSync store: rack {want.StoreShelf}/{want.StoreComp} keeps rejecting box id {want.Id} (slot full or size/type mismatch) - leaving it loose");
                            }
                        }
                        return; // still trying (or no rack yet); don't apply a loose pose mid-attempt
                    }
                    // gave up: fall through and render it as a normal loose box
                }
                else
                {
                    // host says NOT stored: clear any give-up state so a future store
                    // (rack slot freed up, box re-placed) is attempted fresh
                    if (_storeGaveUp.Count > 0) _storeGaveUp.Remove(want.Id);
                    if (_storeFails.Count > 0) _storeFails.Remove(want.Id);
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

                // open/close FIRST: content semantics depend on the resulting state
                if (box.IsBoxOpened() != want.IsOpen && MiSetOpenClose != null)
                {
                    try { MiSetOpenClose.Invoke(box, null); } catch { }
                }

                var comp = box.m_ItemCompartment;
                int cur = comp.GetItemCount();
                if (cur != want.Count)
                {
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
                if (applyPosition && want.Settled)
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
                            // teleported mid-air hangs there frozen until poked
                            var rb = box.GetComponentInChildren<Rigidbody>();
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
            bw.Write((byte)Mathf.Min(entries.Count, 250));
            for (int i = 0; i < entries.Count && i < 250; i++)
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
            int n = br.ReadByte();
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
