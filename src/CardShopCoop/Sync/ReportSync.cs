using CardShopCoop.Util;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The shared "how did our day go?" moment. Every counter in
    /// CPlayerData.m_GameReportDataCollect is filled by the host simulation, and the
    /// joiner's day-end events are suppressed, so without this the son stands in a dark
    /// shop while dad reads the numbers - and his phone review app stays frozen at the
    /// join-time snapshot.
    ///
    /// Host: reviews stream out as they land (hash-gated state broadcast, nudged by an
    /// AddCustomerReview postfix), and the moment the host opens the end-of-day report
    /// the same snapshot is pushed with an open-screen flag so both screens show the
    /// same numbers at the same time.
    ///
    /// Client: the report screen opens READ-ONLY. Its buttons must not advance anything -
    /// the vanilla path would run LightManager.GoNextDay and charge the game-event host
    /// fee through the forwarded ReduceCoin (a second, phantom charge). So BOTH the
    /// click-anywhere continue and the visible next-day button are rerouted to
    /// CloseClientReport(), which conveniently IS the vanilla bookkeeping (append
    /// past-list, reset day collect) that keeps the phone's report history aligned with
    /// the host's - and is what hands the joiner his movement back.
    /// </summary>
    public class ReportSync : TickableCoopModule
    {
        /// <summary>True while ClientApplyState writes host data, so our patches never
        /// mistake a sync write for local play.</summary>
        public static bool ApplyingRemote;

        public Action<INetMessage> BroadcastState; // set by CoopCore: host -> clients

        private const int ReviewTail = 15; // enough to bridge a missed heal; reviews are rare

        // host: set by patches (static, patches can't see the instance), drained by HostTick
        private static bool s_openPending;
        private static GameReportDataCollect s_openSnapshot; // struct copy: survives the reset on host continue
        private static bool s_reviewsDirty;

        // client: the report the open-screen broadcast displayed. If the HOST hits
        // "next day" first, its reset report heals onto the client before the son
        // presses continue - CloseScreen would then append zeros to his history.
        private static GameReportDataCollect s_clientOpenReport;

        // client: TRUE only between "the mirror actually opened the recap" and the close that
        // consumes it. s_clientOpenReport alone can't say that - it is a struct, so "never
        // filled" and "a legitimately all-zero day" look identical - and IsActive() can be
        // true for a screen this mirror never opened (a stray vanilla open, or one that
        // outlived a session). Without this flag those closes filed zeros (or yesterday's
        // numbers) into the joiner's phone history: the exact phantom day the close guard
        // exists to prevent. When it is false we still CLOSE - the joiner gets his legs back -
        // we just let the live host-synced numbers be what CloseScreen files.
        private static bool s_haveOpenReport;

        // client UI state we must read before opening a fullscreen lock
        private static readonly System.Reflection.FieldInfo FiIsLerping =
            ReflectionSurface.RequiredField(typeof(EndOfDayReportScreen), "m_IsLerpingNumber");
        // ...and the screen's own input latch, which we have to hand back the way OpenScreen
        // expects it (see CloseClientReport)
        private static readonly System.Reflection.FieldInfo FiHoldingMouseDown =
            ReflectionSurface.RequiredField(typeof(EndOfDayReportScreen), "m_IsHoldingMouseDown");
        private static readonly System.Reflection.FieldInfo FiMouseDownTime =
            ReflectionSurface.RequiredField(typeof(EndOfDayReportScreen), "m_MouseDownTime");
        private static readonly System.Reflection.FieldInfo FiPhoneMode =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_IsPhoneScreenMode");
        private static readonly System.Reflection.FieldInfo FiCashMode =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_IsCashCounterMode");

        private readonly SnapshotGate _gate = new SnapshotGate(2f, 15f, -4.1f);
        private int _reviewSeq; // client: m_CustomerReviewCount high-water mark (dedup key)

        // NEVER CSingleton<>.Instance for these: the open-screen broadcast can land
        // during the client's reload loading screen, where the getter fabricates a
        // fake empty DontDestroyOnLoad object that shadows the real screen/controller
        // for the rest of the run (see WorldSync.ResolveShelfManager). Static because
        // TryOpenReportScreen is; Unity fake-null re-resolves after scene loads.
        private static EndOfDayReportScreen _screen;
        private static InteractionPlayerController _ipc;

        public override string Name => "report";

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public override void Dispose()
        {
            base.Dispose();
            ApplyingRemote = false;
            s_openPending = false;
            s_openSnapshot = default(GameReportDataCollect);
            s_reviewsDirty = false;
            s_clientOpenReport = default(GameReportDataCollect);
            s_haveOpenReport = false;
            _screen = null;
            _ipc = null;
        }

        public override void Reset()
        {
            _gate.Reset(-4.1f);
            _reviewSeq = -1;
            s_openPending = false;
            s_openSnapshot = default(GameReportDataCollect);
            s_reviewsDirty = false;
            // last session's recap must not ride along: a stale copy here would be filed
            // into the NEXT save's phone history the first time the joiner closes a report
            s_clientOpenReport = default(GameReportDataCollect);
            s_haveOpenReport = false;
            _screen = null;
            _ipc = null;
        }

        public override void ForceResend()
        {
            _gate.Force();
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // Host: the report broadcast keys off the host OPENING the screen, not off
            // 21:00 - OpenScreen is when the day's numbers are final and being looked at,
            // which is exactly the shared moment we want on both monitors.
            Try(h, typeof(EndOfDayReportScreen), "OpenScreen",
                postfix: new HarmonyMethod(typeof(ReportSync), nameof(ReportOpenedPostfix)));

            // Host: stream each landed review promptly (the hash would catch it within a
            // tick anyway; the postfix makes the intent explicit and instant).
            Try(h, typeof(CustomerReviewManager), "AddCustomerReview",
                prefix: new HarmonyMethod(typeof(ReportSync), nameof(ReviewAddPrefix)),
                postfix: new HarmonyMethod(typeof(ReportSync), nameof(ReviewAddPostfix)));

            // Client: continue closes read-only instead of advancing the world.
            Try(h, typeof(EndOfDayReportScreen), "OnPressGoNextButton",
                prefix: new HarmonyMethod(typeof(ReportSync), nameof(NextButtonPrefix)));

            // Client: belt-and-braces - no code path may ever run the day-advance +
            // host-event-fee coroutine on a joiner.
            Try(h, typeof(EndOfDayReportScreen), "OnPressGoNextDay",
                prefix: new HarmonyMethod(typeof(ReportSync), nameof(NextDayBlockPrefix)));

            // A forced client close can interrupt EndDayReportTextUI.UpdateLerp before it
            // reaches its normal sound cleanup. Always clear the looping recap sound on close.
            Try(h, typeof(EndOfDayReportScreen), "CloseScreen",
                postfix: new HarmonyMethod(typeof(ReportSync), nameof(ClientReportClosedPostfix)));
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = ReflectionSurface.RequiredMethod(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed: {type.Name}.{method}: {e.Message}");
            }
        }

        public static void ReportOpenedPostfix()
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            try
            {
                // OpenScreen doubles as a toggle: only a real open broadcasts
                if (!EndOfDayReportScreen.IsActive())
                    return;
            }
            catch (System.Exception e) { Swallow.Log(e); return; }
            s_openSnapshot = CPlayerData.m_GameReportDataCollect; // value copy
            s_openPending = true;
        }

        public static void ReviewAddPrefix(out int __state)
        {
            __state = CPlayerData.m_CustomerReviewCount;
        }

        public static void ReviewAddPostfix(int __state)
        {
            // AddCustomerReview can decline to add (duplicate text roll) - only a count
            // change is a real review
            if (CoopCore.Role == CoopRole.Host && CPlayerData.m_CustomerReviewCount != __state)
                s_reviewsDirty = true;
        }

        public static bool NextButtonPrefix(EndOfDayReportScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            bool lerping = false;
            try
            {
                lerping = FiIsLerping != null && (bool)FiIsLerping.GetValue(__instance);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            if (lerping)
                return true; // vanilla behavior: fast-forward the count-up
            // Vanilla's other branch runs OnPressGoNextDay() whenever GetHasDayEnded() is
            // true - and on a joiner it usually IS. The DayTime mirror clears
            // m_HasDayEnded on the host's 2s beat, but LightManager.Update re-latches it
            // every frame in between (EvaluateTimeClock clamps the mirrored clock to 21:00,
            // which is the hour that sets the flag), so the vanilla path would advance the
            // joiner's OWN day and charge the game-event host fee a second, phantom time
            // through the forwarded ReduceCoin; in the one frame out of ~120 where the flag
            // is momentarily clear it would instead do nothing at all, leaving him locked in
            // the screen. Neither is what we want: close read-only instead.
            CloseClientReport();
            return false;
        }

        public static bool NextDayBlockPrefix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            // This is the big visible "next day" button, and it used to be a silent
            // no-op on a joiner: the ONLY way out of the recap was the click-anywhere
            // raw-input reroute above, so a son who politely aimed at the button sat
            // there with his movement locked (OpenScreen runs EnterLockMoveMode and only
            // CloseScreen releases it) - and TryOpenReportScreen's IsActive() guard then
            // swallowed every later night's report. So the button closes the recap too.
            // It still must never reach the vanilla body: GoNextDay is the host's call,
            // and the game-event host fee would be charged a second, phantom time
            // through the forwarded ReduceCoin.
            CloseClientReport();
            return false;
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || BroadcastState == null)
                return;

            // the open-moment ships immediately (not on the 2s grid): the snapshot was
            // taken at OpenScreen time, so even a host racing to "next day" can't feed
            // the client a reset report
            if (s_openPending)
            {
                s_openPending = false;
                var snap = s_openSnapshot;
                Guarded("host", () =>
                {
                    BroadcastState(BuildState(snap, openScreen: true));
                });
                return;
            }

            if (!_gate.Due(dt))
                return;
            Guarded("host", () =>
            {
                if (s_reviewsDirty)
                {
                    s_reviewsDirty = false;
                    _gate.Force(); // bust the gate: ship the new review this tick
                }
                int hash = HashState();
                if (!_gate.ShouldSend(hash))
                    return;
                var live = CPlayerData.m_GameReportDataCollect;
                BroadcastState(BuildState(live, openScreen: false));
            });
        }

        private static int HashState()
        {
            var r = CPlayerData.m_GameReportDataCollect;
            int h = 17;
            h = h * 31 + r.customerVisited;
            h = h * 31 + r.checkoutCount;
            h = h * 31 + r.customerDisatisfied;
            h = h * 31 + r.customerBoughtItem;
            h = h * 31 + r.customerBoughtCard;
            h = h * 31 + r.customerPlayed;
            h = h * 31 + r.storeExpGained;
            h = h * 31 + r.storeLevelGained;
            h = h * 31 + r.itemAmountSold;
            h = h * 31 + r.cardAmountSold;
            h = h * 31 + (int)(r.totalPlayTableTime * 100f);
            h = h * 31 + (int)(r.totalItemEarning * 100f);
            h = h * 31 + (int)(r.totalCardEarning * 100f);
            h = h * 31 + (int)(r.totalPlayTableEarning * 100f);
            h = h * 31 + (int)(r.supplyCost * 100f);
            h = h * 31 + (int)(r.upgradeCost * 100f);
            h = h * 31 + (int)(r.employeeCost * 100f);
            h = h * 31 + (int)(r.rentCost * 100f);
            h = h * 31 + (int)(r.billCost * 100f);
            h = h * 31 + r.cardPackOpened;
            h = h * 31 + r.smellyCustomerCleaned;
            h = h * 31 + r.manualCheckoutCount;
            h = h * 31 + r.gemMintCardObtained;
            h = h * 31 + CPlayerData.m_CustomerReviewCount;
            return h;
        }

        private static ReportStateMessage BuildState(GameReportDataCollect r, bool openScreen)
        {
            var msg = new ReportStateMessage
            {
                OpenScreen = openScreen,
                CustomerVisited = r.customerVisited,
                CheckoutCount = r.checkoutCount,
                CustomerDisatisfied = r.customerDisatisfied,
                CustomerBoughtItem = r.customerBoughtItem,
                CustomerBoughtCard = r.customerBoughtCard,
                CustomerPlayed = r.customerPlayed,
                StoreExpGained = r.storeExpGained,
                StoreLevelGained = r.storeLevelGained,
                ItemAmountSold = r.itemAmountSold,
                CardAmountSold = r.cardAmountSold,
                TotalPlayTableTime = r.totalPlayTableTime,
                TotalItemEarning = r.totalItemEarning,
                TotalCardEarning = r.totalCardEarning,
                TotalPlayTableEarning = r.totalPlayTableEarning,
                SupplyCost = r.supplyCost,
                UpgradeCost = r.upgradeCost,
                EmployeeCost = r.employeeCost,
                RentCost = r.rentCost,
                BillCost = r.billCost,
                CardPackOpened = r.cardPackOpened,
                SmellyCustomerCleaned = r.smellyCustomerCleaned,
                ManualCheckoutCount = r.manualCheckoutCount,
                GemMintCardObtained = r.gemMintCardObtained,
            };

            // reviews: lifetime count doubles as a sequence number, so the client can
            // append exactly the ones it hasn't seen (list itself is capped at 50)
            var reviews = CPlayerData.m_CustomerReviewDataList;
            msg.ReviewCount = CPlayerData.m_CustomerReviewCount;
            msg.ReviewScoreAverage = CPlayerData.m_CustomerReviewScoreAverage;
            int n = Mathf.Min(reviews != null ? reviews.Count : 0, ReviewTail);
            for (int i = 0; i < n; i++)
            {
                var rv = reviews[reviews.Count - n + i]; // oldest-first tail
                msg.Reviews.Add(new ReportReviewEntry
                {
                    CustomerReviewType = (int)rv.customerReviewType,
                    StarLevel = (byte)Mathf.Clamp(rv.starLevel, 0, 255),
                    TextSOGoodBadLevel = (byte)Mathf.Clamp(rv.textSOGoodBadLevel, 0, 255),
                    TextSOIndex = rv.textSOIndex,
                    Day = rv.day,
                    Hour = (byte)Mathf.Clamp(rv.hour, 0, 255),
                    Minute = (byte)Mathf.Clamp(rv.minute, 0, 255),
                    ItemType = rv.itemType, // modded ids travel as HOST ids (translated by the DTO)
                    CustomerName = rv.customerName ?? "",
                });
            }
            return msg;
        }

        // ---------------- client ----------------

        public void ClientApplyState(ReportStateMessage message)
        {
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(ReportStateMessage message)
        {
            bool openScreen = message.OpenScreen;

            var r = default(GameReportDataCollect);
            r.customerVisited = message.CustomerVisited;
            r.checkoutCount = message.CheckoutCount;
            r.customerDisatisfied = message.CustomerDisatisfied;
            r.customerBoughtItem = message.CustomerBoughtItem;
            r.customerBoughtCard = message.CustomerBoughtCard;
            r.customerPlayed = message.CustomerPlayed;
            r.storeExpGained = message.StoreExpGained;
            r.storeLevelGained = message.StoreLevelGained;
            r.itemAmountSold = message.ItemAmountSold;
            r.cardAmountSold = message.CardAmountSold;
            r.totalPlayTableTime = message.TotalPlayTableTime;
            r.totalItemEarning = message.TotalItemEarning;
            r.totalCardEarning = message.TotalCardEarning;
            r.totalPlayTableEarning = message.TotalPlayTableEarning;
            r.supplyCost = message.SupplyCost;
            r.upgradeCost = message.UpgradeCost;
            r.employeeCost = message.EmployeeCost;
            r.rentCost = message.RentCost;
            r.billCost = message.BillCost;
            r.cardPackOpened = message.CardPackOpened;
            r.smellyCustomerCleaned = message.SmellyCustomerCleaned;
            r.manualCheckoutCount = message.ManualCheckoutCount;
            r.gemMintCardObtained = message.GemMintCardObtained;
            // host truth replaces the joiner's near-zero local counters (his own pack
            // opens etc. are folded into the host numbers only where the host saw them;
            // m_GameReportDataCollectPermanent stays local so achievements keep their
            // per-player pacing)
            CPlayerData.m_GameReportDataCollect = r;

            int totalCount = message.ReviewCount;
            float average = message.ReviewScoreAverage;
            var entries = message.Reviews;
            var reviews = CPlayerData.m_CustomerReviewDataList;
            if (_reviewSeq < 0)
                _reviewSeq = CPlayerData.m_CustomerReviewCount; // join baseline = the save
            int firstSeq = totalCount - entries.Count + 1; // sequence number of tail[0]
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var rv = new CustomerReviewData();
                rv.customerReviewType = (ECustomerReviewType)e.CustomerReviewType;
                rv.starLevel = e.StarLevel;
                rv.textSOGoodBadLevel = e.TextSOGoodBadLevel;
                rv.textSOIndex = e.TextSOIndex;
                rv.day = e.Day;
                rv.hour = e.Hour;
                rv.minute = e.Minute;
                // host id -> ours (already translated by the DTO deserialize); a review about
                // an item from a pack only the host has reads back as EItemType.None, which
                // the phone's review row renders as no icon - the review text itself is
                // unaffected
                rv.itemType = e.ItemType;
                rv.customerName = e.CustomerName;
                if (firstSeq + i > _reviewSeq && reviews != null)
                    reviews.Add(rv); // in place: CustomerReviewManager aliases this list
            }
            if (totalCount > _reviewSeq)
                _reviewSeq = totalCount;
            CPlayerData.m_CustomerReviewCount = totalCount;
            CPlayerData.m_CustomerReviewScoreAverage = average;
            if (reviews != null)
                while (reviews.Count > 50)
                    reviews.RemoveAt(0); // vanilla cap

            if (openScreen)
            {
                s_clientOpenReport = r; // what CloseScreen must file into the history
                TryOpenReportScreen();
            }
        }

        private static void TryOpenReportScreen()
        {
            try
            {
                // a REAL screen in the scene means the vanilla statics below resolve
                // it too; without one they would auto-create a fake (see class fields)
                if (_screen == null)
                    _screen = UnityEngine.Object.FindObjectOfType<EndOfDayReportScreen>();
                if (_screen == null)
                    return;
                if (EndOfDayReportScreen.IsActive())
                    return; // OpenScreen is a toggle: don't close it

                if (_ipc == null)
                    _ipc = UnityEngine.Object.FindObjectOfType<InteractionPlayerController>();
                var pc = _ipc;
                if (pc != null)
                {
                    // the phone owns the same cursor/move locks; fighting it corrupts UI
                    // state, so a joiner mid-phone just keeps the data (report history
                    // still updates) and skips the popup
                    try
                    {
                        if (FiPhoneMode != null && (bool)FiPhoneMode.GetValue(pc))
                            return;
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    // vanilla ShowGoNextDayScreen exits register mode before opening. The
                    // bare OnExitCashCounterMode clears the IPC flag but leaves the counter's
                    // m_IsMannedByPlayer and the co-op claim set, and on a joiner the recap
                    // must also pull him off a register whose customer the host just resolved
                    // - so do the FULL exit (vanilla OnPressEsc) and release the claim too.
                    try
                    {
                        if (FiCashMode != null && (bool)FiCashMode.GetValue(pc))
                            pc.OnExitCashCounterMode();
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    try
                    {
                        Sync.RegisterSync.ForceExitManned();
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                }
                // SaveGameData inside OpenScreen is already no-op'd for joiners by
                // GamePatches.SaveGuardPrefix; everything else in there is pure UI
                EndOfDayReportScreen.OpenScreen();
                // ONLY here: the recap on screen is now the one whose numbers the caller
                // just stored in s_clientOpenReport, so the close is allowed to file them.
                // Every other way the screen could be up (the phone-mode bail above, a
                // vanilla open) leaves this false and the close keeps the live numbers.
                s_haveOpenReport = true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ReportSync open screen: " + e.Message); }
        }

        public static void ClientReportClosedPostfix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            try
            {
                SoundManager.SetEnableSound_CoinIncrease(false);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("report sound cleanup: " + e.Message); }
        }

        /// <summary>
        /// The joiner's only way out of the recap - and the only thing that gives him his
        /// legs back, since OpenScreen took EnterLockMoveMode. Both report buttons route
        /// here, and so does CoopCore when the host advances the day out from under us.
        /// Safe to call blind: it no-ops unless a client actually has the screen up.
        /// </summary>
        public static void CloseClientReport()
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            try
            {
                // same rule as TryOpenReportScreen: only touch the vanilla statics when a
                // REAL screen exists in the scene, or CSingleton fabricates a fake one
                // that shadows the real screen for the rest of the run
                if (_screen == null)
                    _screen = UnityEngine.Object.FindObjectOfType<EndOfDayReportScreen>();
                if (_screen == null)
                    return;
                if (!EndOfDayReportScreen.IsActive())
                    return; // nothing open: don't file a phantom day

                // Closing during the number lerp skips EndDayReportTextUI's normal cleanup,
                // which otherwise leaves the looping coin-increase source enabled forever.
                try
                {
                    SoundManager.SetEnableSound_CoinIncrease(false);
                }
                catch (System.Exception e) { Swallow.Log(e); }

                // Re-assert the report this screen actually displayed so a host that
                // already moved on (and healed a reset report over us) can't make us
                // append zeros - but ONLY when the mirror is what opened it. A screen this
                // mirror never opened has no stored numbers behind it, and forcing
                // s_clientOpenReport in that case is how an all-zero (or duplicated
                // yesterday) day got filed into the joiner's phone history. Without it the
                // live host-synced day-collect is what CloseScreen files: not the frozen
                // open-moment snapshot, but real numbers instead of a phantom.
                if (s_haveOpenReport)
                    CPlayerData.m_GameReportDataCollect = s_clientOpenReport;
                s_haveOpenReport = false;
                // CloseScreen is the vanilla bookkeeping the phone's report history needs -
                // past-list append, day-collect reset - plus the cursor hide and
                // ExitLockMoveMode that unfreeze the joiner.
                EndOfDayReportScreen.CloseScreen();
                // Hand the screen back the way OpenScreen expects it. Its Update() early-
                // returns on !m_IsActive BEFORE it reads the key-UP, and every joiner close
                // path tears the screen down in the same frame as the key-DOWN (the
                // click-anywhere autofire, the Next Day button, the host's day-change
                // mirror) - so the release is never seen and m_IsHoldingMouseDown latches
                // true for the rest of the session. Next night's recap would then autofire
                // OnPressGoNextButton every 0.05s with no input at all and snap the whole
                // count-up past the son. m_MouseDownTime matters for the same reason: a
                // leftover non-zero fires one extra press through Update's else branch.
                try
                {
                    FiHoldingMouseDown?.SetValue(_screen, false);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                try
                {
                    FiMouseDownTime?.SetValue(_screen, 0f);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ReportSync close screen: " + e.Message); }
        }
    }
}
