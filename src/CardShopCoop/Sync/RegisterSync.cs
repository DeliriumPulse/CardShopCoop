using CardShopCoop.Util;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The register, faithful to single-player.
    ///
    /// The HOST is authoritative on the items to be served: it owns the customer's real bag and
    /// broadcasts a RegisterCart digest (item types + prices, card data + prices) for every counter
    /// that has a customer. Identity is the COUNTER INDEX - the same index the whole mod and the
    /// shared save use - because the counter list is identical on every machine.
    ///
    /// The current MANNING PLAYER is authoritative on the register's live state: they run the
    /// register exactly like single-player (click the counter to man it, click the customer's items
    /// to scan them, click the cash/card to take payment, click the change buttons, press the finish
    /// key) against a locally reconstructed bag of REAL spawned items/cards wired to a carrier
    /// Customer through the game's own RegisterScanItem/RegisterScanCard. Every action is emitted as
    /// a RegisterOp and replayed on the host through vanilla public methods, so the host's real
    /// customer and economy progress and finalize with zero re-implementation.
    ///
    /// Exclusivity (symmetric, not defensive): one player mans a station at a time.
    ///  - A guest entering a counter claims it: the host stops any worker there, records the owner,
    ///    and the HOST player (and other guests) are blocked from that counter.
    ///  - The host player entering a counter broadcasts it as host-manned; guests are blocked.
    ///  - A worker can never man a guest-claimed counter (NPCStartManCounter is gated).
    ///
    /// No register hints or tooltips exist in here - the register is the exact vanilla screen.
    /// </summary>
    public class RegisterSync : TickableCoopModule
    {
        // ---- wire op codes ----
        public const byte OpEnter = 1;
        public const byte OpExit = 2;
        public const byte OpScanItem = 3;
        public const byte OpScanCard = 4;
        public const byte OpTakingPayment = 5;
        public const byte OpTookPayment = 6;
        public const byte OpGiveChange = 7;
        public const byte OpFinishCash = 8;
        public const byte OpFinishCard = 9;

        // ---- reflection: InteractableCashierCounter privates ----
        private static readonly FieldInfo FiIsUsingCard = ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_IsUsingCard");
        private static readonly FieldInfo FiPaidAmount = ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_CustomerPaidAmount");
        private static readonly FieldInfo FiTotalScanned = ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_TotalScannedItemCost");
        private static readonly FieldInfo FiCashScreen = ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_UICashCounterScreen");
        private static readonly FieldInfo FiChangeReady = ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_IsChangeReady");
        private static readonly FieldInfo FiStartGivingChange = ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_IsStartGivingChange");
        private static readonly FieldInfo FiCurrentMoneyChange = ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_CurrentMoneyChangeValue");
        private static readonly FieldInfo FiTooMuchChange = ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_TooMuchChangeGiven");
        // ---- reflection: Customer privates ----
        private static readonly FieldInfo FiScannedCount = ReflectionSurface.RequiredField(typeof(Customer), "m_ItemScannedCount");
        private static readonly FieldInfo FiCustTotal = ReflectionSurface.RequiredField(typeof(Customer), "m_TotalScannedItemCost");
        private static readonly FieldInfo FiQueueCounter = ReflectionSurface.RequiredField(typeof(Customer), "m_CurrentQueueCashierCounter");
        private static readonly MethodInfo MiEvaluateFinish = ReflectionSurface.RequiredMethod(typeof(Customer), "EvaluateFinishScanItem");
        // ---- reflection: InteractableCustomerCash privates ----
        private static readonly FieldInfo FiCashCustomer = ReflectionSurface.RequiredField(typeof(InteractableCustomerCash), "m_CurrentCustomer");

        /// <summary>Set by CoopCore: client -> host op (MsgType.RegisterOp).</summary>
        public System.Action<INetMessage> SendOp;
        /// <summary>Set by CoopCore: host -> clients observer state (MsgType.RegisterState).</summary>
        public System.Action<INetMessage> BroadcastState;
        /// <summary>Set by CoopCore: host -> clients cart digest (MsgType.RegisterCart).</summary>
        public System.Action<INetMessage> BroadcastCart;

        // harmony prefixes are static; CoopCore owns the single instance
        private static RegisterSync _live;
        public static bool AllowClientCustomerLifecycle;
        public static bool SuppressClientRegisterEvents;
        public static bool ApplyingAuthoritativePayment;

        public RegisterSync()
        {
            _live = this;
        }

        public override string Name => "register";

        public override void Start()
        {
            ActivateLive(this);
        }

        protected override void OnHostTick(in SyncFrame frame)
        {
            HostTick(frame.Dt, frame.InGame);
        }

        /// <summary>Disable Harmony callbacks before a session's module state is torn down.</summary>
        public static void ClearLive()
        {
            _live = null;
            AllowClientCustomerLifecycle = false;
            SuppressClientRegisterEvents = false;
            ApplyingAuthoritativePayment = false;
        }

        public static void ActivateLive(RegisterSync instance)
        {
            _live = instance;
        }

        private ShelfManager _sm;
        private ShelfManager Sm()
        {
            if (_sm == null)
                _sm = Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        // ---------------- host ----------------
        private float _stateTimer;
        private readonly Dictionary<int, int> _guestManned = new Dictionary<int, int>(); // counter idx -> conn id
        private readonly Dictionary<int, int> _cartCustomer = new Dictionary<int, int>(); // counter idx -> customer instance
        // Register state is latency-sensitive, but it does not need a full 250-counter
        // reflection/string scan every render frame.  The hash is only a change detector;
        // the authoritative cart is still serialized from live game state below.
        private readonly Dictionary<int, int> _cartSignature = new Dictionary<int, int>();
        private float _cartPollTimer;
        private const float CartPollInterval = 0.12f;

        // ---------------- client ----------------
        private readonly Dictionary<int, Customer> _carrier = new Dictionary<int, Customer>();
        private readonly Dictionary<Item, int> _itemBag = new Dictionary<Item, int>();      // spawned item -> bag index
        private readonly Dictionary<Item, int> _itemCounter = new Dictionary<Item, int>();  // spawned item -> counter idx
        private readonly Dictionary<InteractableCard3d, int> _cardBag = new Dictionary<InteractableCard3d, int>();
        private readonly Dictionary<InteractableCard3d, int> _cardCounter = new Dictionary<InteractableCard3d, int>();
        private int _localManned = -1;                            // counter the local player is manning (or -1)
        private readonly Dictionary<int, byte> _mannedBy = new Dictionary<int, byte>();     // counter idx -> 0/1/2
        private readonly Dictionary<int, int> _cartGen = new Dictionary<int, int>();        // counter idx -> applied customer token
        private readonly Dictionary<int, string> _cartScanSignature = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _sourceIndex = new Dictionary<int, int>();    // counter idx -> served customer list index
        private readonly Dictionary<int, double> _authoritativeTotal = new Dictionary<int, double>();

        public override void Reset()
        {
            _stateTimer = 0f;
            _cartPollTimer = 0f;
            _guestManned.Clear();
            _cartCustomer.Clear();
            _cartSignature.Clear();
            _localManned = -1;
            _mannedBy.Clear();
            _cartGen.Clear();
            _cartScanSignature.Clear();
            _sourceIndex.Clear();
            _authoritativeTotal.Clear();
            AllowClientCustomerLifecycle = false;
            SuppressClientRegisterEvents = false;
            ApplyingAuthoritativePayment = false;
            TeardownCarriers();
            _sm = null;
        }

        public override void ForceResend()
        {
            _cartCustomer.Clear(); // force fresh RegisterCart digests on the next host tick
            _cartSignature.Clear();
            _cartPollTimer = CartPollInterval;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(_live, this))
                ClearLive();
        }

        /// <summary>Host: a client disconnected - release whatever it was manning.</summary>
        public void HostReleaseConn(int connId)
        {
            var drop = new List<int>();
            foreach (var kv in _guestManned)
                if (kv.Value == connId)
                    drop.Add(kv.Key);
            foreach (int idx in drop)
                _guestManned.Remove(idx);
            if (drop.Count > 0)
                BroadcastStateNow();
        }

        /// <summary>Host: is this counter claimed by a guest? (worker + host-serve gate)</summary>
        public static bool IsGuestManned(InteractableCashierCounter counter)
        {
            var t = _live;
            if (t == null || counter == null)
                return false;
            var sm = t.Sm();
            if (sm == null)
                return false;
            int idx = sm.m_CashierCounterList.IndexOf(counter);
            return idx >= 0 && t._guestManned.ContainsKey(idx);
        }

        /// <summary>Client: the local player was manning a register and must be moved off
        /// it NOW - the day ended (or the recap opened beneath them) after the host
        /// resolved this counter's customer, so there is nothing left to serve and the
        /// guest was being left frozen manning an empty station. Runs the counter's vanilla
        /// OnPressEsc so the IPC flags, movement stop, NavMesh cut and m_IsMannedByPlayer
        /// are all cleared, and releases the co-op claim via the same OpExit the normal Esc
        /// path sends. No-op when nobody is manning.</summary>
        public static void ForceExitManned()
        {
            var t = _live;
            if (t == null)
                return;
            if (CoopCore.Role != CoopRole.Client || t._localManned < 0)
                return;
            int idx = t._localManned;
            var sm = t.Sm();
            if (sm == null || idx >= sm.m_CashierCounterList.Count)
                return;
            var counter = sm.m_CashierCounterList[idx];
            if (counter == null)
                return;
            try
            {
                // ManningExitPostfix (postfix on OnPressEsc) releases _localManned and
                // forwards OpExit to the host so it stops counting this counter as the
                // guest's.
                counter.OnPressEsc();
            }
            catch (System.Exception e) { CoopPlugin.Log.LogWarning($"RegisterSync force exit {idx}: {e.Message}"); }
        }

        /// <summary>Client: is this customer currently the register carrier (live at a counter)?</summary>
        public static bool IsCarrier(Customer c)
        {
            var t = _live;
            if (t == null || c == null)
                return false;
            foreach (var kv in t._carrier)
                if (ReferenceEquals(kv.Value, c))
                    return true;
            return false;
        }

        // ---------------- patches ----------------
        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(InteractableCashierCounter), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(RegisterSync), nameof(ManningBlockPrefix)),
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(ManningEnterPostfix)));
            Try(h, typeof(InteractableCashierCounter), "OnPressEsc",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(ManningExitPostfix)));
            Try(h, typeof(InteractableCashierCounter), "NPCStartManCounter",
                prefix: new HarmonyMethod(typeof(RegisterSync), nameof(WorkerGatePrefix)));
            Try(h, typeof(InteractableCashierCounter), "OnPressSpaceBar",
                prefix: new HarmonyMethod(typeof(RegisterSync), nameof(FinishPrefix)),
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(FinishPostfix)));
            Try(h, typeof(InteractableCashierCounter), "UpdateCashierCounterState",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(StateChangePostfix)));
            Try(h, typeof(InteractableScanItem), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(ScanItemPostfix)));
            Try(h, typeof(InteractableCard3d), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(ScanCardPostfix)));
            Try(h, typeof(InteractableCustomerCash), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(TakePaymentPostfix)));
            Try(h, typeof(Customer), "EvaluateFinishScanItem",
                prefix: new HarmonyMethod(typeof(RegisterSync), nameof(EvaluateFinishPrefix)));
            Try(h, typeof(InteractableCashierCounter), "EvaluateCreditCard",
                prefix: new HarmonyMethod(typeof(RegisterSync), nameof(EvaluateCreditCardPrefix)));
            Try(h, typeof(InteractableCounterMoneyChange), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(GiveChangeAddPostfix)));
            Try(h, typeof(InteractableCounterMoneyChange), "OnRightMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(GiveChangeRemovePostfix)));
        }

        private static void Try(Harmony h, System.Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = ReflectionSurface.RequiredMethod(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"RegisterSync: patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning($"RegisterSync: patch failed for {type.Name}.{method}: {e.Message}");
            }
        }

        /// <summary>Entering a station someone else already mans is a no-op (vanilla silence, no hint).</summary>
        public static bool ManningBlockPrefix(InteractableCashierCounter __instance)
        {
            var t = _live;
            if (t == null || __instance == null)
                return true;
            if (CoopCore.Role == CoopRole.Host)
                return !IsGuestManned(__instance);
            if (CoopCore.Role != CoopRole.Client)
                return true;

            var sm = t.Sm();
            if (sm == null)
                return true;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0)
                return true;
            byte who = t._mannedBy.TryGetValue(idx, out byte w) ? w : (byte)0;
            if (who == 1)
                return false;                       // the host player mans it
            if (who == 2 && t._localManned != idx)
                return false; // another guest mans it
            return true;
        }

        /// <summary>Client: entering the counter - claim it with the host. Guarded on
        /// IsMannedByPlayer() because a Harmony postfix runs even when the block prefix returned
        /// false (the vanilla body was skipped, so the player never actually entered).</summary>
        public static void ManningEnterPostfix(InteractableCashierCounter __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            if (!__instance.IsMannedByPlayer())
                return; // the block prefix stopped the vanilla entry
            var sm = t.Sm();
            if (sm == null)
                return;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0)
                return;
            t._localManned = idx;
            t.SendOp?.Invoke(new RegisterOpMessage { Index = (byte)idx, Op = OpEnter });
            CoopPlugin.Log.LogDebug($"RegisterSync client: manned counter {idx}");
        }

        /// <summary>Client: leaving the counter (Esc / movement-away) - release the claim.</summary>
        public static void ManningExitPostfix(InteractableCashierCounter __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            var sm = t.Sm();
            if (sm == null)
                return;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0)
                return;
            t._localManned = -1;
            t.SendOp?.Invoke(new RegisterOpMessage { Index = (byte)idx, Op = OpExit });
            CoopPlugin.Log.LogDebug($"RegisterSync client: left counter {idx}");
        }

        /// <summary>Host: a worker must never serve a guest-claimed station.</summary>
        public static bool WorkerGatePrefix(InteractableCashierCounter __instance)
        {
            if (CoopCore.Role != CoopRole.Host)
                return true;
            return !IsGuestManned(__instance);
        }

        /// <summary>Client: finishing a sale - the vanilla body cannot run (economy/AI hit the
        /// deactivated carrier and the economy belongs to the host). Forward the finish; reset locally.</summary>
        public static bool FinishPrefix(InteractableCashierCounter __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null)
                return true;
            var sm = t.Sm();
            if (sm == null)
                return true;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0 || !t._carrier.ContainsKey(idx))
                return true;

            bool isCard = FiIsUsingCard?.GetValue(__instance) is bool c && c;
            if (!(FiChangeReady?.GetValue(__instance) is bool ready) || !ready)
                return true;
            double total = FiTotalScanned?.GetValue(__instance) is double d ? d : 0.0;
            CoopPlugin.Log.LogDebug($"RegisterSync client: finish counter {idx} card={isCard}");
            t.SendOp?.Invoke(new RegisterOpMessage
            {
                Index = (byte)idx,
                Op = isCard ? OpFinishCard : OpFinishCash,
                TotalAmount = total,
            });
            SuppressClientRegisterEvents = true;
            return true;
        }

        public static void FinishPostfix()
        {
            SuppressClientRegisterEvents = false;
        }

        public static bool EvaluateFinishPrefix(Customer __instance)
        {
            if (CoopCore.Role != CoopRole.Client || __instance == null)
                return true;
            var t = _live;
            return t == null || !t._carrier.ContainsValue(__instance);
        }

        /// <summary>Client: validate a manually entered card payment against the host's
        /// authoritative scanned total, rather than the client's locally accumulated card
        /// total. The vanilla card screen calls EvaluateCreditCard directly.</summary>
        public static void EvaluateCreditCardPrefix(InteractableCashierCounter __instance, ref double value)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            var sm = t.Sm();
            if (sm == null)
                return;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0 || !t._carrier.ContainsKey(idx))
                return;
            if (!t._authoritativeTotal.TryGetValue(idx, out double total))
                return;

            // Preserve the amount entered by the player; only replace the expected
            // checkout total used by vanilla's validation.
            FiTotalScanned?.SetValue(__instance, total);
        }

        /// <summary>Client: the counter just entered TakingCash - forward the payment roll the game made.</summary>
        public static void StateChangePostfix(InteractableCashierCounter __instance, ECashierCounterState state)
        {
            if (ApplyingAuthoritativePayment)
                return;
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            if (state != ECashierCounterState.TakingCash)
                return;
            var sm = t.Sm();
            if (sm == null)
                return;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0 || !t._carrier.ContainsKey(idx))
                return;

            bool isCard = FiIsUsingCard?.GetValue(__instance) is bool c && c;
            double paid = FiPaidAmount?.GetValue(__instance) is double p ? p : 0.0;
            CoopPlugin.Log.LogDebug($"RegisterSync client: taking payment counter {idx} card={isCard} paid={paid}");
            t.SendOp?.Invoke(new RegisterOpMessage
            {
                Index = (byte)idx,
                Op = OpTakingPayment,
                IsCard = isCard,
                PaidAmount = paid,
            });
        }

        /// <summary>Client: a scan item was clicked - forward which bag slot it was.</summary>
        public static void ScanItemPostfix(InteractableScanItem __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null || __instance.m_Item == null)
                return;
            if (!t._itemBag.TryGetValue(__instance.m_Item, out int k))
                return;
            if (!t._itemCounter.TryGetValue(__instance.m_Item, out int idx))
                return;
            t.SendOp?.Invoke(new RegisterOpMessage { Index = (byte)idx, Op = OpScanItem, BagIndex = (byte)k });
            CoopPlugin.Log.LogDebug($"RegisterSync client: scan item {k} @ {idx}");
        }

        /// <summary>Client: a scan card was clicked - forward which bag slot it was.</summary>
        public static void ScanCardPostfix(InteractableCard3d __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            if (!t._cardBag.TryGetValue(__instance, out int k))
                return;
            if (!t._cardCounter.TryGetValue(__instance, out int idx))
                return;
            t.SendOp?.Invoke(new RegisterOpMessage { Index = (byte)idx, Op = OpScanCard, BagIndex = (byte)k });
            CoopPlugin.Log.LogDebug($"RegisterSync client: scan card {k} @ {idx}");
        }

        /// <summary>Client: the cash/card was clicked - forward the payment shot.</summary>
        public static void TakePaymentPostfix(InteractableCustomerCash __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            var cust = FiCashCustomer?.GetValue(__instance) as Customer;
            if (cust == null || !t._carrier.ContainsValue(cust))
                return;
            var counter = FiQueueCounter?.GetValue(cust) as InteractableCashierCounter;
            var sm = t.Sm();
            if (counter == null || sm == null)
                return;
            int idx = sm.m_CashierCounterList.IndexOf(counter);
            if (idx < 0)
                return;
            bool isCard = __instance.m_IsCard;
            t.SendOp?.Invoke(new RegisterOpMessage { Index = (byte)idx, Op = OpTookPayment, IsCard = isCard });
            CoopPlugin.Log.LogDebug($"RegisterSync client: took payment @ {idx} card={isCard}");
        }

        private static void GiveChangeAddPostfix(InteractableCounterMoneyChange __instance) => EmitChange(__instance, false);
        private static void GiveChangeRemovePostfix(InteractableCounterMoneyChange __instance) => EmitChange(__instance, true);

        private static void EmitChange(InteractableCounterMoneyChange __instance, bool takingBack)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null || __instance.m_CashierCounter == null)
                return;
            var sm = t.Sm();
            if (sm == null)
                return;
            int idx = sm.m_CashierCounterList.IndexOf(__instance.m_CashierCounter);
            if (idx < 0 || !t._carrier.ContainsKey(idx))
                return;
            double value = __instance.m_ValueDouble;
            t.SendOp?.Invoke(new RegisterOpMessage
            {
                Index = (byte)idx,
                Op = OpGiveChange,
                ChangeIndex = __instance.m_Index,
                ChangeValue = value,
                TakingBack = takingBack,
            });
        }

        // ---------------- host tick ----------------
        public void HostTick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            _stateTimer += dt;
            _cartPollTimer += dt;
            var sm = Sm();
            if (sm == null || sm.m_CashierCounterList == null)
                return;

            bool cartChanged = false;
            if (_cartPollTimer >= CartPollInterval)
            {
                _cartPollTimer -= CartPollInterval;
                if (_cartPollTimer > CartPollInterval)
                    _cartPollTimer = CartPollInterval;
                for (int i = 0; i < sm.m_CashierCounterList.Count && i < 250; i++)
                {
                    var counter = sm.m_CashierCounterList[i];
                    if (counter == null)
                        continue;
                    int custId = counter.m_CurrentCustomer != null && counter.m_CurrentCustomer.m_IsActive
                        ? counter.m_CurrentCustomer.GetInstanceID() : 0;
                    int signature = CartSignature(counter, counter.m_CurrentCustomer, custId);
                    if (_cartCustomer.TryGetValue(i, out int prev) && prev == custId
                        && _cartSignature.TryGetValue(i, out var oldSignature) && oldSignature == signature)
                        continue;
                    _cartCustomer[i] = custId;
                    _cartSignature[i] = signature;
                    cartChanged = true;
                }
            }
            if (cartChanged)
            {
                var cartMsg = WriteCarts();
                if (cartMsg != null)
                    BroadcastCart?.Invoke(cartMsg);
            }

            if (_stateTimer >= 0.5f)
            {
                _stateTimer -= 0.5f;
                var stateMsg = WriteStates();
                if (stateMsg != null)
                    BroadcastState?.Invoke(stateMsg);
            }
        }

        private RegisterCartMessage WriteCarts()
        {
            var sm = Sm();
            var message = new RegisterCartMessage();
            for (int i = 0; i < sm.m_CashierCounterList.Count && i < 250; i++)
            {
                var counter = sm.m_CashierCounterList[i];
                if (counter == null)
                    continue;
                var cust = counter.m_CurrentCustomer;
                bool active = cust != null && cust.m_IsActive;

                var entry = new RegisterCartEntry
                {
                    Index = (byte)i,
                    CustomerId = active ? cust.GetInstanceID() : 0, // opaque change token (0 = customer left)
                };
                if (!active)
                {
                    message.Entries.Add(entry);
                    continue;
                }
                entry.CustomerIndex = (ushort)CustomerListIndex(cust); // for the client's carrier pick + NpcSync suppression
                entry.CustomerGeneration = NpcSync.GetCustomerGeneration(cust);
                entry.CharacterName = cust.m_CharacterCustom != null ? cust.m_CharacterCustom.CharacterName : "";
                entry.State = (byte)counter.m_CashierCounterState;
                entry.IsCard = FiIsUsingCard?.GetValue(counter) is bool card && card;
                entry.PaidAmount = FiPaidAmount?.GetValue(counter) is double paid ? paid : 0.0;
                entry.TotalScanned = FiTotalScanned?.GetValue(counter) is double total ? total : 0.0;
                entry.CustomerTotalScanned = FiCustTotal?.GetValue(cust) is float customerTotal
                    ? customerTotal : (float)entry.TotalScanned;
                var items = cust.GetItemInBagList();
                for (int k = 0; k < items.Count; k++)
                {
                    entry.ItemTypes.Add(items[k].GetItemType());
                    entry.ItemPrices.Add(EffectiveItemPrice(items[k]));
                }
                for (int k = 0; k < items.Count; k++)
                    entry.ItemScanned.Add(items[k].m_InteractableScanItem != null && !items[k].m_InteractableScanItem.IsNotScanned());
                var cards = cust.GetCardInBagList();
                for (int k = 0; k < cards.Count; k++)
                {
                    entry.Cards.Add(cards[k].m_Card3dUI.m_CardUI.GetCardData());
                    entry.CardPrices.Add(EffectiveCardPrice(cards[k]));
                }
                for (int k = 0; k < cards.Count; k++)
                    entry.CardScanned.Add(!cards[k].IsNotScanned());
                message.Entries.Add(entry);
            }
            return message.Entries.Count > 0 ? message : null;
        }

        private static int CustomerListIndex(Customer cust)
        {
            var cm = Object.FindObjectOfType<CustomerManager>();
            if (cm == null)
                return 0;
            var list = cm.GetCustomerList();
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], cust))
                    return i;
            return 0;
        }

        private static float EffectiveItemPrice(Item item)
        {
            if (item == null)
                return 0f;
            float price = item.GetCurrentPrice();
            return price > 0f ? price : CPlayerData.GetItemMarketPrice(item.GetItemType());
        }

        private static float EffectiveCardPrice(InteractableCard3d card)
        {
            if (card == null || card.m_Card3dUI == null || card.m_Card3dUI.m_CardUI == null)
                return 0f;
            float price = card.GetCurrentPrice();
            return price > 0f ? price : CPlayerData.GetCardMarketPrice(card.m_Card3dUI.m_CardUI.GetCardData());
        }

        private static int CartSignature(InteractableCashierCounter counter, Customer cust, int custId)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + custId;
                h = h * 31 + (int)counter.m_CashierCounterState;
                h = h * 31 + ((FiIsUsingCard?.GetValue(counter) is bool card && card) ? 1 : 0);
                h = h * 31 + (FiPaidAmount?.GetValue(counter)?.GetHashCode() ?? 0);
                h = h * 31 + (FiTotalScanned?.GetValue(counter)?.GetHashCode() ?? 0);
                if (cust == null)
                    return h;
                var items = cust.GetItemInBagList();
                h = h * 31 + items.Count;
                for (int i = 0; i < items.Count; i++)
                {
                    h = h * 31 + (items[i] == null ? 0 : (int)items[i].GetItemType());
                    h = h * 31 + (items[i] == null ? 0 : EffectiveItemPrice(items[i]).GetHashCode());
                    h = h * 31 + (items[i] != null && items[i].m_InteractableScanItem != null
                        && !items[i].m_InteractableScanItem.IsNotScanned() ? 1 : 0);
                }
                var cards = cust.GetCardInBagList();
                h = h * 31 + cards.Count;
                for (int i = 0; i < cards.Count; i++)
                {
                    var data = cards[i] != null && cards[i].m_Card3dUI != null && cards[i].m_Card3dUI.m_CardUI != null
                        ? cards[i].m_Card3dUI.m_CardUI.GetCardData() : null;
                    if (data != null)
                    {
                        h = h * 31 + (int)data.expansionType;
                        h = h * 31 + (int)data.monsterType;
                        h = h * 31 + (int)data.borderType;
                        h = h * 31 + (data.isFoil ? 1 : 0);
                        h = h * 31 + (data.isDestiny ? 1 : 0);
                        h = h * 31 + (data.isChampionCard ? 1 : 0);
                        h = h * 31 + data.cardGrade;
                        h = h * 31 + data.gradedCardIndex;
                    }
                    h = h * 31 + (cards[i] == null ? 0 : EffectiveCardPrice(cards[i]).GetHashCode());
                    h = h * 31 + (cards[i] != null && !cards[i].IsNotScanned() ? 1 : 0);
                }
                return h;
            }
        }

        private RegisterStateMessage WriteStates()
        {
            var sm = Sm();
            var message = new RegisterStateMessage();
            for (int i = 0; i < sm.m_CashierCounterList.Count && i < 250; i++)
            {
                var counter = sm.m_CashierCounterList[i];
                if (counter == null)
                    continue;
                byte manned = counter.IsMannedByPlayer() ? (byte)1 : (_guestManned.ContainsKey(i) ? (byte)2 : (byte)0);
                int owner = manned == 2 && _guestManned.TryGetValue(i, out int guestOwner)
                    ? guestOwner : 0;
                message.Entries.Add(new RegisterStateEntry
                {
                    Index = (byte)i,
                    Manned = manned,
                    OwnerConnId = owner
                });
            }
            return message.Entries.Count > 0 ? message : null;
        }

        private void BroadcastStateNow()
        {
            var state = WriteStates();
            if (state != null)
                BroadcastState?.Invoke(state);
        }

        // ---------------- host op application ----------------
        public void HostApplyOp(RegisterOpMessage message, int connId)
        {
            try
            {
                HostApplyOpInner(message, connId);
            }
            catch (System.Exception e) { CoopPlugin.Log.LogError($"RegisterSync: op apply failed connId={connId}: {e}"); }
        }

        private void HostApplyOpInner(RegisterOpMessage message, int connId)
        {
            int idx = message.Index;
            byte op = message.Op;
            var sm = Sm();
            if (sm == null || idx >= sm.m_CashierCounterList.Count)
                return;
            var counter = sm.m_CashierCounterList[idx];
            if (counter == null)
                return;

            if (op == OpEnter)
            {
                if (counter.IsMannedByPlayer())
                {
                    BroadcastStateNow();
                    return;                       // host player already there
                }
                if (_guestManned.TryGetValue(idx, out int owner) && owner != connId)
                {
                    BroadcastStateNow();
                    return; // another guest owns it
                }
                _guestManned[idx] = connId;
                BroadcastStateNow();
                try
                {
                    counter.StopCurrentWorker();
                }
                catch (System.Exception e) { Swallow.Log(e); }
                _cartSignature.Remove(idx);
                var cartMsg = WriteCarts();
                if (cartMsg != null)
                    BroadcastCart?.Invoke(cartMsg);
                CoopPlugin.Log.LogDebug($"RegisterSync host: guest {connId} manned counter {idx}");
                return;
            }
            if (op == OpExit)
            {
                if (_guestManned.TryGetValue(idx, out int owner) && owner == connId)
                    _guestManned.Remove(idx);
                BroadcastStateNow();
                CoopPlugin.Log.LogDebug($"RegisterSync host: guest {connId} left counter {idx}");
                return;
            }
            // serving actions are only accepted from the owner
            if (!_guestManned.TryGetValue(idx, out int who) || who != connId)
                return;
            var cust = counter.m_CurrentCustomer;
            if (cust == null || !cust.m_IsActive)
                return;

            switch (op)
            {
                case OpScanItem:
                    {
                        int k = message.BagIndex;
                        var items = cust.GetItemInBagList();
                        if (k < 0 || k >= items.Count)
                            return;
                        var item = items[k];
                        if (item == null || item.m_InteractableScanItem == null || !item.m_InteractableScanItem.IsNotScanned())
                            return;
                        item.m_InteractableScanItem.OnMouseButtonUp();
                        break;
                    }
                case OpScanCard:
                    {
                        int k = message.BagIndex;
                        var cards = cust.GetCardInBagList();
                        if (k < 0 || k >= cards.Count)
                            return;
                        var card = cards[k];
                        if (card == null || !card.IsNotScanned())
                            return;
                        card.OnMouseButtonUp();
                        break;
                    }
                case OpTakingPayment:
                    {
                        // The final host-side scan already ran Customer.EvaluateFinishScanItem,
                        // which selected payment, presented the real cash/card, and entered the
                        // vanilla TakingCash state. This notification is only a client-side
                        // convergence marker; never replace the customer flow with a manual state
                        // write here. (message.IsCard / message.PaidAmount are carried but the
                        // host's flow is authoritative.)
                        break;
                    }
                case OpTookPayment:
                    {
                        // host's customer/payment object is authoritative (message.IsCard carried but unused)
                        cust.m_CustomerCash.OnMouseButtonUp();
                        break;
                    }
                case OpGiveChange:
                    {
                        int buttonIndex = message.ChangeIndex;
                        double value = message.ChangeValue;
                        bool takingBack = message.TakingBack;
                        var money = counter.m_InteractableCounterMoneyChangeList;
                        if (buttonIndex < 0 || buttonIndex >= money.Count)
                            return;
                        var button = money[buttonIndex];
                        if (takingBack)
                            button.OnRightMouseButtonUp();
                        else
                            button.OnMouseButtonUp();
                        break;
                    }
                case OpFinishCash:
                    {
                        counter.OnPressSpaceBar();
                        CoopPlugin.Log.LogDebug($"RegisterSync host: completed a cash sale at counter {idx}");
                        break;
                    }
                case OpFinishCard:
                    {
                        double total = FiTotalScanned?.GetValue(counter) is double hostTotal ? hostTotal : 0.0;
                        counter.EvaluateCreditCard(total);
                        CoopPlugin.Log.LogDebug($"RegisterSync host: completed a card sale at counter {idx}");
                        break;
                    }
            }
        }

        // ---------------- client ----------------
        private struct Cart
        {
            public byte Index;
            public ushort CustomerIndex;
            public int CustomerGeneration;
            public string CharacterName;
            public byte State;
            public bool IsCard;
            public double PaidAmount;
            public double TotalScanned;
            public float CustomerTotalScanned;
            public List<EItemType> ItemTypes;
            public List<float> ItemPrices;
            public List<CardData> Cards;
            public List<float> CardPrices;
            public List<bool> ItemScanned;
            public List<bool> CardScanned;
            public string ScanSignature;
        }

        /// <summary>Client: the host's authoritative cart for a counter - rebuild a real scannable bag.</summary>
        public void ClientApplyCart(RegisterCartMessage message)
        {
            var entries = message.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                int idx = entry.Index;
                int cid = entry.CustomerId;
                if (cid == 0)
                {
                    // customer left this counter - settle it (only if we were tracking it)
                    if (_cartGen.Remove(idx))
                        ClientSettle(idx);
                    continue;
                }
                var c = new Cart
                {
                    Index = entry.Index,
                    CustomerIndex = entry.CustomerIndex,
                    CustomerGeneration = entry.CustomerGeneration,
                    CharacterName = entry.CharacterName,
                    State = entry.State,
                    IsCard = entry.IsCard,
                    PaidAmount = entry.PaidAmount,
                    TotalScanned = entry.TotalScanned,
                    CustomerTotalScanned = entry.CustomerTotalScanned,
                    ItemTypes = entry.ItemTypes,
                    ItemPrices = entry.ItemPrices,
                    Cards = entry.Cards,
                    CardPrices = entry.CardPrices,
                    ItemScanned = entry.ItemScanned,
                    CardScanned = entry.CardScanned,
                };
                c.ScanSignature = BuildCartSignature(c);
                ApplyCart(c, cid);
            }
        }

        private void ApplyCart(Cart c, int cid)
        {
            var sm = Sm();
            if (sm == null || c.Index >= sm.m_CashierCounterList.Count)
                return;
            var counter = sm.m_CashierCounterList[c.Index];
            if (counter == null)
                return;
            // only re-build when this counter's customer actually changed; an unrelated
            // counter's cart broadcast must never reset a sale in progress elsewhere
            if (_cartGen.TryGetValue(c.Index, out int prev) && prev == cid)
            {
                if (!CartContentsMatch(c))
                {
                    // The first snapshot can arrive while the host is still moving the
                    // customer's bag onto the counter. Rebuild only before local scanning
                    // starts; never destroy a live partial sale to chase a stale snapshot.
                    if (counter.m_CashierCounterState == ECashierCounterState.ScanningItem
                        && LocalScanCount(counter) == 0)
                    {
                        CoopPlugin.Log.LogDebug($"RegisterSync client: rebuilding incomplete cart {c.Index}");
                        Teardown(c.Index);
                        ApplyCart(c, cid);
                        return;
                    }
                    CoopPlugin.Log.LogWarning($"RegisterSync client: cart contents differ during active sale at counter {c.Index}; preserving local bag");
                }
                ApplyCartPrices(c);
                if (!_cartScanSignature.TryGetValue(c.Index, out var oldScan) || oldScan != c.ScanSignature)
                {
                    ApplyScannedItems(c);
                    _cartScanSignature[c.Index] = c.ScanSignature;
                }
                ApplyAuthoritativePayment(c);
                ApplyAuthoritativePhase(c);
                ApplyAuthoritativeTotal(c);
                return;
            }
            _cartGen[c.Index] = cid;
            _cartScanSignature[c.Index] = c.ScanSignature;

            var carrier = GetCarrier(c.Index, c.CustomerIndex);
            if (carrier == null)
                return;
            AllowClientCustomerLifecycle = true;
            try
            {
                carrier.ActivateCustomer(canSpawnSmelly: false, randomizeCharacterMesh: false);
            }
            finally { AllowClientCustomerLifecycle = false; }
            if (carrier.m_CharacterCustom != null && !string.IsNullOrEmpty(c.CharacterName))
            {
                carrier.m_CharacterCustom.CharacterName = c.CharacterName;
                carrier.m_CharacterCustom.Initialize();
            }
            try
            {
                FiQueueCounter?.SetValue(carrier, counter);
            }
            catch (System.Exception e) { Swallow.Log(e); }

            // fresh customer: reset the scan/bookkeeping state the carrier carries across pool reuse.
            // ActivateCustomer is blocked on the client, so the cash was never Init()'d to the
            // carrier - wire it now or the presented payment would click into a null customer.
            try
            {
                FiScannedCount?.SetValue(carrier, 0);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            try
            {
                FiCustTotal?.SetValue(carrier, 0f);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            try
            {
                carrier.m_CustomerCash.Init(carrier);
            }
            catch (System.Exception e) { Swallow.Log(e); }

            // The served customer is LIVE at the register: activate the carrier so its real
            // interactable cash (a child, in its hands) is the presented, clickable payment, and
            // suppress the NpcSync clone so there is only one body here.
            try
            {
                if (counter.m_QueueStartPos != null)
                {
                    carrier.transform.position = counter.m_QueueStartPos.position;
                    var facing = counter.transform.position - carrier.transform.position;
                    facing.y = 0f;
                    carrier.transform.rotation = facing.sqrMagnitude > 0.0001f
                        ? Quaternion.LookRotation(facing, Vector3.up)
                        : counter.m_QueueStartPos.rotation;
                }
                carrier.gameObject.SetActive(true);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            NpcSync.SuppressedCustomer.Add(c.CustomerIndex);
            _sourceIndex[c.Index] = c.CustomerIndex;
            NpcSync.AttachExistingCustomer(c.CustomerIndex, c.CustomerGeneration, carrier);
            counter.UpdateCurrentCustomer(carrier);
            counter.UpdateCashierCounterState(ECashierCounterState.ScanningItem);
            counter.SetPlsaticBagVisibility(true);

            var ctf = counter.transform;
            var placePos = counter.m_CustomerPlaceItemPos;
            for (int i = 0; i < c.ItemTypes.Count; i++)
            {
                var item = SpawnBagItem(counter, ctf, placePos, c.ItemTypes[i], c.ItemPrices[i], carrier, i);
                if (item != null)
                {
                    _itemBag[item] = i;
                    _itemCounter[item] = c.Index;
                }
            }
            for (int j = 0; j < c.Cards.Count; j++)
            {
                var card = SpawnBagCard(counter, ctf, placePos, c.Cards[j], c.CardPrices[j], carrier, j);
                if (card != null)
                {
                    _cardBag[card] = j;
                    _cardCounter[card] = c.Index;
                }
            }
            ApplyScannedItems(c);
            ApplyAuthoritativePayment(c);
            ApplyAuthoritativePhase(c);
            ApplyAuthoritativeTotal(c);
        }

        private static string BuildCartSignature(Cart c)
        {
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < c.ItemTypes.Count; i++)
            {
                result.Append((int)c.ItemTypes[i]).Append(':')
                    .Append(i < c.ItemPrices.Count ? c.ItemPrices[i].ToString("R") : "0").Append(':')
                    .Append(i < c.ItemScanned.Count && c.ItemScanned[i] ? '1' : '0').Append(';');
            }
            result.Append('/');
            for (int i = 0; i < c.Cards.Count; i++)
            {
                var data = c.Cards[i];
                if (data != null)
                    result.Append((int)data.expansionType).Append(':').Append((int)data.monsterType).Append(':')
                        .Append((int)data.borderType).Append(':').Append(data.isFoil ? '1' : '0')
                        .Append(data.isDestiny ? '1' : '0').Append(data.isChampionCard ? '1' : '0')
                        .Append(':').Append(data.cardGrade).Append(':').Append(data.gradedCardIndex);
                result.Append(':').Append(i < c.CardPrices.Count ? c.CardPrices[i].ToString("R") : "0").Append(':')
                    .Append(i < c.CardScanned.Count && c.CardScanned[i] ? '1' : '0').Append(';');
            }
            return result.ToString();
        }

        private bool CartContentsMatch(Cart c)
        {
            if (!_carrier.TryGetValue(c.Index, out var customer) || customer == null)
                return false;
            var items = customer.GetItemInBagList();
            if (items.Count != c.ItemTypes.Count)
                return false;
            for (int i = 0; i < items.Count; i++)
                if (items[i] == null || items[i].GetItemType() != c.ItemTypes[i])
                    return false;
            var cards = customer.GetCardInBagList();
            if (cards.Count != c.Cards.Count)
                return false;
            for (int i = 0; i < cards.Count; i++)
                if (cards[i] == null || cards[i].m_Card3dUI == null || cards[i].m_Card3dUI.m_CardUI == null
                    || !cards[i].m_Card3dUI.m_CardUI.GetCardData().IsSameCardDataType(c.Cards[i]))
                    return false;
            return true;
        }

        private void ApplyCartPrices(Cart c)
        {
            if (!_carrier.TryGetValue(c.Index, out var customer) || customer == null)
                return;
            var items = customer.GetItemInBagList();
            for (int i = 0; i < items.Count && i < c.ItemPrices.Count; i++)
                if (items[i] != null)
                    items[i].SetCurrentPrice(c.ItemPrices[i]);
            var cards = customer.GetCardInBagList();
            for (int i = 0; i < cards.Count && i < c.CardPrices.Count; i++)
                if (cards[i] != null)
                    cards[i].SetCurrentPrice(c.CardPrices[i]);
        }

        private static int LocalScanCount(InteractableCashierCounter counter)
        {
            var screen = FiCashScreen?.GetValue(counter) as UI_CashCounterScreen;
            if (screen == null)
                return 0;
            int count = 0;
            foreach (var pair in screen.GetItemScannedListDict())
                count += pair.Value;
            return count;
        }

        private void ApplyScannedItems(Cart c)
        {
            if (!_carrier.TryGetValue(c.Index, out var customer) || customer == null)
                return;
            var items = customer.GetItemInBagList();
            for (int i = 0; i < c.ItemScanned.Count && i < items.Count; i++)
                if (c.ItemScanned[i] && items[i] != null && items[i].m_InteractableScanItem != null
                    && items[i].m_InteractableScanItem.IsNotScanned())
                    items[i].m_InteractableScanItem.OnMouseButtonUp();
            var cards = customer.GetCardInBagList();
            for (int i = 0; i < c.CardScanned.Count && i < cards.Count; i++)
                if (c.CardScanned[i] && cards[i] != null && cards[i].IsNotScanned())
                    cards[i].OnMouseButtonUp();
        }

        private void ApplyAuthoritativeTotal(Cart c)
        {
            if (!_carrier.TryGetValue(c.Index, out var customer) || customer == null)
                return;
            var sm = Sm();
            if (sm == null || c.Index >= sm.m_CashierCounterList.Count)
                return;
            var counter = sm.m_CashierCounterList[c.Index];
            if (counter == null)
                return;
            _authoritativeTotal[c.Index] = c.TotalScanned;
            FiTotalScanned?.SetValue(counter, c.TotalScanned);
            FiCustTotal?.SetValue(customer, c.CustomerTotalScanned);
            var screen = FiCashScreen?.GetValue(counter) as UI_CashCounterScreen;
            if (screen != null)
            {
                bool ready = FiChangeReady?.GetValue(counter) is bool changeReady && changeReady;
                double paid = FiPaidAmount?.GetValue(counter) is double customerPaid ? customerPaid : 0.0;
                double change = FiCurrentMoneyChange?.GetValue(counter) is double currentChange ? currentChange : 0.0;
                screen.UpdateMoneyChangeAmount(ready, paid, c.TotalScanned, change);

                // The game updates these labels from the client's local card scan total.
                // Card totals are accumulated through a float on the game side, so that
                // local value can differ from the host's authoritative total. Keep the
                // labels synchronized with the networked total instead. Setting the text
                // does not change visibility; the large total remains hidden until the
                // vanilla payment phase calls ShowScaledUpTotalCost().
                string totalText = GameInstance.GetPriceString(c.TotalScanned);
                if (screen.m_TotalItemListCostText != null)
                    screen.m_TotalItemListCostText.text = totalText;
                if (screen.m_ScaledUpTotalText != null)
                    screen.m_ScaledUpTotalText.text = totalText;
            }
        }

        private void ApplyAuthoritativePhase(Cart c)
        {
            if (!_carrier.TryGetValue(c.Index, out var customer) || customer == null)
                return;
            var sm = Sm();
            if (sm == null || c.Index >= sm.m_CashierCounterList.Count)
                return;
            var counter = sm.m_CashierCounterList[c.Index];
            if (counter == null)
                return;
            var state = (ECashierCounterState)c.State;
            if (state != ECashierCounterState.ScanningItem)
                return;
            if ((int)counter.m_CashierCounterState > (int)ECashierCounterState.ScanningItem)
                return;
            FiIsUsingCard?.SetValue(counter, false);
            FiStartGivingChange?.SetValue(counter, false);
            FiChangeReady?.SetValue(counter, false);
            FiCurrentMoneyChange?.SetValue(counter, 0.0);
            FiTooMuchChange?.SetValue(counter, false);
            customer.m_CustomerCash.gameObject.SetActive(false);
            if (customer.m_Anim != null)
                customer.m_Anim.SetBool("HandingOverCash", false);
            counter.UpdateCashierCounterState(state);
        }

        private void ApplyAuthoritativePayment(Cart c)
        {
            if (!_carrier.TryGetValue(c.Index, out var customer) || customer == null)
                return;
            var sm = Sm();
            if (sm == null || c.Index >= sm.m_CashierCounterList.Count)
                return;
            var counter = sm.m_CashierCounterList[c.Index];
            ApplyingAuthoritativePayment = true;
            try
            {
                counter.SetCustomerPaidAmount(c.IsCard, c.PaidAmount);
                if (c.State == (byte)ECashierCounterState.TakingCash)
                {
                    customer.m_CustomerCash.SetIsCard(c.IsCard);
                    customer.m_CustomerCash.gameObject.SetActive(true);
                    customer.m_Anim.SetBool("HandingOverCash", true);
                    counter.UpdateCashierCounterState(ECashierCounterState.TakingCash);
                }
                else if (c.State == (byte)ECashierCounterState.GivingChange
                    && counter.m_CashierCounterState != ECashierCounterState.GivingChange)
                {
                    customer.OnCashTaken(c.IsCard);
                }
            }
            finally { ApplyingAuthoritativePayment = false; }
        }

        private Item SpawnBagItem(InteractableCashierCounter counter, Transform ctf, Transform placePos,
            EItemType type, float price, Customer carrier, int i)
        {
            try
            {
                if (type == EItemType.None)
                    return null;
                var meshData = InventoryBase.GetItemMeshData(type);
                if (meshData == null)
                    return null;
                var item = ItemSpawnManager.GetItem(ctf);
                item.SetMesh(meshData.mesh, meshData.material, type,
                    meshData.meshSecondary, meshData.materialSecondary, meshData.materialList);
                item.SetCurrentPrice(price);
                item.transform.parent = ctf;
                item.transform.position = placePos.position;
                int num = i % 8, num2 = Mathf.Clamp(i / 8, 0, 1), num3 = Mathf.Clamp(i / 16, 0, 2);
                item.transform.position += placePos.forward * (-0.025f * num);
                item.transform.position += placePos.right * (0.1f * num2);
                item.transform.position += Vector3.up * (0.2f * num3);
                item.transform.rotation = placePos.rotation;
                item.transform.Rotate(new Vector3(Random.Range(-30, -5), Random.Range(-5, 5), Random.Range(-5, 5)));
                item.m_Mesh.enabled = true;
                item.gameObject.SetActive(true);
                item.m_Collider.enabled = true;
                // Match Customer.OnCashierCounterQueueMoved: once the customer has placed
                // the item on the counter it must be a real physics object. Keeping this
                // kinematic made the client presentation differ from the host and prevented
                // the item from settling/interacting naturally.
                if (item.m_Rigidbody != null)
                    item.m_Rigidbody.isKinematic = false;
                item.m_InteractableScanItem.enabled = true;
                item.m_InteractableScanItem.RegisterScanItem(carrier, counter.m_ScannedItemLerpPos);
                carrier.m_ItemInBagList.Add(item);
                return item;
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning($"RegisterSync client: bag item spawn: {e.Message}");
                return null;
            }
        }

        private InteractableCard3d SpawnBagCard(InteractableCashierCounter counter, Transform ctf, Transform placePos,
            CardData cardData, float price, Customer carrier, int j)
        {
            try
            {
                if (cardData == null || cardData.monsterType == EMonsterType.None)
                    return null;
                var cardUI = CSingleton<Card3dUISpawner>.Instance.GetCardUI();
                var card = ShelfManager.SpawnInteractableObject(EObjectType.Card3d).GetComponent<InteractableCard3d>();
                if (card == null)
                    return null;
                cardUI.m_CardUI.SetCardUI(cardData);
                card.SetCardUIFollow(cardUI);
                card.SetEnableCollision(false);
                card.SetCurrentPrice(price);
                card.transform.parent = ctf;
                card.transform.position = placePos.position;
                card.transform.localScale = Vector3.one;
                int num = j % 8, num2 = Mathf.Clamp(j / 8, 0, 1), num3 = Mathf.Clamp(j / 16, 0, 2);
                card.transform.position += placePos.forward * (-0.035f * num);
                card.transform.position += placePos.right * (0.1f * num2);
                card.transform.position += Vector3.up * (0.3f * num3);
                card.transform.rotation = placePos.rotation;
                card.transform.Rotate(new Vector3(Random.Range(-30, -5) + 180, Random.Range(-5, 5), Random.Range(-5, 5)));
                card.m_Card3dUI.gameObject.SetActive(true);
                card.gameObject.SetActive(true);
                card.m_Collider.enabled = true;
                if (card.m_Rigidbody != null)
                    card.m_Rigidbody.isKinematic = false;
                card.RegisterScanCard(carrier, counter.m_ScannedItemLerpPos);
                carrier.m_CardInBagList.Add(card);
                return card;
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning($"RegisterSync client: bag card spawn: {e.Message}");
                return null;
            }
        }

        private Customer GetCarrier(int idx, ushort customerIndex)
        {
            if (_carrier.TryGetValue(idx, out var c) && c != null)
                return c;
            Customer carrier = null;
            var cm = Object.FindObjectOfType<CustomerManager>();
            if (cm != null)
            {
                var list = cm.GetCustomerList();
                if (customerIndex < list.Count && IsCarrierAvailable(list[customerIndex]))
                    carrier = list[customerIndex];
                if (carrier == null)
                    for (int i = 0; i < list.Count; i++)
                        if (IsCarrierAvailable(list[i]))
                        {
                            carrier = list[i];
                            break;
                        }
            }
            _carrier[idx] = carrier;
            return carrier;
        }

        private bool IsCarrierAvailable(Customer customer)
        {
            return customer != null && !_carrier.ContainsValue(customer)
                && !NpcSync.IsExistingCustomer(customer);
        }

        /// <summary>Client: observer state broadcast - values the manning gate only. (The
        /// register visuals come from the real RegisterCart reconstruction, so no fake mirror.)</summary>
        public void ClientApplyState(RegisterStateMessage message)
        {
            var entries = message.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                byte idx = entries[i].Index;
                byte manned = entries[i].Manned;
                int owner = entries[i].OwnerConnId;
                if (manned != 0)
                    _mannedBy[idx] = manned;
                else
                    _mannedBy.Remove(idx);
                if (manned != 0 && _localManned == idx
                    && !(manned == 2 && PlayerRegistry.IsLocalConnection(owner)))
                {
                    CoopPlugin.Log.LogInfo($"RegisterSync client: counter {idx} claim rejected; releasing local station");
                    ForceExitManned();
                }
            }
        }

        /// <summary>Client: settle a counter after its customer left (no economy/AI - host owns those).</summary>
        private void ClientSettle(int idx)
        {
            var sm = Sm();
            if (sm == null || idx >= sm.m_CashierCounterList.Count)
                return;
            var counter = sm.m_CashierCounterList[idx];
            if (counter == null)
                return;
            try
            {
                counter.UpdateCashierCounterState(ECashierCounterState.Idle);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            try
            {
                counter.UpdateCurrentCustomer(null);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            try
            {
                counter.SetPlsaticBagVisibility(false);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            Teardown(idx);
        }

        private void Teardown(int idx)
        {
            if (_carrier.TryGetValue(idx, out var carrier) && carrier != null)
            {
                try
                {
                    carrier.m_CustomerCash.gameObject.SetActive(false);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                if (carrier.m_ItemInBagList != null)
                    for (int i = carrier.m_ItemInBagList.Count - 1; i >= 0; i--)
                        if (carrier.m_ItemInBagList[i] != null)
                            carrier.m_ItemInBagList[i].gameObject.SetActive(false);
                carrier.m_ItemInBagList.Clear();
                if (carrier.m_CardInBagList != null)
                    for (int i = carrier.m_CardInBagList.Count - 1; i >= 0; i--)
                        ReleaseCard(carrier.m_CardInBagList[i]);
                carrier.m_CardInBagList.Clear();
            }
            _carrier.Remove(idx);
            _cartGen.Remove(idx);
            _cartScanSignature.Remove(idx);
            _authoritativeTotal.Remove(idx);
            if (_sourceIndex.TryGetValue(idx, out int src))
            {
                _sourceIndex.Remove(idx);
                NpcSync.DetachExistingCustomer(src, carrier);
            }
            var itemDrop = new List<Item>();
            foreach (var kv in _itemCounter)
                if (kv.Value == idx)
                    itemDrop.Add(kv.Key);
            foreach (var it in itemDrop)
            {
                _itemBag.Remove(it);
                _itemCounter.Remove(it);
            }
            var cardDrop = new List<InteractableCard3d>();
            foreach (var kv in _cardCounter)
                if (kv.Value == idx)
                    cardDrop.Add(kv.Key);
            foreach (var card in cardDrop)
            {
                _cardBag.Remove(card);
                _cardCounter.Remove(card);
            }
        }

        private void TeardownCarriers()
        {
            foreach (int idx in _carrier.Keys)
            {
                var carrier = _carrier[idx];
                try
                {
                    if (carrier != null)
                    {
                        carrier.m_CustomerCash.gameObject.SetActive(false);
                        carrier.gameObject.SetActive(false);
                        for (int i = carrier.m_ItemInBagList.Count - 1; i >= 0; i--)
                            if (carrier.m_ItemInBagList[i] != null)
                                carrier.m_ItemInBagList[i].gameObject.SetActive(false);
                        carrier.m_ItemInBagList.Clear();
                        if (carrier.m_CardInBagList != null)
                            for (int i = carrier.m_CardInBagList.Count - 1; i >= 0; i--)
                                ReleaseCard(carrier.m_CardInBagList[i]);
                        carrier.m_CardInBagList.Clear();
                    }
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            _carrier.Clear();
            _cartGen.Clear();
            _sourceIndex.Clear();
            _authoritativeTotal.Clear();
            _itemBag.Clear();
            _itemCounter.Clear();
            _cardBag.Clear();
            _cardCounter.Clear();
        }

        private static void ReleaseCard(InteractableCard3d card)
        {
            try
            {
                if (card != null)
                {
                    if (card.m_Card3dUI != null)
                        card.m_Card3dUI.gameObject.SetActive(false);
                    card.gameObject.SetActive(false);
                }
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }
    }
}
