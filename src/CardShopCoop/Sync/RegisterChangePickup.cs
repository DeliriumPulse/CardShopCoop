using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Vanilla raycasts only drawer controls. Also allow taking back change
    /// by aiming at the money already placed on the tray, without changing its colliders.</summary>
    internal static class RegisterChangePickup
    {
        private static readonly FieldInfo Counter = AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentCashierCounter");
        private static readonly FieldInfo Models = AccessTools.Field(typeof(InteractableCounterMoneyChange), "m_MoneyModelInstanceList");
        private static readonly FieldInfo Returning = AccessTools.Field(typeof(InteractableCounterMoneyChange), "m_IsLerpingBackList");

        public static void ApplyPatches(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(InteractionPlayerController), "RaycastCashCounterState"),
                prefix: new HarmonyMethod(typeof(RegisterChangePickup), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(RegisterChangePickup), nameof(Postfix)));
        }

        private static int Count(InteractableCashierCounter counter)
        {
            int total = 0;
            if (counter != null)
                foreach (var button in counter.m_InteractableCounterMoneyChangeList)
                    total += RegisterSync.GivenCount(button);
            return total;
        }

        private static void Prefix(InteractionPlayerController __instance, out int __state)
        {
            __state = -1;
            if (CoopCore.Role != CoopRole.None && InputManager.GetKeyUpAction(EGameAction.RemoveChange))
                __state = Count(Counter.GetValue(__instance) as InteractableCashierCounter);
        }

        private static void Postfix(InteractionPlayerController __instance, int __state)
        {
            if (__state <= 0 || __instance.m_Cam == null)
                return;
            var counter = Counter.GetValue(__instance) as InteractableCashierCounter;
            // The normal drawer action may already have removed a coin/bill this frame.
            if (counter == null || !counter.IsGivingChange() || Count(counter) != __state)
                return;
            var ray = new Ray(__instance.m_Cam.transform.position, __instance.m_Cam.transform.forward);
            float nearest = __instance.m_RayDistance;
            InteractableCounterMoneyChange selected = null;
            foreach (var button in counter.m_InteractableCounterMoneyChangeList)
            {
                if (RegisterSync.GivenCount(button) <= 0 || !(Models.GetValue(button) is List<Transform> models)
                    || !(Returning.GetValue(button) is List<bool> returning))
                    continue;
                for (int i = 0; i < models.Count && i < returning.Count; i++)
                {
                    if (models[i] == null || !models[i].gameObject.activeInHierarchy || returning[i])
                        continue;
                    foreach (var renderer in models[i].GetComponentsInChildren<Renderer>())
                    {
                        if (!renderer.enabled)
                            continue;
                        var bounds = renderer.bounds;
                        bounds.Expand(0.008f); // Thin bills still need a practical aim target.
                        if (bounds.IntersectRay(ray, out float distance) && distance < nearest)
                        {
                            nearest = distance;
                            selected = button;
                        }
                    }
                }
            }
            selected?.OnRightMouseButtonUp();
        }
    }
}
