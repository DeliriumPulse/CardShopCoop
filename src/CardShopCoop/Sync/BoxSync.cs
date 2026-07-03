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
            public Vector3 Pos;
            public float Yaw;
        }

        private static readonly System.Reflection.MethodInfo MiSetOpenClose =
            AccessTools.Method(typeof(InteractablePackagingBox_Item), "SetOpenCloseBox");

        private readonly List<Entry> _lastApplied = new List<Entry>(); // client: host truth
        private float _timer;
        private RestockManager _rm;

        public Action<List<Entry>> OnHostSnapshot;   // host: broadcast
        public Action<List<Entry>> OnClientChanges;  // client: request

        public void Reset()
        {
            _lastApplied.Clear();
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
                    if (boxes[i] != null) list.Add(Snapshot(boxes[i]));
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
                ApplyToBox(box, want);
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
                        var now = Snapshot(boxes[i]);
                        if (Differs(now, _lastApplied[i])) changed = true;
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

        private static void ApplyToBox(InteractablePackagingBox_Item box, Entry want)
        {
            try
            {
                var comp = box.m_ItemCompartment;
                int cur = comp.GetItemCount();
                if (cur != want.Count)
                {
                    if (box.IsBoxOpened() && want.Count > cur)
                    {
                        comp.SpawnItem(want.Count, spawnFromFront: true); // loader semantics: sets count
                    }
                    else if (box.IsBoxOpened() && want.Count < cur)
                    {
                        for (int k = 0; k < cur - want.Count; k++)
                        {
                            var item = comp.GetLastItem();
                            if (item == null) break;
                            comp.RemoveItem(item);
                            ItemSpawnManager.DisableItem(item);
                        }
                    }
                    else
                    {
                        comp.PreSpawnItemUpdate(want.Count); // closed box: lazy count only
                    }
                }
                if (box.IsBoxOpened() != want.IsOpen && MiSetOpenClose != null)
                {
                    try { MiSetOpenClose.Invoke(box, null); } catch { }
                }
                var t = box.transform;
                if ((t.position - want.Pos).sqrMagnitude > 0.01f
                    || Mathf.Abs(Mathf.DeltaAngle(t.eulerAngles.y, want.Yaw)) > 3f)
                {
                    t.SetPositionAndRotation(want.Pos, Quaternion.Euler(0f, want.Yaw, 0f));
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
                bw.Write((byte)((e.IsBig ? 1 : 0) | (e.IsOpen ? 2 : 0)));
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
                e.Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                e.Yaw = br.ReadSingle();
                list.Add(e);
            }
            return list;
        }
    }
}
