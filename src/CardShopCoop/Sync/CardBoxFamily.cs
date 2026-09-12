using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Graded-returns card boxes. Content is the immutable card list.</summary>
    public sealed class CardBoxFamily : IBoxFamily
    {
        public static Func<InteractablePackagingBox_Card, bool> IsLocallyCarried = _ => false;

        private readonly List<InteractablePackagingBox> _live = new List<InteractablePackagingBox>();
        private Transform _anchor;

        public BoxFamily Family => BoxFamily.Card;

        // Graded card boxes carry an immutable card list: a content mismatch is save drift,
        // not a change, and recreating the mirror is what duplicated boxes.
        public bool RecreateOnContentMismatch => false;

        public int ReadItemCount(InteractablePackagingBox box)
        {
            return 0;
        }

        public int ReadItemType(InteractablePackagingBox box)
        {
            return 0;
        }

        public bool ReconcileContent(InteractablePackagingBox box, in BoxWire w, int baseItemCount,
            int transferType, out int acceptedDelta)
        {
            // A graded box's card list is immutable; nothing to reconcile in place.
            acceptedDelta = 0;
            return false;
        }

        public void ApplyLidOnly(InteractablePackagingBox box, bool open)
        {
            BoxVisuals.EnsureOpenState(box, open);
        }

        public IList<InteractablePackagingBox> LiveBoxes()
        {
            var src = RestockManager.GetCardPackagingBoxList();
            _live.Clear();
            for (int i = 0; i < src.Count; i++)
                if (src[i] != null)
                    _live.Add(src[i]);
            return _live;
        }

        private static List<CardData> CardsOf(InteractablePackagingBox box)
        {
            var b = box as InteractablePackagingBox_Card;
            if (b == null)
                return null;
            try
            {
                return b.GetCardDataList();
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        public void FillContent(InteractablePackagingBox box, ref BoxWire w)
        {
            var cards = CardsOf(box);
            w.Cards = cards == null ? null : new List<CardData>(cards);
        }

        public int ContentSignature(InteractablePackagingBox box)
        {
            var cards = CardsOf(box);
            if (cards == null)
                return 0;
            int h = 17 + cards.Count;
            for (int i = 0; i < cards.Count; i++)
                h = h * 31 + HashCard(cards[i]);
            return h;
        }

        public bool ContentMatches(InteractablePackagingBox box, in BoxWire w)
        {
            var cards = CardsOf(box);
            int wantCount = w.Cards == null ? 0 : w.Cards.Count;
            if (cards == null)
                return wantCount == 0;
            if (cards.Count != wantCount)
                return false;
            for (int i = 0; i < cards.Count; i++)
                if (HashCard(cards[i]) != HashCard(w.Cards[i]))
                    return false;
            return true;
        }

        private static int HashCard(CardData c)
        {
            if (c == null)
                return 0;
            int h = 17;
            h = h * 31 + (int)c.monsterType;
            h = h * 31 + (int)c.expansionType;
            h = h * 31 + (int)c.borderType;
            h = h * 31 + c.cardGrade;
            h = h * 31 + (c.isDestiny ? 1 : 0);
            h = h * 31 + (c.isChampionCard ? 1 : 0);
            h = h * 31 + c.gradedCardIndex;
            h = h * 31 + (c.isFoil ? 1 : 0);
            return h;
        }

        public InteractablePackagingBox Spawn(in BoxWire w)
        {
            if (_anchor == null)
                _anchor = new GameObject("CoopCardBoxSpawnAnchor").transform;
            _anchor.SetPositionAndRotation(w.Pos, Quaternion.Euler(0f, w.Yaw, 0f));
            return RestockManager.SpawnPackageBoxCard(
                new List<CardData>(w.Cards ?? new List<CardData>()), _anchor);
        }

        public void DestroyBox(InteractablePackagingBox box)
        {
            if (box == null)
                return;
            try
            {
                box.OnDestroyed();
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        public void ApplyState(InteractablePackagingBox box, in BoxWire w, bool isOwner)
        {
            if (isOwner)
                return;
            switch (w.Possession)
            {
                case BoxPossession.Held:
                    BoxLifecycle.Apply(box, w.Possession);
                    BoxVisuals.SetVisible(box, false);
                    break;
                case BoxPossession.Placing:
                    BoxLifecycle.Apply(box, w.Possession);
                    BoxVisuals.SetVisible(box, true);
                    BoxPlacement.SetPlacementIntent(box, true,
                        BoxPlacement.ResolvePlacementAvatar((byte)(w.OwnerConn == 0 ? 1 : 2), w.OwnerConn));
                    break;
                case BoxPossession.Free:
                    BoxVisuals.SetVisible(box, true);
                    BoxLifecycle.ApplyEnabled(box, true);
                    BoxPlacement.ClearPlacementIntent(box);
                    BoxPlacement.ApplyPhysicsPose(box, w.Pos, w.Yaw);
                    if (box.m_Rigidbody != null)
                    {
                        box.m_Rigidbody.velocity = w.Velocity;
                        box.m_Rigidbody.angularVelocity = w.AngularVelocity;
                        box.m_Rigidbody.WakeUp();
                    }
                    break;
            }
        }

        public bool TryReadLocal(InteractablePackagingBox box, out BoxPossession possession,
            out Vector3 pos, out float yaw, out Vector3 velocity, out Vector3 angularVelocity,
            out bool stored)
        {
            possession = BoxPossession.Free;
            pos = BoxPlacement.PhysicsPosition(box);
            yaw = BoxPlacement.PhysicsRotation(box).eulerAngles.y;
            velocity = box.m_Rigidbody != null ? box.m_Rigidbody.velocity : Vector3.zero;
            angularVelocity = box.m_Rigidbody != null ? box.m_Rigidbody.angularVelocity : Vector3.zero;
            stored = false;
            if (IsLocallyCarried(box as InteractablePackagingBox_Card) || BoxShared.IsHeldByAnyone(box))
                possession = BoxPossession.Held;
            else if (box.GetIsMovingObject())
                possession = BoxPossession.Placing;
            return true;
        }
    }
}
