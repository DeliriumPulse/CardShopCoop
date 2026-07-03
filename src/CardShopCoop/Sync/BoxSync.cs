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
        private float _timer;
        private RestockManager _rm;

        public Action<List<Entry>> OnHostSnapshot;   // host: broadcast
        public Action<List<Entry>> OnClientChanges;  // client: request

        public void Reset()
        {
            _lastApplied.Clear();
            _carriedLastTick.Clear();
            _remoteCarried.Clear();
            _recentlyReleased.Clear();
            _locallyTouched.Clear();
            _timer = 0f;
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
            return new Entry
            {
                Type = (int)box.m_ItemCompartment.GetItemType(),
                Count = box.m_ItemCompartment.GetItemCount(),
                IsBig = box.m_IsBigBox,
                IsOpen = box.IsBoxOpened(),
                Carried = IsLocallyCarried(box),
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
            _timer += dt;
            if (_timer < 1.5f) return;
            _timer = 0f;
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
                if (entries[i].Carried) _remoteCarried.Add(i);
                else _remoteCarried.Remove(i);
                ApplyToBox(box, entries[i]);
            }
        }

        // ---------------- client ----------------

        /// <summary>Client: reconcile the live box population to the host's snapshot.</summary>
        public void ClientApply(List<Entry> hostList)
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
                // my report reaches the host and the next echo agrees
                if (_locallyTouched.TryGetValue(i, out double touched) && now - touched < 6.0)
                    continue;
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
            _timer += dt;
            if (_timer < 1.5f) return;
            _timer = 0f;
            try
            {
                var boxes = LiveBoxes();
                bool changed = false;
                var list = new List<Entry>(_lastApplied.Count);
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
                            if (_carriedLastTick.Add(i)) changed = true; // pickup transition
                            list.Add(held);
                            continue;
                        }
                        if (_carriedLastTick.Remove(i))
                        {
                            changed = true; // set-down transition
                            _recentlyReleased[i] = Time.realtimeSinceStartupAsDouble;
                        }
                        var now = Snapshot(boxes[i]);
                        if (Differs(now, _lastApplied[i]))
                        {
                            changed = true;
                            _locallyTouched[i] = Time.realtimeSinceStartupAsDouble;
                        }
                        list.Add(now);
                    }
                    else
                    {
                        // vanished locally (trashed/emptied) - report as empty at same spot
                        var gone = _lastApplied[i];
                        gone.Count = 0;
                        list.Add(gone);
                        changed = true;
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
                if (applyPosition)
                {
                    var t = box.transform;
                    if ((t.position - want.Pos).sqrMagnitude > 0.01f
                        || Mathf.Abs(Mathf.DeltaAngle(t.eulerAngles.y, want.Yaw)) > 3f)
                    {
                        t.SetPositionAndRotation(want.Pos, Quaternion.Euler(0f, want.Yaw, 0f));
                        ObjMoveSync.SyncTagGroup(t); // box price tags ride in their own group
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
                bw.Write((byte)((e.IsBig ? 1 : 0) | (e.IsOpen ? 2 : 0) | (e.Carried ? 4 : 0)));
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
                e.Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                e.Yaw = br.ReadSingle();
                list.Add(e);
            }
            return list;
        }
    }
}
