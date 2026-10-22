namespace UnityEngine
{
    public static class Time
    {
        public static double realtimeSinceStartupAsDouble;
        public static float realtimeSinceStartup => (float)realtimeSinceStartupAsDouble;
    }
    public static class Mathf
    {
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int Clamp(int value, int low, int high) => Math.Clamp(value, low, high);
    }
    public sealed class GameObject
    {
        public bool Active = true;
        public void SetActive(bool active) => Active = active;
    }
}
public enum EItemType
{
    None = -1, Pack, OtherPack, Spray
}
public sealed class InteractableObject
{
    public string Name;
}
public sealed class ShelfManager
{
    public readonly Dictionary<int, System.Collections.IList> Lists = new();
}
public sealed class Item
{
    public EItemType Type;
    public bool Destroyed;
    public UnityEngine.GameObject gameObject = new();
    public EItemType GetItemType() => Type;
}
public sealed class InteractionPlayerController
{
    public readonly List<Item> Held = new();
    public bool HoldingBox;
    public int GetHoldItemCount() => Held.Count;
    public void AddHoldItemToFront(Item item) => Held.Insert(0, item);
}
public sealed class ShelfCompartment
{
    public readonly List<Item> Items = new();
    public EItemType Type;
    public EItemType GetItemType() => Type;
    public int GetItemCount() => Items.Count;
    public Item TakeItemToHand()
    {
        if (Items.Count == 0)
            return null;
        var item = Items[^1];
        Items.RemoveAt(Items.Count - 1);
        CardShopCoop.Sync.HandEscrow.NoteTakenItem(item);
        return item;
    }
}
namespace CardShopCoop
{
    public enum CoopRole
    {
        None, Host, Client
    }
    public static class CoopPlugin
    {
        public static readonly TestLog Log = new();
        public sealed class TestLog
        {
            public void LogError(object message)
            {
            }
            public void LogWarning(object message)
            {
            }
            public void LogDebug(object message)
            {
            }
        }
    }
    public sealed class CoopCore
    {
        public static CoopRole Role = CoopRole.Client;
        public static InteractionPlayerController PlayerIpc = new();
        public static CoopCore Instance = new();
        public static bool BlockRemoval;
        public readonly WorldStub World = new();
        public sealed class WorldStub
        {
            public int Refreshes;
            public Action RequestResync;
            public WorldStub() => RequestResync = RequestResyncCoalesced;
            public void RequestResyncCoalesced() => Refreshes++;
        }
        public static List<Item> GetHeldItemList(InteractionPlayerController ipc) => ipc?.Held;
        public static bool RemoveHeldItemFromHand(Item item) => !BlockRemoval && PlayerIpc.Held.Remove(item);
        public static void DestroyDetachedItem(Item item)
        {
            if (item == null)
                return;
            item.Destroyed = true;
            item.gameObject.SetActive(false);
        }
        public static bool HandAcceptsItems() => PlayerIpc != null && !PlayerIpc.HoldingBox;
        public static bool TryAttachHeldItem(Item item)
        {
            if (!HandAcceptsItems() || PlayerIpc.Held.Count >= 8)
                return false;
            item.gameObject.SetActive(true);
            PlayerIpc.AddHoldItemToFront(item);
            return true;
        }
    }
}
namespace CardShopCoop.Sync
{
    internal static class HandProtection
    {
        internal const int HandCapacity = 8;
    }
    internal static class PopulationSync
    {
        internal static System.Collections.IList GetList(ShelfManager manager, int kind)
            => manager.Lists.GetValueOrDefault(kind);
    }
}
