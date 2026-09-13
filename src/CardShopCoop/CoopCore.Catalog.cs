using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        private static int LocalCatalogHash()
        {
            try
            {
                int total = CatalogCount(); // vanilla + EPL virtual entries
                int h = 17;
                for (int i = 0; i < total; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null)
                        h = h * 31 + (((int)rd.itemType << 1) | (rd.isBigBox ? 1 : 0));
                }
                return h;
            }
            catch (System.Exception e) { Swallow.Log(e); return 0; }
        }

        // ---- EPL virtual catalog bridge ----
        // EPL never ADDS modded products to m_RestockDataList: it INTERCEPTS the
        // game's list accesses (count/indexing) and serves the extra entries from
        // its own ItemLibrary. Direct list reads from THIS assembly see only the
        // ~135 vanilla rows - which is why hosts "didn't have" products sitting on
        // their own shelves, catalogs compared "identical (135)", and modded
        // license heals missed. Every catalog walk must span rawCount + EPL's
        // entries and read rows through the game's INTERCEPTED GetRestockData
        // (calling a game method executes its rewritten body - field-proven by
        // client-side catalog reads finding modded identities).
        private static bool _eplProbed;
        private static System.Reflection.PropertyInfo _eplAssetsProp, _eplItemLibProp, _eplRestockProp;

        private static int EplExtraCount()
        {
            try
            {
                if (!_eplProbed)
                {
                    _eplProbed = true;
                    // assembly-qualified bind first, app-domain type walk only if it misses -
                    // see Util.ModParity.ResolveType for why the walk is worth avoiding
                    var t = Util.ModParity.ResolveType("EnhancedPrefabLoader.Core.EplRuntimeData", "EnhancedPrefabLoader");
                    const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    _eplAssetsProp = t?.GetProperty("Assets", F);
                    var assets = _eplAssetsProp?.GetValue(null);
                    _eplItemLibProp = assets?.GetType().GetProperty("ItemLibrary", F);
                    var lib = assets == null ? null : _eplItemLibProp?.GetValue(assets);
                    _eplRestockProp = lib?.GetType().GetProperty("RestockEntries", F);
                    CoopPlugin.Log.LogInfo(_eplRestockProp != null
                        ? "EPL catalog bridge active (virtual restock entries visible)"
                        : "EPL catalog bridge inactive (EPL absent or its internals changed) - vanilla catalog only");
                }
                var a = _eplAssetsProp?.GetValue(null);
                var l = a == null ? null : _eplItemLibProp?.GetValue(a);
                return (l == null ? null : _eplRestockProp?.GetValue(l) as System.Collections.ICollection)?.Count ?? 0;
            }
            catch (System.Exception e) { Swallow.Log(e); return 0; }
        }

        /// <summary>Full catalog size as the GAME sees it: raw vanilla rows plus EPL's
        /// intercepted virtual entries.</summary>
        private static int CatalogCount()
        {
            int raw = 0;
            try
            {
                raw = Inv().m_StockItemData_SO.m_RestockDataList.Count;
            }
            catch (System.Exception e) { Swallow.Log(e); }
            return raw + EplExtraCount();
        }

        /// <summary>Catalog row through the game's intercepted accessor (valid for
        /// vanilla AND virtual indexes); null when out of range or unresolvable.</summary>
        private static RestockData CatalogAt(int i)
        {
            try
            {
                return InventoryBase.GetRestockData(i);
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>Find OUR restock entry for a partner's (itemType, boxSize) identity.
        /// Tiered: exact -> name+size -> same product any size -> name any size. Content
        /// DATA packs are invisible to the plugin-parity hash, so catalogs CAN differ
        /// between machines - a near-match beats a silently lost order.</summary>
        private static int ResolveRestockIndex(int itemType, bool isBig, string name, out bool sizeDiffers)
        {
            sizeDiffers = false;
            try
            {
                int n = CatalogCount();
                for (int i = 0; i < n; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null && (int)rd.itemType == itemType && rd.isBigBox == isBig)
                        return i;
                }
                if (!string.IsNullOrEmpty(name))
                    for (int i = 0; i < n; i++)
                    {
                        var rd = CatalogAt(i);
                        if (rd != null && rd.name == name && rd.isBigBox == isBig)
                            return i;
                    }
                sizeDiffers = true;
                for (int i = 0; i < n; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null && (int)rd.itemType == itemType)
                        return i;
                }
                if (!string.IsNullOrEmpty(name))
                    for (int i = 0; i < n; i++)
                    {
                        var rd = CatalogAt(i);
                        if (rd != null && rd.name == name)
                            return i;
                    }
            }
            catch (System.Exception e) { Swallow.Log(e); }
            return -1;
        }

    }
}
