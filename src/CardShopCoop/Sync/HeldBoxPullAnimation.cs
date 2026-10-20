using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Client: replay vanilla's shelf-to-box flight once the host has done the move.
    /// A client pull runs only on the host (ShelfBoxPull), so the client learns it succeeded
    /// when the host's snapshot adds the item to the box in its hand. Instead of rebuilding
    /// the box, that item is spawned at the shelf slot it left and flown into the box the way
    /// InteractablePackagingBox_Item.RemoveItemFromShelf does. Only the box in the client's
    /// hand changes, and only to the host's count; the shelf is left to the world sync.</summary>
    internal static class HeldBoxPullAnimation
    {
        private struct Pending
        {
            public ushort BoxId;
            public ShelfCompartment Source;
            public int Slot;
            public EItemType Type;
            public float Time;
        }

        private static readonly FieldInfo FiPosList =
            ReflectionSurface.RequiredField(typeof(ShelfCompartment), "m_PosList");
        private static readonly List<Pending> _pending = new List<Pending>();
        // a pull the host hasn't confirmed by then was rejected (or its snapshot was missed)
        private const float MaxWait = 5f;

        public static void Clear() => _pending.Clear();

        /// <summary>Record a pull just sent to the host. Vanilla takes the shelf's last item,
        /// and the client's shelf doesn't change until the host's update, so each earlier
        /// pull still waiting on the same shelf moves this one's slot down by one.</summary>
        public static void Note(ushort boxId, ShelfCompartment source, EItemType type)
        {
            Prune();
            int earlier = 0;
            for (int i = 0; i < _pending.Count; i++)
                if (_pending[i].Source == source)
                    earlier++;
            _pending.Add(new Pending
            {
                BoxId = boxId,
                Source = source,
                Slot = source.GetItemCount() - 1 - earlier,
                Type = type,
                Time = Time.time,
            });
        }

        /// <summary>Add the host's new items to the held box by flying each from the shelf it
        /// was pulled from. Returns true when the box now holds exactly the host's contents;
        /// false leaves the rest to the normal content apply.</summary>
        public static bool TryApply(ushort boxId, InteractablePackagingBox_Item box, EItemType type, int count)
        {
            Prune();
            var comp = box != null ? box.m_ItemCompartment : null;
            if (comp == null || !BoxVisuals.ReadOpen(box) || type == EItemType.None
                || count <= comp.GetItemCount()
                || (comp.GetItemCount() > 0 && comp.GetItemType() != type))
                return false;
            bool prevApplyingRemote = BoxShared.ApplyingRemote;
            BoxShared.ApplyingRemote = true; // reconciliation, not a player edit to report
            try
            {
                while (comp.GetItemCount() < count && FlyOne(boxId, box, comp, type))
                {
                }
            }
            catch (System.Exception e) { CoopPlugin.Log.LogWarning("held box pull animation: " + e.Message); }
            finally { BoxShared.ApplyingRemote = prevApplyingRemote; }
            return comp.GetItemCount() == count && comp.GetItemType() == type;
        }

        private static bool FlyOne(ushort boxId, InteractablePackagingBox_Item box, ShelfCompartment comp, EItemType type)
        {
            int index = _pending.FindIndex(p => p.BoxId == boxId && p.Type == type && p.Source != null);
            if (index < 0)
                return false;
            var pull = _pending[index];
            _pending.RemoveAt(index);
            var slots = FiPosList?.GetValue(pull.Source) as List<Transform>;
            if (slots == null || pull.Slot < 0 || pull.Slot >= slots.Count || slots[pull.Slot] == null)
                return false;
            // vanilla's CheckItemType: an empty box takes the incoming product's type
            if (comp.GetItemCount() <= 0)
            {
                box.SetItemType(type);
                comp.CheckItemType(type);
            }
            if (!comp.HasEnoughSlot())
                return false;
            var mesh = InventoryBase.GetItemMeshData(type);
            var parent = comp.GetEmptySlotParent();
            var item = ItemSpawnManager.GetItem(parent);
            item.SetMesh(mesh.mesh, mesh.material, type, mesh.meshSecondary, mesh.materialSecondary, mesh.materialList);
            var start = slots[pull.Slot];
            item.transform.SetPositionAndRotation(start.position, start.rotation);
            item.transform.localScale = start.localScale;
            item.gameObject.SetActive(true);
            item.LerpToTransform(comp.GetLastEmptySlotTransform(), parent);
            comp.AddItem(item, addToFront: true);
            BoxFields.ItemAmountToSpawn?.SetValue(box, comp.GetItemCount());
            SoundManager.GenericPop(1f, 0.9f);
            return true;
        }

        private static void Prune()
        {
            float now = Time.time;
            _pending.RemoveAll(p => p.Source == null || now - p.Time > MaxWait);
        }
    }
}
