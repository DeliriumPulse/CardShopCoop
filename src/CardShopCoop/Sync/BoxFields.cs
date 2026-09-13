using System.Reflection;
using CardShopCoop.Util;
using HarmonyLib;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// One cached reflection surface for the box system, so field lookups aren't scattered
    /// between ReflectionSurface.RequiredField and raw AccessTools.Field across the
    /// families. Required fields fail loud at load; optional ones return null.
    /// </summary>
    internal static class BoxFields
    {
        // InteractableObject
        public static readonly FieldInfo BeingHold =
            ReflectionSurface.RequiredField(typeof(InteractableObject), "m_IsBeingHold");
        public static readonly FieldInfo MovingValid =
            ReflectionSurface.RequiredField(typeof(InteractableObject), "m_IsMovingObjectValidState");

        // Packaging boxes
        public static readonly FieldInfo BoxedObject =
            AccessTools.Field(typeof(InteractablePackagingBox_Shelf), "m_BoxedObject");
        public static readonly FieldInfo ShelfWorldUi =
            AccessTools.Field(typeof(InteractablePackagingBox), "m_Shelf_WorldUIGrp");
        public static readonly FieldInfo ItemAmountToSpawn =
            ReflectionSurface.RequiredField(typeof(InteractablePackagingBox_Item), "m_ItemAmountToSpawn");
        public static readonly FieldInfo ItemBoxOpened =
            ReflectionSurface.RequiredField(typeof(InteractablePackagingBox_Item), "m_IsBoxOpened");

        // Cashier counter world screens (re-shown after a headless place)
        public static readonly FieldInfo CounterScreen =
            ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_UICashCounterScreen");
        public static readonly FieldInfo CreditScreen =
            ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_UICreditCardScreen");

        // Controller hold fields
        public static readonly FieldInfo HoldingBoxShelf =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_CurrentHoldingBoxShelf");

        // ShelfManager
        public static readonly FieldInfo GenericList =
            AccessTools.Field(typeof(ShelfManager), "m_InteractableObjectList");
    }
}
