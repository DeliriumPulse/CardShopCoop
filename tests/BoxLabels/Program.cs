using CardShopCoop.Sync;

// Exercise the production label refresh with game/UI boundaries stubbed. This
// checks detached-parent recovery and count/visibility transitions, not Unity rendering.
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}

var box = new InteractablePackagingBox_Item();
var compartment = box.m_ItemCompartment;
var tag = new InteractablePriceTag();
compartment.m_InteractablePriceTagList.Add(tag);
compartment.Count = 8;
compartment.ItemType = EItemType.PackA;
tag.UI.gameObject.Parent = box.LabelRoot;
box.LabelRoot.SetActive(false); // Previous empty/open-box code disabled the parent.
tag.SetVisibility(false);
ItemBoxLabel.Refresh(box);
Check(box.LabelRoot.activeInHierarchy && tag.UI.gameObject.activeInHierarchy,
    "filled box restores the detached parent and its previously hidden UI");
Check(tag.UI.Amount == 8 && tag.UI.ItemType == EItemType.PackA,
    "visible box counter and icon match its contents");

// Native opening/refilling hides the tag, even though the box still holds stock.
tag.SetVisibility(false);
compartment.Count = 7;
ItemBoxLabel.Refresh(box);
Check(tag.UI.gameObject.activeInHierarchy && tag.UI.Amount == 7,
    "native open-box hide cannot keep a nonempty counter invisible");
compartment.Count = 2;
ItemBoxLabel.Refresh(box);
Check(tag.UI.Amount == 2, "removing packs updates the displayed quantity");
compartment.Count = 0; // Retain the product type, as locked labels do in the game.
ItemBoxLabel.Refresh(box);
Check(!tag.gameObject.activeSelf && !tag.UI.gameObject.activeSelf,
    "taking the last pack hides both the counter and the retained product icon");
Check(box.LabelRoot.activeSelf, "empty box leaves its UI parent available for a refill");
compartment.Count = 1;
compartment.ItemType = EItemType.PackB;
ItemBoxLabel.Refresh(box);
Check(tag.UI.gameObject.activeInHierarchy && tag.UI.Amount == 1 && tag.UI.ItemType == EItemType.PackB,
    "refilling an empty box restores the new product and quantity without placing it");

// A delayed close animation can try to show a label after the last item is gone.
compartment.Count = 0;
tag.SetVisibility(true);
ItemBoxLabel.Refresh(box);
Check(!tag.UI.gameObject.activeSelf, "late visibility callback cannot resurrect an empty label");
compartment.Count = 3;
box.m_IsStored = true;
ItemBoxLabel.Refresh(box);
Check(!tag.UI.gameObject.activeSelf && box.LabelRoot.activeSelf,
    "storing a full box hides its individual label without disabling the shared parent");
tag.SetVisibility(true);
ItemBoxLabel.Refresh(box);
Check(!tag.UI.gameObject.activeSelf, "late visibility callback cannot show a stored box label");
box.m_IsStored = false;
ItemBoxLabel.Refresh(box);
Check(tag.UI.gameObject.activeInHierarchy && tag.UI.Amount == 3,
    "retrieving a full box restores its count");

box.gameObject.SetActive(false); // Remote-held source is hidden in favor of the avatar prop.
ItemBoxLabel.Refresh(box);
Check(!box.LabelRoot.activeSelf && !tag.UI.gameObject.activeSelf,
    "hidden remote-held box does not leave a floating label behind");
box.gameObject.SetActive(true);
ItemBoxLabel.Refresh(box);
Check(tag.UI.gameObject.activeInHierarchy, "placing the hidden box restores its detached label");
box.gameObject.Parent = new GameObject();
box.gameObject.Parent.SetActive(false);
ItemBoxLabel.Refresh(box);
Check(!box.LabelRoot.activeSelf, "inactive box ancestor also hides the detached UI");
box.gameObject.Parent.SetActive(true);
ItemBoxLabel.Refresh(box);
Check(tag.UI.gameObject.activeInHierarchy, "reactivating a box ancestor permits label recovery");

