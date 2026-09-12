using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Furniture delivery boxes. Content is the boxed object's identity
    /// (object type + kind + stable placed-object index). A client box is created by
    /// boxing up its mirrored placed object, not by a raw spawn.</summary>
    public sealed class FurnitureBoxFamily : IBoxFamily
    {
        private const byte GenericKind = 15;
        private const byte Unresolved = 255;

        private readonly List<InteractablePackagingBox> _live = new List<InteractablePackagingBox>();

        public BoxFamily Family => BoxFamily.Furniture;

        public bool RecreateOnContentMismatch => true;

        public int ReadItemCount(InteractablePackagingBox box)
        {
            return 0;
        }

        public int ReadItemType(InteractablePackagingBox box)
        {
            return 0;
        }

        public bool ReconcileContent(InteractablePackagingBox box, in BoxWire w, int baseItemCount,
            int transferType, out int acceptedDelta)
        {
            // Furniture-box content is the boxed object's identity, owned by the box ops; the
            // owner-path ApplyState handles it, so a non-owner content report is a no-op here.
            acceptedDelta = 0;
            return false;
        }

        public void ApplyLidOnly(InteractablePackagingBox box, bool open)
        {
            BoxVisuals.EnsureOpenState(box, open);
        }

        public IList<InteractablePackagingBox> LiveBoxes()
        {
            var src = RestockManager.GetShelfPackagingBoxList();
            _live.Clear();
            for (int i = 0; i < src.Count; i++)
                if (src[i] != null)
                    _live.Add(src[i]);
            return _live;
        }

        public static InteractableObject BoxedObject(InteractablePackagingBox box)
        {
            try
            {
                return BoxFields.BoxedObject?.GetValue(box) as InteractableObject;
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        private static ShelfManager _sm;

        public static ShelfManager Sm()
        {
            // Cached: Unity's fake-null self-invalidates across a scene change, so a stale
            // reference needs no explicit reset. FindObjectOfType per box was a snapshot stall.
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        public static IList KindList(ShelfManager sm, byte kind)
        {
            if (kind < GenericKind)
                return PopulationSync.GetList(sm, kind);
            if (kind == GenericKind)
                return BoxFields.GenericList?.GetValue(sm) as IList;
            return null;
        }

        public static int Fnv(string s)
        {
            unchecked
            {
                int h = (int)2166136261;
                for (int i = 0; i < s.Length; i++)
                    h = (h ^ s[i]) * 16777619;
                return h;
            }
        }

        public static bool TryFindObjKey(InteractableObject obj, out byte kind, out int idx)
        {
            kind = Unresolved;
            idx = -1;
            var sm = Sm();
            if (sm == null || obj == null)
                return false;
            for (byte k = 0; k <= GenericKind; k++)
            {
                var list = KindList(sm, k);
                if (list == null || list.IndexOf(obj) < 0)
                    continue;
                if (CoopCore.Role == CoopRole.Host)
                    idx = PlacedObjectIdentity.AssignHost(obj);
                else if (!PlacedObjectIdentity.TryGet(obj, out ushort knownId))
                    continue;
                else
                    idx = knownId;
                kind = k;
                return true;
            }
            return false;
        }

        public static InteractableObject ResolveObject(in BoxWire w)
        {
            if (w.Kind == Unresolved)
                return null;
            var sm = Sm();
            if (sm == null || w.ObjIndex <= 0)
                return null;
            if (!PlacedObjectIdentity.TryResolve(sm, w.Kind, (ushort)w.ObjIndex, out var obj))
                return null;
            return EnumMap.TryFromWire(EnumKind.ObjectType, w.ObjType, out int wireType)
                && (int)obj.m_ObjectType == wireType ? obj : null;
        }

        public void FillContent(InteractablePackagingBox box, ref BoxWire w)
        {
            var obj = BoxedObject(box);
            if (obj == null)
                return;
            w.ObjType = EnumMap.ToWire(EnumKind.ObjectType, (int)obj.m_ObjectType);
            w.NameHash = Fnv(obj.m_ObjectType.ToString());
            w.Open = BoxVisuals.ReadOpen(box);
            // R-open places the boxed OBJECT (delivery box hidden); Q-drag places the box.
            w.Unpack = !box.GetIsMovingObject() && obj.GetIsMovingObject();
            if (TryFindObjKey(obj, out byte kind, out int idx))
            {
                w.Kind = kind;
                w.ObjIndex = idx;
            }
            else
            {
                w.Kind = Unresolved;
                w.ObjIndex = -1;
            }
        }

        public int ContentSignature(InteractablePackagingBox box)
        {
            var obj = BoxedObject(box);
            if (obj == null)
                return 0;
            int h = 17;
            h = h * 31 + (int)obj.m_ObjectType;
            h = h * 31 + (!box.GetIsMovingObject() && obj.GetIsMovingObject() ? 1 : 0);
            h = h * 31 + (BoxVisuals.ReadOpen(box) ? 1 : 0);
            return h;
        }

        public bool ContentMatches(InteractablePackagingBox box, in BoxWire w)
        {
            var obj = BoxedObject(box);
            if (obj == null)
                return false;
            return (EnumMap.TryFromWire(EnumKind.ObjectType, w.ObjType, out int wireType)
                    && (int)obj.m_ObjectType == wireType)
                || Fnv(obj.m_ObjectType.ToString()) == w.NameHash;
        }

        public InteractablePackagingBox Spawn(in BoxWire w)
        {
            var obj = ResolveObject(w);
            if (obj == null)
                return null;
            try
            {
                if (!obj.GetIsBoxedUp())
                    obj.BoxUpObject(holdBox: false);
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
            var box = obj.GetPackagingBoxShelf();
            BoxVisuals.EnsureOpenState(box, false);
            return box;
        }

        public void DestroyBox(InteractablePackagingBox box)
        {
            if (box == null)
                return;
            BoxVisuals.Forget(box);
            try
            {
                box.OnDestroyed();
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        public void ApplyState(InteractablePackagingBox box, in BoxWire w, bool isOwner)
        {
            if (isOwner)
                return;
            switch (w.Possession)
            {
                case BoxPossession.Held:
                    BoxLifecycle.Apply(box, w.Possession);
                    SetVisible(box, false);
                    break;
                case BoxPossession.Placing:
                    BoxLifecycle.Apply(box, w.Possession);
                    if (w.Unpack)
                    {
                        // The owner is placing the boxed furniture object; vanilla has hidden
                        // the delivery box. Keep it hidden here instead of following it.
                        BoxPlacement.ClearPlacementIntent(box);
                        SetVisible(box, false);
                    }
                    else
                    {
                        SetVisible(box, true);
                        BoxVisuals.EnsureOpenState(box, w.Open);
                        BoxPlacement.SetPlacementIntent(box, true,
                            BoxPlacement.ResolvePlacementAvatar((byte)(w.OwnerConn == 0 ? 1 : 2), w.OwnerConn));
                    }
                    break;
                case BoxPossession.Free:
                    SetVisible(box, true);
                    BoxVisuals.EnsureOpenState(box, w.Open);
                    BoxLifecycle.ApplyEnabled(box, true);
                    BoxPlacement.ClearPlacementIntent(box);
                    BoxPlacement.ApplyPhysicsPose(box, w.Pos, w.Yaw);
                    if (box.m_Rigidbody != null)
                    {
                        box.m_Rigidbody.velocity = w.Velocity;
                        box.m_Rigidbody.angularVelocity = w.AngularVelocity;
                        box.m_Rigidbody.WakeUp();
                    }
                    break;
            }
        }

        private static void SetVisible(InteractablePackagingBox box, bool visible)
        {
            BoxVisuals.SetVisible(box, visible);
        }

        public bool TryReadLocal(InteractablePackagingBox box, out BoxPossession possession,
            out Vector3 pos, out float yaw, out Vector3 velocity, out Vector3 angularVelocity,
            out bool stored)
        {
            possession = BoxPossession.Free;
            pos = BoxPlacement.PhysicsPosition(box);
            yaw = BoxPlacement.PhysicsRotation(box).eulerAngles.y;
            velocity = box.m_Rigidbody != null ? box.m_Rigidbody.velocity : Vector3.zero;
            angularVelocity = box.m_Rigidbody != null ? box.m_Rigidbody.angularVelocity : Vector3.zero;
            stored = false;
            var boxed = BoxedObject(box);
            if (boxed == null)
                return false; // mid-unpack this frame
            if (FurnitureBoxOps.IsLocallyCarried(box as InteractablePackagingBox_Shelf)
                || BoxShared.IsHeldByAnyone(box))
                possession = BoxPossession.Held;
            else if (box.GetIsMovingObject() || (boxed.GetIsBoxedUp() && boxed.GetIsMovingObject()))
                possession = BoxPossession.Placing;
            return true;
        }
    }
}
