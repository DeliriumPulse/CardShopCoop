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
    /// Client: the report screen opens READ-ONLY. Its continue button must not advance
    /// anything - the vanilla path would run LightManager.GoNextDay and charge the
    /// game-event host fee through the forwarded ReduceCoin (a second, phantom charge).
    /// So the client's button is rerouted to plain CloseScreen(), which conveniently IS
    /// the vanilla bookkeeping (append past-list, reset day collect) that keeps the
    /// phone's report history aligned with the host's.
    /// </summary>
    public class ReportSync
    {
        /// <summary>True while ClientApplyState writes host data, so our patches never
        /// mistake a sync write for local play.</summary>
        public static bool ApplyingRemote;

        public Action<Action<BinaryWriter>> BroadcastState; // set by CoopCore: host -> clients

        private const float Interval = 2f;
        private const float HealEvery = 15f;
        private const int ReviewTail = 15; // enough to bridge a missed heal; reviews are rare

        // host: set by patches (static, patches can't see the instance), drained by HostTick
        private static bool s_openPending;
        private static GameReportDataCollect s_openSnapshot; // struct copy: survives the reset on host continue
        private static bool s_reviewsDirty;

        // client: the report the open-screen broadcast displayed. If the HOST hits
        // "next day" first, its reset report heals onto the client before the son
        // presses continue - CloseScreen would then append zeros to his history.
        private static GameReportDataCollect s_clientOpenReport;

        // client UI state we must read before opening a fullscreen lock
        private static readonly System.Reflection.FieldInfo FiIsLerping =
            AccessTools.Field(typeof(EndOfDayReportScreen), "m_IsLerpingNumber");
        private static readonly System.Reflection.FieldInfo FiPhoneMode =
            AccessTools.Field(typeof(InteractionPlayerController), "m_IsPhoneScreenMode");
        private static readonly System.Reflection.FieldInfo FiCashMode =
            AccessTools.Field(typeof(InteractionPlayerController), "m_IsCashCounterMode");

        private float _timer;
        private int _lastHash;
        private float _heal;
        private int _reviewSeq; // client: m_CustomerReviewCount high-water mark (dedup key)

        public void Reset()
        {
            _timer = -4.1f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            _reviewSeq = -1;
            s_openPending = false;
            s_reviewsDirty = false;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = HealEvery; // next tick broadcasts even if the hash collides
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
                CoopPlugin.Log.LogWarning($"Patch failed: {type.Name}.{method}: {e.Message}");
            }
        }

        public static void ReportOpenedPostfix()
        {
            if (CoopCore.Role != CoopRole.Host) return;
            try
            {
                // OpenScreen doubles as a toggle: only a real open broadcasts
                if (!EndOfDayReportScreen.IsActive()) return;
            }
            catch { return; }
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
            if (CoopCore.Role != CoopRole.Client) return true;
            bool lerping = false;
            try { lerping = FiIsLerping != null && (bool)FiIsLerping.GetValue(__instance); } catch { }
            if (lerping) return true; // vanilla behavior: fast-forward the count-up
            // Vanilla would gate on GetHasDayEnded() - which CoopCore pins false on the
            // client - leaving the joiner locked in the screen forever. Close instead;
            // CloseScreen is also the vanilla past-list append + day reset. Re-assert
            // the report this screen actually displayed so a host that already moved
            // on (and healed a reset report over us) can't make us append zeros.
            try
            {
                CPlayerData.m_GameReportDataCollect = s_clientOpenReport;
                EndOfDayReportScreen.CloseScreen();
            }
            catch { }
            return false;
        }

        public static bool NextDayBlockPrefix()
        {
            return CoopCore.Role != CoopRole.Client;
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || BroadcastState == null) return;

            // the open-moment ships immediately (not on the 2s grid): the snapshot was
            // taken at OpenScreen time, so even a host racing to "next day" can't feed
            // the client a reset report
            if (s_openPending)
            {
                s_openPending = false;
                var snap = s_openSnapshot;
                try
                {
                    BroadcastState(bw => WriteState(bw, snap, openScreen: true));
                    _heal = 0f;
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("ReportSync open: " + e.Message); }
                return;
            }

            _timer += dt;
            if (_timer < Interval) return;
            _timer -= Interval;
            try
            {
                if (s_reviewsDirty)
                {
                    s_reviewsDirty = false;
                    _lastHash = 0; // bust the gate: ship the new review this tick
                }
                int hash = HashState();
                _heal += Interval;
                if (hash == _lastHash && _heal < HealEvery) return;
                _lastHash = hash;
                _heal = 0f;
                var live = CPlayerData.m_GameReportDataCollect;
                BroadcastState(bw => WriteState(bw, live, openScreen: false));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ReportSync host: " + e.Message); }
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

        private static void WriteState(BinaryWriter bw, GameReportDataCollect r, bool openScreen)
        {
            bw.Write((byte)(openScreen ? 1 : 0));
            bw.Write(r.customerVisited);
            bw.Write(r.checkoutCount);
            bw.Write(r.customerDisatisfied);
            bw.Write(r.customerBoughtItem);
            bw.Write(r.customerBoughtCard);
            bw.Write(r.customerPlayed);
            bw.Write(r.storeExpGained);
            bw.Write(r.storeLevelGained);
            bw.Write(r.itemAmountSold);
            bw.Write(r.cardAmountSold);
            bw.Write(r.totalPlayTableTime);
            bw.Write(r.totalItemEarning);
            bw.Write(r.totalCardEarning);
            bw.Write(r.totalPlayTableEarning);
            bw.Write(r.supplyCost);
            bw.Write(r.upgradeCost);
            bw.Write(r.employeeCost);
            bw.Write(r.rentCost);
            bw.Write(r.billCost);
            bw.Write(r.cardPackOpened);
            bw.Write(r.smellyCustomerCleaned);
            bw.Write(r.manualCheckoutCount);
            bw.Write(r.gemMintCardObtained);

            // reviews: lifetime count doubles as a sequence number, so the client can
            // append exactly the ones it hasn't seen (list itself is capped at 50)
            var reviews = CPlayerData.m_CustomerReviewDataList;
            bw.Write(CPlayerData.m_CustomerReviewCount);
            bw.Write(CPlayerData.m_CustomerReviewScoreAverage);
            int n = Mathf.Min(reviews != null ? reviews.Count : 0, ReviewTail);
            bw.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                var rv = reviews[reviews.Count - n + i]; // oldest-first tail
                bw.Write((int)rv.customerReviewType);
                bw.Write((byte)Mathf.Clamp(rv.starLevel, 0, 255));
                bw.Write((byte)Mathf.Clamp(rv.textSOGoodBadLevel, 0, 255));
                bw.Write(rv.textSOIndex);
                bw.Write(rv.day);
                bw.Write((byte)Mathf.Clamp(rv.hour, 0, 255));
                bw.Write((byte)Mathf.Clamp(rv.minute, 0, 255));
                bw.Write((int)rv.itemType);
                bw.Write(rv.customerName ?? "");
            }
        }

        // ---------------- client ----------------

        public void ClientApplyState(BinaryReader br)
        {
            ApplyingRemote = true;
            try { ClientApplyInner(br); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ReportSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(BinaryReader br)
        {
            bool openScreen = br.ReadByte() != 0;

            var r = default(GameReportDataCollect);
            r.customerVisited = br.ReadInt32();
            r.checkoutCount = br.ReadInt32();
            r.customerDisatisfied = br.ReadInt32();
            r.customerBoughtItem = br.ReadInt32();
            r.customerBoughtCard = br.ReadInt32();
            r.customerPlayed = br.ReadInt32();
            r.storeExpGained = br.ReadInt32();
            r.storeLevelGained = br.ReadInt32();
            r.itemAmountSold = br.ReadInt32();
            r.cardAmountSold = br.ReadInt32();
            r.totalPlayTableTime = br.ReadSingle();
            r.totalItemEarning = br.ReadSingle();
            r.totalCardEarning = br.ReadSingle();
            r.totalPlayTableEarning = br.ReadSingle();
            r.supplyCost = br.ReadSingle();
            r.upgradeCost = br.ReadSingle();
            r.employeeCost = br.ReadSingle();
            r.rentCost = br.ReadSingle();
            r.billCost = br.ReadSingle();
            r.cardPackOpened = br.ReadInt32();
            r.smellyCustomerCleaned = br.ReadInt32();
            r.manualCheckoutCount = br.ReadInt32();
            r.gemMintCardObtained = br.ReadInt32();
            // host truth replaces the joiner's near-zero local counters (his own pack
            // opens etc. are folded into the host numbers only where the host saw them;
            // m_GameReportDataCollectPermanent stays local so achievements keep their
            // per-player pacing)
            CPlayerData.m_GameReportDataCollect = r;

            int totalCount = br.ReadInt32();
            float average = br.ReadSingle();
            int n = br.ReadByte();
            var reviews = CPlayerData.m_CustomerReviewDataList;
            if (_reviewSeq < 0) _reviewSeq = CPlayerData.m_CustomerReviewCount; // join baseline = the save
            int firstSeq = totalCount - n + 1; // sequence number of tail[0]
            for (int i = 0; i < n; i++)
            {
                var rv = new CustomerReviewData();
                rv.customerReviewType = (ECustomerReviewType)br.ReadInt32();
                rv.starLevel = br.ReadByte();
                rv.textSOGoodBadLevel = br.ReadByte();
                rv.textSOIndex = br.ReadInt32();
                rv.day = br.ReadInt32();
                rv.hour = br.ReadByte();
                rv.minute = br.ReadByte();
                rv.itemType = (EItemType)br.ReadInt32();
                rv.customerName = br.ReadString();
                if (firstSeq + i > _reviewSeq && reviews != null)
                    reviews.Add(rv); // in place: CustomerReviewManager aliases this list
            }
            if (totalCount > _reviewSeq) _reviewSeq = totalCount;
            CPlayerData.m_CustomerReviewCount = totalCount;
            CPlayerData.m_CustomerReviewScoreAverage = average;
            if (reviews != null)
                while (reviews.Count > 50) reviews.RemoveAt(0); // vanilla cap

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
                var screen = CSingleton<EndOfDayReportScreen>.Instance;
                if (screen == null) return;
                if (EndOfDayReportScreen.IsActive()) return; // OpenScreen is a toggle: don't close it

                var pc = CSingleton<InteractionPlayerController>.Instance;
                if (pc != null)
                {
                    // the phone owns the same cursor/move locks; fighting it corrupts UI
                    // state, so a joiner mid-phone just keeps the data (report history
                    // still updates) and skips the popup
                    try
                    {
                        if (FiPhoneMode != null && (bool)FiPhoneMode.GetValue(pc)) return;
                    }
                    catch { }
                    // vanilla ShowGoNextDayScreen exits register mode before opening
                    try
                    {
                        if (FiCashMode != null && (bool)FiCashMode.GetValue(pc)) pc.OnExitCashCounterMode();
                    }
                    catch { }
                }
                // SaveGameData inside OpenScreen is already no-op'd for joiners by
                // GamePatches.SaveGuardPrefix; everything else in there is pure UI
                EndOfDayReportScreen.OpenScreen();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ReportSync open screen: " + e.Message); }
        }
    }
}
