using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The one place that reads and writes a packaging box's open/closed look.
    ///
    /// The levers differ per box type - item boxes use the (bool, bool) setter, card boxes
    /// the (bool) setter, and the SHELF box is driven by its m_BoxAnim "Open"/"Close" clips
    /// (the game never calls SetOpenCloseBox on a shelf, so its mesh flags alone don't move).
    /// A single bidirectional helper keeps them together and lets any caller correct drift,
    /// rather than one function to read and several to set.
    /// </summary>
    public static class BoxVisuals
    {
        // Which boxes have had their open state applied by us. Needed because the shelf box
        // never maintains a readable open flag; this lets EnsureOpenState skip replaying the
        // animation every snapshot while still correcting a wrong state.
        private static readonly Dictionary<InteractablePackagingBox, bool> Applied
            = new Dictionary<InteractablePackagingBox, bool>();
        // A desired open state the game refused. SetOpenCloseBox ignores calls while its 0.85s
        // toggle animation runs, so an apply can be silently dropped; retry it each frame until
        // the live flag agrees, or the box stays permanently wrong on that peer.
        private static readonly Dictionary<InteractablePackagingBox, bool> Pending
            = new Dictionary<InteractablePackagingBox, bool>();
        private static readonly List<KeyValuePair<InteractablePackagingBox, bool>> PendingScratch
            = new List<KeyValuePair<InteractablePackagingBox, bool>>();

        public static bool ReadOpen(InteractablePackagingBox box)
        {
            if (box == null)
                return false;
            if (box is InteractablePackagingBox_Shelf)
                return Applied.TryGetValue(box, out var tracked) && tracked;
            if (box is InteractablePackagingBox_Item)
            {
                // IsBoxOpened() deliberately reports false while the game's 0.85s
                // open/close animation is running. The private flag is the committed
                // state and is what must travel over the wire during that animation.
                return BoxFields.ItemBoxOpened != null
                    && (bool)BoxFields.ItemBoxOpened.GetValue(box);
            }
            try
            {
                return box.IsBoxOpened();
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        public static void EnsureOpenState(InteractablePackagingBox box, bool open)
        {
            if (box == null)
                return;
            // Idempotent: shelves compare against our tracked value (they have no readable
            // flag); item/card boxes compare against their own live state.
            if (box is InteractablePackagingBox_Shelf)
            {
                if (Applied.TryGetValue(box, out var cur) && cur == open)
                    return;
            }
            else
            {
                try
                {
                    if (ReadOpen(box) == open)
                        return;
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }

            BoxShared.DebugLog("open-set", $"box={box.name} open={open} read={ReadOpen(box)}", box.GetInstanceID(), 0.25f);
            try
            {
                switch (box)
                {
                    case InteractablePackagingBox_Shelf shelf:
                        shelf.SetOpenCloseBox(open);
                        if (shelf.m_BoxAnim != null)
                            shelf.m_BoxAnim.Play(open ? "Open" : "Close");
                        break;
                    case InteractablePackagingBox_Card card:
                        card.SetOpenCloseBox(open);
                        break;
                    case InteractablePackagingBox_Item item:
                        item.SetOpenCloseBox(open, isPlayer: false);
                        break;
                }
            }
            catch (System.Exception e)
            {
                Swallow.Log(e);
                Applied.Remove(box);
                return;
            }
            Applied[box] = open;
            NotePending(box, open);
        }

        /// <summary>Retry any open/close the game refused (toggle animation in flight). Called
        /// once per frame by the box engine on both roles.</summary>
        public static void TickPending()
        {
            if (Pending.Count == 0)
                return;
            PendingScratch.Clear();
            foreach (var kv in Pending)
                PendingScratch.Add(kv);
            Pending.Clear();
            for (int i = 0; i < PendingScratch.Count; i++)
                EnsureOpenState(PendingScratch[i].Key, PendingScratch[i].Value);
        }

        private static void NotePending(InteractablePackagingBox box, bool open)
        {
            if (box == null)
                return;
            if (ReadOpen(box) == open)
                Pending.Remove(box);
            else
                Pending[box] = open;
        }

        /// <summary>Apply an explicit box lifecycle event. Unlike snapshot reconciliation,
        /// creation/box-up events must always write the visual state: the newly-created
        /// object may have inherited a prefab or stale cached appearance.</summary>
        public static void ApplyOpenEvent(InteractablePackagingBox box, bool open)
        {
            if (box == null)
                return;
            switch (box)
            {
                case InteractablePackagingBox_Shelf shelf:
                    shelf.SetOpenCloseBox(open);
                    if (shelf.m_BoxAnim != null)
                        shelf.m_BoxAnim.Play(open ? "Open" : "Close");
                    break;
                case InteractablePackagingBox_Card card:
                    card.SetOpenCloseBox(open);
                    break;
                case InteractablePackagingBox_Item item:
                    item.SetOpenCloseBox(open, isPlayer: false);
                    break;
            }
            Applied[box] = open;
        }

        public static void Forget(InteractablePackagingBox box)
        {
            // Use a CLR null check: Unity-destroyed objects are fake-null but remain valid
            // dictionary keys, and those stale keys are precisely what this removes.
            if (!(box is null))
            {
                Applied.Remove(box);
                Pending.Remove(box);
            }
        }

        /// <summary>The single visibility writer: show/hide a box AND its world label and
        /// compartment price tag together, so the three families cannot drift.</summary>
        public static void SetVisible(InteractablePackagingBox box, bool visible)
        {
            if (box == null)
                return;
            bool wasVisible;
            try
            {
                wasVisible = box.gameObject.activeSelf;
                if (wasVisible != visible)
                    box.gameObject.SetActive(visible);
            }
            catch { wasVisible = visible; }
            BoxPlacement.SetLabelVisible(box, visible);
            try
            {
                switch (box)
                {
                    case InteractablePackagingBox_Item item:
                        item.m_ItemCompartment?.SetPriceTagVisibility(visible);
                        break;
                    case InteractablePackagingBox_Shelf shelf:
                        shelf.m_ItemCompartment?.SetPriceTagVisibility(visible);
                        break;
                }
            }
            catch (System.Exception e) { Swallow.Log(e); }
            // A held box is deactivated; re-enabling a SHELF box restarts its Animation from the
            // prefab default, so a thrown/dropped box can reappear with its lid open even though
            // nothing opened it. Only shelves need this - their animation has no readable flag.
            // Item/card boxes have a real committed flag and must NOT be re-asserted here: the
            // incoming wire value (applied next by EnsureOpenState) would otherwise close then
            // reopen, which reads as a flicker.
            if (visible && !wasVisible && box is InteractablePackagingBox_Shelf)
            {
                bool open = ReadOpen(box);
                if (BoxShared.Debug && box is InteractablePackagingBox_Shelf dbg)
                    BoxShared.DebugLog("open-reassert",
                        $"box={box.name} desired={open} meshOpen={dbg.m_OpenBox?.activeSelf} meshClosed={dbg.m_ClosedBox?.activeSelf}",
                        box.GetInstanceID(), 0.05f);
                ApplyOpenEvent(box, open);
            }
        }

        public static void Reset()
        {
            Applied.Clear();
            Pending.Clear();
        }
    }
}
