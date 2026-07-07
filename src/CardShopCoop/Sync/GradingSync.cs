using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CardShopCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Card grading for the joiner. Grading is DAY-based (GradeCardSubmitSet.m_DayPassed
    /// matures in RestockManager.OnDayStarted after 540 in-game minutes, then the graded
    /// cards return as a card package box) - but the joiner's day never advances and his
    /// save is discarded, so anything he submits locally is simply lost. Instead the
    /// joiner's submit confirm is blocked BEFORE it charges, forwarded as a GradingOp,
    /// and enrolled on the HOST (fee + m_GradeCardInProgressList) where it matures on
    /// real host days. The host broadcasts the pending list (GradingState, hash-gated)
    /// so the joiner's phone grading app shows the truth, and the client's own copy of
    /// RestockManager.OnDayStarted is blocked so the mirrored list never self-matures
    /// into a phantom result box.
    ///
    /// Binder accounting: cards leave the shared binder at SELECTION time (the binder's
    /// OnRightMouseButtonUp calls CPlayerData.ReduceCard, which the CardDelta mirror
    /// already forwards), so the host must NOT reduce again on enroll. The one gap is
    /// re-submitting an already-graded card: selection uses CPlayerData.RemoveGradedCard
    /// (a direct list edit, not mirrored), so HostApplyOp repeats that removal by
    /// identity host-side. Graded results return via RestockManager.SpawnPackageBoxCard -
    /// a card box the host opens; the resulting AddCard calls mirror through CardDelta.
    /// </summary>
    public class GradingSync
    {
        /// <summary>The live module instance, for the static Harmony patches.</summary>
        public static GradingSync Instance;

        /// <summary>Set by CoopCore: client -> host op (MsgType.GradingOp).</summary>
        public Action<Action<BinaryWriter>> SendOp;

        /// <summary>Set by CoopCore: host -> clients state (MsgType.GradingState).</summary>
        public Action<Action<BinaryWriter>> BroadcastState;

        /// <summary>True while ClientApplyState rewrites the mirrored pending list, so
        /// no patch mistakes the authoritative copy for local player action.</summary>
        public static bool ApplyingRemote;

        // vanilla caps active submissions at 4 (GradeCardWebsiteUIScreen) and slots
        // per set at 8; wire caps get headroom but stay bounded
        private const int MaxSets = 8;
        private const int MaxSlots = 8;
        private const float DeliveryFee = 10f; // GradedCardSubmitSelectScreen.EvaluateTotalCost

        private static readonly FieldInfo FiShowingAlpha =
            AccessTools.Field(typeof(GradedCardSubmitSelectScreen), "m_IsShowingCanvasGrpAlpha");
        private static readonly FieldInfo FiHidingAlpha =
            AccessTools.Field(typeof(GradedCardSubmitSelectScreen), "m_IsHidingCanvasGrpAlpha");
        // The on-screen bill. Vanilla EvaluateTotalCost writes it as (10 + m_CostPerCard*n);
        // Grading Overhaul's EvaluateTotalCost prefix REPLACES that and writes the real
        // per-value fee (deliveryFee + sum(marketValue * tier.FeeMultiplier)) into this same
        // private field by reflection (decompiled-grading Grading Overhaul.decompiled.cs :14338,
        // _fiServiceTotalCost.SetValue at :14378). Whatever the guest SEES on the submit screen
        // lives here, so reading it forwards GO's actual number instead of the vanilla-flat guess.
        private static readonly FieldInfo FiServiceTotalCost =
            AccessTools.Field(typeof(GradedCardSubmitSelectScreen), "m_ServiceTotalCost");

        private float _timer;
        private int _lastHash;
        private float _heal;
        private GradeCardWebsiteUIScreen _website; // cached lookup (client UI refresh)

        // NEVER CSingleton<InventoryBase>.Instance: touched while no real manager
        // exists (host mid-session save load) the getter fabricates a fake empty
        // DontDestroyOnLoad InventoryBase that shadows the real one for the rest of
        // the run (see WorldSync.ResolveShelfManager). Static because ClientSubmit
        // is static; Unity fake-null re-resolves after scene loads.
        private static InventoryBase _inv;

        private static InventoryBase Inv()
        {
            if (_inv == null) _inv = UnityEngine.Object.FindObjectOfType<InventoryBase>();
            return _inv;
        }

        public GradingSync()
        {
            Instance = this;
        }

        public void Reset()
        {
            _timer = -6.8f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            _website = null;
            _inv = null;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = 999f; // beats the hash gate even if the real hash is 0
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // The joiner's submit confirm: intercept BEFORE the fee is charged and the
            // set lands in his never-maturing local list.
            Try(h, typeof(GradedCardSubmitSelectScreen), "OnPressSubmitButton",
                prefix: new HarmonyMethod(typeof(GradingSync), nameof(SubmitPrefix)));

            // RestockManager.OnDayStarted is PURE grading maturation (verified: lines
            // 533-619 of the decompile do nothing else). The one mirrored OnDayStarted
            // that GamePatches lets through per host day would otherwise mature the
            // client's mirrored list - re-rolling grades locally and spawning a phantom
            // result box. Grading matures on host days only.
            Try(h, typeof(RestockManager), "OnDayStarted",
                prefix: new HarmonyMethod(typeof(GradingSync), nameof(MatureBlockPrefix)));
        }

        public static bool MatureBlockPrefix()
        {
            // block guest-side maturation whenever we're a client OR still standing in the
            // host's borrowed world (post-disconnect), same guard as the save-guard; the
            // host owns grading progress and the guest is a pure mirror
            return CoopCore.Role != CoopRole.Client && !CoopCore.GuestBorrowedWorld;
        }

        public static bool SubmitPrefix(GradedCardSubmitSelectScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            try
            {
                ClientSubmit(__instance);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GradingSync submit: " + e.Message);
            }
            // never fall through to vanilla on the client: it would charge the mirrored
            // wallet (forwarded as a second contribution) AND strand the set locally.
            // On failure the screen stays open; closing it returns the cards via the
            // vanilla OnCloseScreen -> AddCard path (mirrored by CardDelta).
            return false;
        }

        /// <summary>Client: validate exactly like vanilla, forward the op, then reset the
        /// scratch set and close the screen so OnCloseScreen has nothing to refund.</summary>
        private static void ClientSubmit(GradedCardSubmitSelectScreen screen)
        {
            // mid canvas fade: vanilla no-ops, so do we
            if (FiShowingAlpha?.GetValue(screen) is bool s && s) return;
            if (FiHidingAlpha?.GetValue(screen) is bool hd && hd) return;

            var set = CPlayerData.m_CurrentGradeCardSubmitSet;
            if (set == null || set.m_CardDataList == null) return;

            var picked = new List<CardData>();
            for (int i = 0; i < set.m_CardDataList.Count; i++)
            {
                var c = set.m_CardDataList[i];
                if (c != null && c.monsterType != EMonsterType.None) picked.Add(c);
            }
            if (picked.Count == 0)
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.NoCardSelected);
                return;
            }

            int serviceLevel = set.m_ServiceLevel;
            var inv = Inv();
            if (inv == null) return; // no live world = nothing vanilla could price either
            var svc = inv.m_MonsterData_SO.GetGradeCardServiceData(serviceLevel);
            // Vanilla-flat fee: the number the ORIGINAL EvaluateTotalCost would show. This is
            // the fallback whenever Grading Overhaul isn't driving the screen.
            float total = DeliveryFee + svc.m_CostPerCard * picked.Count;
            // With Grading Overhaul present, the vanilla-flat formula above is the WRONG model:
            // GO's EvaluateTotalCost prefix priced this submission per card market value and wrote
            // the real bill into GradedCardSubmitSelectScreen.m_ServiceTotalCost - the exact number
            // on the guest's screen right now. Forward THAT so the host charges what the guest saw,
            // not a flat guess (root cause of the ~$12k charge for a ~$200k on-screen bill). Only
            // trust it when GO is actually loaded AND the live screen field holds a sane, finite,
            // positive value; otherwise keep the vanilla-flat total so GO-absent sessions are
            // byte-for-byte unchanged.
            if (Util.GradingInterop.Present && FiServiceTotalCost != null && screen != null)
            {
                try
                {
                    if (FiServiceTotalCost.GetValue(screen) is float onScreen
                        && !float.IsNaN(onScreen) && !float.IsInfinity(onScreen) && onScreen > 0f)
                        total = onScreen;
                }
                catch { } // any reflection hiccup falls through to the vanilla-flat total
            }
            if (CPlayerData.m_CoinAmountDouble < (double)total)
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.Money);
                return;
            }
            // the mirrored pending list is the host's truth; respect the vanilla 4-set cap
            if (CPlayerData.m_GradeCardInProgressList != null
                && CPlayerData.m_GradeCardInProgressList.Count >= 4)
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.GenericNoSlot);
                return;
            }

            var inst = Instance;
            if (inst?.SendOp == null)
            {
                CoopPlugin.Log.LogWarning("GradingSync: no host link, submission cancelled");
                return;
            }
            inst.SendOp(bw =>
            {
                // Widened from 0..3 to 0..255: Grading Overhaul cycles up to
                // ServiceTierControlCenter.GetTierCount(company) tiers, which can exceed 4
                // (decompiled-grading Grading Overhaul.decompiled.cs :13952-13957 does
                // m_ServiceLevel = (level+1) % GetTierCount, and GetTierCount at :2146 is
                // company-defined). Clamping to 3 truncated GO's extra tiers, so the host
                // enrolled the wrong duration/fee. A byte still bounds the wire; the receiver's
                // matching widen keeps the true tier. Safe because vanilla GetGradeCardServiceData
                // is an unchecked list index (decompiled MonsterData_ScriptableObject.cs :63-66,
                // would throw on out-of-range) BUT GO's GetGradeCardServiceData prefix clamps any
                // out-of-range serviceLevel to Count-1 before that index runs (decompiled-grading
                // :13971-13993), and a high tier only ever exists in a GO session - so no stray
                // value can reach vanilla's raw indexer.
                bw.Write((byte)Mathf.Clamp(serviceLevel, 0, 255));
                bw.Write((byte)Mathf.Min(picked.Count, MaxSlots));
                for (int i = 0; i < picked.Count && i < MaxSlots; i++)
                    Msg.WriteCard(bw, picked[i]);
                bw.Write(total); // client's fee view; host recomputes authoritatively
            });

            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);

            // Reset the scratch set BEFORE CloseScreen, exactly like vanilla: the cards
            // belong to the host's pending set now, and OnCloseScreen AddCard-refunds
            // anything still sitting in the slots (which would duplicate them).
            var fresh = new GradeCardSubmitSet
            {
                m_ServiceLevel = serviceLevel,
                m_CardDataList = new List<CardData>(MaxSlots),
            };
            for (int j = 0; j < MaxSlots; j++) fresh.m_CardDataList.Add(new CardData());
            CPlayerData.m_CurrentGradeCardSubmitSet = fresh;

            screen.CloseScreen();
            try { screen.m_GradeCardWebsiteUIScreen?.UpdateSubmissionProgressPanelUI(); } catch { }
            try
            {
                CSingleton<InteractionPlayerController>.Instance
                    ?.m_CollectionBinderFlipAnimCtrl?.SetCanUpdateSort(canSort: true);
            }
            catch { }

            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "cards sent for grading - they mature on the host's days";
                CoopCore.Instance.RegisterLineTimer = 4f;
            }
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

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame) return;
            _timer += dt;
            if (_timer < 1.5f) return;
            _timer -= 1.5f;
            try
            {
                var list = CPlayerData.m_GradeCardInProgressList;
                if (list == null) return;
                int hash = ComputeHash(list);
                _heal += 1.5f;
                if (hash == _lastHash && _heal < 15f) return;
                _lastHash = hash;
                _heal = 0f;
                BroadcastState?.Invoke(bw => WriteState(bw, list));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingSync host: " + e.Message); }
        }

        /// <summary>Host: a joiner submitted cards for grading. Replicates the body of
        /// GradedCardSubmitSelectScreen.OnPressSubmitButton (fee transaction, report
        /// supply cost, ReduceCoin, enroll in m_GradeCardInProgressList) WITHOUT going
        /// through the vanilla method - that method reads the host player's own
        /// m_CurrentGradeCardSubmitSet UI scratch buffer, which may hold the host's
        /// half-built submission. The binder is NOT reduced here: the client's selection
        /// already ReduceCard'd each card and the CardDelta mirror carried it over.</summary>
        /// <summary>Both host reject paths: give a refused submission's cards back to the
        /// right places. UNGRADED cards go through the host's AddCard - its postfix mirrors
        /// the +1 to every peer, which is correct because the guest's selection-time
        /// ReduceCard was mirrored to all. A GRADED (re-grade) card is different: its
        /// selection-time RemoveGradedCard was a direct list edit NO mirror carried, so at
        /// reject time only the SUBMITTING guest's album is short and the host's album
        /// still holds the card. A host-side AddCard would (a) land it in the UNGRADED
        /// array - a silent card-type conversion - and (b) duplicate it host-side. Instead
        /// send a targeted graded re-add to the submitter alone: their ApplyCardDelta
        /// routes a >10 encoded grade through GradingInterop.Remember + AddCard, which
        /// Grading Overhaul steers back into the graded album.</summary>
        private static void ReturnRejectedCards(List<CardData> cards, int senderConn)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null) continue;
                if (cards[i].cardGrade > 0)
                    CoopCore.Instance?.SendCardDeltaTo(senderConn, cards[i], 1, isAdd: true);
                else
                    CPlayerData.AddCard(cards[i], 1);
            }
        }

        public void HostApplyOp(BinaryReader br, int senderConn)
        {
            // Widened from 0..3 to 0..255 to match the sender: Grading Overhaul's extra service
            // tiers (GetTierCount(company) can exceed 4) would otherwise be truncated here and the
            // set enrolled with the wrong duration/fee. GO's own GetGradeCardServiceData prefix
            // clamps any out-of-range level to Count-1 before vanilla's raw list indexer runs
            // (decompiled-grading Grading Overhaul.decompiled.cs :13971-13993), so a stray high
            // tier cannot crash vanilla in a GO session. In a GO-ABSENT session no legitimate
            // guest can produce a tier > 3, and the flat-recompute path below guards its raw
            // vanilla GetGradeCardServiceData call so a spoofed high tier can't throw there either.
            int serviceLevel = Mathf.Clamp(br.ReadByte(), 0, 255);
            int n = Mathf.Min(br.ReadByte(), MaxSlots);
            var cards = new List<CardData>(n);
            for (int i = 0; i < n; i++) cards.Add(Msg.ReadCard(br));
            float clientFee = br.ReadSingle(); // GO present: the REAL bill the guest saw; GO absent: client's flat view

            if (CoopCore.Role != CoopRole.Host || cards.Count == 0) return;

            try
            {
                var inv = Inv();
                if (inv == null)
                {
                    // same outcome as the old fee-lookup failure, minus the fake manager
                    CoopPlugin.Log.LogWarning("GradingSync: no InventoryBase (world loading?) - submission dropped");
                    return;
                }
                // Decide what to actually charge the host wallet.
                //   GO present: the guest's screen was driven by Grading Overhaul's per-value
                //     pricing model (deliveryFee + sum(marketValue * tier.FeeMultiplier)). We
                //     CANNOT recompute that here - GO's EvaluateTotalCost reads the HOST player's
                //     own UI scratch state (m_CurrentGradeCardSubmitSet, CurrentWebsiteCompany),
                //     which reflects nothing about the guest's submission. So forward the real
                //     bill the guest already saw and validated: clientFee. Sanity-gate it (finite,
                //     non-negative, affordable) so a corrupt/hostile wire can't charge garbage.
                //     Also NOT computing vanilla GetGradeCardServiceData here avoids that raw list
                //     indexer throwing on one of GO's out-of-vanilla-range tiers.
                //   GO absent: the guest ran the vanilla-flat model, so recompute it authoritatively
                //     (unchanged legacy behavior) and warn on any mismatch.
                float total;
                if (Util.GradingInterop.Present)
                {
                    if (float.IsNaN(clientFee) || float.IsInfinity(clientFee) || clientFee < 0f)
                    {
                        CoopPlugin.Log.LogWarning($"GradingSync: rejecting non-finite/negative clientFee {clientFee} - submission dropped");
                        ReturnRejectedCards(cards, senderConn);
                        return;
                    }
                    total = clientFee;
                    // Diagnostic: what would the old vanilla-flat recompute have charged? Log both
                    // at Info when they disagree so a bad GO integration is easy to spot in the log.
                    try
                    {
                        // clamp defensively - a GO tier can exceed vanilla's list bounds; GO's own
                        // GetGradeCardServiceData prefix clamps for us, but guard for the diagnostic
                        // read in case GO's patch ever fails to apply.
                        int svcIdx = Mathf.Clamp(serviceLevel, 0, inv.m_MonsterData_SO.m_GradeCardServiceDataList.Count - 1);
                        var svcDiag = inv.m_MonsterData_SO.GetGradeCardServiceData(svcIdx);
                        float vanillaFlat = DeliveryFee + svcDiag.m_CostPerCard * cards.Count;
                        if (Mathf.Abs(vanillaFlat - clientFee) > 0.01f)
                            CoopPlugin.Log.LogInfo($"GradingSync: GO fee forwarded - charging guest's on-screen bill {clientFee} (vanilla-flat recompute would have been {vanillaFlat})");
                    }
                    catch { } // diagnostic only; never blocks the charge
                }
                else
                {
                    var svc = inv.m_MonsterData_SO.GetGradeCardServiceData(serviceLevel);
                    total = DeliveryFee + svc.m_CostPerCard * cards.Count;
                    if (Mathf.Abs(total - clientFee) > 0.01f)
                        CoopPlugin.Log.LogWarning($"GradingSync: fee mismatch (client {clientFee}, host {total}) - using host value");
                }

                // full / broke (the client pre-checks against the mirror, so this is a
                // tiny race window): give the cards BACK to the shared binder rather
                // than losing them - AddCard mirrors to the joiner via CardDelta
                if (CPlayerData.m_GradeCardInProgressList == null
                    || CPlayerData.m_GradeCardInProgressList.Count >= 4
                    || CPlayerData.m_CoinAmountDouble < (double)total)
                {
                    CoopPlugin.Log.LogWarning("GradingSync: submission rejected (slots/wallet), returning cards to binder");
                    ReturnRejectedCards(cards, senderConn);
                    return;
                }

                // A re-submitted GRADED card left the joiner's graded album through
                // CPlayerData.RemoveGradedCard - a direct list edit no mirror carries.
                // Repeat it here by identity so the host album doesn't keep a ghost copy
                // that would duplicate once the re-graded card returns.
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i].cardGrade > 0)
                    {
                        try { CPlayerData.RemoveGradedCard(cards[i], ignoreGradedCardIndex: true); }
                        catch (Exception e) { CoopPlugin.Log.LogWarning("GradingSync degrade: " + e.Message); }
                    }
                }

                // the vanilla submit body (GradedCardSubmitSelectScreen.OnPressSubmitButton)
                PriceChangeManager.AddTransaction(0f - total, ETransactionType.GradingFee, serviceLevel);
                CPlayerData.m_GameReportDataCollect.supplyCost -= total;
                CPlayerData.m_GameReportDataCollectPermanent.supplyCost -= total;
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(total));

                var set = new GradeCardSubmitSet
                {
                    m_ServiceLevel = serviceLevel,
                    m_DayPassed = 0,
                    m_MinutePassed = 0f,
                    m_CardDataList = new List<CardData>(MaxSlots),
                };
                set.m_CardDataList.AddRange(cards);
                while (set.m_CardDataList.Count < MaxSlots)
                    set.m_CardDataList.Add(new CardData()); // vanilla sets carry 8 slots
                CPlayerData.m_GradeCardInProgressList.Add(set);

                ForceResend(); // the joiner sees his pending set on the next tick
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingSync op: " + e.Message); }
        }

        // ---------------- client ----------------

        /// <summary>Client: adopt the host's pending-submission list wholesale. The
        /// joiner never enrolls sets locally (submit is forwarded), so there is no
        /// local-edit-vs-echo race - the broadcast is simply the truth.</summary>
        public void ClientApplyState(BinaryReader br)
        {
            ApplyingRemote = true;
            try { ClientApplyInner(br); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(BinaryReader br)
        {
            int sets = Mathf.Min(br.ReadByte(), MaxSets);
            var list = new List<GradeCardSubmitSet>(sets);
            for (int i = 0; i < sets; i++)
            {
                var set = new GradeCardSubmitSet
                {
                    m_ServiceLevel = br.ReadByte(),
                    m_DayPassed = br.ReadByte(),
                    m_MinutePassed = br.ReadSingle(),
                    m_CardDataList = new List<CardData>(MaxSlots),
                };
                // The vanilla status UI draws days-left as (m_ServiceDays - m_DayPassed) with
                // NO lower clamp. On the host a set is destroyed the instant it matures, so it
                // never shows <=0. The guest doesn't run maturation, so a mirrored m_DayPassed
                // that reaches/exceeds m_ServiceDays would render "-1 Jour". Clamp so days-left
                // is always >= 1 for anything the guest can display (matches vanilla semantics).
                try
                {
                    var svc = Inv()?.m_MonsterData_SO?.GetGradeCardServiceData(set.m_ServiceLevel);
                    if (svc != null)
                        set.m_DayPassed = Mathf.Clamp(set.m_DayPassed, 0, Mathf.Max(0, svc.m_ServiceDays - 1));
                }
                catch { }
                int n = Mathf.Min(br.ReadByte(), MaxSlots);
                for (int j = 0; j < n; j++) set.m_CardDataList.Add(Msg.ReadCard(br));
                // GradedCardSetCheckStatusScreen repaints exactly m_CardDataList.Count
                // panels; short lists would leave stale cards from the previous page
                while (set.m_CardDataList.Count < MaxSlots)
                    set.m_CardDataList.Add(new CardData());
                list.Add(set);
            }
            CPlayerData.m_GradeCardInProgressList = list;

            // if the grading app is open right now, repaint its progress panels
            // (they normally refresh only on screen open)
            try
            {
                if (_website == null)
                    _website = UnityEngine.Object.FindObjectOfType<GradeCardWebsiteUIScreen>();
                if (_website != null && _website.gameObject.activeInHierarchy)
                    _website.UpdateSubmissionProgressPanelUI();
            }
            catch { }
        }

        // ---------------- wire / hash ----------------

        private static void WriteState(BinaryWriter bw, List<GradeCardSubmitSet> list)
        {
            int count = Mathf.Min(list.Count, MaxSets);
            bw.Write((byte)count);
            for (int i = 0; i < count; i++)
            {
                var set = list[i];
                bw.Write((byte)Mathf.Clamp(set != null ? set.m_ServiceLevel : 0, 0, 255));
                bw.Write((byte)Mathf.Clamp(set != null ? set.m_DayPassed : 0, 0, 255));
                bw.Write(set != null ? set.m_MinutePassed : 0f);
                var cards = set != null ? set.m_CardDataList : null;
                int n = cards != null ? Mathf.Min(cards.Count, MaxSlots) : 0;
                bw.Write((byte)n);
                for (int j = 0; j < n; j++)
                    Msg.WriteCard(bw, cards[j] ?? new CardData());
            }
        }

        /// <summary>Change detector over everything WriteState sends. m_MinutePassed is
        /// folded at hour granularity (LightManager bumps it +60 per game hour anyway),
        /// so the rebroadcast cadence is one per game hour, not per frame.</summary>
        private static int ComputeHash(List<GradeCardSubmitSet> list)
        {
            int hash = 17;
            hash = hash * 31 + list.Count;
            for (int i = 0; i < list.Count && i < MaxSets; i++)
            {
                var set = list[i];
                if (set == null) continue;
                hash = hash * 31 + set.m_ServiceLevel;
                hash = hash * 31 + set.m_DayPassed;
                hash = hash * 31 + (int)(set.m_MinutePassed / 60f);
                var cards = set.m_CardDataList;
                if (cards == null) continue;
                for (int j = 0; j < cards.Count && j < MaxSlots; j++)
                {
                    var c = cards[j];
                    if (c == null) continue;
                    hash = hash * 31 + (int)c.monsterType;
                    hash = hash * 31 + (int)c.expansionType;
                    hash = hash * 31 + (int)c.borderType;
                    hash = hash * 31 + ((c.isFoil ? 1 : 0) | (c.isDestiny ? 2 : 0) | (c.isChampionCard ? 4 : 0));
                    hash = hash * 31 + c.cardGrade;
                }
            }
            return hash;
        }
    }
}
