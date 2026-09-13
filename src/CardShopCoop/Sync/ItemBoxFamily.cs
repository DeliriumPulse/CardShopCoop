using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Item delivery/restock boxes. Content is item type + count + big, plus
    /// the warehouse-rack address when the box is stored.</summary>
    public sealed class ItemBoxFamily : IBoxFamily
    {
        public static Func<InteractablePackagingBox_Item, bool> IsLocallyCarried = _ => false;

        private readonly List<InteractablePackagingBox> _live = new List<InteractablePackagingBox>();

        public BoxFamily Family => BoxFamily.Item;

        public bool RecreateOnContentMismatch => true;

        public int ReadItemCount(InteractablePackagingBox box)
        {
            return box is InteractablePackagingBox_Item b && b.m_ItemCompartment != null
                ? b.m_ItemCompartment.GetItemCount() : 0;
        }

        public int ReadItemType(InteractablePackagingBox box)
        {
            return box is InteractablePackagingBox_Item b && b.m_ItemCompartment != null
                ? (int)b.m_ItemCompartment.GetItemType() : 0;
        }

        public bool ReconcileContent(InteractablePackagingBox box, in BoxWire w, int baseItemCount,
            int transferType, out int acceptedDelta)
        {
            acceptedDelta = 0;
            if (!(box is InteractablePackagingBox_Item b))
                return false;
            var comp = b.m_ItemCompartment;
            if (comp == null)
                return false;
            if (!EnumMap.TryFromWire(EnumKind.ItemType, w.ItemType, out int localWireType))
                return false;
            int hostCount = comp.GetItemCount();
            var hostType = comp.GetItemType();
            int requested = w.ItemCount - baseItemCount;
            if (requested == 0)
            {
                // Lid-only (or a no-op content report): apply the lid, never write the reported
                // count. This is what stops a delayed/echoed absolute from restoring items.
                BoxVisuals.EnsureOpenState(box, w.Open);
                return true;
            }

            // The type that actually moved, so a take keeps its identity even after vanilla has
            // cleared the compartment to None. A one-sided content pack maps the moved type to
            // None on the wire; merging that would build phantom None items or delete the wrong
            // type, so refuse and let the reporter keep its item.
            if (transferType < 0
                || !EnumMap.TryFromWire(EnumKind.ItemType, transferType, out int localMoveType)
                || localMoveType == (int)EItemType.None)
                return false;
            var moveType = (EItemType)localMoveType;

            var targetType = hostType;
            if (requested < 0)
            {
                // Removal: merge only against the host's matching type. A different non-empty
                // type means the reporter's view predates a refill; reject and re-assert.
                if (hostType != EItemType.None && hostType != moveType)
                    return false;
                int applied = Mathf.Min(-requested, hostCount);
                acceptedDelta = -applied;
                // Emptying clears the type (vanilla), unless the label is locked - then the
                // reporter kept it, so mirror the reporter's post-take type.
                targetType = hostCount + acceptedDelta <= 0 ? (EItemType)localWireType : hostType;
            }
            else
            {
                // Add: only into an empty or same-type compartment, bounded by capacity.
                if (hostType != EItemType.None && hostType != moveType)
                    return false;
                int capacity = comp.GetMaxItemCount();
                if (capacity <= 0)
                    capacity = hostCount + requested; // capacity unknown/unbuilt: trust the add
                int room = Mathf.Max(0, capacity - hostCount);
                acceptedDelta = Mathf.Min(requested, room);
                targetType = hostType == EItemType.None ? moveType : hostType;
            }

            if (acceptedDelta != 0)
            {
                var apply = w;
                apply.ItemType = EnumMap.ToWire(EnumKind.ItemType, (int)targetType);
                apply.ItemCount = hostCount + acceptedDelta;
                ApplyContent(b, apply);
                // ApplyContent clamps to the compartment's real slot count (an unbuilt pos list
                // can hold fewer than requested), so report what actually landed; the requester
                // rolls the remainder back instead of keeping it.
                acceptedDelta = comp.GetItemCount() - hostCount;
            }
            BoxVisuals.EnsureOpenState(box, w.Open);
            return true;
        }

        public void ApplyLidOnly(InteractablePackagingBox box, bool open)
        {
            BoxVisuals.EnsureOpenState(box, open);
        }

        public IList<InteractablePackagingBox> LiveBoxes()
        {
            var src = RestockManager.GetItemPackagingBoxList();
            _live.Clear();
            for (int i = 0; i < src.Count; i++)
                if (src[i] != null)
                    _live.Add(src[i]);
            return _live;
        }

        public void FillContent(InteractablePackagingBox box, ref BoxWire w)
        {
            var b = box as InteractablePackagingBox_Item;
            if (b == null)
                return;
            w.ItemType = EnumMap.ToWire(EnumKind.ItemType, (int)b.m_ItemCompartment.GetItemType());
            w.ItemCount = b.m_ItemCompartment.GetItemCount();
            w.Big = b.m_IsBigBox;
            w.Open = BoxVisuals.ReadOpen(b);
            FillStore(b, ref w);
        }

        public int ContentSignature(InteractablePackagingBox box)
        {
            var b = box as InteractablePackagingBox_Item;
            if (b == null)
                return 0;
            int h = 17;
            h = h * 31 + (int)b.m_ItemCompartment.GetItemType();
            h = h * 31 + b.m_ItemCompartment.GetItemCount();
            h = h * 31 + (b.m_IsBigBox ? 1 : 0);
            h = h * 31 + (BoxVisuals.ReadOpen(b) ? 1 : 0);
            var w = new BoxWire();
            FillStore(b, ref w);
            h = h * 31 + w.StoreShelfId;
            h = h * 31 + w.StoreShelf;
            h = h * 31 + w.StoreComp;
            h = h * 31 + (w.Stored ? 1 : 0);
            return h;
        }

        public bool ContentMatches(InteractablePackagingBox box, in BoxWire w)
        {
            var b = box as InteractablePackagingBox_Item;
            return b != null
                && EnumMap.TryFromWire(EnumKind.ItemType, w.ItemType, out int wireType)
                && (int)b.m_ItemCompartment.GetItemType() == wireType
                && b.m_IsBigBox == w.Big;
        }

        public InteractablePackagingBox Spawn(in BoxWire w)
        {
            EnumMap.TryFromWire(EnumKind.ItemType, w.ItemType, out int type);
            return RestockManager.SpawnPackageBoxItem((EItemType)type, w.ItemCount, w.Big);
        }

        public void DestroyBox(InteractablePackagingBox box)
        {
            if (box == null)
                return;
            var item = box as InteractablePackagingBox_Item;
            if (IsLocallyCarried(item))
                CoopCore.ForceExitHoldBox(box);
            UnhookIfStored(item); // a stored box destroyed without unhooking leaks its rack slot
            BoxVisuals.Forget(box);
            BoxPlacement.ClearThrow(box);
            box.OnDestroyed();
        }

        /// <summary>Detach a box from its warehouse rack slot before destroying it, so the
        /// slot does not leak an occupant that no longer exists.</summary>
        public static void UnhookIfStored(InteractablePackagingBox_Item box)
        {
            try
            {
                if (box == null || !box.m_IsStored)
                    return;
                var comp = box.GetBoxStoredCompartment();
                if (comp != null)
                    comp.RemoveBox(box);
                box.m_IsStored = false;
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        public void ApplyState(InteractablePackagingBox box, in BoxWire w, bool isOwner)
        {
            var b = box as InteractablePackagingBox_Item;
            if (b == null)
                return;
            if (w.Possession != BoxPossession.Removed)
                ApplyContent(b, w);
            switch (w.Possession)
            {
                case BoxPossession.Held:
                    if (isOwner)
                        return;
                    BoxLifecycle.Apply(box, w.Possession);
                    // Hides the box, its world label, and its price tag together.
                    BoxVisuals.SetVisible(box, false);
                    break;
                case BoxPossession.Placing:
                    if (isOwner)
                        return;
                    BoxLifecycle.Apply(box, w.Possession);
                    BoxVisuals.EnsureOpenState(box, w.Open);
                    BoxVisuals.SetVisible(box, true);
                    BoxPlacement.SetPlacementIntent(box, true,
                        BoxPlacement.ResolvePlacementAvatar((byte)(w.OwnerConn == 0 ? 1 : 2), w.OwnerConn));
                    break;
                case BoxPossession.Free:
                    if (isOwner)
                        return;
                    BoxVisuals.SetVisible(box, true);
                    BoxVisuals.EnsureOpenState(box, w.Open);
                    BoxPlacement.ClearPlacementIntent(box);
                    if (w.IsStored)
                    {
                        ApplyStored(b, w);
                        break;
                    }
                    if (b.m_IsStored)
                        UnhookIfStored(b); // host took it off the rack
                    BoxLifecycle.ApplyEnabled(b, true);
                    if (BoxPlacement.IsSanePose(w.Pos, w.Yaw))
                        BoxPlacement.ApplyPhysicsPose(box, w.Pos, w.Yaw);
                    if (b.m_Rigidbody != null)
                    {
                        b.m_Rigidbody.velocity = w.Velocity;
                        b.m_Rigidbody.angularVelocity = w.AngularVelocity;
                        b.m_Rigidbody.WakeUp();
                    }
                    break;
            }
        }

        /// <summary>Apply authoritative item-box contents in place. Closed/stored boxes only
        /// need their deferred count updated; open boxes need their pooled item instances
        /// rebuilt so the visible contents and compartment count agree.</summary>
        private static void ApplyContent(InteractablePackagingBox_Item box, in BoxWire w)
        {
            if (!EnumMap.TryFromWire(EnumKind.ItemType, w.ItemType, out int localType))
                return;
            var comp = box.m_ItemCompartment;
            if (comp == null)
                return;
            var type = (EItemType)localType;
            int count = Mathf.Max(0, w.ItemCount);
            bool open = BoxVisuals.ReadOpen(box);
            bool typeChanged = comp.GetItemType() != type;
            bool countChanged = comp.GetItemCount() != count;
            if (!typeChanged && !countChanged)
                return;

            try
            {
                if (open)
                {
                    // An open compartment owns actual pooled Item objects. Remove those
                    // objects through the game's API before changing the type or respawning,
                    // otherwise the list count and visible stack diverge.
                    HandEscrow.BeginSuppressNote();
                    try
                    {
                        while (comp.GetItemCount() > 0)
                        {
                            var item = comp.TakeItemToHand();
                            if (item == null)
                                break;
                            item.DisableItem();
                        }
                    }
                    finally { HandEscrow.EndSuppressNote(); }
                }
                box.SetItemType(type);
                comp.SetCompartmentItemType(type);
                comp.CalculatePositionList();
                if (open)
                {
                    comp.SpawnItem(count, spawnFromFront: false);
                    BoxFields.ItemAmountToSpawn?.SetValue(box, count);
                }
                else
                {
                    comp.PreSpawnItemUpdate(count);
                    BoxFields.ItemAmountToSpawn?.SetValue(box, count);
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("ItemBoxFamily content apply: " + e.Message);
            }
        }

        public bool TryReadLocal(InteractablePackagingBox box, out BoxPossession possession,
            out Vector3 pos, out float yaw, out Vector3 velocity, out Vector3 angularVelocity,
            out bool stored)
        {
            var b = box as InteractablePackagingBox_Item;
            possession = BoxPossession.Free;
            pos = BoxPlacement.PhysicsPosition(box);
            yaw = BoxPlacement.PhysicsRotation(box).eulerAngles.y;
            velocity = b != null && b.m_Rigidbody != null ? b.m_Rigidbody.velocity : Vector3.zero;
            angularVelocity = b != null && b.m_Rigidbody != null ? b.m_Rigidbody.angularVelocity : Vector3.zero;
            stored = b != null && b.m_IsStored;
            if (b == null)
                return true;
            // Any holder counts as Held, not just the local player: a worker carrying a
            // restock box must keep it hidden on peers instead of dragging a loose copy.
            if (IsLocallyCarried(b) || BoxShared.IsHeldByAnyone(b))
                possession = BoxPossession.Held;
            else if (b.GetIsMovingObject())
                possession = BoxPossession.Placing;
            return true;
        }

        // ---------------- storage ----------------

        /// <summary>Fill the box's warehouse-rack address when it is stored. The host
        /// assigns the stable rack id; a client resolves the host's id back locally.</summary>
        private static void FillStore(InteractablePackagingBox_Item b, ref BoxWire w)
        {
            try
            {
                w.Stored = b.m_IsStored;
                if (!b.m_IsStored)
                    return;
                var sc = b.GetBoxStoredCompartment();
                if (sc == null)
                    return;
                w.StoreShelf = (byte)sc.GetWarehouseIndex();
                w.StoreComp = (byte)sc.GetIndex();
                var shelf = sc.GetWarehouseShelf();
                if (shelf == null)
                    return;
                if (CoopCore.Role == CoopRole.Host)
                    w.StoreShelfId = PlacedObjectIdentity.AssignHost(shelf);
                else
                    PlacedObjectIdentity.TryGet(shelf, out w.StoreShelfId);
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        /// <summary>Run the game's own store recipe (physics off, then DispenseItem) so the
        /// compartment registration mirrors too. A box already at the requested slot is left
        /// alone; a box on a different slot is unhooked and moved; a rejected store is pinned
        /// kinematic at the host pose so it reads shelved instead of dropping loose, and the
        /// next snapshot retries.</summary>
        private static void ApplyStored(InteractablePackagingBox_Item b, in BoxWire w)
        {
            // Cheap path FIRST, before any scene search: if the box already sits at the
            // requested slot, the host pose is already the slot's. Without this, every
            // snapshot re-resolved the rack (a full FindObjectOfType) for every stored box,
            // which stalled the client on every box change.
            if (b.m_IsStored)
            {
                var current = b.GetBoxStoredCompartment();
                if (current != null
                    && current.GetWarehouseIndex() == w.StoreShelf
                    && current.GetIndex() == w.StoreComp)
                    return; // already correctly stored; the slot owns the pose
            }
            var rack = ResolveWarehouseCompartment(w.StoreShelfId, w.StoreShelf, w.StoreComp);
            if (rack == null)
            {
                BoxLifecycle.ApplyEnabled(b, false);
                if (BoxPlacement.IsSanePose(w.Pos, w.Yaw))
                    BoxPlacement.ApplyPhysicsPose(b, w.Pos, w.Yaw);
                return;
            }
            if (b.m_IsStored)
                UnhookIfStored(b); // registered on a different slot; move it
            try
            {
                if (!b.gameObject.activeSelf)
                    b.gameObject.SetActive(true);
                // DispenseItem rejects a genuinely empty box; seed the closed count. Rack
                // storage only ever holds non-empty boxes, so this restores content on a
                // mirror that spawned without it.
                if (b.m_ItemCompartment.GetItemCount() <= 0 && w.ItemCount > 0)
                    ApplyClosedCount(b, w.ItemCount);
                BoxLifecycle.ApplyEnabled(b, false);
                b.DispenseItem(isPlayer: false, rack);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ItemBoxFamily store: " + e.Message); }
            if (!b.m_IsStored)
            {
                BoxShared.DebugLog("store-fail",
                    $"box id {w.Id} not stored at rack {w.StoreShelf}/{w.StoreComp} ({StoreRejectReason(b, rack)})",
                    w.Id, 5f);
                BoxLifecycle.ApplyEnabled(b, false); // stay kinematic at the host pose, not loose
                if (BoxPlacement.IsSanePose(w.Pos, w.Yaw))
                    BoxPlacement.ApplyPhysicsPose(b, w.Pos, w.Yaw);
            }
        }

        /// <summary>Diagnostic: which of DispenseItem's checks rejected the store.</summary>
        private static string StoreRejectReason(InteractablePackagingBox_Item b, ShelfCompartment rack)
        {
            try
            {
                if (rack == null)
                    return "no rack resolved";
                if (!b.CanPickup())
                    return "box is lerping";
                if (!rack.m_CanPutBox)
                    return "compartment does not accept boxes";
                var type = b.m_ItemCompartment.GetItemType();
                if (type == EItemType.None || b.m_ItemCompartment.GetItemCount() <= 0)
                    return $"empty box (type={type} count={b.m_ItemCompartment.GetItemCount()})";
                if (rack.CheckBoxType(b.m_IsBigBox) != b.m_IsBigBox)
                    return "box size mismatch";
                if (!rack.HasEnoughSlot())
                    return "compartment slot full";
                if (!rack.CheckBoxItemType(b.GetItemType()))
                    return "item type mismatch";
                return "unknown";
            }
            catch (Exception e) { Swallow.Log(e); return "probe threw: " + e.Message; }
        }

        private static ShelfManager _sm;

        /// <summary>Cached scene lookup. Unity's fake-null self-invalidates across a scene
        /// change, so this needs no explicit reset. FindObjectOfType per stored box per
        /// snapshot was a per-change client stall.</summary>
        private static ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        /// <summary>Resolve the mirrored warehouse compartment from the host's stable rack
        /// id, falling back to the shelf/compartment indices.</summary>
        private static ShelfCompartment ResolveWarehouseCompartment(ushort shelfId, int shelfIdx, int compIdx)
        {
            try
            {
                var sm = Sm();
                if (sm == null)
                    return null;
                if (shelfId != 0
                    && PlacedObjectIdentity.TryResolve(sm, 1, shelfId, out var identified)
                    && identified is WarehouseShelf resolved)
                    return resolved.GetWarehouseCompartment(compIdx);
                var list = sm.m_WarehouseShelfList;
                for (int i = 0; i < list.Count; i++)
                {
                    var ws = list[i];
                    if (ws == null || ws.GetIndex() != shelfIdx)
                        continue;
                    return ws.GetWarehouseCompartment(compIdx);
                }
            }
            catch (System.Exception e) { Swallow.Log(e); }
            return null;
        }

        /// <summary>Apply a closed-box item count, initializing the compartment position
        /// list first when an adopted mirror never ran FillBoxWithItem (else the count
        /// clamps to zero).</summary>
        private static void ApplyClosedCount(InteractablePackagingBox_Item box, int count)
        {
            try
            {
                var comp = box.m_ItemCompartment;
                if (comp.GetItemCount() == count)
                    return;
                bool storedOrClosed = true;
                try
                {
                    storedOrClosed = box.m_IsStored || !box.IsBoxOpened();
                }
                catch (System.Exception e) { Swallow.Log(e); }
                if (storedOrClosed && count > 0 && comp.GetItemPosListCount() <= 0)
                {
                    try
                    {
                        comp.SetCompartmentItemType(comp.GetItemType());
                        comp.CalculatePositionList();
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                }
                comp.PreSpawnItemUpdate(count);
                BoxFields.ItemAmountToSpawn?.SetValue(box, count);
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }
    }
}
