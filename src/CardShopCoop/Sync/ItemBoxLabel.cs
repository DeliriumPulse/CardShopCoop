namespace CardShopCoop.Sync
{
    /// <summary>Keep the detached UI container available while the box is visible;
    /// only its product/count tag disappears when the box is empty or stored.</summary>
    internal static class ItemBoxLabel
    {
        internal static void Refresh(InteractablePackagingBox_Item box)
        {
            if (box == null)
                return;
            bool visible = box.gameObject.activeInHierarchy;
            BoxPlacement.SetLabelVisible(box, visible);
            var compartment = box.m_ItemCompartment;
            if (compartment == null)
                return;
            int count = compartment.GetItemCount();
            var type = compartment.GetItemType();
            bool show = visible && !box.m_IsStored && count > 0 && type != EItemType.None;
            foreach (var tag in compartment.m_InteractablePriceTagList)
            {
                if (tag == null)
                    continue;
                var ui = tag.GetPriceTagUI();
                if (ui == null)
                    continue; // SpawnPriceTag has not attached this tag's UI yet.
                if (tag.gameObject.activeSelf != show || ui.gameObject.activeSelf != show)
                    tag.SetVisibility(show);
                if (!show)
                    continue;
                tag.SetAmountText(count);
                if (ui.GetItemType() != type)
                    tag.SetItemImage(type);
            }
        }
    }
}
