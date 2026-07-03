using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors the "shop settings" scalars that the vanilla game keeps in per-client
    /// statics: wall/floor/ceiling deco ownership + equips, the nightly play-table game
    /// event (format, expansion, fee), per-cashier-counter checkout/trade toggles, and
    /// tournament play-table numbers. The host copy is truth; the whole state is tiny,
    /// so it travels as one hash-gated snapshot (broadcast on change + a slow heal).
    ///
    /// Money: the joiner's deco BUY is blocked before it charges (prefix returns false
    /// ahead of CEventPlayer_ReduceCoin) and the host's application does the charging
    /// via the vanilla event - the shared wallet pays exactly once. Everything else
    /// here is free scalars, so those forward as postfixes (instant local apply, the
    /// echo confirms).
    /// </summary>
    public class SettingsSync
    {
        // SettingsOp sub-ops (first byte of every op payload)
        private const byte OpBuyDeco = 1;      // client->host: byte category(0 wall/1 floor/2 ceiling), int index
        private const byte OpEquipDeco = 2;    // client->host: six ints (wall, wallB, floor, floorB, ceiling, ceilingB)
        private const byte OpGameEvent = 3;    // client->host: int pendingFormat, int pendingExpansion
        private const byte OpGameEventFee = 4; // client->host: int format, float fee
        private const byte OpCashier = 5;      // client->host: byte counterIndex, byte flags (1 checkout, 2 trade)
        private const byte OpTableNumber = 6;  // client->host: byte tableIndex, int number

        /// <summary>Patches are static; they reach the live engine through here.</summary>
        public static SettingsSync Instance;

        /// <summary>True while we apply remote state, so our own postfixes don't
        /// re-forward the very change we're applying.</summary>
        public static bool ApplyingRemote;

        public Action<Action<BinaryWriter>> SendOp;         // set by CoopCore: client->host
        public Action<Action<BinaryWriter>> BroadcastState; // set by CoopCore: host->clients

        private float _timer;
        private int _lastHash;
        private float _heal;
        // snapshot is serialized once into a reusable buffer, hashed, and (when changed)
        // written out verbatim - the hash can never drift from what actually ships
        private readonly MemoryStream _stateMs = new MemoryStream(1024);
        private BinaryWriter _stateBw;

        // NEVER CSingleton<>.Instance for these: touched while no real manager exists
        // (client reload loading screen, host mid-session save load - ?. does NOT
        // protect, the auto-create happens before it evaluates) the getter fabricates
        // a fake empty DontDestroyOnLoad manager that shadows the real one for the
        // rest of the run (see WorldSync.ResolveShelfManager). Static because the
        // wire writers and patch postfixes are static; Unity fake-null re-resolves
        // after scene loads.
        private static ShelfManager _sm;
        private static InventoryBase _inv;

        private static ShelfManager Sm()
        {
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        private static InventoryBase Inv()
        {
            if (_inv == null) _inv = UnityEngine.Object.FindObjectOfType<InventoryBase>();
            return _inv;
        }

        public SettingsSync()
        {
            Instance = this;
            _stateBw = new BinaryWriter(_stateMs);
        }

        public void Reset()
        {
            _timer = -2.6f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            ApplyingRemote = false;
            _sm = null;
            _inv = null;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = 15f;
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
                _stateMs.SetLength(0);
                _stateMs.Position = 0;
                WriteState(_stateBw);
                _stateBw.Flush();
                int len = (int)_stateMs.Length;
                byte[] buf = _stateMs.GetBuffer();
                int hash = 17;
                for (int i = 0; i < len; i++) hash = hash * 31 + buf[i];
                _heal += 1.5f;
                if (hash == _lastHash && _heal < 15f) return;
                _lastHash = hash;
                _heal = 0f;
                // Msg.Build invokes the writer synchronously, so the reusable buffer is safe
                BroadcastState?.Invoke(bw => bw.Write(buf, 0, len));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("SettingsSync host: " + e.Message); }
        }

        public void HostApplyOp(BinaryReader br)
        {
            byte op = br.ReadByte();
            try
            {
                switch (op)
                {
                    case OpBuyDeco:
                    {
                        int cat = br.ReadByte();
                        int idx = br.ReadInt32();
                        HostBuyDeco(cat, idx);
                        break;
                    }
                    case OpEquipDeco:
                    {
                        int w = br.ReadInt32(); int wB = br.ReadInt32();
                        int f = br.ReadInt32(); int fB = br.ReadInt32();
                        int c = br.ReadInt32(); int cB = br.ReadInt32();
                        ApplyingRemote = true;
                        try { ApplyEquips(w, wB, f, fB, c, cB); }
                        finally { ApplyingRemote = false; }
                        break;
                    }
                    case OpGameEvent:
                    {
                        int fmt = br.ReadInt32();
                        int exp = br.ReadInt32();
                        // the vanilla confirm is exactly these two field writes
                        CPlayerData.m_PendingGameEventFormat = (EGameEventFormat)fmt;
                        CPlayerData.m_PendingGameEventExpansionType = (ECardExpansionType)exp;
                        break;
                    }
                    case OpGameEventFee:
                    {
                        int fmt = br.ReadInt32();
                        float fee = br.ReadSingle();
                        if (fmt >= 0 && fmt < CPlayerData.m_SetGameEventPriceList.Count)
                        {
                            ApplyingRemote = true;
                            try { PriceChangeManager.SetGameEventPrice((EGameEventFormat)fmt, Mathf.Max(0f, fee)); }
                            finally { ApplyingRemote = false; }
                        }
                        break;
                    }
                    case OpCashier:
                    {
                        int idx = br.ReadByte();
                        byte flags = br.ReadByte();
                        var counters = Sm()?.m_CashierCounterList;
                        if (counters != null && idx < counters.Count && counters[idx] != null)
                        {
                            ApplyingRemote = true;
                            try
                            {
                                bool checkout = (flags & 1) != 0;
                                bool trade = (flags & 2) != 0;
                                if (counters[idx].CanCheckout() != checkout) counters[idx].SetCanCheckout(checkout);
                                if (counters[idx].CanTradeCard() != trade) counters[idx].SetCanTradeCard(trade);
                            }
                            finally { ApplyingRemote = false; }
                        }
                        break;
                    }
                    case OpTableNumber:
                    {
                        int idx = br.ReadByte();
                        int number = br.ReadInt32();
                        var tables = Sm()?.m_PlayTableList;
                        if (tables != null && idx < tables.Count && tables[idx] != null)
                        {
                            ApplyingRemote = true;
                            try
                            {
                                if (tables[idx].GetTournamentPlayTableNumber() != number)
                                    tables[idx].SetTournamentPlayTableNumber(Mathf.Max(0, number));
                            }
                            finally { ApplyingRemote = false; }
                        }
                        break;
                    }
                    default:
                        CoopPlugin.Log.LogWarning("SettingsSync: unknown sub-op " + op);
                        break;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"SettingsSync op {op}: " + e.Message); }
            // no explicit echo: the change lands in the very next hash-gated broadcast
        }

        /// <summary>Host: run the vanilla deco purchase for a joiner. The joiner was
        /// blocked BEFORE charging, so the ReduceCoin here is the only charge. Price
        /// comes from OUR data lists - never from the wire.</summary>
        private void HostBuyDeco(int category, int index)
        {
            var so = Inv()?.m_ObjectData_SO;
            if (so == null || category < 0 || category > 2) return;
            List<ShopDecoData> list =
                category == 0 ? so.m_WallDecoDataList :
                category == 1 ? so.m_FloorDecoDataList : so.m_CeilingDecoDataList;
            if (index < 0 || index >= list.Count || list[index] == null) return;
            // already owned (double-click / duplicate op): never charge twice
            bool owned =
                category == 0 ? CPlayerData.IsDecoWallUnlocked(index) :
                category == 1 ? CPlayerData.IsDecoFloorUnlocked(index) : CPlayerData.IsDecoCeilingUnlocked(index);
            if (owned) return;
            float price = list[index].price;
            if (CPlayerData.m_CoinAmountDouble < (double)price)
            {
                CoopPlugin.Log.LogInfo($"deco buy refused (funds): cat {category} idx {index}");
                return;
            }
            // vanilla ShopBuyDecoUIScreen.OnPressBuyShopDeco body, minus the UI refresh
            CPlayerData.m_GameReportDataCollect.upgradeCost -= price;
            CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= price;
            PriceChangeManager.AddTransaction(0f - price, ETransactionType.BuyDecoration, category, index);
            CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(price));
            if (category == 0) CPlayerData.SetUnlockDecoWall(index, isUnlocked: true);
            else if (category == 1) CPlayerData.SetUnlockDecoFloor(index, isUnlocked: true);
            else CPlayerData.SetUnlockDecoCeiling(index, isUnlocked: true);
            CoopPlugin.Log.LogInfo($"partner bought deco: cat {category} idx {index} for {price}");
        }

        // ---------------- client ----------------

        public void ClientApplyState(BinaryReader br)
        {
            ApplyingRemote = true;
            try
            {
                // deco ownership (host list sizes rule; extra local entries keep their state)
                ApplyBoolList(br, CPlayerData.m_UnlockedDecoWallList, CPlayerData.SetUnlockDecoWall);
                ApplyBoolList(br, CPlayerData.m_UnlockedDecoFloorList, CPlayerData.SetUnlockDecoFloor);
                ApplyBoolList(br, CPlayerData.m_UnlockedDecoCeilingList, CPlayerData.SetUnlockDecoCeiling);

                int w = br.ReadInt32(); int wB = br.ReadInt32();
                int f = br.ReadInt32(); int fB = br.ReadInt32();
                int c = br.ReadInt32(); int cB = br.ReadInt32();
                ApplyEquips(w, wB, f, fB, c, cB);

                CPlayerData.m_GameEventFormat = (EGameEventFormat)br.ReadInt32();
                CPlayerData.m_PendingGameEventFormat = (EGameEventFormat)br.ReadInt32();
                CPlayerData.m_GameEventExpansionType = (ECardExpansionType)br.ReadInt32();
                CPlayerData.m_PendingGameEventExpansionType = (ECardExpansionType)br.ReadInt32();
                int feeCount = br.ReadByte();
                for (int i = 0; i < feeCount; i++)
                {
                    float fee = br.ReadSingle();
                    if (i < CPlayerData.m_SetGameEventPriceList.Count)
                        CPlayerData.m_SetGameEventPriceList[i] = fee;
                }

                var counters = Sm()?.m_CashierCounterList;
                int cn = br.ReadByte();
                for (int i = 0; i < cn; i++)
                {
                    byte flags = br.ReadByte();
                    if (counters == null || i >= counters.Count || counters[i] == null) continue;
                    bool checkout = (flags & 1) != 0;
                    bool trade = (flags & 2) != 0;
                    // setters refresh the counter's own signage, so only call on change
                    if (counters[i].CanCheckout() != checkout) counters[i].SetCanCheckout(checkout);
                    if (counters[i].CanTradeCard() != trade) counters[i].SetCanTradeCard(trade);
                }

                var tables = Sm()?.m_PlayTableList;
                int tn = br.ReadByte();
                for (int i = 0; i < tn; i++)
                {
                    int number = br.ReadByte();
                    if (tables == null || i >= tables.Count || tables[i] == null) continue;
                    if (tables[i].GetTournamentPlayTableNumber() != number)
                        tables[i].SetTournamentPlayTableNumber(number);
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("SettingsSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        // ---------------- shared apply ----------------

        private static void ApplyBoolList(BinaryReader br, List<bool> local, Action<int, bool> setter)
        {
            int n = br.ReadByte();
            for (int i = 0; i < n; i++)
            {
                bool v = br.ReadBoolean();
                if (local != null && i < local.Count && local[i] != v) setter(i, v);
            }
        }

        /// <summary>Set the six equipped indices and retexture through the game's own
        /// customization manager - only the ones that actually changed (material writes
        /// touch shared assets, no reason to churn them every snapshot).</summary>
        private static void ApplyEquips(int wall, int wallB, int floor, int floorB, int ceiling, int ceilingB)
        {
            var so = Inv()?.m_ObjectData_SO;
            if (so == null) return;
            if (wall != CPlayerData.m_EquippedWallDecoIndex && wall >= 0 && wall < so.m_WallDecoDataList.Count)
            {
                CPlayerData.m_EquippedWallDecoIndex = wall;
                try { ShopCustomizationManager.ChangeWallMaterial(wall, isShopLotB: false); } catch { }
            }
            if (wallB != CPlayerData.m_EquippedWallDecoIndexB && wallB >= 0 && wallB < so.m_WallDecoDataList.Count)
            {
                CPlayerData.m_EquippedWallDecoIndexB = wallB;
                try { ShopCustomizationManager.ChangeWallMaterial(wallB, isShopLotB: true); } catch { }
            }
            if (floor != CPlayerData.m_EquippedFloorDecoIndex && floor >= 0 && floor < so.m_FloorDecoDataList.Count)
            {
                CPlayerData.m_EquippedFloorDecoIndex = floor;
                try { ShopCustomizationManager.ChangeFloorMaterial(floor, isShopLotB: false); } catch { }
            }
            if (floorB != CPlayerData.m_EquippedFloorDecoIndexB && floorB >= 0 && floorB < so.m_FloorDecoDataList.Count)
            {
                CPlayerData.m_EquippedFloorDecoIndexB = floorB;
                try { ShopCustomizationManager.ChangeFloorMaterial(floorB, isShopLotB: true); } catch { }
            }
            if (ceiling != CPlayerData.m_EquippedCeilingDecoIndex && ceiling >= 0 && ceiling < so.m_CeilingDecoDataList.Count)
            {
                CPlayerData.m_EquippedCeilingDecoIndex = ceiling;
                try { ShopCustomizationManager.ChangeCeilingMaterial(ceiling, isShopLotB: false); } catch { }
            }
            if (ceilingB != CPlayerData.m_EquippedCeilingDecoIndexB && ceilingB >= 0 && ceilingB < so.m_CeilingDecoDataList.Count)
            {
                CPlayerData.m_EquippedCeilingDecoIndexB = ceilingB;
                try { ShopCustomizationManager.ChangeCeilingMaterial(ceilingB, isShopLotB: true); } catch { }
            }
        }

        // ---------------- wire ----------------

        private static void WriteState(BinaryWriter bw)
        {
            WriteBoolList(bw, CPlayerData.m_UnlockedDecoWallList);
            WriteBoolList(bw, CPlayerData.m_UnlockedDecoFloorList);
            WriteBoolList(bw, CPlayerData.m_UnlockedDecoCeilingList);
            bw.Write(CPlayerData.m_EquippedWallDecoIndex);
            bw.Write(CPlayerData.m_EquippedWallDecoIndexB);
            bw.Write(CPlayerData.m_EquippedFloorDecoIndex);
            bw.Write(CPlayerData.m_EquippedFloorDecoIndexB);
            bw.Write(CPlayerData.m_EquippedCeilingDecoIndex);
            bw.Write(CPlayerData.m_EquippedCeilingDecoIndexB);
            bw.Write((int)CPlayerData.m_GameEventFormat);
            bw.Write((int)CPlayerData.m_PendingGameEventFormat);
            bw.Write((int)CPlayerData.m_GameEventExpansionType);
            bw.Write((int)CPlayerData.m_PendingGameEventExpansionType);
            var fees = CPlayerData.m_SetGameEventPriceList;
            int fn = Mathf.Min(fees.Count, 255);
            bw.Write((byte)fn);
            for (int i = 0; i < fn; i++) bw.Write(fees[i]);
            var counters = Sm()?.m_CashierCounterList;
            int cn = counters == null ? 0 : Mathf.Min(counters.Count, 255);
            bw.Write((byte)cn);
            for (int i = 0; i < cn; i++)
            {
                // a destroyed slot reads as vanilla defaults (both enabled)
                byte flags = 3;
                if (counters[i] != null)
                    flags = (byte)((counters[i].CanCheckout() ? 1 : 0) | (counters[i].CanTradeCard() ? 2 : 0));
                bw.Write(flags);
            }
            var tables = Sm()?.m_PlayTableList;
            int tn = tables == null ? 0 : Mathf.Min(tables.Count, 255);
            bw.Write((byte)tn);
            for (int i = 0; i < tn; i++)
            {
                int num = tables[i] != null ? tables[i].GetTournamentPlayTableNumber() : 0;
                bw.Write((byte)Mathf.Clamp(num, 0, 255)); // numbers never exceed the table count
            }
        }

        private static void WriteBoolList(BinaryWriter bw, List<bool> list)
        {
            int n = list == null ? 0 : Mathf.Min(list.Count, 255);
            bw.Write((byte)n);
            for (int i = 0; i < n; i++) bw.Write(list[i]);
        }

        // ---------------- patches ----------------

        private static readonly System.Reflection.FieldInfo FiBuyCategory =
            AccessTools.Field(typeof(ShopBuyDecoUIScreen), "m_CategoryIndex");

        public static void ApplyPatches(Harmony h)
        {
            // Joiner deco purchase: block BEFORE the coin charge; the host charges once.
            Try(h, typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDeco",
                prefix: new HarmonyMethod(typeof(SettingsSync), nameof(BuyDecoPrefix)));

            // Equips are free: apply locally for instant feedback, forward, echo confirms.
            // Patched at the button handler (not ChangeXMaterial) so the save-load path
            // that replays materials at scene start never forwards anything.
            Try(h, typeof(PlaceDecoUIScreen), "OnPressSwitchShopDeco",
                postfix: new HarmonyMethod(typeof(SettingsSync), nameof(EquipDecoPostfix)));

            // Game event: the vanilla "confirm" is a pending-field write in the format
            // screen; reset lives on the parent screen. Both are free scalar writes.
            Try(h, typeof(SetGameEventFormatScreen), "OnPressConfirmBtn",
                postfix: new HarmonyMethod(typeof(SettingsSync), nameof(GameEventPostfix)));
            Try(h, typeof(SetGameEventScreen), "OnPressReset",
                postfix: new HarmonyMethod(typeof(SettingsSync), nameof(GameEventPostfix)));

            // The fee funnel: every fee write (price screen confirm AND the reset
            // restore) goes through this one static setter.
            Try(h, typeof(PriceChangeManager), "SetGameEventPrice",
                postfix: new HarmonyMethod(typeof(SettingsSync), nameof(GameEventFeePostfix)));

            // Cashier toggles + tournament table numbers: the interactable setters are
            // the funnel for every UI path (toggle, swap, clear-renumber).
            Try(h, typeof(InteractableCashierCounter), "SetCanCheckout",
                postfix: new HarmonyMethod(typeof(SettingsSync), nameof(CashierPostfix)));
            Try(h, typeof(InteractableCashierCounter), "SetCanTradeCard",
                postfix: new HarmonyMethod(typeof(SettingsSync), nameof(CashierPostfix)));
            Try(h, typeof(InteractablePlayTable), "SetTournamentPlayTableNumber",
                postfix: new HarmonyMethod(typeof(SettingsSync), nameof(TableNumberPostfix)));
        }

        public static bool BuyDecoPrefix(ShopBuyDecoUIScreen __instance, int shopDecoIndex, float price)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            int cat = -1;
            try { cat = (int)FiBuyCategory.GetValue(__instance); } catch { }
            if (cat < 0 || cat > 2) return true; // item-deco pages use the other handler
            // local funds check is cosmetic (the wallet mirror is authoritative-ish);
            // the host re-checks against the real balance before charging
            if (CPlayerData.m_CoinAmountDouble < (double)price)
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.Money);
                return false;
            }
            var inst = Instance;
            if (inst?.SendOp != null)
            {
                int c = cat;
                inst.SendOp(bw => { bw.Write(OpBuyDeco); bw.Write((byte)c); bw.Write(shopDecoIndex); });
            }
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "deco purchase sent to the host";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false; // ownership echoes back in the next state broadcast
        }

        public static void EquipDecoPostfix()
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client) return;
            var inst = Instance;
            if (inst?.SendOp == null) return;
            inst.SendOp(bw =>
            {
                bw.Write(OpEquipDeco);
                bw.Write(CPlayerData.m_EquippedWallDecoIndex);
                bw.Write(CPlayerData.m_EquippedWallDecoIndexB);
                bw.Write(CPlayerData.m_EquippedFloorDecoIndex);
                bw.Write(CPlayerData.m_EquippedFloorDecoIndexB);
                bw.Write(CPlayerData.m_EquippedCeilingDecoIndex);
                bw.Write(CPlayerData.m_EquippedCeilingDecoIndexB);
            });
        }

        public static void GameEventPostfix()
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client) return;
            var inst = Instance;
            if (inst?.SendOp == null) return;
            inst.SendOp(bw =>
            {
                bw.Write(OpGameEvent);
                bw.Write((int)CPlayerData.m_PendingGameEventFormat);
                bw.Write((int)CPlayerData.m_PendingGameEventExpansionType);
            });
        }

        public static void GameEventFeePostfix(EGameEventFormat gameEventFormat, float price)
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client) return;
            var inst = Instance;
            if (inst?.SendOp == null) return;
            inst.SendOp(bw =>
            {
                bw.Write(OpGameEventFee);
                bw.Write((int)gameEventFormat);
                bw.Write(price);
            });
        }

        public static void CashierPostfix(InteractableCashierCounter __instance)
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client) return;
            var inst = Instance;
            if (inst?.SendOp == null) return;
            var counters = Sm()?.m_CashierCounterList;
            if (counters == null) return;
            int idx = counters.IndexOf(__instance);
            if (idx < 0 || idx > 254) return;
            byte flags = (byte)((__instance.CanCheckout() ? 1 : 0) | (__instance.CanTradeCard() ? 2 : 0));
            inst.SendOp(bw => { bw.Write(OpCashier); bw.Write((byte)idx); bw.Write(flags); });
        }

        public static void TableNumberPostfix(InteractablePlayTable __instance, int tableNumber)
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client) return;
            var inst = Instance;
            if (inst?.SendOp == null) return;
            var tables = Sm()?.m_PlayTableList;
            if (tables == null) return;
            int idx = tables.IndexOf(__instance);
            if (idx < 0 || idx > 254) return;
            inst.SendOp(bw => { bw.Write(OpTableNumber); bw.Write((byte)idx); bw.Write(tableNumber); });
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"SettingsSync patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"SettingsSync patch failed for {type.Name}.{method}: {e.Message}");
            }
        }
    }
}
