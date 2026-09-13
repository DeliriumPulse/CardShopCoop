using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>Execute the whole shelf-to-box move on the host, using current inventory.
    /// Requests use the reliable ordered lane; duplicates must never move another item.</summary>
    internal sealed class ShelfBoxPull
    {
        private readonly Dictionary<int, uint> _lastSequence = new Dictionary<int, uint>();

        public void Clear() => _lastSequence.Clear();
        public void ReleaseConn(int connId) => _lastSequence.Remove(connId);

        public bool Apply(int connId, uint sequence, ShelfCompartment source,
            InteractablePackagingBox_Item box, EItemType expectedType)
        {
            if (sequence == 0 || (_lastSequence.TryGetValue(connId, out uint last) && sequence <= last))
                return false;
            // Consume before invoking vanilla: even an exception must not replay a partial move.
            _lastSequence[connId] = sequence;
            if (source == null || box == null || box.m_ItemCompartment == null
                || source == box.m_ItemCompartment || source.GetWarehouseShelf() != null
                || !source.m_CanPutItem || !BoxVisuals.ReadOpen(box) || box.m_IsStored
                || source.GetItemCount() <= 0 || expectedType == EItemType.None
                || source.GetItemType() != expectedType)
                return false;
            var destination = box.m_ItemCompartment;
            if (destination.GetItemCount() > 0 && destination.GetItemType() != expectedType)
                return false;
            // Vanilla rebinds an empty box and computes the incoming product's capacity.
            // Its RemoveItem and AddItem run synchronously, with no client-side half to undo.
            int before = source.GetItemCount();
            box.RemoveItemFromShelf(false, source);
            return source.GetItemCount() == before - 1;
        }
    }
}
