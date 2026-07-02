using System;
using CardShopCoop.Sync;
using HarmonyLib;

namespace CardShopCoop.Patches
{
    /// <summary>
    /// All Harmony patches, registered one-by-one so a single signature change in a game
    /// update degrades one feature instead of killing the whole plugin.
    ///
    /// Client-role philosophy: the joining player lives inside a mirrored copy of the
    /// host's world. The host's simulation is the only real one, so on the client we
    /// suppress every local mutation source (customers, workers, day-end) and protect
    /// the player's own save slots.
    /// </summary>
    public static class GamePatches
    {
        public static void ApplyAll(Harmony h)
        {
            // Client saves always land in the co-op slot, never the player's own slots.
            Try(h, typeof(CGameManager), "SaveGameData",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(SaveGuardPrefix)));

            // No local customer simulation on the client (host streams the real economy).
            Try(h, typeof(CustomerManager), "Update",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(ClientBlockPrefix)));
            Try(h, typeof(Customer), "ActivateCustomer",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(ClientBlockPrefix)));

            // No local workers on the client either.
            Try(h, typeof(WorkerManager), "ActivateWorker",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(ClientBlockPrefix)));

            // The client's clock follows the host; its own day must never end.
            Try(h, typeof(CEventManager), "QueueEvent",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(DayEndBlockPrefix)));

            // Shared card collection: every add/remove on either side mirrors to the other,
            // so the joiner's pack pulls land in the real binder (and vice versa).
            Try(h, typeof(CPlayerData), "AddCard",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(AddCardPostfix)));
            Try(h, typeof(CPlayerData), "ReduceCard",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(ReduceCardPostfix)));

            // Shared card pricing: SetCardPrice writes the price table and fires the UI
            // refresh event itself, so mirroring the call keeps tags in step on both sides.
            Try(h, typeof(CPlayerData), "SetCardPrice",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(SetCardPricePostfix)));
        }

        public static bool ApplyingRemotePrice;

        public static void SetCardPricePostfix(CardData cardData, float priceSet)
        {
            if (!ApplyingRemotePrice && CoopCore.Role != CoopRole.None)
                CoopCore.Instance?.ForwardCardPrice(cardData, priceSet);
        }

        /// <summary>True while we're applying a card delta that came over the network,
        /// so the postfixes don't echo it back forever.</summary>
        public static bool ApplyingRemoteCards;

        public static void AddCardPostfix(CardData cardData, int addAmount)
        {
            if (!ApplyingRemoteCards && CoopCore.Role != CoopRole.None)
                CoopCore.Instance?.ForwardCardDelta(cardData, addAmount, isAdd: true);
        }

        public static void ReduceCardPostfix(CardData cardData, int reduceAmount)
        {
            if (!ApplyingRemoteCards && CoopCore.Role != CoopRole.None)
                CoopCore.Instance?.ForwardCardDelta(cardData, reduceAmount, isAdd: false);
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

        public static void SaveGuardPrefix(ref int saveSlotIndex)
        {
            if (CoopCore.Role == CoopRole.Client)
                saveSlotIndex = SaveTransfer.CoopSlot;
        }

        public static bool ClientBlockPrefix()
        {
            return CoopCore.Role != CoopRole.Client;
        }

        /// <summary>Set by CoopCore right before it mirrors a host day-change, so exactly
        /// one OnDayStarted gets through to refresh the HUD/day label on the client.</summary>
        public static bool AllowNextDayStarted;

        public static bool DayEndBlockPrefix(CEvent evt)
        {
            if (CoopCore.Role != CoopRole.Client) return true;

            // The client's clock follows the host; its own day must never end.
            if (evt is CEventPlayer_OnDayEnded) return false;
            if (evt is CEventPlayer_OnDayStarted)
            {
                if (AllowNextDayStarted) { AllowNextDayStarted = false; return true; }
                return false;
            }

            // Shared-wallet contribution: gains/spends the JOINER earns are forwarded to
            // the host (who banks them for real) instead of applying to the mirrored copy.
            // Only Add*/Reduce* are intercepted - the Set* events the sync itself uses
            // pass through, so there is no feedback loop.
            if (evt is CEventPlayer_AddCoin addCoin)
            {
                CoopCore.Instance?.ForwardContribution(1, (float)addCoin.m_CoinValue);
                return false;
            }
            if (evt is CEventPlayer_ReduceCoin reduceCoin)
            {
                CoopCore.Instance?.ForwardContribution(2, (float)reduceCoin.m_CoinValue);
                return false;
            }
            if (evt is CEventPlayer_AddShopExp addExp)
            {
                CoopCore.Instance?.ForwardContribution(3, addExp.m_ExpValue);
                return false;
            }
            if (evt is CEventPlayer_AddFame addFame)
            {
                CoopCore.Instance?.ForwardContribution(4, addFame.m_FameValue);
                return false;
            }
            return true;
        }
    }
}
