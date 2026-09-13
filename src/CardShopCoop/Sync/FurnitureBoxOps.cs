using System;
using System.Reflection;
using CardShopCoop.Net.Messages;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Furniture OBJECT ops that are not box possession: unpack-place, sell, box-up.
    /// Possession is owned by <see cref="BoxEngine"/>; these run the host-authoritative
    /// game recipes and are forwarded from the client's vanilla actions.
    /// </summary>
    public static class FurnitureBoxOps
    {
        public static Func<InteractablePackagingBox_Shelf, bool> IsLocallyCarried = _ => false;
        public static Action<FurnitureBoxOpMessage> SendOp;
        public static bool ApplyingRemote;

        private static BoxEngine Engine => CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;

        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(InteractableObject), "PlaceMovedObject",
                prefix: new HarmonyMethod(typeof(FurnitureBoxOps), nameof(PlacePrefix)));
            Try(h, typeof(InteractableObject), "BoxUpObject",
                postfix: new HarmonyMethod(typeof(FurnitureBoxOps), nameof(BoxUpPostfix)));
            Try(h, typeof(InteractablePackagingBox_Shelf), "OnDestroyed",
                prefix: new HarmonyMethod(typeof(FurnitureBoxOps), nameof(DestroyedPrefix)));
        }

        public static bool PlacePrefix(InteractableObject __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            if (__instance == null || !__instance.GetIsBoxedUp())
                return true;
            try
            {
                if (BoxFields.MovingValid != null && !(bool)BoxFields.MovingValid.GetValue(__instance))
                    return true;
                var box = __instance.GetPackagingBoxShelf();
                if (box == null)
                    return true;
                return ClientPlace(__instance, box) ? false : true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("FurnitureBoxOps place: " + e.Message);
                return true;
            }
        }

        public static bool DestroyedPrefix(InteractablePackagingBox_Shelf __instance)
        {
            if (!BoxShared.ApplyingRemote && !ApplyingRemote && !CoopCore.ClientReloading)
                OnLocalDestroyed(__instance);
            return true;
        }

        public static void BoxUpPostfix(InteractableObject __instance, bool holdBox)
        {
            if (__instance == null || __instance.m_DecoObjectType != EDecoObject.None)
                return;
            // A box-up always yields a closed box; force it on the authority too.
            if (!ApplyingRemote)
                BoxVisuals.EnsureOpenState(__instance.GetPackagingBoxShelf(), false);
            if (CoopCore.Role == CoopRole.Client && !ApplyingRemote && holdBox)
                ClientBoxUp(__instance);
        }

        public static bool ClientPlace(InteractableObject obj, InteractablePackagingBox_Shelf box)
        {
            var engine = Engine;
            if (engine == null || SendOp == null)
                return false;
            if (!engine.TryResolveClientBoxId(obj, out ushort boxId))
            {
                BoxShared.DebugLog("furniture-place", $"client: placement not handled; box id unresolved object={obj.name}",
                    obj.GetInstanceID(), 1f);
                return false;
            }
            bool isVertical = obj.m_IsDecorationVertical;
            engine.ForgetClientBox(boxId);

            ApplyingRemote = true;
            try
            {
                obj.PlaceMovedObject();
            }
            finally { ApplyingRemote = false; }

            var pos = obj.transform.position;
            var rotation = obj.transform.rotation;
            if (!isVertical)
                pos.y = 0f;
            SendOp(new FurnitureBoxOpMessage
            {
                Op = FurnitureBoxOpMessage.OpPlace,
                Id = boxId,
                WireType = EnumMap.ToWire(EnumKind.ObjectType, (int)obj.m_ObjectType),
                NameHash = FurnitureBoxFamily.Fnv(obj.m_ObjectType.ToString()),
                Position = pos,
                Yaw = rotation.eulerAngles.y,
                IsVertical = isVertical,
                Rotation = rotation,
                IsWarehouseWall = obj.GetIsVerticalSnapToWarehouseWall(),
                VerticalSnapWallIndex = obj.GetVerticalSnapWallIndex(),
            });
            return true;
        }

        public static void ClientBoxUp(InteractableObject obj)
        {
            var box = obj == null ? null : obj.GetPackagingBoxShelf();
            if (box == null || !FurnitureBoxFamily.TryFindObjKey(obj, out byte kind, out int objIndex))
                return;
            SendOp?.Invoke(new FurnitureBoxOpMessage
            {
                Op = FurnitureBoxOpMessage.OpBoxUp,
                Kind = kind,
                ObjIndex = objIndex,
                WireType = EnumMap.ToWire(EnumKind.ObjectType, (int)obj.m_ObjectType),
                NameHash = FurnitureBoxFamily.Fnv(obj.m_ObjectType.ToString()),
            });
        }

        public static bool ClientSell(InteractionPlayerController controller)
        {
            if (controller == null || CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return false;
            var box = BoxFields.HoldingBoxShelf?.GetValue(controller) as InteractablePackagingBox_Shelf;
            if (box == null)
                return false;
            var obj = FurnitureBoxFamily.BoxedObject(box);
            if (obj == null || !IsLocallyCarried(box))
                return false;
            var engine = Engine;
            if (engine == null || !engine.TryGetClientId(box, out ushort boxId)
                || !FurnitureBoxFamily.TryFindObjKey(obj, out byte kind, out int objIndex))
                return false;

            engine.ForgetClientBox(boxId);
            BoxShared.DebugLog("furniture-op", $"client: forwarding sale of {obj.m_ObjectType}");
            SendOp?.Invoke(new FurnitureBoxOpMessage
            {
                Op = FurnitureBoxOpMessage.OpSell,
                Kind = kind,
                ObjIndex = objIndex,
                WireType = EnumMap.ToWire(EnumKind.ObjectType, (int)obj.m_ObjectType),
                NameHash = FurnitureBoxFamily.Fnv(obj.m_ObjectType.ToString()),
            });
            ApplyingRemote = true;
            try
            {
                controller.OnExitHoldBoxMode();
                try
                {
                    box.OnDestroyed();
                }
                catch (Exception e) { CoopPlugin.Log.LogError($"FurnitureBoxOps: client sell destroy failed for {box.name}; sale was forwarded but not completed: {e}"); }
                BoxFields.HoldingBoxShelf?.SetValue(controller, null);
            }
            finally { ApplyingRemote = false; }
            return true;
        }

        private static void OnLocalDestroyed(InteractablePackagingBox_Shelf box)
        {
            if (!InGameLevel())
                return;
            if (CoopCore.Role != CoopRole.Client)
                return;
            var obj = FurnitureBoxFamily.BoxedObject(box);
            var engine = Engine;
            if (engine == null || !engine.TryGetClientId(box, out ushort boxId))
                return;
            engine.ForgetClientBox(boxId);
            if (obj == null)
                return; // unpack destroys after EmptyBoxShelf; the place op covers it
            SendOp?.Invoke(new FurnitureBoxOpMessage
            {
                Op = FurnitureBoxOpMessage.OpRemoved,
                Id = boxId,
                WireType = EnumMap.ToWire(EnumKind.ObjectType, (int)obj.m_ObjectType),
                NameHash = FurnitureBoxFamily.Fnv(obj.m_ObjectType.ToString()),
            });
        }

        // ---------------- host ----------------

        public static void HostApplyOp(FurnitureBoxOpMessage msg, int connId)
        {
            if (CoopCore.Role != CoopRole.Host || msg == null)
                return;
            switch (msg.Op)
            {
                case FurnitureBoxOpMessage.OpPlace:
                    HostApplyPlace(msg, connId);
                    break;
                case FurnitureBoxOpMessage.OpSell:
                    HostApplySell(msg, connId);
                    break;
                case FurnitureBoxOpMessage.OpBoxUp:
                    HostApplyBoxUp(msg, connId);
                    break;
                case FurnitureBoxOpMessage.OpRemoved:
                    HostApplyRemoved(msg, connId);
                    break;
            }
        }

        private static void HostApplyPlace(FurnitureBoxOpMessage msg, int connId)
        {
            var engine = Engine;
            if (engine == null || !engine.TryGetHostBox(msg.Id, out var boxBase)
                || !(boxBase is InteractablePackagingBox_Shelf box))
                return;
            var obj = FurnitureBoxFamily.BoxedObject(box);
            if (obj == null || IsLocallyCarried(box))
                return;
            if (engine.HostBoxHeldByOther(msg.Id, connId))
            {
                CoopPlugin.Log.LogInfo($"FurnitureBoxOps: place rejected connId={connId} id={msg.Id} (held by another connection)");
                return;
            }
            ApplyingRemote = true;
            try
            {
                PlaceFromBox(obj, msg.Position, msg.Yaw,
                    msg.IsVertical ? msg.Rotation : Quaternion.identity,
                    msg.IsVertical, msg.IsWarehouseWall, msg.VerticalSnapWallIndex);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnitureBoxOps place apply: " + e.Message); }
            finally { ApplyingRemote = false; }
            engine.ForgetHostBox(msg.Id);
            engine.ForceNextTick();
        }

        private static void HostApplyRemoved(FurnitureBoxOpMessage msg, int connId)
        {
            var engine = Engine;
            if (engine == null || !engine.TryGetHostBox(msg.Id, out var boxBase)
                || !(boxBase is InteractablePackagingBox_Shelf box)
                || IsLocallyCarried(box))
                return;
            if (engine.HostBoxHeldByOther(msg.Id, connId))
            {
                CoopPlugin.Log.LogInfo($"FurnitureBoxOps: removal rejected connId={connId} id={msg.Id} (held by another connection)");
                return;
            }
            CoopPlugin.Log.LogInfo($"FurnitureBoxOps: removal accepted connId={connId} id={msg.Id}");
            ApplyingRemote = true;
            try
            {
                box.OnDestroyed();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnitureBoxOps removal: " + e.Message); }
            finally { ApplyingRemote = false; }
            engine.ForgetHostBox(msg.Id);
            engine.ForceNextTick();
        }

        private static void HostApplySell(FurnitureBoxOpMessage msg, int connId)
        {
            var obj = FurnitureBoxFamily.ResolveObject(new BoxWire
            {
                Kind = msg.Kind,
                ObjIndex = msg.ObjIndex,
                ObjType = msg.WireType,
                NameHash = msg.NameHash,
            });
            if (obj == null)
                return;
            var box = obj.GetPackagingBoxShelf();
            if (box != null && !ReferenceEquals(FurnitureBoxFamily.BoxedObject(box), obj))
                box = null;
            if (box != null && Engine != null && Engine.TryGetHostId(box, out var boxId)
                && Engine.HostBoxHeldByOther(boxId, connId))
            {
                CoopPlugin.Log.LogInfo($"FurnitureBoxOps: sell rejected connId={connId} id={boxId} (held by another connection)");
                return;
            }
            if (IsLocallyCarried(box) || (box != null && box.GetIsMovingObject()) || obj.GetIsMovingObject())
                return;
            if (obj.m_ObjectType == EObjectType.CashCounter
                && FurnitureBoxFamily.Sm()?.m_CashierCounterList.Count <= 1)
                return;
            var purchase = InventoryBase.GetFurniturePurchaseData(obj.m_ObjectType);
            if (purchase == null || float.IsNaN(purchase.price) || float.IsInfinity(purchase.price)
                || purchase.price < 0f)
                return;
            float salePrice = purchase.price / 2f;
            CoopPlugin.Log.LogInfo($"FurnitureBoxOps: sell accepted connId={connId} type={obj.m_ObjectType}");
            BoxShared.DebugLog("furniture-op", $"host: accepting guest sale of {obj.m_ObjectType} for {salePrice}");
            var engine = Engine;
            ushort hostBoxId = 0;
            bool hasHostId = box != null && engine != null && engine.TryGetHostId(box, out hostBoxId);
            ApplyingRemote = true;
            bool destroyed = false;
            try
            {
                if (box != null)
                    box.OnDestroyed();
                else
                    obj.OnDestroyed();
                destroyed = true;
            }
            catch (Exception e) { CoopPlugin.Log.LogError($"FurnitureBoxOps: sell destroy failed connId={connId} type={obj.m_ObjectType}; no credit issued: {e}"); }
            finally { ApplyingRemote = false; }
            if (!destroyed)
                return;
            PriceChangeManager.AddTransaction(salePrice, ETransactionType.SellFurniture, (int)obj.m_ObjectType);
            CEventManager.QueueEvent(new CEventPlayer_AddCoin(salePrice));
            if (hasHostId)
                engine.ForgetHostBox(hostBoxId);
            CoopCore.Instance?.NotifyHostStructureChanged();
            Engine?.ForceNextTick();
        }

        private static void HostApplyBoxUp(FurnitureBoxOpMessage msg, int connId)
        {
            var obj = FurnitureBoxFamily.ResolveObject(new BoxWire
            {
                Kind = msg.Kind,
                ObjIndex = msg.ObjIndex,
                ObjType = msg.WireType,
                NameHash = msg.NameHash,
            });
            if (obj == null || obj.GetIsBoxedUp() || obj.GetIsMovingObject()
                || !obj.m_CanBoxUpObject || !obj.m_CanPickupMoveObject)
                return;
            var box = obj.GetPackagingBoxShelf();
            if (box != null && Engine != null && Engine.TryGetHostId(box, out var boxId)
                && Engine.HostBoxHeldByOther(boxId, connId))
            {
                CoopPlugin.Log.LogInfo($"FurnitureBoxOps: box-up rejected connId={connId} id={boxId} (held by another connection)");
                return;
            }
            if (obj.m_ObjectType == EObjectType.CashCounter
                && FurnitureBoxFamily.Sm()?.m_CashierCounterList.Count <= 1)
                return;
            CoopPlugin.Log.LogInfo($"FurnitureBoxOps: box-up accepted connId={connId} type={obj.m_ObjectType} index={msg.ObjIndex}");
            BoxShared.DebugLog("furniture-op", $"host: accepting guest box-up of {obj.m_ObjectType}");
            ApplyingRemote = true;
            try
            {
                obj.BoxUpObject(holdBox: false);
                BoxVisuals.ApplyOpenEvent(obj.GetPackagingBoxShelf(), false); // fresh box-up is closed
            }
            finally { ApplyingRemote = false; }
            Engine?.ForceNextTick();
        }

        private static void PlaceFromBox(InteractableObject obj, Vector3 pos, float yaw,
            Quaternion rotation, bool isVertical, bool isWarehouseWall, int verticalSnapWallIndex)
        {
            obj.gameObject.SetActive(true);
            obj.transform.SetPositionAndRotation(pos,
                isVertical ? rotation : Quaternion.Euler(0f, yaw, 0f));
            BoxFields.MovingValid?.SetValue(obj, true);
            obj.PlaceMovedObject();
            if (isVertical)
                obj.SetVerticalSnapToWarehouseWall(isWarehouseWall, verticalSnapWallIndex);
            if (obj is InteractableCashierCounter)
            {
                try
                {
                    (BoxFields.CounterScreen?.GetValue(obj) as Component)?.gameObject.SetActive(true);
                    (BoxFields.CreditScreen?.GetValue(obj) as Component)?.gameObject.SetActive(true);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            ObjMoveSync.SyncTagGroup(obj.transform);
        }

        /// <summary>Population-sync bridge: unbox a boxed furniture object to its placed
        /// state at the host's pose (boxed -> placed). Used when the host places an object
        /// the client already holds as a boxed mirror.</summary>
        public static void PlaceBoxedObject(InteractableObject obj, Vector3 pos, Quaternion rot)
        {
            if (obj == null)
                return;
            try
            {
                bool vertical = obj.m_IsDecorationVertical;
                PlaceFromBox(obj, pos, rot.eulerAngles.y, rot, vertical,
                    obj.GetIsVerticalSnapToWarehouseWall(), obj.GetVerticalSnapWallIndex());
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnitureBoxOps place transition: " + e.Message); }
        }

        /// <summary>Population-sync bridge: box up a placed furniture object (placed ->
        /// boxed). The box engine then mirrors the new delivery box.</summary>
        public static void BoxUpPlacedObject(InteractableObject obj)
        {
            if (obj == null || obj.GetIsBoxedUp() || obj.GetIsMovingObject())
                return;
            try
            {
                obj.BoxUpObject(holdBox: false);
                BoxVisuals.ApplyOpenEvent(obj.GetPackagingBoxShelf(), false); // a fresh box-up is closed
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnitureBoxOps boxup transition: " + e.Message); }
        }

        public static void AlignJustSpawnedBox(EObjectType objType, Vector3 position, Quaternion rotation)
        {
            var boxes = RestockManager.GetShelfPackagingBoxList();
            for (int i = boxes.Count - 1; i >= 0; i--)
            {
                var box = boxes[i];
                if (box == null)
                    continue;
                var boxed = FurnitureBoxFamily.BoxedObject(box);
                if (boxed == null || boxed.m_ObjectType != objType)
                    continue;
                var rb = box.m_Rigidbody;
                if (rb != null)
                {
                    rb.position = position;
                    rb.rotation = rotation;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    if (!rb.isKinematic)
                        rb.WakeUp();
                }
                box.transform.SetPositionAndRotation(position, rotation);
            }
        }

        private static bool InGameLevel()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel;
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = ReflectionSurface.RequiredMethod(type, method);
                if (original == null)
                    return;
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }
    }
}
