using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors loose delivery/packaging boxes (item boxes) between host and client.
    /// The host's RestockManager list is the single source of truth, broadcast by index
    /// every 1.5s; the client reconciles its own live list to match, spawning via the
    /// game's own RestockManager.SpawnPackageBoxItem (the exact save-load recipe) and
    /// despawning via the box's own OnDestroyed. Client-side changes (dispensing to a
    /// shelf, carrying, trashing) are detected against the last applied state and sent
    /// as requests the host applies and echoes. The joiner's restock ORDERS are forwarded
    /// separately (GamePatches) so deliveries always spawn host-side, officially.
    /// </summary>
    public class BoxSync
    {
        public struct Entry
        {
            public int Type;
            public int Count;
            public bool IsBig;
            public bool IsOpen;
            public bool Carried; // in someone's hands: position is transient, don't apply
            public bool Settled; // physics at rest: only settled poses are applied
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

        private readonly List<Entry> _lastApplied = new List<Entry>(); // client: host truth
        private readonly HashSet<int> _carriedLastTick = new HashSet<int>();
        private readonly HashSet<int> _remoteCarried = new HashSet<int>();   // host: client-held boxes
        private readonly Dictionary<int, double> _recentlyReleased = new Dictionary<int, double>(); // client: ignore stale carried echoes
        private readonly Dictionary<int, double> _locallyTouched = new Dictionary<int, double>();   // client: my recent edits beat stale echoes
        private readonly HashSet<int> _hostCarriedLastTick = new HashSet<int>();                    // host: own-carry transitions
        private readonly Dictionary<int, double> _hostRecentlyReleased = new Dictionary<int, double>(); // host: just set it down; stale client reports must not stomp it
        private float _timer;
        private int _lastHostHash;
        private float _hostHeal;
        private readonly List<Entry> _reportBuf = new List<Entry>();
        private RestockManager _rm;

        public Action<List<Entry>> OnHostSnapshot;   // host: broadcast
        public Action<List<Entry>> OnClientChanges;  // client: request
        public Action<int, int> OnLocalRemoved;      // client: (index, type) I trashed a box

        /// <summary>Wired by CoopCore to the OnDestroyed patch: a box was destroyed by
        /// LOCAL gameplay (trash bin, storage) - not by sync reconciliation.</summary>
        public static Action<InteractablePackagingBox_Item> LocalBoxDestroyed;

        /// <summary>True while sync code itself destroys/spawns boxes, so the OnDestroyed
        /// patch doesn't mistake reconciliation for a player throwing boxes away.</summary>
        public static bool ApplyingRemote;

        public void Reset()
        {
            _lastApplied.Clear();
            _carriedLastTick.Clear();
            _remoteCarried.Clear();
            _recentlyReleased.Clear();
            _locallyTouched.Clear();
            _hostCarriedLastTick.Clear();
            _hostRecentlyReleased.Clear();
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
            return new Entry
            {
                Type = (int)box.m_ItemCompartment.GetItemType(),
                Count = box.m_ItemCompartment.GetItemCount(),
                IsBig = box.m_IsBigBox,
                IsOpen = box.IsBoxOpened(),
                Carried = IsLocallyCarried(box),
                Settled = settled,
                Pos = box.transform.position,
                Yaw = box.transform.eulerAngles.y,
            };
        }

        private static bool Differs(Entry a, Entry b)
        {
            return a.Type != b.Type || a.Count != b.Count || a.IsBig != b.IsBig || a.IsOpen != b.IsOpen
                || (a.Pos - b.Pos).sqrMagnitude > 0.01f || Mathf.Abs(Mathf.DeltaAngle(a.Yaw, b.Yaw)) > 3f;
        }

        // ---------------- host ----------------

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
                    if (IsLocallyCarried(scan[i]))
                    {
                        if (_hostCarriedLastTick.Add(i)) force = true;
                    }
                    else if (_hostCarriedLastTick.Remove(i))
                    {
                        force = true;
                        _hostRecentlyReleased[i] = Time.realtimeSinceStartupAsDouble;
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
                    if (_remoteCarried.Contains(i)) e.Carried = true; // a client holds it
                    list.Add(e);
                }
                // skip identical snapshots (boxes sit still most of the time); a slow
                // heal broadcast still repairs any client that missed one
                int hash = 17;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    hash = hash * 31 + e.Type;
                    hash = hash * 31 + e.Count;
                    hash = hash * 31 + ((e.IsBig ? 1 : 0) | (e.IsOpen ? 2 : 0) | (e.Carried ? 4 : 0) | (e.Settled ? 8 : 0));
                    hash = hash * 31 + (int)(e.Pos.x * 8f);
                    hash = hash * 31 + (int)(e.Pos.z * 8f);
                }
                _hostHeal += 1.5f;
                if (hash == _lastHostHash && _hostHeal < 10f) return;
                _lastHostHash = hash;
                _hostHeal = 0f;
                OnHostSnapshot?.Invoke(list);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync host: " + e.Message); }
        }

        /// <summary>Host: a client asked for box states (their local edits).</summary>
        public void HostApplyRequest(List<Entry> entries)
        {
            var boxes = LiveBoxes();
            for (int i = 0; i < entries.Count && i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null) continue;
                // type must match: index may have shifted between snapshot and request
                if ((int)box.m_ItemCompartment.GetItemType() != entries[i].Type) continue;
                if (IsLocallyCarried(box)) continue; // never stomp a box in the host's hands
                // just set down: a report the client built while we still carried it is
                // stale by definition - the race that teleported boxes mid-restock
                if (_hostRecentlyReleased.TryGetValue(i, out double rel)
                    && Time.realtimeSinceStartupAsDouble - rel < 6.0) continue;
                if (entries[i].Carried) _remoteCarried.Add(i);
                else _remoteCarried.Remove(i);
                ApplyToBox(box, entries[i]);
            }
            // fan the change out to everyone NOW - with 3+ players the other
            // clients otherwise wait out the periodic tick and the hash gate.
            // Exactly one period: a larger sentinel made the keep-the-phase
            // decrement fire EVERY frame for seconds (the post-throw jitter)
            _timer = 1.5f;
            _lastHostHash = 0;
        }

        private double _removalWindowStart;
        private int _removalWindowCount;

        /// <summary>Host: a client trashed a box - destroy the real one so the next
        /// broadcast doesn't resurrect it at its old spot. Flood-guarded: a client
        /// whose world is reloading can echo its ENTIRE box population as "trashed"
        /// (old clients did exactly that) - no human trashes 5 boxes in 2 seconds.</summary>
        public void HostApplyRemoval(int index, int type)
        {
            double nowT = Time.realtimeSinceStartupAsDouble;
            if (nowT - _removalWindowStart > 2.0)
            {
                _removalWindowStart = nowT;
                _removalWindowCount = 0;
            }
            if (++_removalWindowCount > 4)
            {
                if (_removalWindowCount == 5)
                    CoopPlugin.Log.LogWarning("ignoring box-removal flood from a client (reload echo, not gameplay)");
                return;
            }
            var boxes = LiveBoxes();
            if (index < 0 || index >= boxes.Count || boxes[index] == null) return;
            if ((int)boxes[index].m_ItemCompartment.GetItemType() != type) return;
            if (IsLocallyCarried(boxes[index])) return;
            ApplyingRemote = true;
            try { boxes[index].OnDestroyed(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync removal: " + e.Message); }
            finally { ApplyingRemote = false; }
            _remoteCarried.Clear(); // indices shifted; holds re-assert within a tick
        }

        /// <summary>Host: the host player destroyed a box locally - the index-keyed
        /// client-held markers have all shifted.</summary>
        public void HostNotifyLocalDestroyed()
        {
            _remoteCarried.Clear();
        }

        /// <summary>Client: the local player destroyed a box (trash bin etc.). Tell the
        /// host to remove the real one and stop tracking it, so no ghost report or stale
        /// echo brings it back.</summary>
        public void NotifyLocalDestroyed(InteractablePackagingBox_Item box)
        {
            int idx = LiveBoxes().IndexOf(box);
            if (idx < 0) return;
            int type = 0;
            try { type = (int)box.m_ItemCompartment.GetItemType(); } catch { }
            if (idx < _lastApplied.Count) _lastApplied.RemoveAt(idx);
            _carriedLastTick.Clear();   // index-keyed trackers all shifted;
            _locallyTouched.Clear();    // they re-establish within a tick
            _recentlyReleased.Clear();
            OnLocalRemoved?.Invoke(idx, type);
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
            var boxes = LiveBoxes();

            // shrink extras (from the end, so indices stay aligned)
            for (int i = boxes.Count - 1; i >= hostList.Count; i--)
            {
                try { if (boxes[i] != null) boxes[i].OnDestroyed(); } catch { }
            }
            // grow / fix / update
            for (int i = 0; i < hostList.Count; i++)
            {
                var want = hostList[i];
                InteractablePackagingBox_Item box = i < boxes.Count ? boxes[i] : null;
                if (box != null && (box.m_ItemCompartment.GetItemType() != (EItemType)want.Type
                                    || box.m_IsBigBox != want.IsBig))
                {
                    try { box.OnDestroyed(); } catch { }
                    box = null;
                    boxes = LiveBoxes(); // list mutated
                }
                if (box == null)
                {
                    try
                    {
                        box = RestockManager.SpawnPackageBoxItem((EItemType)want.Type, want.Count, want.IsBig);
                        boxes = LiveBoxes();
                    }
                    catch (Exception e)
                    {
                        CoopPlugin.Log.LogWarning("BoxSync spawn: " + e.Message);
                        continue;
                    }
                }
                // a box in MY hands is mine until I put it down; a box in the HOST's
                // hands has a transient position we don't copy
                if (IsLocallyCarried(box)) continue;
                double now = Time.realtimeSinceStartupAsDouble;
                // a stale "carried" echo about a box I JUST released must not hide it
                if (want.Carried && _recentlyReleased.TryGetValue(i, out double t) && now - t < 6.0)
                    continue;
                // my own recent edits (took an item, kicked it) win over stale echoes;
                // my report reaches the host and the next echo agrees. VISIBILITY is
                // exempt: someone else's pickup/set-down must show here immediately,
                // or their set-down box stays invisible to me for the whole window
                if (_locallyTouched.TryGetValue(i, out double touched) && now - touched < 6.0)
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
                var scan = LiveBoxes();
                int n = Mathf.Min(scan.Count, _lastApplied.Count);
                for (int i = 0; i < n; i++)
                {
                    if (scan[i] == null) continue;
                    if (IsLocallyCarried(scan[i]))
                    {
                        if (_carriedLastTick.Add(i)) force = true; // pickup transition
                    }
                    else if (_carriedLastTick.Remove(i))
                    {
                        force = true; // set-down transition
                        _recentlyReleased[i] = Time.realtimeSinceStartupAsDouble;
                    }
                }
            }
            catch { }
            _timer += dt;
            if (!force && _timer < 1.5f) return;
            if (_timer >= 1.5f) _timer -= 1.5f;
            try
            {
                var boxes = LiveBoxes();
                bool changed = force;
                _reportBuf.Clear(); // serialized synchronously by the callback; safe to reuse
                var list = _reportBuf;
                double nowT = Time.realtimeSinceStartupAsDouble;
                for (int i = 0; i < _lastApplied.Count; i++)
                {
                    if (i < boxes.Count && boxes[i] != null)
                    {
                        // while I'M carrying it: tell the host (so everyone else hides
                        // their copy) but keep reporting the last settled position
                        if (IsLocallyCarried(boxes[i]))
                        {
                            var held = _lastApplied[i];
                            held.Carried = true;
                            list.Add(held);
                            continue;
                        }
                        // hidden because ANOTHER player carries it: no local truth to
                        // report (its transform is parked at the hide spot, contents
                        // stale) - UNLESS I just set it down myself: that report IS the
                        // set-down, and skipping it deadlocks the box as carried-forever
                        if (_lastApplied[i].Carried
                            && !(_recentlyReleased.TryGetValue(i, out double rr) && nowT - rr < 6.0))
                        {
                            list.Add(_lastApplied[i]);
                            continue;
                        }
                        var now = Snapshot(boxes[i]);
                        if (Differs(now, _lastApplied[i]))
                        {
                            changed = true;
                            _locallyTouched[i] = nowT;
                        }
                        list.Add(now);
                    }
                    else
                    {
                        // list shrank without OnDestroyed telling us (trash is forwarded
                        // separately) - stop and let the next host snapshot re-align
                        break;
                    }
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
                bw.Write(e.Type);
                bw.Write((ushort)Mathf.Clamp(e.Count, 0, ushort.MaxValue));
                bw.Write((byte)((e.IsBig ? 1 : 0) | (e.IsOpen ? 2 : 0) | (e.Carried ? 4 : 0) | (e.Settled ? 8 : 0)));
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
                var e = new Entry { Type = br.ReadInt32(), Count = br.ReadUInt16() };
                byte f = br.ReadByte();
                e.IsBig = (f & 1) != 0;
                e.IsOpen = (f & 2) != 0;
                e.Carried = (f & 4) != 0;
                e.Settled = (f & 8) != 0;
                e.Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                e.Yaw = br.ReadSingle();
                list.Add(e);
            }
            return list;
        }
    }
}
