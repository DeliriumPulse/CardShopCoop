using System;
using HarmonyLib;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Fail-safe around the local player's HAND. A card is removed from the shared collection the
    /// instant it is picked up (binder pull / graded-return box), and `CPlayerData.m_HoldCardDataList`
    /// is local-only - nothing mirrors it - so any card dropped from the hand is gone from BOTH
    /// players. Vanilla has two unchecked ways to drop one:
    ///
    ///   1. <see cref="InteractionPlayerController.AddHoldCard"/> appends to the hand with NO
    ///      bounds check, indexing `m_HoldCardPosList[count]`. The binder checks
    ///      <see cref="InteractionPlayerController.HasEnoughSlotToHoldCard"/> first, but
    ///      <c>InteractablePackagingBox_Card.OnPressOpenBox</c> calls it once per stored card with
    ///      no cap at all - so opening a graded-return box while holding cards throws mid-loop (the
    ///      box then despawns with its remaining cards) or pushes the data list past the save trim.
    ///   2. <see cref="InteractionPlayerController.OnGameDataFinishLoaded"/> silently trims the hand
    ///      to 8 entries on a world/save load, dropping any excess without returning it.
    ///
    /// Both are fixed the same way: instead of overflowing or dropping, bank the card back into
    /// the shared collection (<c>CPlayerData.AddCard</c>, which the CardDelta mirror propagates).
    /// </summary>
    public static class HandProtection
    {
        // HasEnoughSlotToHoldCard is `count < 8`; the hand holds at most 8 (indices 0..7) and the
        // load trim keeps 8. Anything at/over that is overflow.
        internal const int HandCapacity = 8;

        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(InteractionPlayerController), "AddHoldCard",
                prefix: new HarmonyMethod(typeof(HandProtection), nameof(AddHoldCardPrefix)));
            Try(h, typeof(InteractionPlayerController), "OnGameDataFinishLoaded",
                prefix: new HarmonyMethod(typeof(HandProtection), nameof(GameDataFinishLoadedPrefix)));
        }

        /// <summary>Vanilla AddHoldCard has no capacity check. When the hand is full, bank the card
        /// and skip the body rather than index past the hand slots / grow a local-only list.</summary>
        public static bool AddHoldCardPrefix(InteractableCard3d card3d)
        {
            if (card3d == null)
                return true;
            try
            {
                if (InteractionPlayerController.HasEnoughSlotToHoldCard())
                    return true;
                CardData card = card3d.m_Card3dUI != null && card3d.m_Card3dUI.m_CardUI != null
                    ? card3d.m_Card3dUI.m_CardUI.GetCardData() : null;
                if (card == null)
                {
                    CoopPlugin.Log.LogWarning("hand full: AddHoldCard called with no readable card data - skipped");
                    return false;
                }
                Bank(card, "hand full");
                // The packaging box clears its stored data immediately after this call and only
                // destroys the box later.  Retire the physical card now; otherwise its UI group
                // remains registered with Card3dUISpawner as a live (but unreachable) card.
                card3d.OnDestroyed();
                return false;
            }
            catch (Exception e)
            {
                // Do NOT fall back to vanilla here: we only reach this branch because the hand is
                // full, and vanilla's unchecked `m_HoldCardPosList[count]` would throw out of the
                // caller's per-card loop, aborting the box open and dropping the remaining cards.
                // Contain the damage to this one card and fail loud.
                CoopPlugin.Log.LogError("HandProtection.AddHoldCardPrefix: could not bank a full-hand card: " + e);
                return false;
            }
        }

        /// <summary>Bank any held cards beyond the hand capacity BEFORE vanilla trims the list, so a
        /// saved hand that already overflowed on an older build recovers instead of losing them.
        /// Vanilla's own trim then removes exactly those entries from the hand.</summary>
        public static void GameDataFinishLoadedPrefix()
        {
            var held = CPlayerData.m_HoldCardDataList;
            if (held == null)
                return;
            // PER-CARD: one un-bankable entry must not stop the rest from being recovered before
            // vanilla trims the whole overflow away.
            for (int i = HandCapacity; i < held.Count; i++)
            {
                if (held[i] == null)
                    continue;
                try
                {
                    Bank(held[i], "held-card save overflow");
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("HandProtection.GameDataFinishLoadedPrefix: could not bank held-card overflow: " + e);
                }
            }
        }

        private static void Bank(CardData card, string why)
        {
            if (card == null)
                return;
            // Graded cards must be registered with Grading Overhaul before AddCard, or its
            // anti-cheat re-encodes the certificate as fake (same rule as ApplyCardDelta).
            if (card.cardGrade > 10 && Util.GradingInterop.Present)
                Util.GradingInterop.Remember(card);
            CoopPlugin.Log.LogWarning(
                $"HandProtection: {why} - {card.expansionType}#{(int)card.monsterType}"
                + (card.cardGrade > 0 ? " grade " + card.cardGrade : "")
                + " banked back into the shared binder instead of being lost");
            CPlayerData.AddCard(card, 1);
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }
    }
}
