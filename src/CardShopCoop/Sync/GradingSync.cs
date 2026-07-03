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
            return CoopCore.Role != CoopRole.Client;
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
            float total = DeliveryFee + svc.m_CostPerCard * picked.Count;
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
                bw.Write((byte)Mathf.Clamp(serviceLevel, 0, 3));
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
        public void HostApplyOp(BinaryReader br)
        {
            int serviceLevel = Mathf.Clamp(br.ReadByte(), 0, 3);
            int n = Mathf.Min(br.ReadByte(), MaxSlots);
            var cards = new List<CardData>(n);
            for (int i = 0; i < n; i++) cards.Add(Msg.ReadCard(br));
            float clientFee = br.ReadSingle(); // informational only

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
                var svc = inv.m_MonsterData_SO.GetGradeCardServiceData(serviceLevel);
                float total = DeliveryFee + svc.m_CostPerCard * cards.Count;
                if (Mathf.Abs(total - clientFee) > 0.01f)
                    CoopPlugin.Log.LogWarning($"GradingSync: fee mismatch (client {clientFee}, host {total}) - using host value");

                // full / broke (the client pre-checks against the mirror, so this is a
                // tiny race window): give the cards BACK to the shared binder rather
                // than losing them - AddCard mirrors to the joiner via CardDelta
                if (CPlayerData.m_GradeCardInProgressList == null
                    || CPlayerData.m_GradeCardInProgressList.Count >= 4
                    || CPlayerData.m_CoinAmountDouble < (double)total)
                {
                    CoopPlugin.Log.LogWarning("GradingSync: submission rejected (slots/wallet), returning cards to binder");
                    for (int i = 0; i < cards.Count; i++) CPlayerData.AddCard(cards[i], 1);
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
