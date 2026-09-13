using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
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
    /// already forwards), so the host must NOT reduce again on enroll. A re-submitted
    /// already-graded card is handled the same way: selection uses CPlayerData.RemoveGradedCard,
    /// which IS mirrored - RemoveGradedCardPostfix -> ForwardGradedRemoval (GamePatches, since
    /// 1.0.22) applies the removal on the host AND relays it to every guest. So by the time the
    /// submit op reaches HostApplyOp, every peer's graded album has already dropped the submitted
    /// card, and the enrolled submission set carries the card data forward through grading; the
    /// host must NOT remove it again (a second RemoveGradedCard matches by identity and would
    /// delete a duplicate copy on every peer that held one). Graded results return via
    /// RestockManager.SpawnPackageBoxCard - a card box the host opens; the resulting AddCard
    /// calls mirror through CardDelta.
    ///
    /// THE CERT AUTHORITY RULE - THE HOST IS THE SOLE MINTER OF CERTIFICATES.
    /// A Grading Overhaul certificate serial is only unique while exactly one machine issues it.
    /// The guest must NEVER reach a Grading Overhaul code path that calls CertCounter.GetNextCert:
    /// every serial it holds arrives over the wire and is adopted VERBATIM. The wire already
    /// carries CardData.cardGrade unmodified in both directions (Net/Msg's card writer, and the
    /// graded digest in CoopCore), and the receive path is Remember-then-AddCard
    /// (CoopCore.ApplyCardDelta, and CoopCore.GradedAdopt) - GradingInterop.Remember burns and
    /// binds the HOST's serial into the guest's store without minting anything.
    /// Everything that could break that rule is blocked on the guest, and each block is commented
    /// where it lives:
    ///  - local submission never enrols locally (SubmitPrefix below, forwarded as a GradingOp),
    ///    and it is not forwarded at all if Grading Overhaul's own validator refuses it
    ///    (GoVetoesSubmit - HarmonyX gives GO no veto of its own here, so we have to ask);
    ///  - local maturation never runs: MatureBlockPrefix below stops the VANILLA path, and a
    ///    second patch on Grading Overhaul's OWN day-start prefix stops GO's. Patch ORDER cannot
    ///    do this job - under HarmonyX every prefix runs whatever the others return - and
    ///    believing otherwise is the hole 1.0.41 shipped with. Read the comment on those two
    ///    registrations before changing either;
    ///  - a serial the guest's store has ALREADY bound to a different card is REFUSED rather than
    ///    rebound (GradingInterop.CertFreeForCard). There is deliberately no automatic re-mint and
    ///    no silent overwrite: re-minting on the guest is the exact divergence this rule exists to
    ///    end, and overwriting the binding would make Grading Overhaul flag the RESIDENT card as a
    ///    fake. Collisions that already exist are reported (CoopCore's CERT COLLISION category) and
    ///    are genuinely unrepairable in place; the clean reset is a rejoin, which transfers the
    ///    host's grading store again.
    /// </summary>
    public class GradingSync : TickableCoopModule
    {
        /// <summary>The live module instance, for the static Harmony patches.</summary>
        public static GradingSync Instance;

        /// <summary>Set by CoopCore: client -> host op (MsgType.GradingOp).</summary>
        public Action<INetMessage> SendOp;

        /// <summary>Set by CoopCore: host -> clients state (MsgType.GradingState).</summary>
        public Action<INetMessage> BroadcastState;

        /// <summary>True while ClientApplyState rewrites the mirrored pending list, so
        /// no patch mistakes the authoritative copy for local player action.</summary>
        public static bool ApplyingRemote;

        // vanilla caps active submissions at 4 (GradeCardWebsiteUIScreen) and slots
        // per set at 8; wire caps get headroom but stay bounded.
        //
        // MaxSlots is VANILLA's eight - the SHAPE of a vanilla GradeCardSubmitSet and the pad
        // target for one, and that is the ONLY thing it may be used for. It is NOT the
        // submission cap and it is NOT a wire bound: with Grading Overhaul the submit screen
        // holds up to 52 cards (GradingInterop.MaxSubmitSlots, which reads GO's own MAX_SLOTS).
        // Every count that decides how many cards actually move - the submit op, the
        // host->guest GradingState broadcast (BuildState / ClientApplyInner) and the change
        // detector that gates it (ComputeHash) - reads MaxSubmitSlots. Using 8 in any of those
        // silently dropped every card past the eighth.
        private const int MaxSets = 8;
        private const int MaxSlots = 8;
        private const float DeliveryFee = 10f; // GradedCardSubmitSelectScreen.EvaluateTotalCost

        private static readonly FieldInfo FiShowingAlpha =
            Util.ReflectionSurface.RequiredField(typeof(GradedCardSubmitSelectScreen), "m_IsShowingCanvasGrpAlpha");
        private static readonly FieldInfo FiHidingAlpha =
            Util.ReflectionSurface.RequiredField(typeof(GradedCardSubmitSelectScreen), "m_IsHidingCanvasGrpAlpha");
        // The on-screen bill. Vanilla EvaluateTotalCost writes it as (10 + m_CostPerCard*n);
        // Grading Overhaul's EvaluateTotalCost prefix REPLACES that and writes the real
        // per-value fee (deliveryFee + sum(marketValue * tier.FeeMultiplier)) into this same
        // private field by reflection (decompiled-grading Grading Overhaul.decompiled.cs :14338,
        // _fiServiceTotalCost.SetValue at :14378). Whatever the guest SEES on the submit screen
        // lives here, so reading it forwards GO's actual number instead of the vanilla-flat guess.
        private static readonly FieldInfo FiServiceTotalCost =
            Util.ReflectionSurface.RequiredField(typeof(GradedCardSubmitSelectScreen), "m_ServiceTotalCost");

        /// <summary>Grading Overhaul's own submit validator,
        /// GradingSubmit_CompanyValidation_Patch.Prefix(GradedCardSubmitSelectScreen)
        /// (decompiled-grading :13032-13036) - a private static in a public static class, so
        /// reflection-only. Invoked, not reimplemented: it reads
        /// m_CurrentGradeCardSubmitSet directly, honours GO's own EnableMod switch, and shows GO's
        /// own error popup, so calling it gives the guest byte-for-byte the refusal a solo player
        /// would get. See <see cref="GoVetoesSubmit"/> for why we have to call it ourselves.</summary>
        private static readonly MethodInfo MiGoSubmitVeto = ResolveGoSubmitVeto();

        private static MethodInfo ResolveGoSubmitVeto()
        {
            try
            {
                var t = Util.ModParity.ResolveType(
                    "TCGCardShopSimulator.GradingOverhaul.GradingSubmit_CompanyValidation_Patch",
                    Util.GradingInterop.GradingAssembly);
                return t == null ? null
                    : Util.ReflectionSurface.OptionalMethod(t, "Prefix", new[] { typeof(GradedCardSubmitSelectScreen) });
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>One-shot latch for the fail-open notice in <see cref="GoVetoesSubmit"/>.</summary>
        private static bool _goVetoWarned;

        /// <summary>Did Grading Overhaul just reject this submission? True = do not forward it.
        ///
        /// WHY THIS EXISTS AT ALL: under HarmonyX a prefix returning false does not stop any other
        /// prefix (see the note on ApplyPatches), so GO's validator cannot stop OUR prefix from
        /// forwarding the op - it can only stop the vanilla method neither of us wants to run. A
        /// guest without this check ships the host a set GO would have refused, the host enrols it,
        /// and the cards mature into something the guest's own install considers illegal.
        ///
        /// FAILS OPEN, DELIBERATELY. If GO is absent there is nothing to veto; if GO is present but
        /// the validator cannot be reached (a rename, a refactor), we FORWARD anyway and log it
        /// once. The alternative - refusing every submission because a reflection lookup missed -
        /// would break grading for a guest whose install is otherwise fine, and the host still
        /// applies its own caps. An exception out of GO's validator is treated the same way.
        ///
        /// It runs GO's validator a SECOND time (HarmonyX already ran it and discarded the
        /// answer), which is safe and deliberate: the method only reads the submit set and calls
        /// GradingSubmitErrorPopup_Patch.ShowCustomError, which just re-shows the same popup with
        /// the same message (decompiled-grading :15524-15528). The one visible artefact is a
        /// duplicated "[GradingSubmit] Blocked" line in the log - do not go looking for two
        /// separate rejections.</summary>
        private static bool GoVetoesSubmit(GradedCardSubmitSelectScreen screen)
        {
            if (!Util.GradingInterop.Present)
                return false;
            if (MiGoSubmitVeto == null)
            {
                if (!_goVetoWarned)
                {
                    _goVetoWarned = true;
                    CoopPlugin.Log.LogWarning("GradingSync: Grading Overhaul is loaded but its submit validator could not be resolved"
                        + " (GradingSubmit_CompanyValidation_Patch.Prefix) - submissions are forwarded to the host WITHOUT that check,"
                        + " so a set Grading Overhaul would have refused (fake, mixed-company or already-regraded cards) can still be enrolled.");
                }
                return false;
            }
            try
            {
                return !(bool)MiGoSubmitVeto.Invoke(null, new object[] { screen });
            }
            catch (Exception e)
            {
                if (!_goVetoWarned)
                {
                    _goVetoWarned = true;
                    CoopPlugin.Log.LogWarning("GradingSync: Grading Overhaul's submit validator threw - " + e.Message
                        + " - forwarding the submission anyway (fail open)");
                }
                return false;
            }
        }

        private readonly SnapshotGate _gate = new SnapshotGate(1.5f, 15f, -6.8f);
        private GradeCardWebsiteUIScreen _website; // cached lookup (client UI refresh)

        // NEVER CSingleton<InventoryBase>.Instance: touched while no real manager
        // exists (host mid-session save load) the getter fabricates a fake empty
        // DontDestroyOnLoad InventoryBase that shadows the real one for the rest of
        // the run (see WorldSync.ResolveShelfManager). Static because ClientSubmit
        // is static; Unity fake-null re-resolves after scene loads.
        private static InventoryBase _inv;

        private static InventoryBase Inv()
        {
            if (_inv == null)
                _inv = UnityEngine.Object.FindObjectOfType<InventoryBase>();
            return _inv;
        }

        public GradingSync()
        {
            Instance = this;
        }

        public override string Name => "grading";

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        /// <summary>Disable static Harmony hooks before session state is torn down.</summary>
        public static void ClearLive()
        {
            Instance = null;
            ApplyingRemote = false;
        }

        public static void ActivateLive(GradingSync instance)
        {
            Instance = instance;
        }

        public override void Reset()
        {
            _gate.Reset(-6.8f);
            _website = null;
            _inv = null;
        }

        public override void ForceResend()
        {
            _gate.Force();
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // The joiner's submit confirm: intercept BEFORE the fee is charged and the
            // set lands in his never-maturing local list.
            //
            // Priority.Low IS NOT A VETO, and do not read it as one. Grading Overhaul puts two
            // PREFIXES on this same method: GradingSubmit_CompanyValidation_Patch at default
            // priority (decompiled-grading Grading Overhaul.decompiled.cs :13032) rejects fake
            // cards, mixed-company regrades and already-regraded cards, and
            // GradingJobLimitGuard_Submit at 800 (:13007) enforces the 4-job cap. Under HarmonyX -
            // which is what the game ships - EVERY prefix runs regardless of what any other one
            // returns; a false return only skips the ORIGINAL method (the full mechanism is
            // written out on the day-start patch below). So sorting ourselves last does NOT let GO
            // veto first: GO returns false, we still run, and 1.0.41 forwarded a submission GO had
            // just rejected. The low priority is kept only so GO's error popups are the ones the
            // player sees last; the veto itself is REPLICATED in ClientSubmit, which asks GO's own
            // validator before it forwards anything.
            Try(h, typeof(GradedCardSubmitSelectScreen), "OnPressSubmitButton",
                prefix: new HarmonyMethod(typeof(GradingSync), nameof(SubmitPrefix)) { priority = Priority.Low });

            // RestockManager.OnDayStarted is PURE grading maturation (verified: lines
            // 533-619 of the decompile do nothing else). The one mirrored OnDayStarted
            // that GamePatches lets through per host day would otherwise mature the
            // client's mirrored list - re-rolling grades locally and spawning a phantom
            // result box. Grading matures on host days only.
            //
            // THE PRIORITY AND `before` HERE BUY ORDER ONLY - THEY DO NOT SKIP GRADING OVERHAUL.
            // Read this before touching either: the game ships HarmonyX (BepInEx/core/0Harmony.dll,
            // HarmonyLib.Internal.Patching.HarmonyManipulator, MonoMod-based), NOT pardeike
            // Harmony 2.x, and the two do not agree about what a false return means.
            // HarmonyManipulator.WritePrefixes emits `runOriginal = true`, then calls EVERY prefix
            // in the sorted list back to back with NO branch between them; a bool-returning prefix
            // only gets its result folded in with `Ldloc runOriginal; And; Stloc runOriginal`, and
            // the single `Brfalse` that consumes that local is emitted AFTER the loop and skips
            // only the ORIGINAL METHOD BODY. There is no per-prefix skip anywhere in that emitter.
            // So "we return false, therefore GO's prefix never runs" - the claim 1.0.41 and the
            // first cut of 1.0.42 both shipped - is simply not how this runtime behaves. GO's
            // CompanyStamp_RestockManager_OnDayStartedPatch.Prefix (decompiled-grading :7977-7980,
            // [HarmonyPriority(800)]) runs on the guest every host day no matter what we return:
            // it forces m_MinutePassed=540 on the MIRRORED in-progress list (:7992-7997), finds no
            // pre-roll (PreRollOnSubmit wrote the HOST's store), falls into GradeJobWithCompany and
            // MINTS fresh certs from the GUEST's own counter (CertCounter.GetNextCert :8087,
            // :8099) - permanently advancing NextSerialByCompany and seeding the cert collisions
            // that have no repair - and it fills _completedJobs, so its [HarmonyPriority(0)]
            // postfix spawns a phantom package box.
            //
            // The registration below is kept because the ORDER half is correct and free
            // (PriorityComparer sorts descending, PatchSorter matches `before` against a Harmony
            // OWNER id, and GO's really is "munch.gradingoverhaul" - decompiled-grading :16959),
            // so if this ever runs on vanilla Harmony we are already in the right slot. It stops
            // VANILLA maturation, which is its whole job. What stops GO is the patch after it.
            Try(h, typeof(RestockManager), "OnDayStarted",
                prefix: new HarmonyMethod(typeof(GradingSync), nameof(MatureBlockPrefix))
                {
                    priority = 1000,
                    before = new[] { "munch.gradingoverhaul" },
                });

            // THE ACTUAL GRADING OVERHAUL BLOCK: patch GO's OWN prefix method.
            //
            // A Harmony patch method is an ordinary static method and the manipulator emits a
            // plain `call` to it - so from the point of view of a patch ON that method, GO's
            // prefix BODY is the "original" that HarmonyX's single `__runOriginal` gate skips.
            // Returning false here therefore really does stop GO's day-start work, which is the
            // one thing priority could never do. Nothing else about GO is touched: its postfix
            // still runs, still sees _completedJobs null (that field is only ever assigned inside
            // the body we skipped, and the postfix nulls it again after every host day it does
            // handle) and takes its early return at :8205-8208 - no phantom box, no minting, and
            // the guest's cert counter never moves.
            //
            // DEGRADES TO A NO-OP: no Grading Overhaul, a renamed class or a refactored prefix and
            // this simply does not register - one line in the log, and the vanilla block above is
            // unaffected.
            TryPatchGoDayStart(h);
        }

        /// <summary>Registers <see cref="GoMatureBlockPrefix"/> on Grading Overhaul's own day-start
        /// prefix. Separate from <see cref="ApplyPatches"/> so the reflection can fail alone and
        /// loudly - see the comment at the call site for why order-based blocking cannot work
        /// under HarmonyX.</summary>
        private static void TryPatchGoDayStart(Harmony h)
        {
            try
            {
                var t = Util.ModParity.ResolveType(
                    "TCGCardShopSimulator.GradingOverhaul.CompanyStamp_RestockManager_OnDayStartedPatch",
                    Util.GradingInterop.GradingAssembly);
                var m = t == null ? null : Util.ReflectionSurface.OptionalMethod(t, "Prefix", Type.EmptyTypes);
                if (m == null)
                {
                    // Present separates "GO is not installed" (normal - there is nothing to block)
                    // from "GO is installed but moved" (a real hole: that guest mints certs again).
                    if (Util.GradingInterop.Present)
                        CoopPlugin.Log.LogWarning("GradingSync: Grading Overhaul is loaded but its day-start patch class could not be resolved"
                            + " (CompanyStamp_RestockManager_OnDayStartedPatch.Prefix) - a JOINER in this session may still mint its own"
                            + " certificate numbers on every host day, which is what creates the cert collisions that cannot be repaired."
                            + " Check whether Grading Overhaul has been updated.");
                    else
                        CoopPlugin.Log.LogInfo("GradingSync: Grading Overhaul not loaded - no day-start guard needed");
                    return;
                }
                h.Patch(m, prefix: new HarmonyMethod(typeof(GradingSync), nameof(GoMatureBlockPrefix)));
                CoopPlugin.Log.LogInfo("GradingSync: Grading Overhaul day-start grading is guarded (while joining, the host mints every certificate)");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GradingSync: could not guard Grading Overhaul's day-start patch - " + e.Message
                    + " (vanilla maturation is still blocked; a joiner may still mint certificate numbers)");
            }
        }

        public static bool MatureBlockPrefix()
        {
            // block guest-side maturation whenever we're a client OR still standing in the
            // host's borrowed world (post-disconnect), same guard as the save-guard; the
            // host owns grading progress and the guest is a pure mirror
            return CoopCore.Role != CoopRole.Client && !CoopCore.GuestBorrowedWorld;
        }

        /// <summary>Prefix on GRADING OVERHAUL'S OWN day-start prefix (see TryPatchGoDayStart).
        /// Same predicate as <see cref="MatureBlockPrefix"/> and deliberately a separate method:
        /// this one names GO in a stack trace, and the two must stay independently removable.
        /// Side-effect-free, so running it on the host costs a comparison.</summary>
        public static bool GoMatureBlockPrefix()
        {
            return CoopCore.Role != CoopRole.Client && !CoopCore.GuestBorrowedWorld;
        }

        public static bool SubmitPrefix(GradedCardSubmitSelectScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            try
            {
                ClientSubmit(__instance);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogError("coop: grading client submit failed; the screen was left open so its cards can be returned: " + e);
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
            if (FiShowingAlpha?.GetValue(screen) is bool s && s)
                return;
            if (FiHidingAlpha?.GetValue(screen) is bool hd && hd)
                return;

            var set = CPlayerData.m_CurrentGradeCardSubmitSet;
            if (set == null || set.m_CardDataList == null)
                return;

            var picked = new List<CardData>();
            for (int i = 0; i < set.m_CardDataList.Count; i++)
            {
                var c = set.m_CardDataList[i];
                if (c != null && c.monsterType != EMonsterType.None)
                    picked.Add(c);
            }
            if (picked.Count == 0)
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.NoCardSelected);
                return;
            }

            // GRADING OVERHAUL'S VETO, ASKED FOR EXPLICITLY. HarmonyX ran GO's validator already
            // and threw its answer away (only the vanilla original is gated by a false return), so
            // this is the only place that answer can still stop us. Refuse exactly as GO would:
            // return WITHOUT sending the op and WITHOUT resetting the scratch set, so the screen
            // keeps every card and closing it refunds them through the vanilla OnCloseScreen ->
            // AddCard path (mirrored by CardDelta) - the same shape as every other abort here. GO
            // has already put its own error popup on screen explaining which card it objected to.
            // The vanilla 4-job cap that GO's OTHER submit prefix enforces is checked below, on
            // the mirrored in-progress list, and is the same limit with the same effect.
            if (GoVetoesSubmit(screen))
            {
                CoopPlugin.Log.LogInfo("GradingSync: submission refused by Grading Overhaul's own validator - not forwarded to the host");
                return;
            }

            // How many of those cards this session can actually carry. Vanilla's screen holds 8;
            // Grading Overhaul transpiles THIS method's slot count to 52 and builds the panels to
            // match (see GradingInterop.MaxSubmitSlots for the citations), so a GO guest can walk
            // in here with far more than eight selected.
            int cap = Util.GradingInterop.MaxSubmitSlots;
            if (picked.Count > cap)
            {
                // NEVER truncate-and-continue. Everything past the cap would be written nowhere,
                // and the scratch-set reset further down replaces m_CurrentGradeCardSubmitSet with
                // fresh empty slots - so those cards would be DESTROYED, not refunded: the binder
                // already gave them up at selection time (ReduceCard / RemoveGradedCard) and the
                // open screen is the only thing still holding them.
                //
                // Abort instead, exactly like every other client-side abort in this method (empty
                // selection, wallet, the 4-set cap, no host link): return WITHOUT sending the op
                // and WITHOUT resetting the scratch set,
                // so the screen keeps every card and closing it refunds them through the vanilla
                // OnCloseScreen -> AddCard path (mirrored by CardDelta). Surfaced through the
                // screen's own error popup - the same NotEnoughResourceTextPopup those aborts use -
                // plus a HUD line, because GenericNoSlot alone cannot say "split it up".
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.GenericNoSlot);
                CoopPlugin.Log.LogWarning($"GradingSync: {picked.Count} cards selected but only {cap} fit one co-op submission - submission aborted, cards left in the screen");
                if (CoopCore.Instance != null)
                {
                    CoopCore.Instance.RegisterLine = $"too many cards for one grading submission (max {cap}) - remove some, or close the screen to get them all back";
                    CoopCore.Instance.RegisterLineTimer = 6f;
                }
                return;
            }

            int serviceLevel = set.m_ServiceLevel;
            var inv = Inv();
            if (inv == null)
                return; // no live world = nothing vanilla could price either
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
                catch (System.Exception e) { Swallow.Log(e); } // any reflection hiccup falls through to the vanilla-flat total
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
            var gradOp = new GradingOpMessage
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
                ServiceLevel = (byte)Mathf.Clamp(serviceLevel, 0, 255),
                // client's fee view; host recomputes authoritatively
                Total = total,
                // WHICH grading company the guest was looking at. Without this the host had no
                // way to stamp the submission as a Grading Overhaul job at all: GO records the
                // company in an OnPressSubmitButton POSTFIX gated on the in-progress list already
                // containing the local scratch set (decompiled-grading :12879), which is never
                // true for a set the host enrolled off the wire - so GO's maturation fell back to
                // vanilla grades and the guest paid a GO price for them.
                //
                // Read RAW, exactly like GO's own recording postfixes (:7940, :12884). GO's
                // VALIDATION prefix (:13064) substitutes ConfigSettings.ActiveCompanyProfile when
                // the raw value is Cardinals, but that fallback belongs to validation only;
                // copying it here would stamp jobs with a company that never recorded them.
                // 255 (GradingInterop.NoCompany) when GO is absent or unreadable - the host
                // rejects it by the same allow-list test it applies to every other value, so a
                // GO-absent session enrolls exactly as it always did.
                CompanyId = (byte)Mathf.Clamp(Util.GradingInterop.CurrentCompanyId, 0, 255),
            };
            // cap, not MaxSlots: with Grading Overhaul this is 52. The cap check above already
            // guarantees picked.Count <= cap <= 255, so the Min/loop bound can no longer drop a
            // card - it only keeps the byte cast provably safe.
            for (int i = 0; i < picked.Count && i < cap; i++)
                gradOp.Cards.Add(picked[i]);
            inst.SendOp(gradOp);

            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);

            // Reset the scratch set BEFORE CloseScreen, exactly like vanilla: the cards
            // belong to the host's pending set now, and OnCloseScreen AddCard-refunds
            // anything still sitting in the slots (which would duplicate them).
            // Rebuilt with `cap` slots, not a flat 8: this replaces vanilla's own reset loop, and
            // that loop is precisely what Grading Overhaul transpiles from 8 to 52 (decompiled
            // GradedCardSubmitSelectScreen.cs :193 vs decompiled-grading :1498-1515). Handing a
            // GO-driven screen an 8-slot scratch set would leave its expanded panels indexing past
            // the list until GO's next OnOpenScreen resize; `cap` reproduces GO's own shape.
            var fresh = new GradeCardSubmitSet
            {
                m_ServiceLevel = serviceLevel,
                m_CardDataList = new List<CardData>(cap),
            };
            for (int j = 0; j < cap; j++)
                fresh.m_CardDataList.Add(new CardData());
            CPlayerData.m_CurrentGradeCardSubmitSet = fresh;

            screen.CloseScreen();
            try
            {
                screen.m_GradeCardWebsiteUIScreen?.UpdateSubmissionProgressPanelUI();
            }
            catch (System.Exception e) { Swallow.Log(e); }
            try
            {
                CSingleton<InteractionPlayerController>.Instance
                    ?.m_CollectionBinderFlipAnimCtrl?.SetCanUpdateSort(canSort: true);
            }
            catch (System.Exception e) { Swallow.Log(e); }

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
                var original = Util.ReflectionSurface.RequiredMethod(type, method);
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
            if (!inGame)
                return;
            if (!_gate.Due(dt))
                return;
            Guarded("host", () =>
            {
                var list = CPlayerData.m_GradeCardInProgressList;
                if (list == null)
                    return;
                int hash = ComputeHash(list);
                if (!_gate.ShouldSend(hash))
                    return;
                BroadcastState?.Invoke(BuildState(list));
            });
        }

        /// <summary>Host: a joiner submitted cards for grading. Replicates the body of
        /// GradedCardSubmitSelectScreen.OnPressSubmitButton (fee transaction, report
        /// supply cost, ReduceCoin, enroll in m_GradeCardInProgressList) WITHOUT going
        /// through the vanilla method - that method reads the host player's own
        /// m_CurrentGradeCardSubmitSet UI scratch buffer, which may hold the host's
        /// half-built submission. The binder is NOT reduced here: the client's selection
        /// already ReduceCard'd each card and the CardDelta mirror carried it over.</summary>
        /// <summary>Both host reject paths: give a refused submission's cards back. The
        /// guest's SELECTION already took every card from every peer - an ungraded card via
        /// the mirrored ReduceCard, and a GRADED card via RemoveGradedCardPostfix ->
        /// ForwardGradedRemoval (the 1.0.22 mirror), which the host applied AND relayed, so
        /// by reject time host + all guests are at -1. The restore therefore must reach
        /// EVERYONE, and a single host-side AddCard does exactly that (AddCardPostfix
        /// broadcasts the +1). The graded subtlety is WHICH album the add lands in: a bare
        /// AddCard would file an encoded-grade card into the UNGRADED array. Register the
        /// cert with Grading Overhaul first (GradingInterop.Remember, same as
        /// ApplyCardDelta's add path) so GO's AddCard patch steers it back into the graded
        /// album - on the host directly, and on every guest via the broadcast delta whose
        /// receive path does its own Remember. Everyone nets back to zero; no targeted
        /// send (that would double-credit the submitter on top of the broadcast).
        /// senderConn stays plumbed for future targeted repairs.</summary>
        private static void ReturnRejectedCards(List<CardData> cards, int senderConn)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null)
                    continue;
                // Wire-derived card: AddCard would mis-index (vanilla) or throw on
                // CardCountList[-1] (EPL) for content this PC doesn't have. Unlike the delta
                // path there is nothing to relay here - the restore IS the host's AddCard - so
                // the card really is lost, and skipping it silently left no trace at all.
                if (!CoopCore.CardSetInstalledHere(cards[i]))
                {
                    CoopCore.WarnRefusedCard(cards[i], "grade-return");
                    continue;
                }
                try
                {
                    if (cards[i].cardGrade > 10 && Util.GradingInterop.Present)
                        Util.GradingInterop.Remember(cards[i]);
                    CPlayerData.AddCard(cards[i], 1);
                }
                catch (Exception e)
                {
                    // Restoration is a recovery operation, so one bad card must not prevent
                    // the remaining cards from being returned. Include its complete wire
                    // identity: this is the last useful trace if the host cannot represent it.
                    CoopPlugin.Log.LogError($"coop: grading card restore failed (conn {senderConn}, card {CardIdentity(cards[i])}); card may be lost: {e}");
                }
            }
        }

        private static string CardIdentity(CardData card)
        {
            if (card == null)
                return "<null>";
            return $"monster={card.monsterType}, expansion={card.expansionType}, border={card.borderType}, grade={card.cardGrade}, foil={card.isFoil}, destiny={card.isDestiny}, champion={card.isChampionCard}";
        }

        private static void CompensateSubmission(List<CardData> cards, GradeCardSubmitSet set,
            bool coinQueued, float total, float reportSupplyCost, float permanentSupplyCost,
            bool reportMutated, bool permanentReportMutated, int senderConn, Exception failure)
        {
            CoopPlugin.Log.LogError($"coop: grading submission transaction failed (conn {senderConn}, cards {cards.Count}, chargeQueued={coinQueued}, total={total:F2}); compensating: {failure}");

            if (set != null && CPlayerData.m_GradeCardInProgressList != null)
            {
                if (CPlayerData.m_GradeCardInProgressList.Remove(set))
                    CoopPlugin.Log.LogInfo($"GradingSync: removed half-created grading set while compensating conn {senderConn}");
            }

            if (reportMutated)
                CPlayerData.m_GameReportDataCollect.supplyCost = reportSupplyCost;
            if (permanentReportMutated)
                CPlayerData.m_GameReportDataCollectPermanent.supplyCost = permanentSupplyCost;

            ReturnRejectedCards(cards, senderConn);

            if (coinQueued)
            {
                CoopPlugin.Log.LogInfo($"GradingSync: queuing compensating coin credit {total:F2} for conn {senderConn}");
                try
                {
                    CEventManager.QueueEvent(new CEventPlayer_AddCoin(total));
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError($"coop: compensating coin credit failed (conn {senderConn}, amount {total:F2}); wallet may remain charged: {e}");
                }
            }
        }

        private static bool TryEnrollGoJob(GradeCardSubmitSet set, int companyId, int serviceLevel)
        {
            if (!Util.GradingInterop.Present || !Util.GradingInterop.CanEnroll)
                return false;
            if (!Util.GradingInterop.IsAllowedCompany(companyId))
            {
                CoopPlugin.Log.LogInfo($"GradingSync: no GO enrollment - wire company {companyId} is not a website-selectable GradingCompany");
                return false;
            }

            try
            {
                bool useCheats = Util.GradingInterop.HostUseCheatsWebsite;
                bool registered = Util.GradingInterop.RegisterJobCompany(set, companyId, useCheats);
                int jobId = Util.GradingInterop.NextJobId();
                int encoded = jobId > 0 ? Util.GradingInterop.EncodeServiceLevel(companyId, serviceLevel, jobId) : 0;
                if (encoded == 0 && jobId > 0)
                    encoded = 100000 + jobId * 10000 + companyId * 1000 + Mathf.Clamp(serviceLevel, 0, 999);

                if (encoded > 0
                    && Util.GradingInterop.TryDecodeServiceLevel(encoded, out int backCompany, out int backTier)
                    && backCompany == companyId && backTier == serviceLevel)
                {
                    set.m_ServiceLevel = encoded;
                    // A failure here can occur after GO has burned certs. Keep the set as a
                    // degraded vanilla job, matching the old behavior, rather than charging
                    // and then losing the submission while attempting to undo GO's store.
                    Util.GradingInterop.PreRollJob(set, companyId, useCheats, jobId);
                    CoopPlugin.Log.LogInfo($"GradingSync: enrolled guest submission as a Grading Overhaul job - company {Util.GradingInterop.CompanyName(companyId)}, tier {serviceLevel}, jobId {jobId} (serviceLevel {encoded})");
                    return true;
                }

                CoopPlugin.Log.LogWarning($"GradingSync: GO service-level encode failed round-trip (company {companyId}, tier {serviceLevel}, jobId {jobId}, encoded {encoded}) - keeping the raw tier"
                    + (registered ? "; company registry entry stands" : "; company registry entry also failed"));
            }
            catch (Exception e)
            {
                // Do not leave an encoded level behind when pre-roll or a later GO step
                // fails; that would make the fallback look like a valid GO job at maturation.
                set.m_ServiceLevel = serviceLevel;
                CoopPlugin.Log.LogError($"coop: GO grading enrollment failed after the submission was accepted (company {companyId}, tier {serviceLevel}); keeping a degraded vanilla job: {e}");
            }
            return false;
        }

        public void HostApplyOp(GradingOpMessage message, int senderConn)
        {
            // Widened from 0..3 to 0..255 to match the sender: Grading Overhaul's extra service
            // tiers (GetTierCount(company) can exceed 4) would otherwise be truncated here and the
            // set enrolled with the wrong duration/fee. GO's own GetGradeCardServiceData prefix
            // clamps any out-of-range level to Count-1 before vanilla's raw list indexer runs
            // (decompiled-grading Grading Overhaul.decompiled.cs :13971-13993), so a stray high
            // tier cannot crash vanilla in a GO session. In a GO-ABSENT session no legitimate
            // guest can produce a tier > 3, and the flat-recompute path below guards its raw
            // vanilla GetGradeCardServiceData call so a spoofed high tier can't throw there either.
            int serviceLevel = Mathf.Clamp(message.ServiceLevel, 0, 255);

            // Card count. VALIDATED, never clamped - this used to be Min(count, 8), which meant a
            // guest submitting more than eight had the tail of his submission silently discarded
            // here as well as on the send side. Truncating an ACCEPTED submission is the card-loss
            // bug; a count past the cap can only be a malformed or hostile message, so it is
            // refused whole rather than partially applied.
            //
            // READ FIRST, judge after. The bound check used to sit ABOVE the read loop and return
            // without touching the cards - which quietly made this the one reject path that
            // DESTROYS the submission: the guest's selection already took every card off every
            // peer (mirrored ReduceCard / ForwardGradedRemoval), his screen was reset the moment
            // he pressed submit, and this message is the only remaining copy. Returning before
            // decoding it threw that copy away with no refund, while every other reject path here
            // hands the cards back through ReturnRejectedCards.
            //
            // Reading first is safe: the DTO's Deserialize already decoded the whole card blob (the
            // count is a BYTE, so the loop is bounded at 255 iterations no matter what a crafted
            // message claims - it cannot spin the reader; a payload too short for the count it
            // advertises makes Msg.ReadCard throw during deserialize, which lands in Dispatch's
            // existing catch). Every op carries its own payload, so nothing shared is misaligned.
            //
            // The cap is the HOST's: 8 without Grading Overhaul (unchanged legacy behavior), GO's
            // MAX_SLOTS with it. Version + PluginHash parity means a GO guest can only join a GO
            // host, so both ends agree on the number.
            int cap = Util.GradingInterop.MaxSubmitSlots;
            var cards = message.Cards;
            int n = cards.Count;
            if (n > cap)
            {
                CoopPlugin.Log.LogWarning($"GradingSync: malformed grading submission from conn {senderConn} (cardCount {n} > cap {cap}) - submission refused whole; returning its {cards.Count} cards to the shared binder so they are not destroyed");
                if (CoopCore.Role == CoopRole.Host)
                    ReturnRejectedCards(cards, senderConn);
                return;
            }
            float clientFee = message.Total; // GO present: the REAL bill the guest saw; GO absent: client's flat view
            // The grading company the guest's website was showing (GradingInterop.NoCompany=255
            // when he has no Grading Overhaul). Validated below before it is allowed to stamp
            // anything; see the enrollment block after the set is enrolled.
            int companyId = message.CompanyId;

            if (CoopCore.Role != CoopRole.Host || cards.Count == 0)
                return;

            GradeCardSubmitSet set = null;
            bool coinQueued = false;
            bool reportMutated = false;
            bool permanentReportMutated = false;
            float reportSupplyCost = 0f;
            float permanentSupplyCost = 0f;
            float total = 0f;
            try
            {
                // ---- content-parity gate (before ANY charge, enrollment or set construction) ----
                // Nothing above ever asked whether this PC actually HAS the card sets the wire is
                // describing. One-sided content packs are allowed (1.0.34), so a guest can submit
                // a card from an expansion the host never installed - and a card the host lacks
                // does not decode into nothing, it decodes toward the TETRAMON fallback (see
                // CoopCore.CardSetInstalledHere for why GetShownMonsterList is the only honest
                // oracle). Enrolled, that becomes a dead slot-0 card sitting in a grading set GO's
                // own submissions can never contain: it grades, matures, and spawns as a card the
                // guest never sent.
                //
                // WHOLE-submission rejection, not a filter. The fee was computed guest-side over
                // ALL the cards (with GO it is the per-value bill off his screen), so charging it
                // for a smaller job would bill him for work that is not being done - and silently
                // shrinking a submission is the same card-loss class this file already refuses on
                // the send side. So: if even one card can't live here, nothing is charged, nothing
                // is enrolled, and everything goes back.
                //
                // The refund is best-effort by construction: ReturnRejectedCards can only AddCard
                // what this PC can represent, and warns (memoized, WarnRefusedCard) for each card
                // it cannot - which is exactly the set that triggered this rejection. Those stay
                // lost on the host; the guest's peers hold what they hold. Better than the
                // alternative, which was enrolling them as phantom Tetramon slots.
                int refused = 0;
                for (int i = 0; i < cards.Count; i++)
                {
                    if (CoopCore.CardSetInstalledHere(cards[i]))
                        continue;
                    refused++;
                    CoopCore.WarnRefusedCard(cards[i], "grade-submit");
                }
                if (refused > 0)
                {
                    CoopPlugin.Log.LogWarning($"GradingSync: grading submission from conn {senderConn} refused whole - {refused} of {cards.Count} cards are from card sets this PC doesn't have, and the fee was priced for all {cards.Count}. Nothing charged, nothing enrolled; returning the cards");
                    ReturnRejectedCards(cards, senderConn);
                    return;
                }

                var inv = Inv();
                if (inv == null)
                {
                    CoopPlugin.Log.LogWarning($"GradingSync: no InventoryBase (world loading?) - submission dropped; returning {cards.Count} cards");
                    ReturnRejectedCards(cards, senderConn);
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
                    catch (System.Exception e) { Swallow.Log(e); } // diagnostic only; never blocks the charge
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

                // NOTE: a re-submitted GRADED card was ALREADY removed from every peer's graded
                // album at selection time. The binder's selection calls CPlayerData.RemoveGradedCard,
                // which since 1.0.22 is mirrored by RemoveGradedCardPostfix -> ForwardGradedRemoval
                // (GamePatches ~320): the host applied that removal and relayed it to all guests. So
                // by the time we get here host + all guests are already at -1 for this card. We must
                // NOT remove it again - RemoveGradedCard matches by identity and would delete a
                // SECOND identical copy wherever the album held a duplicate, and (running outside
                // GamePatches.ApplyingRemoteCards) its postfix would re-broadcast another GradedRemove
                // so every guest with a duplicate loses one too. The enrolled submission set below
                // carries the card data forward through grading.

                // the vanilla submit body (GradedCardSubmitSelectScreen.OnPressSubmitButton)
                // Snapshot the report values before the first mutation. If any later operation
                // throws, restoring these fields keeps the economic/report transaction whole.
                reportSupplyCost = CPlayerData.m_GameReportDataCollect.supplyCost;
                permanentSupplyCost = CPlayerData.m_GameReportDataCollectPermanent.supplyCost;
                PriceChangeManager.AddTransaction(0f - total, ETransactionType.GradingFee, serviceLevel);
                CPlayerData.m_GameReportDataCollect.supplyCost -= total;
                reportMutated = true;
                CPlayerData.m_GameReportDataCollectPermanent.supplyCost -= total;
                permanentReportMutated = true;
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(total));
                coinQueued = true;

                set = new GradeCardSubmitSet
                {
                    m_ServiceLevel = serviceLevel,
                    m_DayPassed = 0,
                    m_MinutePassed = 0f,
                    // capacity only - the pad-to-vanilla-8 below still decides the final shape for
                    // an un-enrolled set, and a GO-enrolled set is trimmed rather than padded
                    m_CardDataList = new List<CardData>(Mathf.Max(cards.Count, MaxSlots)),
                };
                set.m_CardDataList.AddRange(cards);
                CPlayerData.m_GradeCardInProgressList.Add(set);

                // Enrollment is intentionally isolated: GO can burn certificates before a
                // reflection/codec failure. Its established fallback is a degraded vanilla
                // job, which is safe because the core fee/list mutation is already consistent.
                bool enrolled = TryEnrollGoJob(set, companyId, serviceLevel);

                // Pad to vanilla's 8 slots ONLY when this stayed a vanilla job. GO's chain ends by
                // trimming every empty slot off an enrolled set (TrimEmptyCards, :12936), and its
                // progress UI is written against that trimmed shape; padding an enrolled set back
                // out would hand GO a list shape its own submissions never produce. The guest
                // re-pads SHORT sets back to eight on receive (ClientApplyInner) because the
                // vanilla status screen repaints exactly m_CardDataList.Count panels; a set that
                // arrives with more than eight keeps its real length for GO's own UI. That claim
                // used to be stated as "the trimmed set still displays correctly there", which
                // only held while a set was <= 8 cards: the broadcast truncated every set to
                // eight, so a longer job crossed the wire mutilated no matter what shape the host
                // built. The wire now carries GradingInterop.MaxSubmitSlots (see BuildState).
                // Moved below the list Add (it used to sit above): enrollment has to see
                // the set already enrolled, and nothing between the two reads the list.
                if (!enrolled)
                {
                    while (set.m_CardDataList.Count < MaxSlots)
                        set.m_CardDataList.Add(new CardData()); // vanilla sets carry 8 slots
                }

                ForceResend(); // the joiner sees his pending set on the next tick

                // ...and the HOST's own grading app, if he happens to be standing in it. Vanilla
                // repaints those progress panels only on screen OPEN, so a set enrolled off the
                // wire while the host had the website up did not appear until he backed out and
                // reopened it. Grading Overhaul's panel patches hang off this same call, so the
                // company/tier text repaints with it. Same move ClientSubmit makes for the guest
                // after its own submit; cached lookup, because FindObjectOfType is not free and
                // this runs on a message. try/catch: a UI refresh must never lose the enrollment
                // that already happened above.
                try
                {
                    if (_website == null)
                        _website = UnityEngine.Object.FindObjectOfType<GradeCardWebsiteUIScreen>();
                    if (_website != null && _website.gameObject.activeInHierarchy)
                        _website.UpdateSubmissionProgressPanelUI();
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            catch (Exception e)
            {
                if (CoopCore.Role == CoopRole.Host)
                    CompensateSubmission(cards, set, coinQueued, total,
                        reportSupplyCost, permanentSupplyCost, reportMutated, permanentReportMutated, senderConn, e);
                else
                    CoopPlugin.Log.LogError($"coop: grading op failed outside host role (conn {senderConn}): {e}");
            }
        }

        // ---------------- client ----------------

        /// <summary>Client: adopt the host's pending-submission list wholesale. The
        /// joiner never enrolls sets locally (submit is forwarded), so there is no
        /// local-edit-vs-echo race - the broadcast is simply the truth.</summary>
        public void ClientApplyState(GradingStateMessage message)
        {
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(GradingStateMessage message)
        {
            int sets = Mathf.Min(message.Sets.Count, MaxSets);
            var list = new List<GradeCardSubmitSet>(sets);
            for (int i = 0; i < sets; i++)
            {
                var se = message.Sets[i];
                var set = new GradeCardSubmitSet
                {
                    // Int32 to match BuildState: a Grading Overhaul m_ServiceLevel is an encoded
                    // (company, tier, jobId) value, not a 0..255 tier index.
                    m_ServiceLevel = se.ServiceLevel,
                    m_DayPassed = se.DayPassed,
                    m_MinutePassed = se.MinutePassed,
                    m_CardDataList = new List<CardData>(MaxSlots),
                };
                // The vanilla status UI draws days-left as (m_ServiceDays - m_DayPassed) with
                // NO lower clamp. On the host a set is destroyed the instant it matures, so it
                // never shows <=0. The guest doesn't run maturation, so a mirrored m_DayPassed
                // that reaches/exceeds m_ServiceDays would render "-1 Jour". Clamp so days-left
                // is always >= 1 for anything the guest can display (matches vanilla semantics).
                //
                // SKIPPED for a GO-encoded level. The clamp's bound comes from vanilla
                // GetGradeCardServiceData, and GO's prefix on that method clamps any level past
                // the vanilla list to Count-1 (decompiled-grading :13980-13992) - so for an
                // encoded level it would answer with the LAST VANILLA TIER's m_ServiceDays, a
                // number belonging to a different tier than the job is actually running. Clamping
                // against it would corrupt the very display this is meant to protect. The
                // alternative - decoding and asking GO's ServiceTierControlCenter.GetTier for the
                // real Days - is the more accurate fix but needs two more reflected internals
                // (GetTier plus the Tier.Days field) inside the receive path; skipping is chosen
                // because it is the option that cannot throw and cannot be wrong: GO's own UI
                // patches already own the display for an encoded level, so vanilla's day maths is
                // not the thing rendering it.
                if (set.m_ServiceLevel < Util.GradingInterop.EncodedLevelFloor)
                {
                    try
                    {
                        var svc = Inv()?.m_MonsterData_SO?.GetGradeCardServiceData(set.m_ServiceLevel);
                        if (svc != null)
                            set.m_DayPassed = Mathf.Clamp(set.m_DayPassed, 0, Mathf.Max(0, svc.m_ServiceDays - 1));
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                }
                // Read bound = MaxSubmitSlots, the same number BuildState uses. An 8 here would
                // STOP READING mid-set on any Grading Overhaul job longer than eight cards and
                // leave decoding the rest of the set's card blob from garbage. Both ends agree on
                // the number: Version + PluginHash parity means a GO guest can only ever join a GO
                // host.
                int n = Mathf.Min(se.Cards.Count, Util.GradingInterop.MaxSubmitSlots);
                for (int j = 0; j < n; j++)
                    set.m_CardDataList.Add(se.Cards[j]);
                // MaxSlots (8) is right HERE and only here: it is the vanilla SHAPE.
                // GradedCardSetCheckStatusScreen repaints exactly m_CardDataList.Count
                // panels; short lists would leave stale cards from the previous page. A set
                // that already carries more than eight (a GO job) is left at its real length.
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
            catch (System.Exception e) { Swallow.Log(e); }
        }

        // ---------------- wire / hash ----------------

        private static GradingStateMessage BuildState(List<GradeCardSubmitSet> list)
        {
            var msg = new GradingStateMessage();
            int count = Mathf.Min(list.Count, MaxSets);
            for (int i = 0; i < count; i++)
            {
                var set = list[i];
                var entry = new GradingSetEntry
                {
                    // Int32, NOT a clamped byte. With Grading Overhaul a live m_ServiceLevel is an
                    // ENCODED (company, tier, jobId) triple starting at 100000 (ServiceLevelCodec,
                    // decompiled-grading :5601) - every one of those truncated to 255 on the way out,
                    // so the guest's grading app rendered garbage for the HOST's own jobs long before
                    // any of this touched guest submissions. A tier index needed one byte; an encoded
                    // level needs the whole int.
                    ServiceLevel = set != null ? set.m_ServiceLevel : 0,
                    DayPassed = (byte)Mathf.Clamp(set != null ? set.m_DayPassed : 0, 0, 255),
                    MinutePassed = set != null ? set.m_MinutePassed : 0f,
                };
                var cards = set != null ? set.m_CardDataList : null;
                // MaxSubmitSlots, NOT MaxSlots. A Grading Overhaul set legitimately holds up to
                // 52 cards (enrolled sets are GO-trimmed to their real length, and the guest's
                // own submissions come back through here), so an 8 here truncated EVERY set on
                // the way out - the guest's grading app showed the first eight cards of his own
                // 30-card job and nothing else. The count still ships as a byte, and
                // MaxSubmitSlots is itself bounded to 255, so the cast stays provably safe.
                int n = cards != null ? Mathf.Min(cards.Count, Util.GradingInterop.MaxSubmitSlots) : 0;
                for (int j = 0; j < n; j++)
                    entry.Cards.Add(cards[j] ?? new CardData());
                msg.Sets.Add(entry);
            }
            return msg;
        }

        /// <summary>Change detector over everything BuildState sends. m_MinutePassed is
        /// folded at hour granularity (LightManager bumps it +60 per game hour anyway),
        /// so the rebroadcast cadence is one per game hour, not per frame.</summary>
        private static int ComputeHash(List<GradeCardSubmitSet> list)
        {
            int hash = 17;
            hash = hash * 31 + list.Count;
            for (int i = 0; i < list.Count && i < MaxSets; i++)
            {
                var set = list[i];
                if (set == null)
                    continue;
                hash = hash * 31 + set.m_ServiceLevel;
                hash = hash * 31 + set.m_DayPassed;
                hash = hash * 31 + (int)(set.m_MinutePassed / 60f);
                var cards = set.m_CardDataList;
                if (cards == null)
                    continue;
                // Same bound as BuildState. The change detector has to cover everything the wire
                // carries: hashing only the first eight cards of a 52-card set meant an edit past
                // the eighth produced an IDENTICAL hash, so the rebroadcast never fired and the
                // guest kept a stale set until the 15s heal timer happened to come round.
                for (int j = 0; j < cards.Count && j < Util.GradingInterop.MaxSubmitSlots; j++)
                {
                    var c = cards[j];
                    if (c == null)
                        continue;
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
