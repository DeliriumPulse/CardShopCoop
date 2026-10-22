using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.Collections.Generic;
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
    public class SettingsSync : TickableCoopModule
    {
        public override string Name => "settings";

        // SettingsOp sub-ops (first byte of every op payload)
        private const byte OpBuyDeco = 1;      // client->host: byte category(0 wall/1 floor/2 ceiling), int index
        private const byte OpEquipDeco = 2;    // client->host: six ints (wall, wallB, floor, floorB, ceiling, ceilingB)
        private const byte OpGameEvent = 3;    // client->host: int pendingFormat, int pendingExpansion
        private const byte OpGameEventFee = 4; // client->host: int format, float fee
        private const byte OpCashier = 5;      // client->host: stable counter ObjectKey, flags (1 checkout, 2 trade)
        private const byte OpTableNumber = 6;  // client->host: byte tableIndex, int number
        private const byte OpBuyItemDeco = 7;
        private const byte OpPlaceItemDeco = 8;
        private const byte OpRemoveItemDeco = 9;
        private const byte OpAutoTableNumber = 10;
        private const byte OpClearTableNumber = 11;
        private static readonly System.Reflection.FieldInfo CurrentNumberTable =
            AccessTools.Field(typeof(SetPlayerTableNumberScreen), "m_CurrentPlayTable");
        private static readonly System.Reflection.FieldInfo RequestedTableNumber =
            AccessTools.Field(typeof(SetPlayerTableNumberScreen), "m_PlayTableNumber");

        /// <summary>Patches are static; they reach the live engine through here.</summary>
        public static SettingsSync Instance;

        /// <summary>True while we apply remote state, so our own postfixes don't
        /// re-forward the very change we're applying.</summary>
        public static bool ApplyingRemote;

        public Action<INetMessage> SendOp;         // set by CoopCore: client->host
        public Action<INetMessage> BroadcastState; // set by CoopCore: host->clients

        private readonly SnapshotGate _gate = new SnapshotGate(1.5f, 15f, -2.6f, hasHashInitially: false);

        // NEVER CSingleton<>.Instance for these: touched while no real manager exists
        // (client reload loading screen, host mid-session save load - ?. does NOT
        // protect, the auto-create happens before it evaluates) the getter fabricates
        // a fake empty DontDestroyOnLoad manager that shadows the real one for the
        // rest of the run (see WorldSync.ResolveShelfManager). Static because the
        // wire writers and patch postfixes are static; Unity fake-null re-resolves
        // after scene loads.
        private static ShelfManager _sm;
        private static InventoryBase _inv;
        private static readonly System.Reflection.FieldInfo FiPlacePage =
            AccessTools.Field(typeof(PlaceDecoUIScreen), "m_PageIndex");
        private static readonly System.Reflection.FieldInfo FiBuyPage =
            AccessTools.Field(typeof(ShopBuyDecoUIScreen), "m_PageIndex");
        private static readonly System.Reflection.MethodInfo MiPlacePage =
            AccessTools.Method(typeof(PlaceDecoUIScreen), "EvaluatePanelUIPage");
        private static readonly System.Reflection.MethodInfo MiBuyPage =
            AccessTools.Method(typeof(ShopBuyDecoUIScreen), "EvaluatePanelUIPage");

        private static ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        private static InventoryBase Inv()
        {
            if (_inv == null)
                _inv = UnityEngine.Object.FindObjectOfType<InventoryBase>();
            return _inv;
        }

        public SettingsSync()
        {
            Instance = this;
        }

        public override void Start() => Instance = this;

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public override void Reset()
        {
            _gate.Reset(-2.6f);
            ApplyingRemote = false;
            _sm = null;
            _inv = null;
        }

        public override void ForceResend()
        {
            _gate.Force();
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(Instance, this))
                Instance = null;
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
                int hash = HashState();
                if (!_gate.ShouldSend(hash))
                    return;
                var msg = BuildStateMessage();
                BroadcastState?.Invoke(msg);
            });
        }

        public bool HostApplyOp(SettingsOpMessage message)
        {
            bool purchased = false;
            byte op = message.Op;
            Guarded("apply", () =>
            {
                switch (op)
                {
                    case OpBuyDeco:
                        {
                            int cat = message.Category;
                            int idx = message.Index;
                            purchased = HostBuyDeco(cat, idx);
                            break;
                        }
                    case OpEquipDeco:
                        {
                            int w = message.Wall;
                            int wB = message.WallB;
                            int f = message.Floor;
                            int fB = message.FloorB;
                            int c = message.Ceiling;
                            int cB = message.CeilingB;
                            ApplyingRemote = true;
                            try
                            {
                                ApplyEquips(w, wB, f, fB, c, cB);
                            }
                            finally { ApplyingRemote = false; }
                            break;
                        }
                    case OpGameEvent:
                        {
                            int fmt = message.Index;
                            // the DTO already translated the wire id (we are the host, so this is
                            // the identity function); it goes through the helper anyway so the
                            // op's two ends stay visibly paired
                            var exp = message.Expansion;
                            // the vanilla confirm is exactly these two field writes
                            CPlayerData.m_PendingGameEventFormat = (EGameEventFormat)fmt;
                            CPlayerData.m_PendingGameEventExpansionType = exp;
                            break;
                        }
                    case OpGameEventFee:
                        {
                            int fmt = message.Index;
                            float fee = message.Fee;
                            if (fmt >= 0 && fmt < CPlayerData.m_SetGameEventPriceList.Count)
                            {
                                ApplyingRemote = true;
                                try
                                {
                                    PriceChangeManager.SetGameEventPrice((EGameEventFormat)fmt, Mathf.Max(0f, fee));
                                }
                                finally { ApplyingRemote = false; }
                            }
                            break;
                        }
                    case OpCashier:
                        {
                            byte flags = message.CashierFlags;
                            if (message.ObjectKey >> 24 == 4 && PlacedObjectIdentity.TryResolve(Sm(), 4,
                                PlacedObjectIdentity.ObjectIdFromObjectKey(message.ObjectKey), out var rawCounter)
                                && rawCounter is InteractableCashierCounter counter)
                            {
                                ApplyingRemote = true;
                                try
                                {
                                    bool checkout = (flags & 1) != 0;
                                    bool trade = (flags & 2) != 0;
                                    if (counter.CanCheckout() != checkout)
                                        counter.SetCanCheckout(checkout);
                                    if (counter.CanTradeCard() != trade)
                                        counter.SetCanTradeCard(trade);
                                }
                                finally { ApplyingRemote = false; }
                            }
                            break;
                        }
                    case OpAutoTableNumber:
                    case OpClearTableNumber:
                    case OpTableNumber:
                        {
                            var manager = Sm();
                            if (message.ObjectKey >> 24 != 6 || !PlacedObjectIdentity.TryResolve(manager, 6,
                                PlacedObjectIdentity.ObjectIdFromObjectKey(message.ObjectKey), out var raw)
                                || !(raw is InteractablePlayTable table))
                                break;
                            var tables = manager.m_PlayTableList;
                            var numbers = new int[tables.Count];
                            for (int i = 0; i < numbers.Length; i++)
                                numbers[i] = tables[i] != null ? tables[i].GetTournamentPlayTableNumber() : 0;
                            var action = op == OpAutoTableNumber ? TableNumberAction.Auto
                                : op == OpClearTableNumber ? TableNumberAction.Clear : TableNumberAction.Set;
                            var plan = TableNumberRules.Plan(numbers, tables.IndexOf(table), action,
                                message.TableNumber, CPlayerData.m_TournamentData.m_IsTournamentDay);
                            if (plan == null)
                                break;
                            ApplyingRemote = true;
                            try
                            {
                                for (int i = 0; i < plan.Length; i++)
                                {
                                    if (tables[i] == null || numbers[i] == plan[i])
                                        continue;
                                    tables[i].SetTournamentPlayTableNumber(plan[i]);
                                    if (plan[i] > 0 && tables[i].GetHasStartPlayerPlayCard())
                                        tables[i].ExitPlayerCardGame(false, false);
                                    manager.UpdatePlayTableSaveData(tables[i]);
                                }
                            }
                            finally { ApplyingRemote = false; }
                            break;
                        }
                    case OpBuyItemDeco:
                        purchased = HostBuyItemDeco(message.DecoType);
                        break;
                    case OpPlaceItemDeco:
                        HostPlaceItemDeco(message.DecoType, message.Position, message.Rotation);
                        break;
                    case OpRemoveItemDeco:
                        HostRemoveItemDeco(message.ObjectKey);
                        break;
                    default:
                        CoopPlugin.Log.LogWarning("SettingsSync: unknown sub-op " + op);
                        break;
                }
            });
            // no explicit echo: the change lands in the very next hash-gated broadcast
            return purchased;
        }

        /// <summary>Host: run the vanilla deco purchase for a joiner. The joiner was
        /// blocked BEFORE charging, so the ReduceCoin here is the only charge. Price
        /// comes from OUR data lists - never from the wire.</summary>
        private bool HostBuyDeco(int category, int index)
        {
            var so = Inv()?.m_ObjectData_SO;
            if (so == null || category < 0 || category > 2)
                return false;
            List<ShopDecoData> list =
                category == 0 ? so.m_WallDecoDataList :
                category == 1 ? so.m_FloorDecoDataList : so.m_CeilingDecoDataList;
            if (index < 0 || index >= list.Count || list[index] == null)
                return false;
            // already owned (double-click / duplicate op): never charge twice
            bool owned =
                category == 0 ? CPlayerData.IsDecoWallUnlocked(index) :
                category == 1 ? CPlayerData.IsDecoFloorUnlocked(index) : CPlayerData.IsDecoCeilingUnlocked(index);
            if (owned)
                return false;
            float price = list[index].price;
            if (!(CoopCore.Instance?.TryReserveGuestSpend(price) ?? false))
            {
                CoopPlugin.Log.LogInfo($"deco buy refused (funds): cat {category} idx {index}");
                return false;
            }
            // vanilla ShopBuyDecoUIScreen.OnPressBuyShopDeco body, minus the UI refresh
            CPlayerData.m_GameReportDataCollect.upgradeCost -= price;
            CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= price;
            PriceChangeManager.AddTransaction(0f - price, ETransactionType.BuyDecoration, category, index);
            CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(price));
            if (category == 0)
                CPlayerData.SetUnlockDecoWall(index, isUnlocked: true);
            else if (category == 1)
                CPlayerData.SetUnlockDecoFloor(index, isUnlocked: true);
            else
                CPlayerData.SetUnlockDecoCeiling(index, isUnlocked: true);
            CoopPlugin.Log.LogInfo($"partner bought deco: cat {category} idx {index} for {price}");
            return true;
        }

        // ---------------- client ----------------

        public void ClientApplyState(SettingsStateMessage message)
        {
            int decoBefore = DecoStateHash();
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () =>
                {
                    // deco ownership (host list sizes rule; extra local entries keep their state)
                    ApplyBoolList(message.WallUnlocked, CPlayerData.m_UnlockedDecoWallList, CPlayerData.SetUnlockDecoWall);
                    ApplyBoolList(message.FloorUnlocked, CPlayerData.m_UnlockedDecoFloorList, CPlayerData.SetUnlockDecoFloor);
                    ApplyBoolList(message.CeilingUnlocked, CPlayerData.m_UnlockedDecoCeilingList, CPlayerData.SetUnlockDecoCeiling);

                    int w = message.EquippedWallIndex;
                    int wB = message.EquippedWallIndexB;
                    int f = message.EquippedFloorIndex;
                    int fB = message.EquippedFloorIndexB;
                    int c = message.EquippedCeilingIndex;
                    int cB = message.EquippedCeilingIndexB;
                    ApplyEquips(w, wB, f, fB, c, cB);

                    CPlayerData.m_GameEventFormat = (EGameEventFormat)message.GameEventFormat;
                    CPlayerData.m_PendingGameEventFormat = (EGameEventFormat)message.PendingGameEventFormat;
                    // host ids -> ours (see the DTO). A game event on an expansion only the
                    // host has resolves to ECardExpansionType.None, which reads exactly like
                    // "no expansion picked yet" - the joiner's own packs are untouched
                    CPlayerData.m_GameEventExpansionType = message.GameEventExpansion;
                    CPlayerData.m_PendingGameEventExpansionType = message.PendingGameEventExpansion;
                    int feeCount = message.GameEventPrices.Count;
                    for (int i = 0; i < feeCount; i++)
                    {
                        float fee = message.GameEventPrices[i];
                        if (i < CPlayerData.m_SetGameEventPriceList.Count)
                            CPlayerData.m_SetGameEventPriceList[i] = fee;
                    }

                    int cn = message.CashierFlags.Count;
                    for (int i = 0; i < cn; i++)
                    {
                        byte flags = message.CashierFlags[i];
                        if (message.CashierIds == null || i >= message.CashierIds.Count
                            || !PlacedObjectIdentity.TryResolve(Sm(), 4, message.CashierIds[i], out var rawCounter)
                            || !(rawCounter is InteractableCashierCounter counter))
                            continue;
                        bool checkout = (flags & 1) != 0;
                        bool trade = (flags & 2) != 0;
                        // setters refresh the counter's own signage, so only call on change
                        if (counter.CanCheckout() != checkout)
                            counter.SetCanCheckout(checkout);
                        if (counter.CanTradeCard() != trade)
                            counter.SetCanTradeCard(trade);
                    }

                    int tn = message.TableNumbers.Count;
                    for (int i = 0; i < tn; i++)
                    {
                        int number = message.TableNumbers[i];
                        if (i >= message.TableIds.Count || !PlacedObjectIdentity.TryResolve(Sm(), 6,
                            message.TableIds[i], out var raw) || !(raw is InteractablePlayTable table))
                            continue;
                        if (table.GetTournamentPlayTableNumber() != number)
                            table.SetTournamentPlayTableNumber(number);
                    }
                    var stock = CPlayerData.m_DecorationInventoryList;
                    if (stock != null)
                    {
                        for (int i = 0; i < stock.Count; i++)
                            stock[i] = 0;
                        if (message.DecoStock != null)
                            for (int i = 0; i < message.DecoStock.Count; i++)
                            {
                                int local;
                                var e = message.DecoStock[i];
                                if (Util.EnumMap.TryFromWire(Util.EnumKind.DecoObject, e.DecoType, out local)
                                    && local >= 0 && local < stock.Count)
                                    stock[local] = Mathf.Max(0, e.Count);
                            }
                    }
                });
            }
            finally { ApplyingRemote = false; }
            if (decoBefore != DecoStateHash())
                RefreshOpenDecoUI();
        }

        private static int DecoStateHash()
        {
            unchecked
            {
                int h = 17;
                HashBools(ref h, CPlayerData.m_UnlockedDecoWallList);
                HashBools(ref h, CPlayerData.m_UnlockedDecoFloorList);
                HashBools(ref h, CPlayerData.m_UnlockedDecoCeilingList);
                h = h * 31 + CPlayerData.m_EquippedWallDecoIndex;
                h = h * 31 + CPlayerData.m_EquippedWallDecoIndexB;
                h = h * 31 + CPlayerData.m_EquippedFloorDecoIndex;
                h = h * 31 + CPlayerData.m_EquippedFloorDecoIndexB;
                h = h * 31 + CPlayerData.m_EquippedCeilingDecoIndex;
                h = h * 31 + CPlayerData.m_EquippedCeilingDecoIndexB;
                var stock = CPlayerData.m_DecorationInventoryList;
                h = h * 31 + (stock == null ? 0 : stock.Count);
                if (stock != null)
                    for (int i = 0; i < stock.Count; i++)
                        h = h * 31 + stock[i];
                return h;
            }
        }

        private static void RefreshOpenDecoUI()
        {
            try
            {
                var place = UnityEngine.Object.FindObjectOfType<PlaceDecoUIScreen>(true);
                if (place != null && place.IsScreenOpened() && FiPlacePage != null && MiPlacePage != null)
                    MiPlacePage.Invoke(place, new object[] { (int)FiPlacePage.GetValue(place) });

                var buy = UnityEngine.Object.FindObjectOfType<ShopBuyDecoUIScreen>(true);
                if (buy != null && buy.IsScreenOpened() && FiBuyPage != null && MiBuyPage != null)
                    MiBuyPage.Invoke(buy, new object[] { (int)FiBuyPage.GetValue(buy) });
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("decoration UI refresh: " + e.Message);
            }
        }

        // ---------------- shared apply ----------------

        private static void ApplyBoolList(List<bool> incoming, List<bool> local, Action<int, bool> setter)
        {
            int n = incoming == null ? 0 : incoming.Count;
            for (int i = 0; i < n; i++)
            {
                bool v = incoming[i];
                if (local != null && i < local.Count && local[i] != v)
                    setter(i, v);
            }
        }

        /// <summary>Set the six equipped indices and retexture through the game's own
        /// customization manager - only the ones that actually changed (material writes
        /// touch shared assets, no reason to churn them every snapshot).</summary>
        private static void ApplyEquips(int wall, int wallB, int floor, int floorB, int ceiling, int ceilingB)
        {
            var so = Inv()?.m_ObjectData_SO;
            if (so == null)
                return;
            if (wall != CPlayerData.m_EquippedWallDecoIndex && wall >= 0 && wall < so.m_WallDecoDataList.Count)
            {
                CPlayerData.m_EquippedWallDecoIndex = wall;
                try
                {
                    ShopCustomizationManager.ChangeWallMaterial(wall, isShopLotB: false);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            if (wallB != CPlayerData.m_EquippedWallDecoIndexB && wallB >= 0 && wallB < so.m_WallDecoDataList.Count)
            {
                CPlayerData.m_EquippedWallDecoIndexB = wallB;
                try
                {
                    ShopCustomizationManager.ChangeWallMaterial(wallB, isShopLotB: true);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            if (floor != CPlayerData.m_EquippedFloorDecoIndex && floor >= 0 && floor < so.m_FloorDecoDataList.Count)
            {
                CPlayerData.m_EquippedFloorDecoIndex = floor;
                try
                {
                    ShopCustomizationManager.ChangeFloorMaterial(floor, isShopLotB: false);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            if (floorB != CPlayerData.m_EquippedFloorDecoIndexB && floorB >= 0 && floorB < so.m_FloorDecoDataList.Count)
            {
                CPlayerData.m_EquippedFloorDecoIndexB = floorB;
                try
                {
                    ShopCustomizationManager.ChangeFloorMaterial(floorB, isShopLotB: true);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            if (ceiling != CPlayerData.m_EquippedCeilingDecoIndex && ceiling >= 0 && ceiling < so.m_CeilingDecoDataList.Count)
            {
                CPlayerData.m_EquippedCeilingDecoIndex = ceiling;
                try
                {
                    ShopCustomizationManager.ChangeCeilingMaterial(ceiling, isShopLotB: false);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            if (ceilingB != CPlayerData.m_EquippedCeilingDecoIndexB && ceilingB >= 0 && ceilingB < so.m_CeilingDecoDataList.Count)
            {
                CPlayerData.m_EquippedCeilingDecoIndexB = ceilingB;
                try
                {
                    ShopCustomizationManager.ChangeCeilingMaterial(ceilingB, isShopLotB: true);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
        }

        // ---------------- wire ----------------

        private static SettingsStateMessage BuildStateMessage()
        {
            var msg = new SettingsStateMessage();
            CopyBools(CPlayerData.m_UnlockedDecoWallList, msg.WallUnlocked);
            CopyBools(CPlayerData.m_UnlockedDecoFloorList, msg.FloorUnlocked);
            CopyBools(CPlayerData.m_UnlockedDecoCeilingList, msg.CeilingUnlocked);
            msg.EquippedWallIndex = CPlayerData.m_EquippedWallDecoIndex;
            msg.EquippedWallIndexB = CPlayerData.m_EquippedWallDecoIndexB;
            msg.EquippedFloorIndex = CPlayerData.m_EquippedFloorDecoIndex;
            msg.EquippedFloorIndexB = CPlayerData.m_EquippedFloorDecoIndexB;
            msg.EquippedCeilingIndex = CPlayerData.m_EquippedCeilingDecoIndex;
            msg.EquippedCeilingIndexB = CPlayerData.m_EquippedCeilingDecoIndexB;
            // EGameEventFormat is a vanilla-only id space (identical on every PC) and
            // stays raw; the two ECardExpansionTypes are NOT - EPL mints modded ids into
            // that enum - so they travel as HOST ids like every other modded id
            msg.GameEventFormat = (int)CPlayerData.m_GameEventFormat;
            msg.PendingGameEventFormat = (int)CPlayerData.m_PendingGameEventFormat;
            msg.GameEventExpansion = CPlayerData.m_GameEventExpansionType;
            msg.PendingGameEventExpansion = CPlayerData.m_PendingGameEventExpansionType;
            var fees = CPlayerData.m_SetGameEventPriceList;
            int fn = Mathf.Min(fees.Count, 255);
            for (int i = 0; i < fn; i++)
                msg.GameEventPrices.Add(fees[i]);
            var counters = Sm()?.m_CashierCounterList;
            int cn = counters == null ? 0 : Mathf.Min(counters.Count, 255);
            for (int i = 0; i < cn; i++)
            {
                // a destroyed slot reads as vanilla defaults (both enabled)
                byte flags = 3;
                if (counters[i] != null)
                    flags = (byte)((counters[i].CanCheckout() ? 1 : 0) | (counters[i].CanTradeCard() ? 2 : 0));
                msg.CashierFlags.Add(flags);
                msg.CashierIds.Add(PlacedObjectIdentity.AssignHost(counters[i]));
            }
            var tables = Sm()?.m_PlayTableList;
            int tn = tables == null ? 0 : Mathf.Min(tables.Count, 255);
            for (int i = 0; i < tn; i++)
            {
                int num = tables[i] != null ? tables[i].GetTournamentPlayTableNumber() : 0;
                msg.TableNumbers.Add((byte)Mathf.Clamp(num, 0, 255)); // numbers never exceed the table count
                msg.TableIds.Add(PlacedObjectIdentity.AssignHost(tables[i]));
            }
            var stock = CPlayerData.m_DecorationInventoryList;
            if (stock != null)
                for (int i = 0; i < stock.Count; i++)
                    if (stock[i] != 0)
                        msg.DecoStock.Add(new DecoStockEntry { DecoType = Util.EnumMap.ToWire(Util.EnumKind.DecoObject, i), Count = stock[i] });
            return msg;
        }

        private static bool HostBuyItemDeco(EDecoObject type)
        {
            var data = InventoryBase.GetItemDecoPurchaseData(type);
            var stock = CPlayerData.m_DecorationInventoryList;
            int id = (int)type;
            if (data == null || stock == null || id < 0 || id >= stock.Count
                || !(CoopCore.Instance?.TryReserveGuestSpend(data.price) ?? false))
                return false;
            CPlayerData.m_GameReportDataCollect.supplyCost -= data.price;
            CPlayerData.m_GameReportDataCollectPermanent.supplyCost -= data.price;
            PriceChangeManager.AddTransaction(-data.price, ETransactionType.BuyDecoration, 3, id);
            CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(data.price));
            CPlayerData.AddDecoItemToInventory(type, 1);
            Instance?.BroadcastState?.Invoke(BuildStateMessage());
            return true;
        }

        private static void HostPlaceItemDeco(EDecoObject type, Vector3 pos, Quaternion rot)
        {
            var stock = CPlayerData.m_DecorationInventoryList;
            int id = (int)type;
            if (stock == null || id < 0 || id >= stock.Count || stock[id] <= 0)
                return;
            if (InventoryBase.GetSpawnDecoObjectPrefab(type) == null)
                return;
            var obj = ShelfManager.SpawnDecoObject(type);
            if (obj == null)
                return;
            obj.Init();
            obj.transform.SetPositionAndRotation(pos, rot);
            CPlayerData.AddDecoItemToInventory(type, -1);
            PlacedObjectIdentity.AssignHost(obj);
            CoopPlugin.Log.LogInfo("partner placed item decoration: " + type);
            Instance?.BroadcastState?.Invoke(BuildStateMessage());
            CoopCore.Instance?.NotifyHostStructureChanged();
        }

        private static void HostRemoveItemDeco(int objectKey)
        {
            if ((objectKey >> 24) != 5)
                return;
            var sm = Sm();
            if (sm == null || !PlacedObjectIdentity.TryResolve(sm, 5,
                PlacedObjectIdentity.ObjectIdFromObjectKey(objectKey), out var obj))
                return;
            if (obj.m_DecoObjectType == EDecoObject.None)
                return;
            CPlayerData.AddDecoItemToInventory(obj.m_DecoObjectType, 1);
            CoopPlugin.Log.LogInfo("partner returned item decoration: " + obj.m_DecoObjectType);
            obj.OnDestroyed();
            Instance?.BroadcastState?.Invoke(BuildStateMessage());
            CoopCore.Instance?.NotifyHostStructureChanged();
        }

        private static int HashState()
        {
            int h = 17;
            HashBools(ref h, CPlayerData.m_UnlockedDecoWallList);
            HashBools(ref h, CPlayerData.m_UnlockedDecoFloorList);
            HashBools(ref h, CPlayerData.m_UnlockedDecoCeilingList);
            h = h * 31 + CPlayerData.m_EquippedWallDecoIndex;
            h = h * 31 + CPlayerData.m_EquippedWallDecoIndexB;
            h = h * 31 + CPlayerData.m_EquippedFloorDecoIndex;
            h = h * 31 + CPlayerData.m_EquippedFloorDecoIndexB;
            h = h * 31 + CPlayerData.m_EquippedCeilingDecoIndex;
            h = h * 31 + CPlayerData.m_EquippedCeilingDecoIndexB;
            h = h * 31 + (int)CPlayerData.m_GameEventFormat;
            h = h * 31 + (int)CPlayerData.m_PendingGameEventFormat;
            h = h * 31 + (int)CPlayerData.m_GameEventExpansionType;
            h = h * 31 + (int)CPlayerData.m_PendingGameEventExpansionType;
            var fees = CPlayerData.m_SetGameEventPriceList;
            int fn = fees == null ? 0 : Mathf.Min(fees.Count, 255);
            h = h * 31 + fn;
            for (int i = 0; i < fn; i++)
                h = h * 31 + fees[i].GetHashCode();
            var counters = Sm()?.m_CashierCounterList;
            int cn = counters == null ? 0 : Mathf.Min(counters.Count, 255);
            h = h * 31 + cn;
            for (int i = 0; i < cn; i++)
            {
                byte flags = 3;
                if (counters[i] != null)
                    flags = (byte)((counters[i].CanCheckout() ? 1 : 0) | (counters[i].CanTradeCard() ? 2 : 0));
                h = h * 31 + flags;
                h = h * 31 + PlacedObjectIdentity.AssignHost(counters[i]);
            }
            var tables = Sm()?.m_PlayTableList;
            int tn = tables == null ? 0 : Mathf.Min(tables.Count, 255);
            h = h * 31 + tn;
            for (int i = 0; i < tn; i++)
            {
                h = h * 31 + PlacedObjectIdentity.AssignHost(tables[i]);
                h = h * 31 + (tables[i] == null ? 0 : Mathf.Clamp(tables[i].GetTournamentPlayTableNumber(), 0, 255));
            }
            var stock = CPlayerData.m_DecorationInventoryList;
            h = h * 31 + (stock == null ? 0 : stock.Count);
            if (stock != null)
                for (int i = 0; i < stock.Count; i++)
                    h = h * 31 + stock[i];
            return h;
        }

        private static void HashBools(ref int hash, List<bool> list)
        {
            int n = list == null ? 0 : Mathf.Min(list.Count, 255);
            hash = hash * 31 + n;
            for (int i = 0; i < n; i++)
                hash = hash * 31 + (list[i] ? 1 : 0);
        }

        private static void CopyBools(List<bool> list, List<bool> into)
        {
            int n = list == null ? 0 : Mathf.Min(list.Count, 255);
            for (int i = 0; i < n; i++)
                into.Add(list[i]);
        }

        // ---------------- patches ----------------

        private static readonly System.Reflection.FieldInfo FiBuyCategory =
            AccessTools.Field(typeof(ShopBuyDecoUIScreen), "m_CategoryIndex");

        public static void ApplyPatches(Harmony h)
        {
            // Joiner deco purchase: block BEFORE the coin charge; the host charges once.
            Try(h, typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDeco",
                prefix: new HarmonyMethod(typeof(SettingsSync), nameof(BuyDecoPrefix)));
            Try(h, typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDecoItem",
                prefix: new HarmonyMethod(typeof(SettingsSync), nameof(BuyDecoItemPrefix)));

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
            Try(h, typeof(SetPlayerTableNumberScreen), "OnPressSetNextNumber",
                prefix: new HarmonyMethod(typeof(SettingsSync), nameof(AutoTableNumberPrefix)));
            Try(h, typeof(SetPlayerTableNumberScreen), "OnPressConfirm",
                prefix: new HarmonyMethod(typeof(SettingsSync), nameof(ConfirmTableNumberPrefix)));
            Try(h, typeof(SetPlayerTableNumberScreen), "OnPressClear",
                prefix: new HarmonyMethod(typeof(SettingsSync), nameof(ClearTableNumberPrefix)));
        }

        public static bool BuyDecoPrefix(ShopBuyDecoUIScreen __instance, int shopDecoIndex, float price)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            int cat = -1;
            try
            {
                cat = (int)FiBuyCategory.GetValue(__instance);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            if (cat < 0 || cat > 2)
                return true; // item-deco pages use the other handler
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
                inst.SendOp(new SettingsOpMessage { Op = OpBuyDeco, Category = (byte)c, Index = shopDecoIndex });
            }
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "deco purchase sent to the host";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false; // ownership echoes back in the next state broadcast
        }

        public static bool BuyDecoItemPrefix(EDecoObject itemType, float price)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            Instance?.SendOp?.Invoke(new SettingsOpMessage { Op = OpBuyItemDeco, DecoType = itemType });
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "deco purchase sent to the host";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false;
        }

        public static void EquipDecoPostfix()
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client)
                return;
            var inst = Instance;
            if (inst?.SendOp == null)
                return;
            inst.SendOp(new SettingsOpMessage
            {
                Op = OpEquipDeco,
                Wall = CPlayerData.m_EquippedWallDecoIndex,
                WallB = CPlayerData.m_EquippedWallDecoIndexB,
                Floor = CPlayerData.m_EquippedFloorDecoIndex,
                FloorB = CPlayerData.m_EquippedFloorDecoIndexB,
                Ceiling = CPlayerData.m_EquippedCeilingDecoIndex,
                CeilingB = CPlayerData.m_EquippedCeilingDecoIndexB,
            });
        }

        public static void GameEventPostfix()
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client)
                return;
            var inst = Instance;
            if (inst?.SendOp == null)
                return;
            inst.SendOp(new SettingsOpMessage
            {
                Op = OpGameEvent,
                Index = (int)CPlayerData.m_PendingGameEventFormat, // vanilla ids: raw
                // ...but the expansion is a modded id space: send the HOST's id for the
                // pack we picked, so the host schedules the event the joiner meant
                Expansion = CPlayerData.m_PendingGameEventExpansionType,
            });
        }

        public static void GameEventFeePostfix(EGameEventFormat gameEventFormat, float price)
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client)
                return;
            var inst = Instance;
            if (inst?.SendOp == null)
                return;
            inst.SendOp(new SettingsOpMessage
            {
                Op = OpGameEventFee,
                Index = (int)gameEventFormat,
                Fee = price,
            });
        }

        public static void CashierPostfix(InteractableCashierCounter __instance)
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client)
                return;
            var inst = Instance;
            if (inst?.SendOp == null)
                return;
            if (!PlacedObjectIdentity.TryMakeObjectKey(4, __instance, out int key))
                return;
            byte flags = (byte)((__instance.CanCheckout() ? 1 : 0) | (__instance.CanTradeCard() ? 2 : 0));
            inst.SendOp(new SettingsOpMessage { Op = OpCashier, ObjectKey = key, CashierFlags = flags });
        }

        public static void TableNumberPostfix(InteractablePlayTable __instance, int tableNumber)
        {
            if (ApplyingRemote || CoopCore.Role != CoopRole.Client)
                return;
            var inst = Instance;
            if (inst?.SendOp == null)
                return;
            var tables = Sm()?.m_PlayTableList;
            if (tables == null)
                return;
            int idx = tables.IndexOf(__instance);
            if (idx < 0 || idx > 254)
                return;
            if (PlacedObjectIdentity.TryMakeObjectKey(6, __instance, out int key))
                inst.SendOp(new SettingsOpMessage { Op = OpTableNumber, ObjectKey = key, TableNumber = tableNumber });
        }

        public static bool AutoTableNumberPrefix(SetPlayerTableNumberScreen __instance)
            => SendTableNumberAction(__instance, OpAutoTableNumber);

        public static bool ConfirmTableNumberPrefix(SetPlayerTableNumberScreen __instance)
            => SendTableNumberAction(__instance, OpTableNumber);

        public static bool ClearTableNumberPrefix(SetPlayerTableNumberScreen __instance)
            => SendTableNumberAction(__instance, OpClearTableNumber);

        private static bool SendTableNumberAction(SetPlayerTableNumberScreen screen, byte op)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            var table = CurrentNumberTable.GetValue(screen) as InteractablePlayTable;
            if (table != null && PlacedObjectIdentity.TryMakeObjectKey(6, table, out int key))
            {
                Instance?.SendOp?.Invoke(new SettingsOpMessage
                {
                    Op = op,
                    ObjectKey = key,
                    TableNumber = (int)RequestedTableNumber.GetValue(screen)
                });
                screen.CloseScreen();
            }
            else
                CoopCore.Instance?.ReportInteraction("This table is still syncing. Try again after refreshing guest objects.");
            return false;
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
