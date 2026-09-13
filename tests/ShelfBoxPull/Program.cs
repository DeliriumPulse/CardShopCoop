using CardShopCoop.Sync;

// Runs the production request executor with game boundaries stubbed. These checks prove
// inventory conservation and request ordering, not Unity visuals or multiplayer transport.
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}

var executor = new ShelfBoxPull();
var shelf = new ShelfCompartment { Count = 7, ItemType = EItemType.A };
var box = new InteractablePackagingBox_Item();
Check(executor.Apply(1, 1, shelf, box, EItemType.A)
    && shelf.Count == 6 && box.m_ItemCompartment.Count == 1,
    "customer took one of eight: host moves one of the remaining seven");
Check(shelf.Count + box.m_ItemCompartment.Count + 1 == 8, "customer plus containers conserve stock");
Check(!executor.Apply(1, 1, shelf, box, EItemType.A)
    && shelf.Count == 6 && box.m_ItemCompartment.Count == 1, "duplicate request does not pull twice");

shelf.Count = 1;
var otherBox = new InteractablePackagingBox_Item();
Check(executor.Apply(1, 2, shelf, box, EItemType.A)
    && !executor.Apply(2, 1, shelf, otherBox, EItemType.A)
    && shelf.Count == 0 && otherBox.m_ItemCompartment.Count == 0,
    "two guests pulling the last item: only one box receives it");
shelf.Count = 1;
Check(!executor.Apply(2, 1, shelf, otherBox, EItemType.A) && shelf.Count == 1,
    "a refused request cannot become accepted when retried after a refill");

box.m_ItemCompartment.Capacity = box.m_ItemCompartment.Count;
Check(!executor.Apply(1, 3, shelf, box, EItemType.A) && shelf.Count == 1,
    "full destination leaves source unchanged");
shelf.ItemType = EItemType.B;
Check(!executor.Apply(1, 4, shelf, otherBox, EItemType.A)
    && shelf.Count == 1 && otherBox.m_ItemCompartment.Count == 0,
    "source changed product before request: neither inventory changes");
Check(!executor.Apply(1, 5, shelf, box, EItemType.B) && shelf.Count == 1,
    "nonempty box with another product refuses the pull");
otherBox.m_ItemCompartment.ItemType = EItemType.A;
Check(executor.Apply(2, 2, shelf, otherBox, EItemType.B)
    && shelf.Count == 0 && otherBox.m_ItemCompartment.ItemType == EItemType.B,
    "empty labeled box rebinds to the incoming product");
shelf.Count = 1;
Check(!executor.Apply(2, 1, shelf, otherBox, EItemType.B) && shelf.Count == 1,
    "out-of-order old request is ignored");
otherBox.Open = false;
Check(!executor.Apply(2, 3, shelf, otherBox, EItemType.B) && shelf.Count == 1,
    "closed box leaves the shelf untouched");
executor.ReleaseConn(2);
otherBox.Open = true;
Check(executor.Apply(2, 1, shelf, otherBox, EItemType.B), "reconnected peer gets a fresh sequence space");
Console.WriteLine($"{passed} regression checks passed.");

public enum EItemType
{
    None, A, B
}
public sealed class ShelfCompartment
{
    public int Count;
    public int Capacity = 8;
    public EItemType ItemType;
    public bool m_CanPutItem = true;
    public object GetWarehouseShelf() => null;
    public int GetItemCount() => Count;
    public EItemType GetItemType() => ItemType;
}
public sealed class InteractablePackagingBox_Item
{
    public ShelfCompartment m_ItemCompartment = new ShelfCompartment();
    public bool Open = true;
    public bool m_IsStored;
    // Inventory-only stand-in for vanilla RemoveItemFromShelf. Rendering/pooling are excluded.
    public void RemoveItemFromShelf(bool isPlayer, ShelfCompartment source)
    {
        if (!Open || !source.m_CanPutItem || source.Count <= 0)
            return;
        if (m_ItemCompartment.Count == 0)
            m_ItemCompartment.ItemType = source.ItemType;
        if (m_ItemCompartment.ItemType != source.ItemType
            || m_ItemCompartment.Count >= m_ItemCompartment.Capacity)
            return;
        source.Count--;
        m_ItemCompartment.Count++;
    }
}
namespace CardShopCoop.Sync
{
    internal static class BoxVisuals
    {
        public static bool ReadOpen(InteractablePackagingBox_Item box) => box.Open;
    }
}
