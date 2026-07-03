using System;
using System.Collections.Generic;
using System.IO;
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
    public class MarketSync
    {
        /// <summary>True while ClientApplyState writes host data, so any future patch on
        /// these tables can tell a sync write from a local one.</summary>
        public static bool ApplyingRemote;

        public Action<Action<BinaryWriter>> BroadcastState; // set by CoopCore: host -> clients

        private const float Interval = 2f;
        private const float HealEvery = 20f; // ~32KB per snapshot; keep the heal slow

        private float _timer;
        private int _lastHash;
        private float _heal;
        private int _lastAppliedGen; // client: which host roll's history append already ran

        // Host: bumped AFTER PriceChangeManager finishes a day-start roll. The raw day
        // number is not a safe stamp - HostTick could sample in the frames between the
        // day increment and the (queued-event) reroll and ship pre-roll values under
        // the new day, making the client append a wrong graph point.
        private static int s_rollGen;

        public void Reset()
        {
            _timer = -3.4f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            _lastAppliedGen = int.MinValue;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = HealEvery; // next tick broadcasts even if the hash collides
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
            if (CoopCore.Role == CoopRole.Host) s_rollGen++;
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || BroadcastState == null) return;
            _timer += dt;
            if (_timer < Interval) return;
            _timer -= Interval;
            try
            {
                // tables exist only after CPlayerData init; an empty item list means the
                // save hasn't landed yet
                if (CPlayerData.m_ItemPricePercentChangeList == null
                    || CPlayerData.m_ItemPricePercentChangeList.Count == 0) return;

                // the market changes once per host day (plus rare game-event price edits),
                // so the hash keeps this ~32KB snapshot off the wire almost always
                int hash = 17;
                hash = hash * 31 + s_rollGen; // a value-neutral roll still appends history
                hash = HashFloats(hash, CPlayerData.m_ItemPricePercentChangeList);
                hash = HashMarket(hash, CPlayerData.m_GenCardMarketPriceList);
                hash = HashMarket(hash, CPlayerData.m_GenCardMarketPriceListDestiny);
                hash = HashMarket(hash, CPlayerData.m_GenCardMarketPriceListGhost);
                hash = HashMarket(hash, CPlayerData.m_GenCardMarketPriceListGhostBlack);
                hash = HashMarket(hash, CPlayerData.m_GenCardMarketPriceListMegabot);
                hash = HashMarket(hash, CPlayerData.m_GenCardMarketPriceListFantasyRPG);
                hash = HashMarket(hash, CPlayerData.m_GenCardMarketPriceListCatJob);
                hash = HashFloats(hash, CPlayerData.m_SetGameEventPriceList);
                hash = HashFloats(hash, CPlayerData.m_GeneratedGameEventPriceList);
                hash = HashFloats(hash, CPlayerData.m_GameEventPricePercentChangeList);

                _heal += Interval;
                if (hash == _lastHash && _heal < HealEvery) return;
                _lastHash = hash;
                _heal = 0f;
                BroadcastState(WriteState);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("MarketSync host: " + e.Message); }
        }

        private static void WriteState(BinaryWriter bw)
        {
            bw.Write(s_rollGen); // history-append stamp: post-roll broadcasts only
            WritePercents(bw, CPlayerData.m_ItemPricePercentChangeList);
            WriteMarket(bw, CPlayerData.m_GenCardMarketPriceList);
            WriteMarket(bw, CPlayerData.m_GenCardMarketPriceListDestiny);
            WriteMarket(bw, CPlayerData.m_GenCardMarketPriceListGhost);
            WriteMarket(bw, CPlayerData.m_GenCardMarketPriceListGhostBlack);
            WriteMarket(bw, CPlayerData.m_GenCardMarketPriceListMegabot);
            WriteMarket(bw, CPlayerData.m_GenCardMarketPriceListFantasyRPG);
            WriteMarket(bw, CPlayerData.m_GenCardMarketPriceListCatJob);
            // game-event rows are raw prices, not clamped percents - full floats
            WriteFloats(bw, CPlayerData.m_SetGameEventPriceList);
            WriteFloats(bw, CPlayerData.m_GeneratedGameEventPriceList);
            WriteFloats(bw, CPlayerData.m_GameEventPricePercentChangeList);
        }

        // ---------------- client ----------------

        public void ClientApplyState(BinaryReader br)
        {
            ApplyingRemote = true;
            try { ClientApplyInner(br); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("MarketSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(BinaryReader br)
        {
            int rollGen = br.ReadInt32();
            ReadPercentsInto(br, CPlayerData.m_ItemPricePercentChangeList);
            ReadMarketInto(br, CPlayerData.m_GenCardMarketPriceList);
            ReadMarketInto(br, CPlayerData.m_GenCardMarketPriceListDestiny);
            ReadMarketInto(br, CPlayerData.m_GenCardMarketPriceListGhost);
            ReadMarketInto(br, CPlayerData.m_GenCardMarketPriceListGhostBlack);
            ReadMarketInto(br, CPlayerData.m_GenCardMarketPriceListMegabot);
            ReadMarketInto(br, CPlayerData.m_GenCardMarketPriceListFantasyRPG);
            ReadMarketInto(br, CPlayerData.m_GenCardMarketPriceListCatJob);
            ReadFloatsInto(br, CPlayerData.m_SetGameEventPriceList);
            ReadFloatsInto(br, CPlayerData.m_GeneratedGameEventPriceList);
            ReadFloatsInto(br, CPlayerData.m_GameEventPricePercentChangeList);

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
                _lastAppliedGen = rollGen;
                try
                {
                    CPlayerData.UpdateItemPricePercentChange();
                    CPlayerData.UpdatePastCardPricePercentChange();
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("MarketSync history: " + e.Message); }
            }
        }

        // ---------------- wire helpers ----------------

        // Percent changes are game-clamped to [-80, 200]; x100 fits a short, and 0.01%
        // resolution is beyond anything the UI displays. Halves the snapshot size.
        private static void WritePercents(BinaryWriter bw, List<float> list)
        {
            int n = Mathf.Min(list?.Count ?? 0, ushort.MaxValue);
            bw.Write((ushort)n);
            for (int i = 0; i < n; i++)
                bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(list[i] * 100f), short.MinValue, short.MaxValue));
        }

        private static void ReadPercentsInto(BinaryReader br, List<float> list)
        {
            int n = br.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                float v = br.ReadInt16() / 100f; // always consume the wire bytes
                if (list != null && i < list.Count) list[i] = v;
            }
        }

        private static void WriteMarket(BinaryWriter bw, List<MarketPrice> list)
        {
            int n = Mathf.Min(list?.Count ?? 0, ushort.MaxValue);
            bw.Write((ushort)n);
            for (int i = 0; i < n; i++)
            {
                float v = list[i] != null ? list[i].pricePercentChangeList : 0f;
                bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(v * 100f), short.MinValue, short.MaxValue));
            }
        }

        private static void ReadMarketInto(BinaryReader br, List<MarketPrice> list)
        {
            int n = br.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                float v = br.ReadInt16() / 100f;
                if (list != null && i < list.Count && list[i] != null)
                    list[i].pricePercentChangeList = v; // in place: consumers hold the object
            }
        }

        private static void WriteFloats(BinaryWriter bw, List<float> list)
        {
            int n = Mathf.Min(list?.Count ?? 0, ushort.MaxValue);
            bw.Write((ushort)n);
            for (int i = 0; i < n; i++) bw.Write(list[i]);
        }

        private static void ReadFloatsInto(BinaryReader br, List<float> list)
        {
            int n = br.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                float v = br.ReadSingle();
                if (list != null && i < list.Count) list[i] = v;
            }
        }

        private static int HashFloats(int h, List<float> list)
        {
            if (list == null) return h;
            for (int i = 0; i < list.Count; i++)
                h = h * 31 + (int)(list[i] * 100f);
            return h;
        }

        private static int HashMarket(int h, List<MarketPrice> list)
        {
            if (list == null) return h;
            for (int i = 0; i < list.Count; i++)
                h = h * 31 + (int)((list[i] != null ? list[i].pricePercentChangeList : 0f) * 100f);
            return h;
        }
    }
}