tag.UI.gameObject.SetActive(false); // A child alone can be hidden by vanilla.
ItemBoxLabel.Refresh(box);
Check(tag.UI.gameObject.activeInHierarchy && tag.gameObject.activeSelf,
    "a hidden UI child is repaired even if its interactable tag was still enabled");
var secondTag = new InteractablePriceTag();
secondTag.UI.gameObject.Parent = box.LabelRoot;
compartment.m_InteractablePriceTagList.Add(secondTag);
ItemBoxLabel.Refresh(box);
Check(secondTag.UI.Amount == 3 && secondTag.UI.ItemType == EItemType.PackB,
    "all bound count labels are updated");
int visibilityWrites = tag.VisibilityWrites;
int imageWrites = tag.ImageWrites;
ItemBoxLabel.Refresh(box);
Check(tag.VisibilityWrites == visibilityWrites && tag.ImageWrites == imageWrites,
    "unchanged refresh does not toggle active objects or replace the product image");
Check(compartment.Count == 3 && compartment.ItemType == EItemType.PackB,
    "label recovery never changes inventory quantity or type");

compartment.ItemType = EItemType.None;
ItemBoxLabel.Refresh(box);
Check(!tag.UI.gameObject.activeSelf && !secondTag.UI.gameObject.activeSelf,
    "untyped box cannot show a stale product icon");
compartment.m_InteractablePriceTagList.Add(null);
compartment.m_InteractablePriceTagList.Add(new InteractablePriceTag { UI = null });
compartment.ItemType = EItemType.PackA;
ItemBoxLabel.Refresh(box);
Check(tag.UI.ItemType == EItemType.PackA, "unbound or absent tags do not prevent other labels refreshing");
ItemBoxLabel.Refresh(null);
box.m_ItemCompartment = null;
ItemBoxLabel.Refresh(box);
Check(box.LabelRoot.activeSelf, "missing box or not-yet-attached compartment is safe");
Console.WriteLine($"{passed} box-label regression checks passed.");

public enum EItemType { None, PackA, PackB }
public sealed class GameObject
{
    public GameObject Parent;
    public bool activeSelf = true;
    public bool activeInHierarchy => activeSelf && (Parent?.activeInHierarchy ?? true);
    public void SetActive(bool active) => activeSelf = active;
}
public sealed class InteractablePackagingBox_Item
{
    public GameObject gameObject = new();
    public GameObject LabelRoot = new();
    public bool m_IsStored;
    public ShelfCompartment m_ItemCompartment = new();
}
public sealed class ShelfCompartment
{
    public int Count;
    public EItemType ItemType;
    public List<InteractablePriceTag> m_InteractablePriceTagList = new();
    public int GetItemCount() => Count;
    public EItemType GetItemType() => ItemType;
}
public sealed class InteractablePriceTag
{
    public GameObject gameObject = new();
    public UI_PriceTag UI = new();
    public int VisibilityWrites;
    public int ImageWrites;
    public UI_PriceTag GetPriceTagUI() => UI;
    public void SetVisibility(bool visible)
    {
        gameObject.SetActive(visible);
        UI.gameObject.SetActive(visible);
        VisibilityWrites++;
    }
    public void SetAmountText(int amount) => UI.Amount = amount;
    public void SetItemImage(EItemType type)
    {
        UI.ItemType = type;
        ImageWrites++;
    }
}
public sealed class UI_PriceTag
{
    public GameObject gameObject = new();
    public int Amount;
    public EItemType ItemType;
    public EItemType GetItemType() => ItemType;
}
namespace CardShopCoop.Sync
{
    internal static class BoxPlacement
    {
        internal static void SetLabelVisible(InteractablePackagingBox_Item box, bool visible)
            => box.LabelRoot.SetActive(visible);
    }
}
