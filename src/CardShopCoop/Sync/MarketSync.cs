using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// One shared market. PriceChangeManager rerolls every item/card percent change with
    /// UnityEngine.Random at each day start, so from day 2 the joiner would price cards
    /// against a market that does not exist (host customers judge his tags against the
    /// HOST's numbers). The joiner's roll is blocked outright and the host's post-roll
    /// table is broadcast: item % changes, all seven per-expansion card % changes, and
    /// the game-event price rows the phone apps read.
    ///
    /// Price HISTORY (the graph screens) is never shipped: the vanilla day-start append
    /// (UpdateItemPricePercentChange / UpdatePastCardPricePercentChange) just pushes the
    /// CURRENT values, so the client replays exactly one append per host day after
    /// applying that day's snapshot - identical graphs without sending 30 days x
    /// thousands of floats. The join-time save download provides the matching baseline.
    ///
    /// All writes go INTO the existing lists / MarketPrice objects, never replacing them:
    /// PriceChangeManager.Init aliases its own fields to the CPlayerData lists, so a
    /// fresh list instance would silently orphan every consumer.
    /// </summary>
    public class MarketSync : TickableCoopModule
    {
        public override string Name => "market";

        /// <summary>True while ClientApplyState writes host data, so any future patch on
        /// these tables can tell a sync write from a local one.</summary>
        public static bool ApplyingRemote;

        public Action<INetMessage> BroadcastState; // set by CoopCore: host -> clients
        public Action RequestResync;

        private int _lastAppliedGen; // client: which host roll's history append already ran

        // Client: newest snapshot received while not in game. Applied the instant
        // InGameLevel() turns true, i.e. AFTER CGameData.PropagateLoadData has swapped the
        // card tables - a snapshot applied any earlier targets list instances the load then
        // replaces (the periodic heal used to mask this). Latest wins; snapshots are full.
        private MarketStateMessage _pendingState;
        private float _diagTimer;

        // Host: set whenever the market data is known to have changed. Every writer of the
        // synced tables flags this (day roll, base generation, game-event price edits, load,
        // cost updates, join) and HostTick broadcasts on the next tick. No polling, no timer:
        // the snapshot is a Reliable message, so a send is a delivery.
        private static bool s_dirty;

        // Cached EPL modded-id list (walking the EPL item dictionary + Convert.ToInt32 per key
        // was happening on every snapshot build). The registry is fixed for a session.
        private static List<int> s_eplModdedCache;
        private static float s_eplModdedCacheAt = -999f;

        // Host: bumped AFTER PriceChangeManager finishes a day-start roll. The raw day
        // number is not a safe stamp - HostTick could sample in the frames between the
        // day increment and the (queued-event) reroll and ship pre-roll values under
        // the new day, making the client append a wrong graph point.
        private static int s_rollGen;

        // Host: modded-expansion card-market changes (deltas), captured by the
        // AddCardPricePercentChange / SetCardGeneratedMarketPrice postfixes. EPL prefixes
        // those SAME game methods (postfixes still run when a prefix returns false), so this
        // covers modded expansions without touching EPL's internals; the client replays the
        // exact same calls and EPL's own prefix stores them locally.
        private struct PendingModCard
        {
            public ECardExpansionType Expansion;
            public int Index;
            public bool IsDestiny;
            public bool HasPercent;
            public float Percent;   // absolute pricePercentChangeList
            public float Base;
            public bool HasBase;
        }
        private static readonly Dictionary<long, PendingModCard> s_modCardPending
            = new Dictionary<long, PendingModCard>();

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.InGame);

        protected override void OnClientTick(in SyncFrame frame)
        {
            // Flush uses InGame (the snapshot may have arrived mid-load) and diagnostics
            // use dt, matching the original per-frame order.
            FlushPending(frame.InGame);
            ClientDiag(frame.Dt);
        }

        public override void Reset()
        {
            _lastAppliedGen = int.MinValue;
            if (_pendingState != null)
                Diag($"[market-buf] reset-dropped gen={_pendingState.RollGen}");
            _pendingState = null;
            s_dirty = false;
            s_eplModdedCache = null;
            s_eplModdedCacheAt = -999f;
            s_modCardPending.Clear();
            ApplyingRemote = false;
        }

        public override void ForceResend()
        {
            if (CoopCore.Role == CoopRole.Client)
                RequestResync?.Invoke();
            else
                s_dirty = true;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // The joiner must never roll his own market: CoopCore lets exactly one
            // OnDayStarted event through per mirrored host day (for the HUD), and that
            // event would run this handler's Random-driven reroll + history append.
            // Blocking here kills both; the host snapshot is the only market writer.
            // The host-side postfix stamps "a roll just finished" for the broadcast.
            Try(h, typeof(PriceChangeManager), "OnDayStarted",
                prefix: new HarmonyMethod(typeof(MarketSync), nameof(ClientBlockPrefix)),
                postfix: new HarmonyMethod(typeof(MarketSync), nameof(HostRolledPostfix)));
            // Other writers of the synced market tables: a game-event price edit, the
            // initial load, first-seen base generation, and per-purchase cost updates.
            // Boot them all into the dirty flag so HostTick never has to poll.
            Try(h, typeof(PriceChangeManager), "SetGameEventPrice",
                postfix: new HarmonyMethod(typeof(MarketSync), nameof(MarkDirtyHostPostfix)));
            Try(h, typeof(PriceChangeManager), "Init",
                postfix: new HarmonyMethod(typeof(MarketSync), nameof(MarkDirtyHostPostfix)));
            // RestockManager.Init is the only caller of GenerateCardMarketPrice and the one
            // place generated item cost/market bases are first filled (a newly met item).
            Try(h, typeof(RestockManager), "Init",
                postfix: new HarmonyMethod(typeof(MarketSync), nameof(MarkDirtyHostPostfix)));
            // Average item cost moves during normal gameplay (buying stock).
            Try(h, typeof(CPlayerData), "UpdateAverageItemCost",
                postfix: new HarmonyMethod(typeof(MarketSync), nameof(MarkDirtyHostPostfix)));
            Try(h, typeof(CPlayerData), "SetAverageItemCost",
                postfix: new HarmonyMethod(typeof(MarketSync), nameof(MarkDirtyHostPostfix)));
            // Modded-expansion card market: EPL PREFIXES these game writers (returning false
            // to divert to its own save data), but a postfix still runs when a prefix skips
            // the original, so this captures vanilla AND modded writes. The client replays the
            // same call, letting EPL's own prefix store it locally - no EPL types touched here.
            Try(h, typeof(CPlayerData), "AddCardPricePercentChange",
                postfix: new HarmonyMethod(typeof(MarketSync), nameof(CardPercentChangedPostfix)));
            Try(h, typeof(CPlayerData), "SetCardGeneratedMarketPrice",
                postfix: new HarmonyMethod(typeof(MarketSync), nameof(CardBaseChangedPostfix)));
        }

        /// <summary>Vanilla expansions are owned by the dense GenCardMarketPriceList sync;
        /// only modded (EPL) expansions go through the delta hook.</summary>
        private static bool IsVanillaCardExpansion(ECardExpansionType expansion)
        {
            switch (expansion)
            {
                case ECardExpansionType.Tetramon:
                case ECardExpansionType.Destiny:
                case ECardExpansionType.Ghost:
                case ECardExpansionType.Megabot:
                case ECardExpansionType.FantasyRPG:
                case ECardExpansionType.CatJob:
                    return true;
                default:
                    return false;
            }
        }

        private static long ModCardKey(ECardExpansionType expansion, int index, bool isDestiny)
        {
            return ((long)(int)expansion << 33) | ((long)(uint)index << 1) | (isDestiny ? 1L : 0L);
        }

        public static void CardPercentChangedPostfix(int cardIndex, ECardExpansionType expansionType, bool isDestiny, float percentChange)
        {
            if (CoopCore.Role != CoopRole.Host || IsVanillaCardExpansion(expansionType))
                return;
            // Ship the ABSOLUTE percent (not the delta) so the client can converge exactly:
            // the delta is quantized and preserves any save-rounding offset, which flips a cent.
            // GetCardPricePercentChange is the public reader and is EPL-prefixed too.
            float absolute;
            try
            {
                absolute = CPlayerData.GetCardPricePercentChange(cardIndex, expansionType, isDestiny);
            }
            catch (System.Exception e) { Swallow.Log(e); return; }
            long key = ModCardKey(expansionType, cardIndex, isDestiny);
            s_modCardPending.TryGetValue(key, out var p);
            p.Expansion = expansionType;
            p.Index = cardIndex;
            p.IsDestiny = isDestiny;
            p.Percent = absolute;
            p.HasPercent = true;
            s_modCardPending[key] = p;
            s_dirty = true;
        }

        public static void CardBaseChangedPostfix(int cardIndex, ECardExpansionType expansionType, bool isDestiny, float price)
        {
            if (CoopCore.Role != CoopRole.Host || IsVanillaCardExpansion(expansionType))
                return;
            long key = ModCardKey(expansionType, cardIndex, isDestiny);
            s_modCardPending.TryGetValue(key, out var p);
            p.Expansion = expansionType;
            p.Index = cardIndex;
            p.IsDestiny = isDestiny;
            p.Base = price;
            p.HasBase = true;
            s_modCardPending[key] = p;
            s_dirty = true;
        }

        /// <summary>Host: a market table was written; flush on the next tick.</summary>
        public static void MarkDirtyHostPostfix()
        {
            if (CoopCore.Role == CoopRole.Host && !ApplyingRemote)
                s_dirty = true;
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

        public static bool ClientBlockPrefix()
        {
            return CoopCore.Role != CoopRole.Client;
        }

        public static void HostRolledPostfix()
        {
            // postfixes run even when the prefix skipped the original - host gate here
            if (CoopCore.Role == CoopRole.Host)
            {
                s_rollGen++;
                s_dirty = true; // a day roll changed the market; flush promptly
                Diag($"[market] roll gen={s_rollGen}");
            }
        }

        /// <summary>Diagnostics: Tetramon row 0 raw samples (host raw vs client wire-rounded).</summary>
        private static string RowSample()
        {
            var list = CPlayerData.m_GenCardMarketPriceList;
            var m = list != null && list.Count > 0 ? list[0] : null;
            return $"n={list?.Count ?? -1} "
                + $"pct0={(m != null ? m.pricePercentChangeList : -999f):F3} "
                + $"base0={(m != null ? m.generatedMarketPrice : -999f):F3} "
                + $"hist0={(m != null && m.pastPricePercentChangeList != null ? m.pastPricePercentChangeList.Count : -1)}";
        }

        /// <summary>Checksum over the WIRE values in a built snapshot (host side).</summary>
        private static int WireChecksum(MarketStateMessage msg)
        {
            int h = 17;
            h = HashWire(h, msg.GenCardMarketPriceList);
            h = HashWire(h, msg.GenCardMarketPriceListDestiny);
            h = HashWire(h, msg.GenCardMarketPriceListGhost);
            h = HashWire(h, msg.GenCardMarketPriceListGhostBlack);
            h = HashWire(h, msg.GenCardMarketPriceListMegabot);
            h = HashWire(h, msg.GenCardMarketPriceListFantasyRPG);
            h = HashWire(h, msg.GenCardMarketPriceListCatJob);
            return h;
        }

        private static int HashWire(int h, List<MarketCardEntry> list)
        {
            if (list == null)
                return h;
            for (int i = 0; i < list.Count; i++)
                h = h * 31 + list[i].Percent;
            return h;
        }

        /// <summary>Same checksum, recomputed from the client's live lists (so it is directly
        /// comparable to <see cref="WireChecksum"/>).</summary>
        private static int WireChecksumFromLists()
        {
            int h = 17;
            h = HashWireList(h, CPlayerData.m_GenCardMarketPriceList);
            h = HashWireList(h, CPlayerData.m_GenCardMarketPriceListDestiny);
            h = HashWireList(h, CPlayerData.m_GenCardMarketPriceListGhost);
            h = HashWireList(h, CPlayerData.m_GenCardMarketPriceListGhostBlack);
            h = HashWireList(h, CPlayerData.m_GenCardMarketPriceListMegabot);
            h = HashWireList(h, CPlayerData.m_GenCardMarketPriceListFantasyRPG);
            h = HashWireList(h, CPlayerData.m_GenCardMarketPriceListCatJob);
            return h;
        }

        private static int HashWireList(int h, List<MarketPrice> list)
        {
            if (list == null)
                return h;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                h = h * 31 + (m != null ? (int)Mathf.Clamp(Mathf.RoundToInt(m.pricePercentChangeList * 100f), short.MinValue, short.MaxValue) : 0);
            }
            return h;
        }

        private static void Diag(string message)
        {
            if (BoxShared.Debug)
                CoopPlugin.Log.LogInfo(message);
        }

        // ---------------- host ----------------

        public void HostTick(bool inGame)
        {
            if (!inGame || BroadcastState == null || !s_dirty)
                return;
            try
            {
                // tables exist only after CPlayerData init; an empty item list means the
                // save hasn't landed yet. Keep s_dirty so the flush still happens later.
                if (CPlayerData.m_ItemPricePercentChangeList == null
                    || CPlayerData.m_ItemPricePercentChangeList.Count == 0)
                    return;
                var msg = BuildState();
                Diag($"[market-tx] gen={s_rollGen} rows={msg.GenCardMarketPriceList.Count} mod={msg.ModdedCards.Count} wcs={WireChecksum(msg)} {RowSample()}");
                BroadcastState(msg);
                s_modCardPending.Clear();
                s_dirty = false;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("MarketSync host: " + e.Message); }
        }

        private static MarketStateMessage BuildState()
        {
            var modded = EplModdedItemTypes(); // empty when the bridge is inactive
            var msg = new MarketStateMessage
            {
                RollGen = s_rollGen, // history-append stamp: post-roll broadcasts only
            };
            FillPercents(msg.ItemPricePercentChangeList, CPlayerData.m_ItemPricePercentChangeList, modded);
            FillMarket(msg.GenCardMarketPriceList, CPlayerData.m_GenCardMarketPriceList);
            FillMarket(msg.GenCardMarketPriceListDestiny, CPlayerData.m_GenCardMarketPriceListDestiny);
            FillMarket(msg.GenCardMarketPriceListGhost, CPlayerData.m_GenCardMarketPriceListGhost);
            FillMarket(msg.GenCardMarketPriceListGhostBlack, CPlayerData.m_GenCardMarketPriceListGhostBlack);
            FillMarket(msg.GenCardMarketPriceListMegabot, CPlayerData.m_GenCardMarketPriceListMegabot);
            FillMarket(msg.GenCardMarketPriceListFantasyRPG, CPlayerData.m_GenCardMarketPriceListFantasyRPG);
            FillMarket(msg.GenCardMarketPriceListCatJob, CPlayerData.m_GenCardMarketPriceListCatJob);
            // game-event rows are raw prices, not clamped percents - full floats
            FillFloats(msg.SetGameEventPriceList, CPlayerData.m_SetGameEventPriceList);
            FillFloats(msg.GeneratedGameEventPriceList, CPlayerData.m_GeneratedGameEventPriceList);
            FillFloats(msg.GameEventPricePercentChangeList, CPlayerData.m_GameEventPricePercentChangeList);
            // Graded-card price multiplier: the client regenerates it locally (RNG) otherwise.
            FillFloats(msg.GenGradedCardPriceMultiplierList, CPlayerData.m_GenGradedCardPriceMultiplierList);
            FillModdedCards(msg.ModdedCards);
            FillSparse(msg.GeneratedMarketPriceList, CPlayerData.m_GeneratedMarketPriceList, modded, s_eplGenMarket);
            FillSparse(msg.GeneratedCostPriceList, CPlayerData.m_GeneratedCostPriceList, modded, s_eplGenCost);
            FillSparse(msg.AverageItemCostList, CPlayerData.m_AverageItemCostList, modded, s_eplAvgCost);
            return msg;
        }

        // ---------------- client ----------------

        public void ClientApplyState(MarketStateMessage message)
        {
            ApplyingRemote = true;
            try
            {
                ClientApplyInner(message);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("MarketSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        /// <summary>Client: apply now if in game, otherwise hold the newest snapshot until
        /// the world load finishes (see <see cref="_pendingState"/>).</summary>
        public void ClientApplyOrBuffer(MarketStateMessage message, bool inGame)
        {
            Diag($"[market-buf] {(inGame ? "apply" : "buffer")} gen={message.RollGen}");
            if (!inGame)
            {
                _pendingState = message;
                return;
            }
            if (_pendingState != null)
            {
                // a newer snapshot supersedes a buffered (older) one; never apply it later
                Diag($"[market-buf] drop-buffered gen={_pendingState.RollGen}");
                _pendingState = null;
            }
            ClientApplyState(message);
        }

        /// <summary>Client: periodic diagnostic so a post-apply revert (load swap / stale
        /// buffered flush) is visible. No-op unless BoxSyncDebug.</summary>
        public void ClientDiag(float dt)
        {
            if (!BoxShared.Debug)
                return;
            _diagTimer += dt;
            if (_diagTimer < 5f)
                return;
            _diagTimer = 0f;
            Diag($"[market-live] wcs={WireChecksumFromLists()} {RowSample()}");
        }

        /// <summary>Client: apply a snapshot buffered while not in game. Call every tick.</summary>
        public void FlushPending(bool inGame)
        {
            if (!inGame || _pendingState == null)
                return;
            var message = _pendingState;
            _pendingState = null;
            Diag($"[market-buf] flush gen={message.RollGen}");
            ClientApplyState(message);
        }

        private void ClientApplyInner(MarketStateMessage message)
        {
            int rollGen = message.RollGen;
            ApplySection("item price changes", () => ReadPercentsInto(message.ItemPricePercentChangeList, CPlayerData.m_ItemPricePercentChangeList));
            ApplySection("Tetramon market", () => ReadMarketInto(message.GenCardMarketPriceList, CPlayerData.m_GenCardMarketPriceList));
            ApplySection("Destiny market", () => ReadMarketInto(message.GenCardMarketPriceListDestiny, CPlayerData.m_GenCardMarketPriceListDestiny));
            ApplySection("Ghost market", () => ReadMarketInto(message.GenCardMarketPriceListGhost, CPlayerData.m_GenCardMarketPriceListGhost));
            ApplySection("Ghost Black market", () => ReadMarketInto(message.GenCardMarketPriceListGhostBlack, CPlayerData.m_GenCardMarketPriceListGhostBlack));
            ApplySection("Megabot market", () => ReadMarketInto(message.GenCardMarketPriceListMegabot, CPlayerData.m_GenCardMarketPriceListMegabot));
            ApplySection("FantasyRPG market", () => ReadMarketInto(message.GenCardMarketPriceListFantasyRPG, CPlayerData.m_GenCardMarketPriceListFantasyRPG));
            ApplySection("CatJob market", () => ReadMarketInto(message.GenCardMarketPriceListCatJob, CPlayerData.m_GenCardMarketPriceListCatJob));
            ApplySection("set game-event prices", () => ReadFloatsInto(message.SetGameEventPriceList, CPlayerData.m_SetGameEventPriceList));
            ApplySection("generated game-event prices", () => ReadFloatsInto(message.GeneratedGameEventPriceList, CPlayerData.m_GeneratedGameEventPriceList));
            ApplySection("game-event price changes", () => ReadFloatsInto(message.GameEventPricePercentChangeList, CPlayerData.m_GameEventPricePercentChangeList));
            ApplySection("graded-card multipliers", () => ReadFloatsInto(message.GenGradedCardPriceMultiplierList, CPlayerData.m_GenGradedCardPriceMultiplierList));
            ApplySection("generated market prices", () => ReadSparseFloatsInto(message.GeneratedMarketPriceList, CPlayerData.m_GeneratedMarketPriceList, ModdedGenMarket));
            ApplySection("generated cost prices", () => ReadSparseFloatsInto(message.GeneratedCostPriceList, CPlayerData.m_GeneratedCostPriceList, ModdedGenCost));
            ApplySection("average item costs", () => ReadSparseFloatsInto(message.AverageItemCostList, CPlayerData.m_AverageItemCostList, ModdedAvgCost));

            // Modded-expansion card values: replay through the game's own writers so EPL's
            // prefix stores them locally. Must run BEFORE the history append below so the
            // modded history gains the same point the host's did.
            ApplySection("modded cards", () => ApplyModdedCards(message.ModdedCards));

            // Replay the vanilla once-per-day history append AFTER the day's values are
            // in, so the graph gains the same last point the host's did. The first
            // snapshot after joining never appends: the downloaded save already carries
            // today's history entry.
            if (_lastAppliedGen == int.MinValue)
            {
                _lastAppliedGen = rollGen;
            }
            else if (rollGen != _lastAppliedGen)
            {
                bool historyApplied = true;
                try
                {
                    CPlayerData.UpdateItemPricePercentChange();
                    CPlayerData.UpdatePastCardPricePercentChange();
                }
                catch (Exception e)
                {
                    historyApplied = false;
                    CoopPlugin.Log.LogWarning("MarketSync history: " + e.Message);
                }
                if (historyApplied)
                    _lastAppliedGen = rollGen;
            }
            Diag($"[market-rx] gen={rollGen} rows0={message.GenCardMarketPriceList.Count} mod={message.ModdedCards.Count} wcs={WireChecksumFromLists()} {RowSample()}");
        }

        private static void ApplySection(string name, Action apply)
        {
            try
            {
                apply();
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("MarketSync section '" + name + "': " + e.Message);
            }
        }

        // ---------------- wire helpers ----------------

        // Percent changes are game-clamped to [-80, 200]; x100 fits a short, and 0.01%
        // resolution is beyond anything the UI displays. Entries are keyed by RAW
        // itemType and EPL registers modded items at huge enum values (>= 200k), so
        // only non-zero entries ship, as (index, pct) pairs - a dense send both
        // truncated past 65535 (modded items never synced) and wasted ~128KB per roll.
        // Vanilla values come off the raw list; modded values off the EPL bridge.
        private static void FillPercents(List<MarketPercentEntry> out_list, List<float> list, List<int> modded)
        {
            int vanilla = VanillaWalkCount(list);
            var moddedVals = CollectModded(modded, s_eplPctChange);
            for (int i = 0; i < vanilla; i++)
                if (list[i] != 0f)
                {
                    // the index IS an EItemType; below the modded floor WriteItemType (invoked
                    // by the DTO Serialize) is the identity, so this whole vanilla half is
                    // byte-for-byte what it always was
                    out_list.Add(new MarketPercentEntry
                    {
                        ItemType = (EItemType)i,
                        Percent = (short)Mathf.Clamp(Mathf.RoundToInt(list[i] * 100f), short.MinValue, short.MaxValue),
                    });
                }
            for (int k = 0; k < moddedVals.Count; k++)
            {
                out_list.Add(new MarketPercentEntry
                {
                    ItemType = (EItemType)moddedVals[k].Key,
                    Percent = (short)Mathf.Clamp(Mathf.RoundToInt(moddedVals[k].Value * 100f), short.MinValue, short.MaxValue),
                });
            }
        }

        // Generated base prices are FULL floats keyed by raw itemType (the >= 200k
        // modded id space), non-zero entries only. Unlike percents, absent entries keep
        // their local value - the host may legitimately have gaps we filled at join.
        private static void FillSparse(List<MarketSparseEntry> out_list, List<float> list, List<int> modded, PropertyInfo eplField)
        {
            int vanilla = VanillaWalkCount(list);
            var moddedVals = CollectModded(modded, eplField);
            for (int i = 0; i < vanilla; i++)
                if (list[i] != 0f)
                {
                    out_list.Add(new MarketSparseEntry { ItemType = (EItemType)i, Value = list[i] }); // identity below the modded floor
                }
            for (int k = 0; k < moddedVals.Count; k++)
            {
                out_list.Add(new MarketSparseEntry { ItemType = (EItemType)moddedVals[k].Key, Value = moddedVals[k].Value });
            }
        }

        private static void ReadSparseFloatsInto(List<MarketSparseEntry> entries, List<float> list, Action<int, float> moddedWrite)
        {
            for (int k = 0; k < entries.Count; k++)
            {
                // host id -> ours. The DTO's deserialize already translated the wire id via
                // FromWire; an unmappable modded id (a content pack only the host has) has no
                // row here at all and collapsed to the None sentinel (-1) - refusing it rather
                // than writing anywhere would park the host's price on the wrong item. Dropping
                // it is the harmless case for a sparse table: absent entries keep their local
                // value by design (see FillSparse).
                int i = (int)entries[k].ItemType;
                float v = entries[k].Value;
                if (!TryResolveIndex(list, i, out bool modded))
                    continue;
                if (modded)
                {
                    // raw writes up here are shadow rows the game never reads (the
                    // woven accessors serve EPL save data instead) - the original
                    // sin behind modded items' $0 market prices on the joiner
                    try
                    {
                        moddedWrite(i, v);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    continue;
                }
                while (list.Count <= i)
                    list.Add(0f); // grow-on-demand (no-EPL fallback)
                list[i] = v;
            }
        }

        private static void ReadPercentsInto(List<MarketPercentEntry> entries, List<float> list)
        {
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    list[i] = 0f; // absent = no change rolled
            if (EplMarketBridge())
            {
                // modded percents need the same absent-means-zero treatment, but they
                // live in EPL save data, not in the raw list zeroed above
                var modded = EplModdedItemTypes();
                for (int k = 0; k < modded.Count; k++)
                {
                    // ...except for a pack only WE have. This zeroing pass is the "absent from
                    // the incoming table means no change rolled" rule, and that rule only holds
                    // for items the host can actually SEND: an item with no counterpart there is
                    // absent from every snapshot forever, so zeroing it here would peg its
                    // market at 0% for the rest of the session instead of leaving our own
                    // rolled value alone. ToWire answers "does the host know this item" and is
                    // the identity (never None) on vanilla ids and in an untranslated session,
                    // so nothing that used to be zeroed here stops being zeroed.
                    if (Util.EnumMap.ToWire(Util.EnumKind.ItemType, modded[k]) == (int)EItemType.None)
                        continue;
                    EplSetFloat(modded[k], s_eplPctChange, 0f);
                }
            }
            for (int k = 0; k < entries.Count; k++)
            {
                // host id -> ours (already translated during the DTO deserialize; an unmappable
                // modded id collapsed to the None sentinel and is refused by the range check
                // below, leaving whatever the zeroing pass above wrote - see
                // ReadSparseFloatsInto)
                int i = (int)entries[k].ItemType;
                float v = entries[k].Percent / 100f;
                if (!TryResolveIndex(list, i, out bool modded))
                    continue;
                if (modded)
                {
                    EplSetFloat(i, s_eplPctChange, v);
                    continue;
                }
                while (list.Count <= i)
                    list.Add(0f); // grow-on-demand (no-EPL fallback)
                list[i] = v;
            }
        }

        private static void FillMarket(List<MarketCardEntry> out_list, List<MarketPrice> list)
        {
            int n = Mathf.Min(list?.Count ?? 0, ushort.MaxValue);
            for (int i = 0; i < n; i++)
            {
                out_list.Add(new MarketCardEntry
                {
                    Percent = (short)Mathf.Clamp(Mathf.RoundToInt((list[i] != null ? list[i].pricePercentChangeList : 0f) * 100f), short.MinValue, short.MaxValue),
                    // The card BASE, for the same reason the item bases ride along above: a save
                    // whose card price block failed to restore leaves every base at 0, and the
                    // percent alone multiplies 0 into $0.00 cards forever. Full float - unlike the
                    // percent these are raw prices with no game clamp.
                    GeneratedMarketPrice = list[i] != null ? list[i].generatedMarketPrice : 0f,
                });
            }
        }

        private static void ReadMarketInto(List<MarketCardEntry> entries, List<MarketPrice> list)
        {
            if (list == null)
                return;
            for (int i = 0; i < entries.Count; i++)
            {
                float v = entries[i].Percent / 100f;
                float gen = entries[i].GeneratedMarketPrice;
                // CGameData.PropagateLoadData gates all seven card tables on ONE unrelated
                // list (m_CardPriceSetList), so a join save whose gate read false can leave
                // this table shorter than the host's. Create rows as they arrive instead of
                // silently skipping; the instance is kept so RestockManager's alias stays live.
                while (list.Count <= i)
                    list.Add(new MarketPrice { pastPricePercentChangeList = new List<float>() });
                var row = list[i];
                if (row == null)
                {
                    row = new MarketPrice { pastPricePercentChangeList = new List<float>() };
                    list[i] = row;
                }
                row.pricePercentChangeList = v; // in place: consumers hold the object
                // The snapshot is authoritative, including rows the host legitimately leaves at
                // zero; copying the value makes a stale client row converge to the host state.
                row.generatedMarketPrice = gen;
            }
        }

        /// <summary>Host: move pending modded-card values into the snapshot.</summary>
        private static void FillModdedCards(List<MarketModdedCardEntry> out_list)
        {
            if (s_modCardPending.Count == 0)
                return;
            foreach (var kv in s_modCardPending)
            {
                var p = kv.Value;
                out_list.Add(new MarketModdedCardEntry
                {
                    Expansion = p.Expansion,
                    Index = p.Index,
                    IsDestiny = p.IsDestiny,
                    HasPercent = p.HasPercent,
                    Percent = p.Percent,
                    Base = p.Base,
                    HasBase = p.HasBase,
                });
            }
            // The pending map is cleared by the host tick only AFTER a successful broadcast:
            // BuildState must not consume it, or a failed send would lose those deltas.
        }

        /// <summary>Client: replay modded-card changes through the game's public writers so
        /// EPL's own prefix (if installed) stores them; without EPL the vanilla original runs.
        /// A content-incomplete client can hit an out-of-range index inside the game/EPL body,
        /// so each row is isolated.</summary>
        private static void ApplyModdedCards(List<MarketModdedCardEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.HasPercent)
                {
                    try
                    {
                        float current = CPlayerData.GetCardPricePercentChange(e.Index, e.Expansion, e.IsDestiny);
                        float delta = e.Percent - current;
                        if (delta != 0f)
                            CPlayerData.AddCardPricePercentChange(e.Index, e.Expansion, e.IsDestiny, delta);
                    }
                    catch (Exception ex) { CoopPlugin.Log.LogWarning("modded card percent apply: " + ex.Message); }
                }
                if (e.HasBase)
                {
                    try
                    {
                        CPlayerData.SetCardGeneratedMarketPrice(e.Index, e.Expansion, e.IsDestiny, e.Base);
                    }
                    catch (Exception ex) { CoopPlugin.Log.LogWarning("modded card base apply: " + ex.Message); }
                }
            }
        }

        private static void FillFloats(List<float> out_list, List<float> list)
        {
            int n = Mathf.Min(list?.Count ?? 0, ushort.MaxValue);
            for (int i = 0; i < n; i++)
                out_list.Add(list[i]);
        }

        private static void ReadFloatsInto(List<float> entries, List<float> list)
        {
            if (entries == null || list == null)
                return;
            while (list.Count < entries.Count)
                list.Add(0f);
            for (int i = 0; i < entries.Count; i++)
            {
                float v = entries[i];
                list[i] = v;
            }
        }

        // ---------------- EPL market bridge ----------------

        // EPL IL-weaves the GAME assembly's accesses to the per-item price lists
        // (generated market/cost, percent change, average cost, set price): for
        // index >= 129 the woven Count/GetItem/SetItem serve its per-item save data
        // instead of the list (ItemPriceListHandler). Raw List access from THIS
        // assembly is NOT woven - it sees only the vanilla rows plus shadow rows the
        // game never reads, so modded market data raw-read here never left the host
        // and raw-written here never reached the joiner's game. Modded rows therefore
        // go through EPL's save data: reads and percent/generated writes mirror
        // ItemPriceListHandler.GetItem/SetItem via reflection (no clean game accessor
        // returns the RAW values - GetItemMarketPrice/GetItemCost bake the percent in,
        // GetAverageItemCost rounds and substitutes cost when out of range); average
        // cost writes ride CPlayerData.SetAverageItemCost, a clean setter whose woven
        // body IS the SetItem path. Wire indexes stay raw EItemType ints: EPL keeps
        // modded ids >= 200000, which resolve identically on every machine, unlike
        // its alternate [129, 129+modCount) index space, which is ordered by the
        // LOCALLY installed mod set and would cross-assign prices between machines.
        private static bool s_eplProbed;
        private static object s_eplSaveMgr;    // EplServices.SaveDataManager (created once, never reassigned)
        private static MethodInfo s_eplTryGet; // TryGetSaveData<EItemType, ItemSaveData>(key, out data)
        private static PropertyInfo s_eplAssetsProp, s_eplItemLibProp, s_eplItemDataProp;
        private static PropertyInfo s_eplGenMarket, s_eplGenCost, s_eplPctChange, s_eplAvgCost;

        private static bool EplMarketBridge()
        {
            if (!s_eplProbed)
            {
                s_eplProbed = true;
                try
                {
                    // assembly-qualified bind first, app-domain type walk only if it misses -
                    // see Util.ModParity.ResolveType for why the walk is worth avoiding
                    const string EplAsm = "EnhancedPrefabLoader";
                    var t = Util.ModParity.ResolveType("EnhancedPrefabLoader.Core.EplRuntimeData", EplAsm);
                    var save = Util.ModParity.ResolveType("EnhancedPrefabLoader.Core.Models.SaveData.ItemSaveData", EplAsm);
                    const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    s_eplAssetsProp = t?.GetProperty("Assets", F);
                    var assets = s_eplAssetsProp?.GetValue(null);
                    s_eplItemLibProp = assets?.GetType().GetProperty("ItemLibrary", F);
                    var lib = assets == null ? null : s_eplItemLibProp?.GetValue(assets);
                    s_eplItemDataProp = lib?.GetType().GetProperty("ItemData", F);
                    var services = t?.GetProperty("Services", F)?.GetValue(null);
                    s_eplSaveMgr = services?.GetType().GetProperty("SaveDataManager", F)?.GetValue(services);
                    if (s_eplSaveMgr != null && save != null)
                        foreach (var m in s_eplSaveMgr.GetType().GetMethods(F))
                            if (m.Name == "TryGetSaveData" && m.GetGenericArguments().Length == 2)
                            {
                                s_eplTryGet = m.MakeGenericMethod(typeof(EItemType), save);
                                break;
                            }
                    s_eplGenMarket = save?.GetProperty("GeneratedMarketPrice", F);
                    s_eplGenCost = save?.GetProperty("GeneratedCostPrice", F);
                    s_eplPctChange = save?.GetProperty("ItemPriceChangePercent", F);
                    s_eplAvgCost = save?.GetProperty("AverageItemCost", F);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                if (s_eplTryGet == null || s_eplItemDataProp == null || s_eplGenMarket == null
                    || s_eplGenCost == null || s_eplPctChange == null || s_eplAvgCost == null)
                {
                    s_eplTryGet = null; // all-or-nothing: half a bridge would desync the lists
                    CoopPlugin.Log.LogInfo("EPL market bridge inactive (EPL absent or its internals changed) - vanilla market only");
                }
                else
                {
                    CoopPlugin.Log.LogInfo("EPL market bridge active (modded item market data syncs)");
                }
            }
            return s_eplTryGet != null;
        }

        // With the bridge active the raw list is authoritative only below the woven
        // boundary; anything above it is shadow data (e.g. leftovers from a pre-fix
        // session) that must not ship. Without EPL the raw list is the whole truth.
        private static int VanillaWalkCount(List<float> list)
        {
            int n = list?.Count ?? 0;
            return EplMarketBridge() ? Mathf.Min(n, VanillaItemTypeCount()) : n;
        }

        private static int VanillaItemTypeCount()
        {
            int max = -1;
            Array values = Enum.GetValues(typeof(EItemType));
            for (int i = 0; i < values.Length; i++)
            {
                int value = Convert.ToInt32(values.GetValue(i));
                if (value >= 0 && value < 200000 && value > max)
                    max = value;
            }
            return max + 1;
        }

        private static bool TryResolveIndex(List<float> list, int index, out bool modded)
        {
            modded = false;
            if (list == null || index < 0 || index > 500000)
            {
                WarnInvalidIndex(index);
                return false;
            }
            if (!EplMarketBridge())
            {
                return true; // vanilla fallback tables may grow when sparse rows arrive
            }
            int vanilla = VanillaItemTypeCount();
            if (index < vanilla)
            {
                if (index >= list.Count)
                    WarnInvalidIndex(index);
                return index < list.Count;
            }
            modded = EplSaveData(index) != null;
            if (!modded)
                WarnInvalidIndex(index);
            return modded;
        }

        private static readonly HashSet<int> s_warnedInvalidIndexes = new HashSet<int>();

        private static void WarnInvalidIndex(int index)
        {
            if (s_warnedInvalidIndexes.Add(index))
                CoopPlugin.Log.LogWarning($"MarketSync: skipped out-of-range or unresolved item index {index}");
        }

        /// <summary>Raw itemType ints of EPL's modded items (ItemLibrary.ItemData keys);
        /// empty without the bridge. Ids outside [200000, 500000] are unshippable: below
        /// lands in EPL's machine-local index space, above fails the receive cap.</summary>
        private static List<int> EplModdedItemTypes()
        {
            // Cached: the EPL item registry is fixed for a session, and this walk (dictionary
            // keys + Convert.ToInt32) was running on every market tick. Refresh slowly in case
            // a mid-session content load adds rows.
            float now = Time.realtimeSinceStartup;
            if (s_eplModdedCache != null && now - s_eplModdedCacheAt < 30f)
                return s_eplModdedCache;
            var result = new List<int>();
            if (EplMarketBridge())
            {
                try
                {
                    var assets = s_eplAssetsProp.GetValue(null);
                    var lib = assets == null ? null : s_eplItemLibProp.GetValue(assets);
                    var dict = lib == null ? null : s_eplItemDataProp.GetValue(lib) as System.Collections.IDictionary;
                    if (dict != null)
                        foreach (object key in dict.Keys)
                        {
                            int v = Convert.ToInt32(key);
                            if (v >= 200000 && v <= 500000)
                                result.Add(v);
                        }
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            s_eplModdedCache = result;
            s_eplModdedCacheAt = now;
            return result;
        }

        private static object EplSaveData(int itemType)
        {
            try
            {
                var args = new object[] { (EItemType)itemType, null };
                return (bool)s_eplTryGet.Invoke(s_eplSaveMgr, args) ? args[1] : null;
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        private static float EplGetFloat(int itemType, PropertyInfo field)
        {
            object d = EplSaveData(itemType);
            return d == null ? 0f : (float)field.GetValue(d, null);
        }

        private static void EplSetFloat(int itemType, PropertyInfo field, float value)
        {
            // items the host has but we don't: no save data -> silently dropped,
            // matching ItemPriceListHandler.SetItem for an unresolvable index
            object d = EplSaveData(itemType);
            if (d != null)
                field.SetValue(d, value, null);
        }

        private static List<KeyValuePair<int, float>> CollectModded(List<int> modded, PropertyInfo field)
        {
            var vals = new List<KeyValuePair<int, float>>(modded.Count);
            if (field != null)
                for (int k = 0; k < modded.Count; k++)
                {
                    float v = EplGetFloat(modded[k], field);
                    if (v != 0f)
                        vals.Add(new KeyValuePair<int, float>(modded[k], v));
                }
            return vals;
        }

        // modded-index writers for ReadSparseFloatsInto (bridge verified by the caller)
        private static void ModdedGenMarket(int itemType, float v)
        {
            EplSetFloat(itemType, s_eplGenMarket, v);
        }
        private static void ModdedGenCost(int itemType, float v)
        {
            EplSetFloat(itemType, s_eplGenCost, v);
        }
        private static void ModdedAvgCost(int itemType, float v)
        {
            // the GAME's setter: no math in its body, and its woven form routes
            // >= 129 into EPL save data exactly like the reflection path
            CPlayerData.SetAverageItemCost((EItemType)itemType, v);
        }
    }
}
