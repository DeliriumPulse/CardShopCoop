using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Shop-level state the host owns and the joiner must agree with: the three phone
    /// bills (rent / electric / employee), room + warehouse-room + shop-lot-B unlocks,
    /// and the two physical signs (OPEN/CLOSED, warehouse customer entry).
    ///
    /// Everything here is CPlayerData statics, so an unblocked joiner action would only
    /// mutate his mirrored copy - and for bills/unlocks it would ALSO charge the shared
    /// wallet (ReduceCoin is forwarded) for something the real simulation never gets.
    /// So every joiner handler is blocked BEFORE its coin charge and forwarded as an op;
    /// the host's vanilla path does the one and only charge, and the state broadcast is
    /// the echo that updates the joiner's phone/signs.
    /// </summary>
    public class ShopStateSync
    {
        // ShopOp sub-ops (first byte of the payload)
        private const byte OpPayBill = 1;    // + byte: 0=all, else (byte)EBillType
        private const byte OpUnlock = 2;     // + byte kind: 0=room, 1=warehouseRoom, 2=shopB
        private const byte OpToggleSign = 3; // + byte which: 0=open/close, 1=warehouse entry

        private static ShopStateSync _instance; // patches are static; ops route through here

        /// <summary>True while ClientApplyState drives game code, so our own patches
        /// never mistake a sync-applied change for a local click and re-forward it.</summary>
        public static bool ApplyingRemote;

        // private game methods we drive on the client to make the UI/meshes tell the truth
        private static readonly System.Reflection.MethodInfo MiBillEvaluateUI =
            AccessTools.Method(typeof(RentBillScreen), "EvaluateUI");
        private static readonly System.Reflection.MethodInfo MiBillNotification =
            AccessTools.Method(typeof(RentBillScreen), "EvaluateBillNotification");
        private static readonly System.Reflection.MethodInfo MiOpenSignMesh =
            AccessTools.Method(typeof(InteractableOpenCloseSign), "EvaluateSignOpenCloseMesh");
        private static readonly System.Reflection.MethodInfo MiWarehouseSignMesh =
            AccessTools.Method(typeof(InteractableWarehouseAllowEnterSign), "EvaluateSignOpenCloseMesh");

        public Action<Action<BinaryWriter>> SendOp;         // set by CoopCore: client -> host
        public Action<Action<BinaryWriter>> BroadcastState; // set by CoopCore: host -> clients

        private float _timer;
        private int _lastHash;
        private float _heal;
        private RentBillScreen _billScreen;                        // phone screen, often inactive
        private InteractableOpenCloseSign _openSign;               // world object by the door
        private InteractableWarehouseAllowEnterSign _warehouseSign;

        public ShopStateSync()
        {
            _instance = this;
        }

        public void Reset()
        {
            _timer = -1.9f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            _billScreen = null;
            _openSign = null;
            _warehouseSign = null;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = 15f; // next tick broadcasts even if the hash collides
        }

        // ---------------- cached lookups ----------------

        private RentBillScreen BillScreen()
        {
            // phone screens live disabled until opened - the plain overload misses them
            if (_billScreen == null)
                _billScreen = UnityEngine.Object.FindObjectOfType<RentBillScreen>(true);
            return _billScreen;
        }

        private InteractableOpenCloseSign OpenSign()
        {
            if (_openSign == null)
                _openSign = UnityEngine.Object.FindObjectOfType<InteractableOpenCloseSign>(true);
            return _openSign;
        }

        private InteractableWarehouseAllowEnterSign WarehouseSign()
        {
            if (_warehouseSign == null)
                _warehouseSign = UnityEngine.Object.FindObjectOfType<InteractableWarehouseAllowEnterSign>(true);
            return _warehouseSign;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // Bills: block the joiner's pay buttons before CEventPlayer_ReduceCoin fires
            // (that event is forwarded to the shared wallet - letting it through would
            // charge everyone for a payment the host never records).
            Try(h, typeof(RentBillScreen), "OnPressPayRentBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(PayRentPrefix)));
            Try(h, typeof(RentBillScreen), "OnPressPayElectricBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(PayElectricPrefix)));
            Try(h, typeof(RentBillScreen), "OnPressPaySalaryBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(PaySalaryPrefix)));
            Try(h, typeof(RentBillScreen), "OnPressPayAllBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(PayAllPrefix)));

            // Bill accrual is host math (host room counts, host worker salaries, host
            // light hours). The joiner gets one allowed OnDayStarted per host day, which
            // would run PhoneManager -> EvaluateNewDayBill with LOCAL numbers - including
            // auto-force-paying overdue bills straight past our pay-button blocks.
            Try(h, typeof(RentBillScreen), "EvaluateNewDayBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(BillAccrualPrefix)));

            // Expansions: EvaluateCartCheckout is the single charge+unlock point for both
            // shop rooms and warehouse rooms; OnPressUnlockShopB is the lot-B purchase.
            Try(h, typeof(ExpansionShopUIScreen), "EvaluateCartCheckout",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(RoomCheckoutPrefix)));
            Try(h, typeof(ExpansionShopUIScreen), "OnPressUnlockShopB",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(UnlockShopBPrefix)));

            // Signs: a joiner flip must run in the real simulation (customer entry is
            // gated on the HOST's CPlayerData booleans), so forward and let the echo
            // flip the local sign.
            Try(h, typeof(InteractableOpenCloseSign), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(OpenSignPrefix)));
            Try(h, typeof(InteractableWarehouseAllowEnterSign), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(WarehouseSignPrefix)));
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

        private static bool PayBillPrefix(byte billType, bool forcePay)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            // forcePay only comes from the (blocked) accrual auto-pay; never forward it
            if (!forcePay)
                _instance?.SendOp?.Invoke(bw => { bw.Write(OpPayBill); bw.Write(billType); });
            return false;
        }

        public static bool PayRentPrefix(bool forcePay) { return PayBillPrefix((byte)EBillType.Rent, forcePay); }
        public static bool PayElectricPrefix(bool forcePay) { return PayBillPrefix((byte)EBillType.Electric, forcePay); }
        public static bool PaySalaryPrefix(bool forcePay) { return PayBillPrefix((byte)EBillType.Employee, forcePay); }
        public static bool PayAllPrefix() { return PayBillPrefix(0, forcePay: false); }

        public static bool BillAccrualPrefix()
        {
            return CoopCore.Role != CoopRole.Client; // accrual is host truth, echoed back
        }

        public static bool RoomCheckoutPrefix(bool isShopB)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            byte kind = isShopB ? (byte)1 : (byte)0;
            _instance?.SendOp?.Invoke(bw => { bw.Write(OpUnlock); bw.Write(kind); });
            return false;
        }

        public static bool UnlockShopBPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            // the vanilla body screens level/owned/coins itself; re-checked host-side
            if (CPlayerData.m_IsWarehouseRoomUnlocked) return true; // let it show 'owned'
            _instance?.SendOp?.Invoke(bw => { bw.Write(OpUnlock); bw.Write((byte)2); });
            return false;
        }

        public static bool OpenSignPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            _instance?.SendOp?.Invoke(bw => { bw.Write(OpToggleSign); bw.Write((byte)0); });
            return false;
        }

        public static bool WarehouseSignPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            _instance?.SendOp?.Invoke(bw => { bw.Write(OpToggleSign); bw.Write((byte)1); });
            return false;
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || BroadcastState == null) return;
            _timer += dt;
            if (_timer < 1f) return;
            _timer -= 1f;
            try
            {
                // tiny fixed-size snapshot: hash-gate so the wire stays quiet while
                // nothing changes; the slow heal repairs any client that missed one
                int hash = 17;
                for (EBillType t = EBillType.Rent; t <= EBillType.Employee; t++)
                {
                    var bill = CPlayerData.GetBill(t);
                    hash = hash * 31 + bill.billDayPassed;
                    hash = hash * 31 + (int)(bill.amountToPay * 100f);
                }
                hash = hash * 31 + CPlayerData.m_UnlockRoomCount;
                hash = hash * 31 + CPlayerData.m_UnlockWarehouseRoomCount;
                hash = hash * 31 + ((CPlayerData.m_IsWarehouseRoomUnlocked ? 1 : 0)
                                  | (CPlayerData.m_IsShopOpen ? 2 : 0)
                                  | (CPlayerData.m_IsWarehouseDoorClosed ? 4 : 0));
                _heal += 1f;
                if (hash == _lastHash && _heal < 15f) return;
                _lastHash = hash;
                _heal = 0f;
                BroadcastState(WriteState);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ShopStateSync host: " + e.Message); }
        }

        private static void WriteState(BinaryWriter bw)
        {
            for (EBillType t = EBillType.Rent; t <= EBillType.Employee; t++)
            {
                var bill = CPlayerData.GetBill(t);
                bw.Write(bill.billDayPassed);
                bw.Write(bill.amountToPay);
            }
            bw.Write(CPlayerData.m_UnlockRoomCount);
            bw.Write(CPlayerData.m_UnlockWarehouseRoomCount);
            bw.Write(CPlayerData.m_IsWarehouseRoomUnlocked);
            bw.Write(CPlayerData.m_IsShopOpen);
            bw.Write(CPlayerData.m_IsWarehouseDoorClosed);
        }

        public void HostApplyOp(BinaryReader br)
        {
            if (CoopCore.Role != CoopRole.Host) return;
            byte op = br.ReadByte();
            byte arg = br.ReadByte();
            try
            {
                switch (op)
                {
                    case OpPayBill: HostPayBill(arg); break;
                    case OpUnlock: HostUnlock(arg); break;
                    case OpToggleSign: HostToggleSign(arg); break;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ShopStateSync op " + op + ": " + e.Message); }
            ForceResend(); // echo promptly even if the op was refused (re-aligns the joiner)
        }

        private void HostPayBill(byte billType)
        {
            var screen = BillScreen();
            if (screen == null)
            {
                CoopPlugin.Log.LogWarning("ShopStateSync: RentBillScreen not found; pay op dropped");
                return;
            }
            // the vanilla handlers hold all the rules: amount>0, coin check, transaction
            // log, report bookkeeping, SetBill. A double-clicked op no-ops on the re-run.
            switch (billType)
            {
                case 0: screen.OnPressPayAllBill(); break;
                case (byte)EBillType.Rent: screen.OnPressPayRentBill(); break;
                case (byte)EBillType.Electric: screen.OnPressPayElectricBill(); break;
                case (byte)EBillType.Employee: screen.OnPressPaySalaryBill(); break;
            }
        }

        private void HostUnlock(byte kind)
        {
            // Mirrors ExpansionShopUIScreen.EvaluateCartCheckout / OnPressUnlockShopB
            // verbatim, minus the screen-refresh calls: the vanilla methods need a live
            // screen instance with the right private tab selected, which the host may not
            // have open. Cost/eligibility are recomputed from HOST state so a stale
            // joiner UI can neither underpay nor double-buy.
            var urm = CSingleton<UnlockRoomManager>.Instance;
            if (urm == null) return;
            if (CSingleton<CGameManager>.Instance != null && CSingleton<CGameManager>.Instance.m_IsPrologue) return;

            if (kind == 2) // shop lot B
            {
                if (CPlayerData.m_IsWarehouseRoomUnlocked) return;
                float price = urm.m_ShopB_UnlockPrice;
                if (CPlayerData.m_ShopLevel + 1 < urm.m_ShopB_UnlockLevelRequired) return;
                if (CPlayerData.m_CoinAmountDouble < (double)price) return;
                PriceChangeManager.AddTransaction(0f - price, ETransactionType.ShopExpansion, 1, -1);
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(price));
                urm.SetUnlockWarehouseRoom(isUnlocked: true);
                AchievementManager.OnShopLotBUnlocked();
                CEventManager.QueueEvent(new CEventPlayer_AddShopExp(Mathf.Clamp(Mathf.RoundToInt(price / 100f), 5, 100)));
                CPlayerData.m_GameReportDataCollect.upgradeCost -= price;
                CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= price;
                SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
            }
            else if (kind == 1) // next warehouse room
            {
                if (!CPlayerData.m_IsWarehouseRoomUnlocked) return; // lot B must exist first
                int index = CPlayerData.m_UnlockWarehouseRoomCount;
                if (index >= urm.m_LockedWarehouseRoomBlockerList.Count) return;
                float cost = CPlayerData.GetUnlockWarehouseRoomCost(index);
                if (CPlayerData.m_CoinAmountDouble < (double)cost) return;
                PriceChangeManager.AddTransaction(0f - cost, ETransactionType.ShopExpansion, 0, index);
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(cost));
                urm.StartUnlockNextWarehouseRoom();
                CEventManager.QueueEvent(new CEventPlayer_AddShopExp(Mathf.Clamp(Mathf.RoundToInt(cost / 100f), 5, 100)));
                CPlayerData.m_GameReportDataCollect.upgradeCost -= cost;
                CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= cost;
                SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
            }
            else // next shop room
            {
                int index = CPlayerData.m_UnlockRoomCount;
                if (index >= urm.m_LockedRoomBlockerList.Count) return;
                float cost = CPlayerData.GetUnlockShopRoomCost(index);
                if (CPlayerData.m_CoinAmountDouble < (double)cost) return;
                PriceChangeManager.AddTransaction(0f - cost, ETransactionType.ShopExpansion, 1, index);
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(cost));
                urm.StartUnlockNextRoom();
                CEventManager.QueueEvent(new CEventPlayer_AddShopExp(Mathf.Clamp(Mathf.RoundToInt(cost / 100f), 5, 100)));
                CPlayerData.m_GameReportDataCollect.upgradeCost -= cost;
                CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= cost;
                SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
            }
            // vanilla defers this by a second from the screen; immediate is equivalent
            try { CSingleton<ShelfManager>.Instance.SaveInteractableObjectData(); } catch { }
        }

        private void HostToggleSign(byte which)
        {
            // the sign's own click handler: tutorial gate, flip animation, mesh swap,
            // and the m_IsSwapping debounce (a mid-swap op is dropped; the echo simply
            // re-asserts the unchanged state and the joiner's sign snaps back)
            if (which == 0)
            {
                var sign = OpenSign();
                if (sign != null) sign.OnMouseButtonUp();
            }
            else
            {
                var sign = WarehouseSign();
                if (sign != null) sign.OnMouseButtonUp();
            }
        }

        // ---------------- client ----------------

        public void ClientApplyState(BinaryReader br)
        {
            ApplyingRemote = true;
            try { ClientApplyInner(br); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ShopStateSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(BinaryReader br)
        {
            // bills: dumb data copy - the fields are public and GetBill creates the
            // record when missing, so the joiner's phone reads exactly the host's dues
            bool billsChanged = false;
            for (EBillType t = EBillType.Rent; t <= EBillType.Employee; t++)
            {
                int day = br.ReadInt32();
                float amount = br.ReadSingle();
                var bill = CPlayerData.GetBill(t);
                if (bill.billDayPassed != day || bill.amountToPay != amount)
                {
                    bill.billDayPassed = day;
                    bill.amountToPay = amount;
                    billsChanged = true;
                }
            }
            int wantRooms = br.ReadInt32();
            int wantWarehouseRooms = br.ReadInt32();
            bool wantShopB = br.ReadBoolean();
            bool wantShopOpen = br.ReadBoolean();
            bool wantWarehouseClosed = br.ReadBoolean();

            if (billsChanged && BillScreen() != null)
            {
                // repaint the totals if the screen happens to be open, and keep the
                // phone's red bill badge honest either way
                try { MiBillEvaluateUI?.Invoke(_billScreen, null); } catch { }
                try { MiBillNotification?.Invoke(_billScreen, null); } catch { }
            }

            // unlocks: the manager methods are pure world changes (blocker off, door
            // anim, count++) - every coin charge lives in the UI handlers we never call
            var urm = CSingleton<UnlockRoomManager>.Instance;
            if (urm != null)
            {
                if (wantShopB && !CPlayerData.m_IsWarehouseRoomUnlocked)
                    urm.SetUnlockWarehouseRoom(isUnlocked: true);
                for (int guard = 0; CPlayerData.m_UnlockRoomCount < wantRooms && guard < 64; guard++)
                    urm.StartUnlockNextRoom();
                for (int guard = 0; CPlayerData.m_UnlockWarehouseRoomCount < wantWarehouseRooms && guard < 64; guard++)
                    urm.StartUnlockNextWarehouseRoom();
            }

            // signs: set the booleans the (suppressed) local sim would have written and
            // re-evaluate the meshes so the physical sign matches what customers do
            if (CPlayerData.m_IsShopOpen != wantShopOpen)
            {
                CPlayerData.m_IsShopOpen = wantShopOpen;
                var sign = OpenSign();
                if (sign != null) { try { MiOpenSignMesh?.Invoke(sign, null); } catch { } }
            }
            if (CPlayerData.m_IsWarehouseDoorClosed != wantWarehouseClosed)
            {
                CPlayerData.m_IsWarehouseDoorClosed = wantWarehouseClosed;
                var sign = WarehouseSign();
                if (sign != null) { try { MiWarehouseSignMesh?.Invoke(sign, null); } catch { } }
                else if (urm != null) urm.EvaluateWarehouseRoomOpenClose(); // entry gate still must move
            }
        }
    }
}
