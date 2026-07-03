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
                // generated BASE prices: per-save tables filled the first time a machine
                // "meets" an item. Content packs installed mid-save get bases only on the
                // host - without this the joiner sees $0 market prices for them forever
                hash = HashFloats(hash, CPlayerData.m_GeneratedMarketPriceList);
                hash = HashFloats(hash, CPlayerData.m_GeneratedCostPriceList);
                hash = HashFloats(hash, CPlayerData.m_AverageItemCostList);
                // modded rows live in EPL's save data, not in the raw lists above -
                // without this the hash never moves when only a modded price changes
                hash = HashEplMarket(hash);

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
            var modded = EplModdedItemTypes(); // empty when the bridge is inactive
            bw.Write(s_rollGen); // history-append stamp: post-roll broadcasts only
            WritePercents(bw, CPlayerData.m_ItemPricePercentChangeList, modded);
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
            WriteSparseFloats(bw, CPlayerData.m_GeneratedMarketPriceList, modded, s_eplGenMarket);
            WriteSparseFloats(bw, CPlayerData.m_GeneratedCostPriceList, modded, s_eplGenCost);
            WriteSparseFloats(bw, CPlayerData.m_AverageItemCostList, modded, s_eplAvgCost);
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
            ReadSparseFloatsInto(br, CPlayerData.m_GeneratedMarketPriceList, ModdedGenMarket);
            ReadSparseFloatsInto(br, CPlayerData.m_GeneratedCostPriceList, ModdedGenCost);
            ReadSparseFloatsInto(br, CPlayerData.m_AverageItemCostList, ModdedAvgCost);

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
        // resolution is beyond anything the UI displays. Entries are keyed by RAW
        // itemType and EPL registers modded items at huge enum values (>= 200k), so
        // only non-zero entries ship, as (index, pct) pairs - a dense send both
        // truncated past 65535 (modded items never synced) and wasted ~128KB per roll.
        // Vanilla values come off the raw list; modded values off the EPL bridge.
        private static void WritePercents(BinaryWriter bw, List<float> list, List<int> modded)
        {
            int vanilla = VanillaWalkCount(list);
            int nonZero = 0;
            for (int i = 0; i < vanilla; i++) if (list[i] != 0f) nonZero++;
            var moddedVals = CollectModded(modded, s_eplPctChange);
            bw.Write(nonZero + moddedVals.Count);
            for (int i = 0; i < vanilla; i++)
                if (list[i] != 0f)
                {
                    bw.Write(i);
                    bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(list[i] * 100f), short.MinValue, short.MaxValue));
                }
            for (int k = 0; k < moddedVals.Count; k++)
            {
                bw.Write(moddedVals[k].Key);
                bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(moddedVals[k].Value * 100f), short.MinValue, short.MaxValue));
            }
        }

        // Generated base prices are FULL floats keyed by raw itemType (the >= 200k
        // modded id space), non-zero entries only. Unlike percents, absent entries keep
        // their local value - the host may legitimately have gaps we filled at join.
        private static void WriteSparseFloats(BinaryWriter bw, List<float> list, List<int> modded, PropertyInfo eplField)
        {
            int vanilla = VanillaWalkCount(list);
            int nonZero = 0;
            for (int i = 0; i < vanilla; i++) if (list[i] != 0f) nonZero++;
            var moddedVals = CollectModded(modded, eplField);
            bw.Write(nonZero + moddedVals.Count);
            for (int i = 0; i < vanilla; i++)
                if (list[i] != 0f)
                {
                    bw.Write(i);
                    bw.Write(list[i]);
                }
            for (int k = 0; k < moddedVals.Count; k++)
            {
                bw.Write(moddedVals[k].Key);
                bw.Write(moddedVals[k].Value);
            }
        }

        private static void ReadSparseFloatsInto(BinaryReader br, List<float> list, Action<int, float> moddedWrite)
        {
            int n = br.ReadInt32();
            for (int k = 0; k < n; k++)
            {
                int i = br.ReadInt32();
                float v = br.ReadSingle(); // always consume the wire bytes
                if (list == null || i < 0 || i > 500000) continue;
                if (i >= VanillaItemTypes && EplMarketBridge())
                {
                    // raw writes up here are shadow rows the game never reads (the
                    // woven accessors serve EPL save data instead) - the original
                    // sin behind modded items' $0 market prices on the joiner
                    try { moddedWrite(i, v); } catch { }
                    continue;
                }
                while (list.Count <= i) list.Add(0f); // grow-on-demand (no-EPL fallback)
                list[i] = v;
            }
        }

        private static void ReadPercentsInto(BinaryReader br, List<float> list)
        {
            if (list != null)
                for (int i = 0; i < list.Count; i++) list[i] = 0f; // absent = no change rolled
            if (EplMarketBridge())
            {
                // modded percents need the same absent-means-zero treatment, but they
                // live in EPL save data, not in the raw list zeroed above
                var modded = EplModdedItemTypes();
                for (int k = 0; k < modded.Count; k++) EplSetFloat(modded[k], s_eplPctChange, 0f);
            }
            int n = br.ReadInt32();
            for (int k = 0; k < n; k++)
            {
                int i = br.ReadInt32();
                float v = br.ReadInt16() / 100f; // always consume the wire bytes
                if (list == null || i < 0 || i > 500000) continue;
                if (i >= VanillaItemTypes && EplMarketBridge())
                {
                    EplSetFloat(i, s_eplPctChange, v);
                    continue;
                }
                while (list.Count <= i) list.Add(0f); // grow-on-demand (no-EPL fallback)
                list[i] = v;
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
        private const int VanillaItemTypes = 129; // EPL's hardcoded woven boundary

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
                    var t = AccessTools.TypeByName("EnhancedPrefabLoader.Core.EplRuntimeData");
                    var save = AccessTools.TypeByName("EnhancedPrefabLoader.Core.Models.SaveData.ItemSaveData");
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
                catch { }
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
            return EplMarketBridge() ? Mathf.Min(n, VanillaItemTypes) : n;
        }

        /// <summary>Raw itemType ints of EPL's modded items (ItemLibrary.ItemData keys);
        /// empty without the bridge. Ids outside [200000, 500000] are unshippable: below
        /// lands in EPL's machine-local index space, above fails the receive cap.</summary>
        private static List<int> EplModdedItemTypes()
        {
            var result = new List<int>();
            if (!EplMarketBridge()) return result;
            try
            {
                var assets = s_eplAssetsProp.GetValue(null);
                var lib = assets == null ? null : s_eplItemLibProp.GetValue(assets);
                var dict = lib == null ? null : s_eplItemDataProp.GetValue(lib) as System.Collections.IDictionary;
                if (dict != null)
                    foreach (object key in dict.Keys)
                    {
                        int v = Convert.ToInt32(key);
                        if (v >= 200000 && v <= 500000) result.Add(v);
                    }
            }
            catch { }
            return result;
        }

        private static object EplSaveData(int itemType)
        {
            try
            {
                var args = new object[] { (EItemType)itemType, null };
                return (bool)s_eplTryGet.Invoke(s_eplSaveMgr, args) ? args[1] : null;
            }
            catch { return null; }
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
            if (d != null) field.SetValue(d, value, null);
        }

        private static List<KeyValuePair<int, float>> CollectModded(List<int> modded, PropertyInfo field)
        {
            var vals = new List<KeyValuePair<int, float>>(modded.Count);
            if (field != null)
                for (int k = 0; k < modded.Count; k++)
                {
                    float v = EplGetFloat(modded[k], field);
                    if (v != 0f) vals.Add(new KeyValuePair<int, float>(modded[k], v));
                }
            return vals;
        }

        // modded-index writers for ReadSparseFloatsInto (bridge verified by the caller)
        private static void ModdedGenMarket(int itemType, float v) { EplSetFloat(itemType, s_eplGenMarket, v); }
        private static void ModdedGenCost(int itemType, float v) { EplSetFloat(itemType, s_eplGenCost, v); }
        private static void ModdedAvgCost(int itemType, float v)
        {
            // the GAME's setter: no math in its body, and its woven form routes
            // >= 129 into EPL save data exactly like the reflection path
            CPlayerData.SetAverageItemCost((EItemType)itemType, v);
        }

        private static int HashEplMarket(int h)
        {
            var modded = EplModdedItemTypes();
            for (int k = 0; k < modded.Count; k++)
            {
                object d = EplSaveData(modded[k]);
                if (d == null) continue;
                h = h * 31 + (int)((float)s_eplPctChange.GetValue(d, null) * 100f);
                h = h * 31 + (int)((float)s_eplGenMarket.GetValue(d, null) * 100f);
                h = h * 31 + (int)((float)s_eplGenCost.GetValue(d, null) * 100f);
                h = h * 31 + (int)((float)s_eplAvgCost.GetValue(d, null) * 100f);
            }
            return h;
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
