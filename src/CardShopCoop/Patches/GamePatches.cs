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

            // The joiner's restock ORDERS spawn on the HOST (officially, visible to all,
            // mirrored back by BoxSync) instead of as local phantoms.
            Try(h, typeof(RestockManager), "SpawnPackageBoxItemMultipleFrame",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(OrderPrefix)));

            // The client is a pure box mirror. RestockManager.Update's out-of-bounds /
            // warehouse-lock sweep teleports stray boxes to an INDEPENDENT random spawn
            // point; run on the guest it scatters boxes to spots that diverge from the host
            // (then get adopted as authoritative). Keep the OOB timer pinned below its 5s
            // threshold on the client so that sweep never fires, while the spawn-drip and
            // delayed-reset work earlier in Update still run.
            Try(h, typeof(RestockManager), "Update",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(RestockUpdatePrefix)));

            // The shop already has a name (the host's); the joiner's copy must neither
            // prompt for one nor let it be changed.
            Try(h, typeof(ShopRenamer), "ShowRenameShopScreen",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(RenamerBlockPrefix)));

            // The joiner's FURNITURE purchases spawn on the host (as the official delivery
            // box); the placed object mirrors back through the population sync.
            Try(h, typeof(ShelfManager), "SpawnInteractableObjectInPackageBox",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(FurnitureOrderPrefix)));

            // A joiner's forwarded furniture spawn runs BoxUpObject -> OnPlacedMovedObject,
            // whose interactive-move teardown DisableMoveObjectPreviewMode dereferences
            // m_MoveObjectPreviewModel. With no live player-move preview (there isn't one
            // for a programmatic spawn) that's null and NRE'd, aborting the spawn mid-way
            // so the delivery box never finished and the table never arrived - money gone
            // (field report: PlayTable). Skip the teardown when there's nothing to tear down.
            Try(h, typeof(ShelfManager), "DisableMoveObjectPreviewMode",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(DisablePreviewGuardPrefix)));

            // A trashed box must die on the host too, or the next broadcast resurrects
            // it at its old spot on the ground.
            Try(h, typeof(InteractablePackagingBox_Item), "OnDestroyed",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(BoxDestroyedPrefix)));

            // Product licenses are shared: bought by either player, unlocked for both.
            // Identity travels as (itemType + box size), never a restock-list index -
            // modded restock lists can be ordered differently per machine.
            Try(h, typeof(CPlayerData), "SetUnlockItemLicense",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(LicenseUnlockPostfix)));

            // Workbench crafting and storage quick-fill remove cards through a direct
            // array write that never routes through ReduceCard - without this mirror
            // the shared binder silently keeps cards the other side already consumed.
            Try(h, typeof(CPlayerData), "ReduceCardUsingIndex",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(ReduceCardIndexPostfix)));

            // Graded cards leave the album ONLY via RemoveGradedCard (a direct list edit
            // ReduceCard never sees). Mirror it so a graded card traded/donated/re-graded
            // away on one side also leaves the other side's album instead of ghosting.
            Try(h, typeof(CPlayerData), "RemoveGradedCard",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(RemoveGradedCardPostfix)));

            // Domain sync modules register their own patch sets (each guarded internally).
            TryModule("staff", Sync.StaffSync.ApplyPatches, h);
            TryModule("shopstate", Sync.ShopStateSync.ApplyPatches, h);
            TryModule("settings", Sync.SettingsSync.ApplyPatches, h);
            TryModule("market", Sync.MarketSync.ApplyPatches, h);
            TryModule("report", Sync.ReportSync.ApplyPatches, h);
            TryModule("containers", Sync.ContainerSync.ApplyPatches, h);
            TryModule("tournament", Sync.TournamentSync.ApplyPatches, h);
            TryModule("grading", Sync.GradingSync.ApplyPatches, h);
            TryModule("trades", Sync.TradeServe.ApplyPatches, h);
            TryModule("playtables", Sync.PlayTableSync.ApplyPatches, h);
            TryModule("cardboxes", Sync.CardBoxSync.ApplyPatches, h);
            TryModule("furnboxes", Sync.FurnBoxSync.ApplyPatches, h);
        }

        private static void TryModule(string name, Action<Harmony> apply, Harmony h)
        {
            try { apply(h); }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"Module patches failed ({name}): {e.Message}"); }
        }

        public static void ReduceCardIndexPostfix(int index, ECardExpansionType expansionType, bool isDestiny, int reduceAmount)
        {
            if (ApplyingRemoteCards || CoopCore.Role == CoopRole.None) return;
            try
            {
                var card = CPlayerData.GetCardData(index, expansionType, isDestiny);
                if (card != null)
                    CoopCore.Instance?.ForwardCardDelta(card, reduceAmount, isAdd: false);
            }
            catch { }
        }

        public static bool BoxDestroyedPrefix(InteractablePackagingBox_Item __instance)
        {
            if (!BoxSync.ApplyingRemote) BoxSync.LocalBoxDestroyed?.Invoke(__instance);
            return true;
        }

        public static bool ApplyingRemoteLicense;

        public static void LicenseUnlockPostfix(int index)
        {
            if (ApplyingRemoteLicense || CoopCore.Role == CoopRole.None) return;
            try { CoopCore.Instance?.ForwardLicense(index); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("LicenseUnlockPostfix forward failed: " + e.Message); }
        }

        public static bool FurnitureOrderPrefix(EObjectType objType, UnityEngine.Vector3 spawnPos, UnityEngine.Quaternion spawnRot)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            CoopCore.Instance?.ForwardFurniture((int)objType, spawnPos, spawnRot);
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "furniture delivered at the host's shop";
                CoopCore.Instance.RegisterLineTimer = 4f;
            }
            return false;
        }

        public static bool OrderPrefix(int restockIndex, int count)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            CoopCore.Instance?.ForwardOrder(restockIndex, count);
            return false; // no local phantom boxes; the host's delivery mirrors back
        }

        private static readonly System.Reflection.FieldInfo FiOobTimer =
            AccessTools.Field(typeof(RestockManager), "m_OutofBoundCheckTimer");

        public static void RestockUpdatePrefix(RestockManager __instance)
        {
            // client only: hold the OOB timer under its 5s trigger so the teleport sweep
            // (RestockManager.Update, guarded by `if (!(m_OutofBoundCheckTimer > 5f)) return`)
            // never runs; the host owns box placement and re-broadcasts it every 1.5s.
            if (CoopCore.Role != CoopRole.Client) return;
            try { FiOobTimer?.SetValue(__instance, 0f); } catch { }
        }

        /// <summary>Skip the move-preview teardown when there's no preview to tear down
        /// (a programmatically-spawned forwarded furniture order). The vanilla body
        /// dereferences m_MoveObjectPreviewModel unconditionally and NRE'd there,
        /// aborting the furniture spawn - so the table never arrived but was paid for.
        /// Runs only inside the game's own DisableMoveObjectPreviewMode call, so the
        /// ShelfManager provably exists (no fake-singleton hazard).</summary>
        public static bool DisablePreviewGuardPrefix()
        {
            try
            {
                var sm = CSingleton<ShelfManager>.Instance;
                // no live preview model = no interactive move in progress = nothing to
                // clean up; skipping avoids the NRE and lets the spawn finish
                if (sm == null || sm.m_MoveObjectPreviewModel == null) return false;
            }
            catch { return false; }
            return true; // real interactive move: run the vanilla teardown
        }

        public static bool RenamerBlockPrefix()
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "the host names the shop";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false;
        }

        public static bool ApplyingRemotePrice;

        public static void SetCardPricePostfix(CardData cardData, float priceSet)
        {
            if (ApplyingRemotePrice || CoopCore.Role == CoopRole.None) return;
            try { CoopCore.Instance?.ForwardCardPrice(cardData, priceSet); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("SetCardPricePostfix forward failed: " + e.Message); }
        }

        /// <summary>True while we're applying a card delta that came over the network,
        /// so the postfixes don't echo it back forever.</summary>
        public static bool ApplyingRemoteCards;

        // These postfixes run INSIDE the game's own AddCard/ReduceCard calls - including
        // the manual pack-open loop (CardOpeningSequence.OpenScreen) which AddCards all 7
        // cards BEFORE building its reveal lists. A throw escaping here aborts that loop
        // half-built, and the per-frame reveal then IndexOutOfRanges forever = the guest
        // "freezes on the first card". Never let a forward failure escape into the caller.
        public static void AddCardPostfix(CardData cardData, int addAmount)
        {
            if (ApplyingRemoteCards || CoopCore.Role == CoopRole.None) return;
            try { CoopCore.Instance?.ForwardCardDelta(cardData, addAmount, isAdd: true); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("AddCardPostfix forward failed: " + e.Message); }
        }

        public static void ReduceCardPostfix(CardData cardData, int reduceAmount)
        {
            if (ApplyingRemoteCards || CoopCore.Role == CoopRole.None) return;
            try { CoopCore.Instance?.ForwardCardDelta(cardData, reduceAmount, isAdd: false); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ReduceCardPostfix forward failed: " + e.Message); }
        }

        /// <summary>Mirror host-side graded-card REMOVALS (trade-in of a wanted-back graded
        /// card, grading re-submit, donation). Graded cards live in a separate inventory
        /// (m_GradedCardInventoryList) that ReduceCard never touches, so without this the
        /// other side keeps a ghost graded card the owner no longer has - the "graded card
        /// turned fake" report. Runs inside the game's RemoveGradedCard; must never throw.</summary>
        public static void RemoveGradedCardPostfix(CardData cardData)
        {
            if (ApplyingRemoteCards || CoopCore.Role == CoopRole.None) return;
            if (cardData == null || cardData.cardGrade <= 0) return;
            try { CoopCore.Instance?.ForwardGradedRemoval(cardData); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("RemoveGradedCardPostfix forward failed: " + e.Message); }
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

        public static bool SaveGuardPrefix()
        {
            // A joiner is a visitor in the HOST's save: it re-downloads the world at every
            // join, so client-side saving is pure waste - and worse, it would overwrite the
            // guest's OWN save slot with the host's shop. Block a save whenever we're a
            // client OR still holding a borrowed (host) world - the latter covers the
            // window AFTER a mid-session disconnect (Role back to None) while the guest is
            // still standing in the host's world, where a day-end autosave or a quit-save
            // used to slip through and pollute the guest's slot. The host's save is the
            // single source of truth. (No ref param: we only ever block, never reroute.)
            return CoopCore.Role != CoopRole.Client && !CoopCore.GuestBorrowedWorld;
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
            // The joiner set an item price: apply locally AND tell the host, whose price
            // table is authoritative and echoes to everyone (guarded against the echo).
            if (evt is CEventPlayer_ItemPriceChanged priceEvt && !ApplyingRemotePrice)
            {
                CoopCore.Instance?.ForwardItemPrice(priceEvt.m_ItemType, priceEvt.m_Price);
                return true; // let it apply locally too - instant feedback
            }
            return true;
        }
    }
}
