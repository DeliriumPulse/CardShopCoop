using CardShopCoop.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using I2.Loc;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Lets the joiner answer walk-up TRADE / SELL-IN customers at the counter
    /// (MsgType.TradeState host->client, MsgType.TradeOp client->host).
    ///
    /// RESEARCH NOTES (decompiled/Customer.cs, CustomerTradeCardScreen.cs,
    /// InteractableCashierCounter.cs):
    ///  - A customer decides to trade in Customer.DetermineShopAction (~line 858):
    ///    m_CurrentTradeCardCashierCounter = ShelfManager.GetCashierCounterToTradeCard()
    ///    (which flags the counter's m_IsCustomerTradingCard), walks to
    ///    GetTradeCardStandLoc(), state WantToTradeCard; on arrival (Customer.cs:2204)
    ///    it enables m_ExclaimationMesh + m_InteractCollider and enters
    ///    ECustomerState.WaitingToTradeCard.
    ///  - The host serves by clicking the "!" collider: Customer.OnMousePress ->
    ///    CustomerManager.m_CustomerTradeCardScreen.SetCustomer(this, m_CustomerTradeData)
    ///    + OpenScreen(). CRITICALLY, the OFFER (sell vs trade direction m_IsTrading,
    ///    offered card m_CardData_L, wanted trade-back card m_CardData_R, ask price
    ///    m_SellCardAskPrice) is ROLLED INSIDE SetCustomer - a waiting customer carries
    ///    no offer data until SetCustomer has run once. The vanilla persistence hook is
    ///    CustomerTradeData: "Let Me Think" stores the rolled offer on the customer
    ///    (Customer.OnPressRefreshInteract -> private m_CustomerTradeData) and the next
    ///    SetCustomer call replays it verbatim. We exploit exactly that hook twice: the
    ///    host PRE-ROLLS the offer by calling SetCustomer on the (closed) screen so the
    ///    broadcast digest is the offer the host would see on click, and the JOINER
    ///    replays a digest-built CustomerTradeData into its own screen the same way.
    ///  - JOINER UX: the joiner opens the game's REAL CustomerTradeCardScreen by clicking
    ///    the trade customer - Customer.OnMousePress runs its own calls
    ///    (Customer.OnMousePress:236-250 - EnterWorkerInteractMode/EnterUIMode/
    ///    EnterLockMoveMode + tooltip/GameUI hides). A DEACTIVATED puppet customer from
    ///    CustomerManager.GetCustomerList() (the pool NpcSweep suppresses) is the data
    ///    carrier: SetCustomer(puppet, digestData) reads NOTHING from the customer beyond
    ///    storing m_CurrentCustomer, and data != null skips every local RNG roll. The
    ///    joiner's buttons then follow the single-player flow: Accept forwards
    ///    TradeOp{accept, counterIdx, m_PriceSet} and KEEPS the screen open while the host
    ///    decides; Decline forwards and lets vanilla's sound+close run; Let Me Think runs
    ///    vanilla verbatim. The host's confirmed result arrives in TradeStateMessage and is
    ///    replayed on that same screen through its own text lists, animation, price labels
    ///    and buttons (accepted -> accepted text + Done; refused -> haggle/decline text and
    ///    the host's moved ask), so the guest sees exactly what single-player would. Closing
    ///    goes through the vanilla CloseScreen -> OnCloseScreen -> Customer.OnPressStopInteract
    ///    chain, whose client prefix replays only the player-restore half (the vanilla tail
    ///    would run DetermineShopAction on an INACTIVE puppet - StartCoroutine throws there).
    ///  - Accept paths (CustomerTradeCardScreen.OnPressAccept): trading -> pure card
    ///    swap CPlayerData.AddCard(L,1) + ReduceCard(R,1) / RemoveGradedCard(R);
    ///    selling -> haggle RNG on m_PriceSet (m_PriceSet >= m_SellCardAskPrice-0.01
    ///    forces a 100% accept), then the REAL money+card path:
    ///    PriceChangeManager.AddTransaction, CEventPlayer_ReduceCoin(m_PriceSet),
    ///    CPlayerData.AddCard(L,1), customer.SetSoldCard(L). The host pins m_PriceSet
    ///    to the joiner's FORWARDED price, so lowballs haggle exactly like vanilla:
    ///    the not-accepted branches move m_SellCardAskPrice / the decline counters,
    ///    which we persist back into m_CustomerTradeData ("Let Me Think" recipe) and
    ///    re-broadcast so the guest can replay the same feedback; the out-of-patience
    ///    final else (no counter change) walks the customer off.
    ///  - Headless resolution recipe: the 60s wait timeout (Customer.cs:3639-3660)
    ///    resolves a waiting customer with NO UI/InteractionPlayerController calls:
    ///    clear m_CustomerTradeData/m_Timer/m_IsPausingAction, hide mesh+collider,
    ///    m_HasTradedCard = true, counter.CustomerFinishTradingCard(),
    ///    DetermineShopAction(). We reuse it for decline, walk-off and post-accept
    ///    cleanup, so the host's screen NEVER opens and the host player is never
    ///    yanked into UI mode.
    ///  - Player reach: InteractionPlayerController sits on a STATIONARY manager
    ///    object - its transform never moves (CoopCore's frozen-avatar bug). The
    ///    moving body is its public m_WalkerCtrl (CMF walker); measuring reach from
    ///    ipc.transform was why the old V/B keys never fired.
    ///
    /// Money safety: the sell-in charge runs ONCE, host-side, through the vanilla
    /// OnPressAccept (CEventPlayer_ReduceCoin -> shared econ mirror; AddCard -> the
    /// CardDelta mirror). The client never runs any vanilla trade code: OnPressAccept's
    /// body is blocked client-side and its result is replayed from the host's confirmation.
    /// </summary>
    public class TradeServe : TickableCoopModule
    {
        private const float StaleAfter = 13f;    // client: > 2x heal + margin
        private static float Reach => CoopPlugin.ServeReach.Value; // same reach as RegisterServe
        private const float VanillaWait = 60f;   // Customer.cs WaitingToTradeCard timeout
        private const int MaxOffers = 32;

        /// <summary>Set by CoopCore: client -> host op (MsgType.TradeOp).</summary>
        public Action<INetMessage> SendOp;
        /// <summary>Set by CoopCore: host -> clients state (MsgType.TradeState).</summary>
        public Action<INetMessage> BroadcastState;

        private const byte OpAccept = 1;
        private const byte OpDecline = 2;
        private const byte OpScreen = 3; // joiner's native screen open-claim (renewed ~2s)

        // host -> client result codes (TradeStateMessage.ResultOutcome). The joiner's Accept
        // is confirmed asynchronously by the host; these drive the REAL screen to the state
        // single-player would have reached, without running local economy.
        private const byte OutcomeNone = 0;
        private const byte OutcomeAccepted = 1;
        private const byte OutcomeRefusedAsk = 2;   // haggle: the ask moved up
        private const byte OutcomeRefusedSame = 3;  // plain refusal: the ask held
        private const byte OutcomeWalkOff = 4;      // decline / lost patience: the customer leaves
        private const byte OutcomeInsufficientMoney = 5;
        private const byte OutcomeMissingCard = 6;
        private const byte OutcomeBlocked = 7;      // transient error: keep the screen open
        private const byte NoCounter = 255;

        // harmony prefixes are static; CoopCore owns the single instance
        private static TradeServe _live;

        /// <summary>Disable Harmony callbacks before a session's module state is torn down.</summary>
        public static void ClearLive()
        {
            _live = null;
        }

        public static void ActivateLive(TradeServe instance)
        {
            _live = instance;
        }

        public TradeServe()
        {
            _live = this;
        }

        public override string Name => "trade-serve";

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);
        protected override void OnClientTick(in SyncFrame frame) => ClientTick(frame.Dt, frame.InGame);

        // ---- reflection: Customer privates (verified against decompiled/Customer.cs)
        private static readonly FieldInfo FiTradeData = ReflectionSurface.RequiredField(typeof(Customer), "m_CustomerTradeData");
        private static readonly FieldInfo FiTradeCounter = ReflectionSurface.RequiredField(typeof(Customer), "m_CurrentTradeCardCashierCounter");
        private static readonly FieldInfo FiHasTraded = ReflectionSurface.RequiredField(typeof(Customer), "m_HasTradedCard");
        private static readonly FieldInfo FiCustTimer = ReflectionSurface.RequiredField(typeof(Customer), "m_Timer");
        private static readonly FieldInfo FiCustTimerMax = ReflectionSurface.RequiredField(typeof(Customer), "m_TimerMax");
        private static readonly FieldInfo FiPausing = ReflectionSurface.RequiredField(typeof(Customer), "m_IsPausingAction");
        private static readonly MethodInfo MiDetermine = ReflectionSurface.RequiredMethod(typeof(Customer), "DetermineShopAction");

        // ---- reflection: CustomerTradeCardScreen privates
        private static readonly FieldInfo FiScrTrading = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_IsTrading");
        private static readonly FieldInfo FiScrAccepted = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_HasAccepted");
        private static readonly FieldInfo FiScrPriceSet = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_PriceSet");
        private static readonly FieldInfo FiScrLastPrice = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_LastPriceSet");
        private static readonly FieldInfo FiScrAsk = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_SellCardAskPrice");
        private static readonly FieldInfo FiScrMarket = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_SellCardMarketPrice");
        private static readonly FieldInfo FiScrMaxDecline = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_MaxDeclineCount");
        private static readonly FieldInfo FiScrDecline = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_DeclineCount");
        private static readonly FieldInfo FiScrCardL = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_CardData_L");
        private static readonly FieldInfo FiScrCardR = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_CardData_R");

        // ---- reflection: UIScreenBase privates (verified against decompiled/UIScreenBase.cs)
        // used ONLY by the S2 hard-teardown path, which mirrors base.OnCloseScreen when the
        // vanilla close chain threw before it (m_IsScreenOpen protected bool ~line 15,
        // m_ScreenGroup public GameObject ~line 5). Cached on the screen's runtime type
        // (CustomerTradeCardScreen), AccessTools walks the base hierarchy to find them.
        private static readonly FieldInfo FiScrIsOpen = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_IsScreenOpen");
        private static readonly FieldInfo FiScrGroup = ReflectionSurface.RequiredField(typeof(CustomerTradeCardScreen), "m_ScreenGroup");

        private struct Offer
        {
            public byte CounterIdx;
            public bool Known;    // pre-roll succeeded: card/price fields are valid
            public bool Trading;  // true = card-for-card trade, false = sell-in for money
            public CardData CardL;   // what the customer offers
            public CardData CardR;   // what the customer wants back (Trading only)
            public float Price;      // asking price (sell-in only)
            public float PriceSet;
            public float LastPriceSet;
            public int MaxDeclineCount;
            public int DeclineCount;
            public float Remaining;  // seconds before the customer gives up
            public ushort CustomerIndex;
            public int CustomerGeneration;
            public Vector3 Position;
            public float Yaw;
        }

        // host
        private readonly SnapshotGate _gate = new SnapshotGate(0.5f, 6f, -0.83f);
        private int _lastHash; // logging only; SnapshotGate owns send gating
        private byte _resultSeq;
        private string _result = "";
        private byte _resultCounter = NoCounter;  // counter the latest result belongs to
        private byte _resultOutcome = OutcomeNone;
        // NEVER CSingleton<>.Instance for these three: touched while no real manager
        // exists (client reload loading screen, host mid-session save load) the getter
        // fabricates a fake empty DontDestroyOnLoad manager that shadows the real one
        // for the rest of the run (see WorldSync.ResolveShelfManager). Cached; Unity
        // fake-null re-resolves after scene loads.
        private ShelfManager _sm;
        private CustomerManager _cm;
        private InteractionPlayerController _ipc;
        private readonly List<Offer> _hostBuf = new List<Offer>();
        private readonly Dictionary<int, float> _preRollFailUntil = new Dictionary<int, float>(); // customer id -> retry-after (transient failure backoff, never permanent)
        private readonly HashSet<int> _unknownLogged = new HashSet<int>();   // customer ids; log known=false once

        // client
        private readonly Dictionary<int, Offer> _offers = new Dictionary<int, Offer>();
        private readonly Dictionary<int, Customer> _carriers = new Dictionary<int, Customer>();
        private readonly Dictionary<int, int> _carrierSource = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _carrierGeneration = new Dictionary<int, int>();
        private readonly Dictionary<int, Renderer[]> _carrierRenderers = new Dictionary<int, Renderer[]>();
        private readonly Dictionary<int, bool[]> _carrierRendererStates = new Dictionary<int, bool[]>();
        private readonly List<int> _keyBuf = new List<int>();
        private float _staleTimer;
        private float _opThrottle;
        private int _seenSeq = -1;
        private int _lastOfferCount = -1; // for change-only receive logging
        private int _pendingCounter = -1; // counter our native screen is answering
        private bool _awaitingResult;     // client: accept forwarded, waiting for the host decision
        private bool _acceptedShown;      // client: host accepted; the screen shows Done and owns its close
        private Transform _playerTf;      // the joiner's MOVING body (ipc.m_WalkerCtrl)
        private bool _hostBusy;           // host is mid-trade: don't open ours
        private float _claimTimer;        // renews our open-screen claim to the host

        // host: a joiner's screen is open for this counter (renewed every ~2s;
        // stale after 5s so a crash/disconnect never wedges the host's trading)
        // counter idx -> the claiming conn + heartbeat time. PER-COUNTER (was a single slot):
        // with two guests holding screens at two different counters, the single slot
        // flip-flopped on every ~2s heartbeat and whichever guest wasn't the most recent
        // claimant lost both the host-click block AND the customer-timer pin - their
        // customer walked at 60s mid-screen, the very bug the pin exists to stop.
        // Claims expire by time (5s) everywhere they're read; HostReleaseConn also drops a
        // disconnected guest's claim immediately so the host is not blocked by a dead one.
        private struct GuestClaim
        {
            public double Time;
            public int ConnId;
        }

        private readonly Dictionary<int, GuestClaim> _guestClaims = new Dictionary<int, GuestClaim>();

        public override void Reset()
        {
            _gate.Reset(-0.83f);
            _lastHash = 0;
            _resultSeq = 0;
            _result = "";
            _resultCounter = NoCounter;
            _resultOutcome = OutcomeNone;
            _sm = null;
            _cm = null;
            _ipc = null;
            _hostBuf.Clear();
            _preRollFailUntil.Clear();
            _unknownLogged.Clear();
            _offers.Clear();
            TeardownCarriers();
            _staleTimer = 0f;
            _opThrottle = 0f;
            _seenSeq = -1;
            _lastOfferCount = -1;
            _pendingCounter = -1;
            _awaitingResult = false;
            _acceptedShown = false;
            _playerTf = null;
            _hostBusy = false;
            _claimTimer = 0f;
            _guestClaims.Clear();
        }

        public override void ForceResend()
        {
            _gate.Force();
            _lastHash = 0;
        }

        public override void Dispose()
        {
            // A session that dies (host disconnect / quit) while our native trade screen
            // is open would otherwise leave the guest standing in UI mode with the screen
            // up, because the close path is driven by host state that is now gone.
            if (CoopCore.Role == CoopRole.Client && _pendingCounter >= 0)
                TeardownOpenScreenOnDisconnect();
            if (ReferenceEquals(_live, this))
                ClearLive();
            base.Dispose();
        }

        /// <summary>Client: session-end cleanup for an open trade screen. Runs before the
        /// static callbacks are dropped, so the real close chain (and its
        /// OnPressStopInteract prefix) can still restore the player.</summary>
        private void TeardownOpenScreenOnDisconnect()
        {
            try
            {
                var cm = Cm();
                var screen = cm != null ? cm.m_CustomerTradeCardScreen : null;
                if (screen != null && screen.IsScreenOpened())
                {
                    CoopPlugin.Log.LogInfo("TradeServe client: session ended with the trade screen open - closing it");
                    try
                    {
                        screen.CloseScreen();
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    if (screen.IsScreenOpened())
                    {
                        // the host is gone, so the vanilla close chain may not run - hard teardown
                        FiScrIsOpen?.SetValue(screen, false);
                        if (FiScrGroup?.GetValue(screen) is GameObject group)
                            group.SetActive(false);
                    }
                }
                // the disconnect means the counterpart's restore never arrives; do it now
                RestorePlayerFromUiMode();
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("TradeServe client: disconnect teardown: " + e.Message);
            }
            _pendingCounter = -1;
            _awaitingResult = false;
            _acceptedShown = false;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // The joiner's world has puppet customers only; clicking one would run a
            // local trade that mutates the mirrored wallet/binder outside the host's
            // simulation. Block the vanilla entry point; the counter prompt + the
            // remote-controlled screen below are the joiner's UI.
            Try(h, typeof(Customer), "OnMousePress",
                prefix: new HarmonyMethod(typeof(TradeServe), nameof(ClientTradeBlockPrefix)));
            Try(h, typeof(Customer), "OnMousePress",
                postfix: new HarmonyMethod(typeof(TradeServe), nameof(ClientTradeEnterPostfix)));
            // The native-screen remote controls: on the client the screen's buttons
            // forward a TradeOp to the host instead of resolving locally, then close
            // through the screen's own path. The host role passes every prefix through.
            Try(h, typeof(CustomerTradeCardScreen), "OnPressAccept",
                prefix: new HarmonyMethod(typeof(TradeServe), nameof(ClientAcceptPrefix)));
            Try(h, typeof(CustomerTradeCardScreen), "SetCustomer",
                postfix: new HarmonyMethod(typeof(TradeServe), nameof(ClientSetCustomerPostfix)));
            Try(h, typeof(CustomerTradeCardScreen), "OnCloseScreen",
                postfix: new HarmonyMethod(typeof(TradeServe), nameof(ClientCloseScreenPostfix)));
            Try(h, typeof(CustomerTradeCardScreen), "OnPressDecline",
                prefix: new HarmonyMethod(typeof(TradeServe), nameof(ClientDeclinePrefix)));
            Try(h, typeof(CustomerTradeCardScreen), "OnPressLetMeThink",
                prefix: new HarmonyMethod(typeof(TradeServe), nameof(ClientLetMeThinkPrefix)));
            // CloseScreen -> OnCloseScreen -> m_CurrentCustomer.OnPressStopInteract:
            // the vanilla tail runs DetermineShopAction on our INACTIVE carrier puppet
            // (StartCoroutine throws on inactive objects), so replay only the
            // player-restore half (Customer.cs:255-267) on the client.
            Try(h, typeof(Customer), "OnPressStopInteract",
                prefix: new HarmonyMethod(typeof(TradeServe), nameof(ClientStopInteractPrefix)));
        }

        public static bool ClientTradeBlockPrefix(Customer __instance)
        {
            // HOST half of the both-players-on-one-customer guard: while a joiner's
            // native screen is open for this customer's counter (claim renewed ~2s,
            // stale after 5s), the host's click must not open a second screen
            if (CoopCore.Role == CoopRole.Host)
            {
                var t = _live;
                if (t != null && t._guestClaims.Count > 0)
                {
                    try
                    {
                        var sm = t.Sm();
                        double now = Time.realtimeSinceStartupAsDouble;
                        foreach (var kv in t._guestClaims)
                        {
                            if (now - kv.Value.Time >= 5.0)
                                continue; // stale claim
                            var counter = sm != null && kv.Key >= 0 && kv.Key < sm.m_CashierCounterList.Count
                                ? sm.m_CashierCounterList[kv.Key] : null;
                            if (counter != null && ReferenceEquals(FiTradeCounter?.GetValue(__instance), counter))
                            {
                                if (CoopCore.Instance != null)
                                {
                                    CoopCore.Instance.RegisterLine = "your partner is answering that customer";
                                    CoopCore.Instance.RegisterLineTimer = 3f;
                                }
                                return false;
                            }
                        }
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                }
                return true;
            }
            if (CoopCore.Role != CoopRole.Client)
                return true;
            var client = _live;
            return client != null && client.IsCarrier(__instance) && !client._hostBusy;
        }

        private bool IsCarrier(Customer customer)
        {
            foreach (var carrier in _carriers.Values)
                if (ReferenceEquals(carrier, customer))
                    return true;
            return false;
        }

        public static void ClientTradeEnterPostfix(Customer __instance)
        {
            if (CoopCore.Role != CoopRole.Client || _live == null || _live._hostBusy || !(_live.IsCarrier(__instance)))
                return;
            foreach (var kv in _live._carriers)
            {
                if (!ReferenceEquals(kv.Value, __instance))
                    continue;
                _live._pendingCounter = kv.Key;
                _live._claimTimer = 999f;
                _live._awaitingResult = false;
                _live._acceptedShown = false;
                _live.SendOp?.Invoke(new TradeOpMessage { Op = OpScreen, CounterIdx = (byte)kv.Key, Price = 0f });
                return;
            }
        }

        /// <summary>Client: the screen's Accept button forwards the CURRENT price field to
        /// the host (which owns the haggle RNG and the economy), then KEEPS the screen open.
        /// The host confirms the outcome through TradeStateMessage; ApplyOutcome replays it
        /// on this same screen (accepted text + Done, or the haggle/decline feedback), so the
        /// guest sees the vanilla flow. Vanilla's own body must never run here - it would move
        /// the mirrored wallet/binder locally.</summary>
        public static bool ClientAcceptPrefix(CustomerTradeCardScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            var t = _live;
            if (t == null)
                return false;
            // Done after a confirmed accept: vanilla's body is just the sound + CloseScreen
            // (its m_HasAccepted early-return). Let it run so Done closes through the real path.
            if (FiScrAccepted?.GetValue(__instance) is bool done && done)
                return true;
            int idx = t._pendingCounter;
            // THE haggle "$0" bug: the game only writes m_PriceSet on the input field's
            // onEndEdit (CustomerTradeCardScreen.OnInputTextUpdated). Typing alone runs
            // OnInputChanged, which updates the DISPLAY only. Pressing Accept (mouse or
            // our forwarded button) can beat that commit, so m_PriceSet stays stale/$0
            // while the field visibly shows the typed amount - "it shows the last entered
            // amount but reads $0". Force-commit the visible field so the forwarded bid is
            // exactly what the player sees. Sell-in only (trades carry no price field).
            bool trading = FiScrTrading?.GetValue(__instance) is bool tr && tr;
            if (!trading)
            {
                try
                {
                    var field = __instance.m_SetPriceInput;
                    string txt = field != null ? field.text : null;
                    if (!string.IsNullOrWhiteSpace(txt))
                        __instance.OnInputTextUpdated(txt); // parses visible text -> m_PriceSet
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe client: price commit: " + e.Message); }
            }
            float price = FiScrPriceSet?.GetValue(__instance) is float p ? p : 0f;
            // a zero/garbage field means "at the asking price" (-1 tells the host so);
            // a genuine $0 bid is never what an accept press means
            if (price <= 0f)
                price = -1f;
            // S1: the screen can be ORPHANED - visually open while _pendingCounter == -1
            // (the "offer vanished" close raced the CloseScreen chain and left the screen
            // up, or a native close swallowed a throw). The old code silently dropped the
            // accept whenever idx < 0 (the customer walked while we stared at a dead
            // screen). Instead try to REBIND to whatever offer we're actually standing at
            // before giving up, so a real press is never eaten.
            if (idx < 0)
            {
                idx = t.RebindOrphanedScreen("accept");
                // the orphaned screen was showing SOME OTHER customer's card and price -
                // forwarding that stale figure to the rebound counter could force-accept
                // the new offer at an amount the guest never saw for it (the host pins
                // m_PriceSet to whatever we send). -1 = "accept at the asking price", so
                // the rebound accept can only ever pay the rebound offer's own ask.
                if (idx >= 0)
                    price = -1f;
            }
            if (idx < 0)
            {
                // genuinely dead screen: tear it down so the clicked press is not left hanging
                try
                {
                    __instance.CloseScreen();
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe client: screen close: " + e.Message); }
                return false;
            }
            if (t._awaitingResult)
            {
                CoopPlugin.Log.LogInfo("TradeServe client: accept ignored - the host has not answered the previous press yet");
                return false;
            }
            CoopPlugin.Log.LogInfo($"TradeServe client: accept pressed on native screen (counter {idx}, price {price:F2}) - forwarding and waiting for the host result");
            // vanilla OnPressAccept plays this before anything else; match it since we skip the body
            try
            {
                SoundManager.GenericConfirm();
            }
            catch (System.Exception e) { Swallow.Log(e); }
            // keep the offer while the host decides: ApplyOutcome needs it for the refusal fields
            if (t.SendOpFor(OpAccept, idx, price, null, clearOffer: false))
                t._awaitingResult = true;
            return false;
        }

        /// <summary>Client: SetCustomer(data) restores the display labels but vanilla does
        /// not restore the editable sell-in input. Seed it from the authoritative ask so a
        /// new customer cannot inherit the previous customer's bid.</summary>
        public static void ClientSetCustomerPostfix(CustomerTradeCardScreen __instance,
            Customer customer, CustomerTradeData customerTradeData)
        {
            if (CoopCore.Role != CoopRole.Client || __instance == null || customerTradeData == null
                || customerTradeData.m_IsTrading || __instance.m_SetPriceInput == null)
                return;
            try
            {
                __instance.m_SetPriceInput.SetTextWithoutNotify(GameInstance.GetPriceString(
                    customerTradeData.m_SellCardAskPrice,
                    useDashAsZero: false, useCurrencySymbol: false));
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning("TradeServe client: seed price field: " + e.Message);
            }
        }

        /// <summary>Client: clear the reused sell-in input when the native screen closes so
        /// a bid from the previous customer can never be forwarded to the next one.</summary>
        public static void ClientCloseScreenPostfix(CustomerTradeCardScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            try
            {
                __instance.m_SetPriceInput?.SetTextWithoutNotify("");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("TradeServe client: clear price field: " + e.Message);
            }
        }

        /// <summary>Client: the screen's Decline button = forward the decline, then let
        /// the vanilla body run (it only plays a sound and closes the screen).</summary>
        public static bool ClientDeclinePrefix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            var t = _live;
            if (t != null)
            {
                // S1: same orphan rebind as the accept path - a decline press on a screen
                // that lost its binding (idx < 0) must still resolve the customer the
                // guest is standing at rather than being silently dropped. Vanilla still
                // runs (return true) so the screen plays its sound and closes normally.
                int idx = t._pendingCounter;
                if (idx < 0)
                    idx = t.RebindOrphanedScreen("decline");
                if (idx >= 0)
                {
                    CoopPlugin.Log.LogInfo($"TradeServe client: decline pressed on native screen (counter {idx})");
                    t.SendOpFor(OpDecline, idx, 0f, "declining...");
                }
            }
            return true;
        }

        /// <summary>Client: "Let Me Think" = close without answering; the offer stays
        /// live on the host. Vanilla's close path restores the local UI and releases the
        /// carrier, so allow the original method to run.</summary>
        public static bool ClientLetMeThinkPrefix(CustomerTradeCardScreen __instance)
        {
            return true;
        }

        /// <summary>Client: the player-restore half of Customer.OnPressStopInteract
        /// (Customer.cs:255-267) without the shop-AI tail - the carrier puppet is
        /// deactivated, so DetermineShopAction/StartCoroutine would throw on it.</summary>
        public static bool ClientStopInteractPrefix(Customer __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            // S3: the puppet-data clears used to run OUTSIDE the try below, so a throw in
            // the UI-restore block could skip the "_pendingCounter = -1" at the tail and
            // leave a half-closed screen wedged. Move them inside a single guarded block
            // whose finally ALWAYS clears _pendingCounter.
            try
            {
                FiTradeData?.SetValue(__instance, null);
                FiPausing?.SetValue(__instance, false);
                RestorePlayerFromUiMode();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe client: UI restore: " + e.Message); }
            finally
            {
                if (_live != null)
                {
                    int counter = _live._pendingCounter;
                    _live._pendingCounter = -1;
                    if (counter >= 0)
                        _live.ReleaseCarrier(counter);
                }
            }
            return false;
        }

        /// <summary>Client: the player-restore half of Customer.OnPressStopInteract
        /// (Customer.cs:255-267) - exit worker/UI interaction mode, unfreeze the walker,
        /// and restore the tooltip/next-day indicator/tutorial GameUI. Factored out (S2)
        /// so the S2 hard-teardown path can reuse the exact same restore the normal
        /// close chain runs. Caller wraps this in its own try/catch.</summary>
        private static void RestorePlayerFromUiMode()
        {
            var ipc = _live != null ? _live.Ipc() : null;
            if (ipc != null)
            {
                ipc.ExitWorkerInteractMode();
                ipc.StopAimLookAt();
                if (ipc.m_WalkerCtrl != null)
                    ipc.m_WalkerCtrl.SetStopMovement(isStop: false);
                ipc.ExitUIMode();
            }
            GameUIScreen.ResetToolTipVisibility();
            GameUIScreen.ResetEnterGoNextDayIndicatorVisible();
            TutorialManager.SetGameUIVisible(isVisible: true);
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
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }

        // ---------------- shared helpers ----------------

        private ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        private CustomerManager Cm()
        {
            if (_cm == null)
                _cm = UnityEngine.Object.FindObjectOfType<CustomerManager>();
            return _cm;
        }

        private InteractionPlayerController Ipc()
        {
            if (_ipc == null)
                _ipc = UnityEngine.Object.FindObjectOfType<InteractionPlayerController>();
            return _ipc;
        }

        private static string CardName(CardData c)
        {
            if (c == null)
                return "a card";
            string name = null;
            try
            {
                // GetMonsterData is NOT EPL-aware for third-party callers: EPL redirects the
                // game's own CALL SITES with a transpiler rather than patching the method, so a
                // modded expansion's card runs the ORIGINAL body here and comes back null - or,
                // worse, as the WRONG vanilla monster's data, because modded cards are numbered
                // as small ordinals that collide with vanilla ids. Only trust it inside the
                // vanilla expansion range; anything at or past MAX falls through to the numeric
                // form below, which is at least honest.
                if ((int)c.expansionType < (int)ECardExpansionType.MAX)
                {
                    var md = InventoryBase.GetMonsterData(c.monsterType);
                    if (md != null)
                        name = md.GetName();
                }
            }
            catch (System.Exception e) { Swallow.Log(e); }
            // The MODDED fallback must NOT be monsterType.ToString(): modded cards are small
            // ordinals, so 1..122 print a colliding VANILLA member name - a confidently wrong
            // card name in the trade prompt. Expansion#N is honest across the whole range.
            // A vanilla expansion whose GetMonsterData came back null keeps the old text.
            if (string.IsNullOrEmpty(name))
                name = (int)c.expansionType < (int)ECardExpansionType.MAX
                    ? c.monsterType.ToString()
                    : c.expansionType + "#" + (int)c.monsterType;
            if (c.isFoil)
                name += " (foil)";
            // S5: with Grading Overhaul installed, cardGrade is an ENCODED int (e.g.
            // 370009134) packing company+grade+cert - printing it raw showed the guest a
            // ten-digit "grade". Decode to the real 1-10 for display. Actual() is identity
            // for bare 1-10 (encoded <= 10 passes straight through), so ungraded/plain
            // grades still render correctly whether or not GO is present.
            if (c.cardGrade > 0)
            {
                int g = Util.GradingInterop.Present ? Util.GradingInterop.Actual(c.cardGrade) : c.cardGrade;
                if (g > 0)
                    name += $" [grade {g}]";
            }
            return name;
        }

        private static string Price(float p)
        {
            try
            {
                return GameInstance.GetPriceString(p);
            }
            catch (System.Exception e) { Swallow.Log(e); return "$" + p.ToString("F2"); }
        }

        private static int CardHash(CardData c)
        {
            if (c == null)
                return 0;
            int hash = 17;
            hash = hash * 31 + (int)c.monsterType;
            hash = hash * 31 + (int)c.expansionType;
            hash = hash * 31 + (int)c.borderType;
            hash = hash * 31 + ((c.isFoil ? 1 : 0) | (c.isDestiny ? 2 : 0));
            hash = hash * 31 + c.cardGrade;
            return hash;
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
                var cm = Cm();
                var sm = Sm();
                if (cm == null || sm == null)
                    return;

                _hostBuf.Clear();
                var customers = cm.GetCustomerList();
                for (int i = 0; i < customers.Count && _hostBuf.Count < MaxOffers; i++)
                {
                    var cust = customers[i];
                    if (cust == null || !cust.m_IsActive)
                        continue;
                    if (cust.m_CurrentState != ECustomerState.WaitingToTradeCard)
                        continue;

                    var counter = FiTradeCounter?.GetValue(cust) as InteractableCashierCounter;
                    if (counter == null)
                        continue;
                    int idx = sm.m_CashierCounterList.IndexOf(counter);
                    if (idx < 0 || idx > 250)
                        continue;

                    // S4: pause THIS customer's patience while a guest holds the trade screen
                    // for it. Vanilla only stops m_Timer from advancing when the HOST-local
                    // m_IsPlayerTrading is true (Customer.Update WaitingToTradeCard branch,
                    // decompiled/Customer.cs:3641-3644); a guest's screen sets no such flag,
                    // so the timer kept climbing and the customer force-left at 60s
                    // (Customer.cs:3645) mid-interaction. The guest heartbeats an OpScreen
                    // claim for its open counter every ~2s (_guestClaims, per counter);
                    // while that claim is FRESH (< 5s, same staleness the ClientTradeBlockPrefix
                    // claim consumer uses), reset ONLY this customer's m_Timer to 0f each tick -
                    // mirroring vanilla's m_IsPlayerTrading pause. If the guest crashes or
                    // disconnects the claim goes stale, the reset stops, and the timer resumes.
                    if (_guestClaims.TryGetValue(idx, out var claim)
                        && Time.realtimeSinceStartupAsDouble - claim.Time < 5.0)
                        FiCustTimer?.SetValue(cust, 0f);

                    var data = FiTradeData?.GetValue(cust) as CustomerTradeData;
                    if (data == null)
                        data = PreRoll(cm, cust);

                    float waited = FiCustTimer?.GetValue(cust) is float t ? t : 0f;
                    // "known" also demands the cards the wire format will write exist
                    // (a null CardData must never reach Msg.WriteCard)
                    bool known = data != null && data.m_CardData_L != null
                        && (!data.m_IsTrading || data.m_CardData_R != null);
                    if (!known && _unknownLogged.Add(cust.GetInstanceID()))
                        CoopPlugin.Log.LogInfo($"TradeServe host: offer at counter {idx} broadcast as host-only (no pre-rolled data yet)");
                    var offer = new Offer
                    {
                        CounterIdx = (byte)idx,
                        Known = known,
                        Remaining = Mathf.Clamp(VanillaWait - waited, 0f, VanillaWait),
                        CustomerIndex = (ushort)i,
                        CustomerGeneration = NpcSync.GetCustomerGeneration(cust),
                        Position = cust.transform.position,
                        Yaw = cust.transform.eulerAngles.y,
                    };
                    if (known)
                    {
                        offer.Trading = data.m_IsTrading;
                        offer.CardL = data.m_CardData_L;
                        offer.CardR = data.m_CardData_R;
                        offer.Price = data.m_SellCardAskPrice;
                        offer.PriceSet = data.m_PriceSet;
                        offer.LastPriceSet = data.m_LastPriceSet;
                        offer.MaxDeclineCount = data.m_MaxDeclineCount;
                        offer.DeclineCount = data.m_DeclineCount;
                    }
                    _hostBuf.Add(offer);
                }

                // hash skips Remaining (it changes every tick); the heal broadcast
                // keeps the client's local countdown honest enough
                int hash = 17;
                hash = hash * 31 + _resultSeq; // a fresh op result always broadcasts
                hash = hash * 31 + _resultCounter;
                hash = hash * 31 + _resultOutcome;
                for (int i = 0; i < _hostBuf.Count; i++)
                {
                    var o = _hostBuf[i];
                    hash = hash * 31 + o.CounterIdx;
                    hash = hash * 31 + ((o.Known ? 1 : 0) | (o.Trading ? 2 : 0));
                    hash = hash * 31 + CardHash(o.CardL);
                    hash = hash * 31 + CardHash(o.CardR);
                    hash = hash * 31 + (int)(o.Price * 100f);
                    hash = hash * 31 + (int)(o.PriceSet * 100f);
                    hash = hash * 31 + (int)(o.LastPriceSet * 100f);
                    hash = hash * 31 + o.MaxDeclineCount;
                    hash = hash * 31 + o.DeclineCount;
                    hash = hash * 31 + o.CustomerIndex;
                    hash = hash * 31 + o.CustomerGeneration;
                }

                bool changed = hash != _lastHash;
                if (!_gate.ShouldSend(hash))
                    return;
                _lastHash = hash;
                if (changed) // real change (offers moved or a result landed) - keep the pipeline loud
                {
                    string summary = "";
                    for (int i = 0; i < _hostBuf.Count; i++)
                    {
                        var o = _hostBuf[i];
                        summary += $" [{o.CounterIdx}:{(!o.Known ? "unknown" : o.Trading ? "trade " + CardName(o.CardL) : "sell " + CardName(o.CardL) + " @ " + Price(o.Price))}]";
                    }
                    CoopPlugin.Log.LogInfo($"TradeServe host: broadcasting {_hostBuf.Count} offer(s){summary}");
                }
                BroadcastState?.Invoke(BuildState());
            });
        }

        /// <summary>Host: roll the customer's offer WITHOUT opening the screen, using the
        /// game's own "Let Me Think" persistence slot. SetCustomer is safe on the closed
        /// screen: it only writes inspector-assigned UI children (legal while inactive;
        /// Animation.Play on an inactive object is a no-op) and rolls the offer from
        /// CPlayerData. The stored CustomerTradeData is replayed verbatim by any later
        /// SetCustomer call - so the host clicking the customer sees THIS same offer.</summary>
        /// <summary>Freeze a snapshot of a game CardData: the trade screen reuses one
        /// object across customers, so anything we keep past this frame must be a copy.</summary>
        private static CardData CopyCard(CardData src)
        {
            if (src == null)
                return null;
            var c = new CardData();
            try
            {
                c.CopyData(src);
            }
            catch
            {
                c.expansionType = src.expansionType;
                c.monsterType = src.monsterType;
                c.borderType = src.borderType;
                c.isFoil = src.isFoil;
                c.isDestiny = src.isDestiny;
                c.isChampionCard = src.isChampionCard;
                c.isNew = src.isNew;
                c.cardGrade = src.cardGrade;
                c.gradedCardIndex = src.gradedCardIndex;
            }
            return c;
        }

        private CustomerTradeData PreRoll(CustomerManager cm, Customer cust)
        {
            // without the storage field every roll would be lost and re-rolled (and
            // re-broadcast) each tick - better to leave the offer host-served
            if (FiTradeData == null)
                return null;
            var screen = cm.m_CustomerTradeCardScreen;
            // never stomp a live trade the host is running in the real UI
            if (screen == null || cm.m_IsPlayerTrading || screen.IsScreenOpened())
                return null;
            int id = cust.GetInstanceID();
            // transient backoff, NOT a permanent blacklist: one flaky roll used to mark the
            // offer host-only for the customer's whole life (guest could never answer it)
            if (_preRollFailUntil.TryGetValue(id, out var until) && Time.time < until)
                return null;
            try
            {
                screen.SetCustomer(cust, null); // sets m_IsPlayerTrading = true itself
                var data = new CustomerTradeData
                {
                    m_IsTrading = FiScrTrading?.GetValue(screen) is bool tr && tr,
                    m_PriceSet = FiScrPriceSet?.GetValue(screen) is float ps ? ps : 0f,
                    m_LastPriceSet = FiScrLastPrice?.GetValue(screen) is float lp ? lp : 0f,
                    m_SellCardAskPrice = FiScrAsk?.GetValue(screen) is float ap ? ap : 0f,
                    m_SellCardMarketPrice = FiScrMarket?.GetValue(screen) is float mp ? mp : 0f,
                    m_MaxDeclineCount = FiScrMaxDecline?.GetValue(screen) is int md ? md : 0,
                    m_DeclineCount = FiScrDecline?.GetValue(screen) is int dc ? dc : 0,
                    // DEEP COPY, never the screen's reference: the game reuses one
                    // CardData object across customers (hence CardData.CopyData), so a
                    // stored reference gets mutated by the NEXT pre-roll - corrupting
                    // the grade (fake graded card) and the monsterType (wrong name on
                    // wrong art). Freezing a copy is the fix for both field reports.
                    m_CardData_L = CopyCard(FiScrCardL?.GetValue(screen) as CardData),
                    m_CardData_R = CopyCard(FiScrCardR?.GetValue(screen) as CardData),
                };
                if (data.m_CardData_L == null)
                {
                    // no poison: the card can be momentarily null during pooled activation;
                    // just retry next tick instead of marking the offer host-only forever
                    CoopPlugin.Log.LogWarning("TradeServe: pre-roll produced no card; will retry next tick");
                    return null;
                }
                FiTradeData?.SetValue(cust, data);
                CoopPlugin.Log.LogInfo($"TradeServe host: pre-rolled {(data.m_IsTrading ? $"trade {CardName(data.m_CardData_L)} for {CardName(data.m_CardData_R)}" : $"sell-in {CardName(data.m_CardData_L)} @ {Price(data.m_SellCardAskPrice)}")}");
                return data;
            }
            catch (Exception e)
            {
                _preRollFailUntil[id] = Time.time + 5f; // back off 5s, then retry
                CoopPlugin.Log.LogWarning("TradeServe pre-roll: " + e.Message);
                return null;
            }
            finally
            {
                try
                {
                    cm.m_IsPlayerTrading = false;
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
        }

        private TradeStateMessage BuildState()
        {
            // both-players-on-one-customer guard, host half: while the HOST has the
            // trade screen up (or is mid-trade), joiners must not open theirs
            bool hostBusy = false;
            try
            {
                var cm = Cm();
                hostBusy = cm != null && (cm.m_IsPlayerTrading
                    || (cm.m_CustomerTradeCardScreen != null && cm.m_CustomerTradeCardScreen.IsScreenOpened()));
            }
            catch (System.Exception e) { Swallow.Log(e); }
            var msg = new TradeStateMessage
            {
                HostBusy = hostBusy,
                ResultSeq = _resultSeq,
                Result = _result ?? "",
                ResultCounter = _resultCounter,
                ResultOutcome = _resultOutcome,
            };
            for (int i = 0; i < _hostBuf.Count; i++)
            {
                var o = _hostBuf[i];
                msg.Offers.Add(new TradeOfferEntry
                {
                    CounterIdx = o.CounterIdx,
                    Known = o.Known,
                    Trading = o.Trading,
                    CustomerIndex = o.CustomerIndex,
                    CustomerGeneration = o.CustomerGeneration,
                    Position = o.Position,
                    Yaw = o.Yaw,
                    PriceSet = o.PriceSet,
                    LastPriceSet = o.LastPriceSet,
                    MaxDeclineCount = o.MaxDeclineCount,
                    DeclineCount = o.DeclineCount,
                    CardL = o.Known ? o.CardL : null,
                    CardR = (o.Known && o.Trading) ? o.CardR : null,
                    Price = (o.Known && !o.Trading) ? o.Price : 0f,
                    Remaining = o.Remaining,
                });
            }
            return msg;
        }

        /// <summary>Host: publish a result. Increments ResultSeq so the change gate always
        /// broadcasts it, and records the counter + outcome so the guest holding that
        /// counter's screen can replay the confirmed outcome on the native UI.</summary>
        private void Result(string text, byte outcome = OutcomeBlocked, int counter = -1)
        {
            _resultSeq++;
            _result = text;
            _resultCounter = counter >= 0 && counter < NoCounter ? (byte)counter : NoCounter;
            _resultOutcome = _resultCounter == NoCounter ? OutcomeNone : outcome;
            // Trade outcomes are handled by the native trade screen.  Do not surface
            // the old co-op status-line hints (for example, "the customer moves on").
            CoopPlugin.Log.LogInfo("TradeServe: " + text);
        }

        /// <summary>Host: a joiner answered a counter offer (accept carries the price
        /// the joiner's screen had set; decline/trade ops carry 0).</summary>
        public void HostApplyOp(TradeOpMessage message, int connId)
        {
            byte op = message.Op;
            int idx = message.CounterIdx;
            float price = message.Price;
            if (op == OpScreen)
            {
                var sm = Sm();
                if (sm == null || idx < 0 || idx >= sm.m_CashierCounterList.Count)
                {
                    CoopPlugin.Log.LogError($"TradeServe host: rejected invalid screen claim counter {idx} from conn {connId}");
                    return;
                }
                // joiner's screen-open claim (renewed ~2s; expiry is time-based so no
                // close message is ever needed). Not logged - it's a heartbeat.
                _guestClaims[idx] = new GuestClaim { Time = Time.realtimeSinceStartupAsDouble, ConnId = connId };
                return;
            }
            if (!_guestClaims.TryGetValue(idx, out var claim) || claim.ConnId != connId)
            {
                CoopPlugin.Log.LogWarning($"TradeServe host: rejected answer without claim counter={idx} conn={connId}");
                return;
            }
            CoopPlugin.Log.LogInfo($"TradeServe host: received {(op == OpAccept ? "accept" : op == OpDecline ? "decline" : "op " + op)} @ counter {idx}, price {price:F2}");
            Guarded("apply", () => HostApplyOpInner(op, idx, price));
        }

        /// <summary>Host: a guest disconnected - drop any trade-screen claim it held, so the
        /// host is not blocked by a dead claim until the 5s expiry (and the customer's
        /// patience timer resumes immediately).</summary>
        public void HostReleaseConn(int connId)
        {
            var drop = new List<int>();
            foreach (var kv in _guestClaims)
                if (kv.Value.ConnId == connId)
                    drop.Add(kv.Key);
            if (drop.Count == 0)
                return;
            for (int i = 0; i < drop.Count; i++)
                _guestClaims.Remove(drop[i]);
            CoopPlugin.Log.LogInfo($"TradeServe host: released {drop.Count} trade claim(s) for departing conn {connId}");
        }

        private void HostApplyOpInner(byte op, int idx, float price)
        {
            _guestClaims.Remove(idx); // an answer means the joiner's screen at THIS counter is closing
            var cm = Cm();
            var sm = Sm();
            if (cm == null || sm == null || idx < 0 || idx >= sm.m_CashierCounterList.Count)
            {
                Result("no counter there", OutcomeBlocked, idx);
                return;
            }
            var counter = sm.m_CashierCounterList[idx];

            // validate the customer still stands there waiting
            Customer cust = null;
            var customers = cm.GetCustomerList();
            for (int i = 0; i < customers.Count; i++)
            {
                var c = customers[i];
                if (c != null && c.m_IsActive && c.m_CurrentState == ECustomerState.WaitingToTradeCard
                    && ReferenceEquals(FiTradeCounter?.GetValue(c), counter))
                {
                    cust = c;
                    break;
                }
            }
            if (counter == null || cust == null)
            {
                Result("the customer already left", OutcomeWalkOff, idx);
                return;
            }
            if (cm.m_IsPlayerTrading)
            {
                Result("the host is talking to that customer right now", OutcomeBlocked, idx);
                return;
            }

            if (op == OpDecline)
            {
                _preRollFailUntil.Remove(cust.GetInstanceID()); // pooled id reuse must not carry backoff
                FinishCustomer(cust, counter);
                Result("trade declined - the customer moves on", OutcomeWalkOff, idx);
                return;
            }
            if (op != OpAccept)
                return;

            var data = FiTradeData?.GetValue(cust) as CustomerTradeData;
            if (data == null)
                data = PreRoll(cm, cust);
            if (data == null || data.m_CardData_L == null)
            {
                Result("couldn't read the offer - the host must serve this one", OutcomeBlocked, idx);
                return;
            }

            // a garbage/absent price means "accept at the asking price" (the prompt+key
            // fallback path); the native screen always forwards its real price field
            float bid = (float.IsNaN(price) || price < 0f) ? data.m_SellCardAskPrice : price;

            // pre-checks mirror OnPressAccept's own failure branches so we can report
            // cleanly instead of letting a host-side popup swallow the click
            if (data.m_IsTrading)
            {
                var r = data.m_CardData_R;
                bool haveR = r != null && (r.cardGrade == 0
                    ? CPlayerData.GetCardAmount(r) > 0
                    : CPlayerData.HasGradedCardInAlbum(r));
                if (!haveR)
                {
                    Result($"the binder no longer has {CardName(r)} to trade", OutcomeMissingCard, idx);
                    return;
                }
            }
            else if (CPlayerData.m_CoinAmountDouble < (double)bid)
            {
                Result($"not enough money to pay {Price(bid)}", OutcomeInsufficientMoney, idx);
                return;
            }

            // headless vanilla accept: SetCustomer replays the stored offer onto the
            // CLOSED screen, we pin the price at the joiner's FORWARDED bid (>= ask -
            // 0.01 forces the 100% branch; a lowball runs the vanilla haggle RNG),
            // then the screen's own OnPressAccept moves the money
            // (CEventPlayer_ReduceCoin) and cards (AddCard/ReduceCard) so every
            // existing econ/CardDelta mirror carries the change. No UI ever opens.
            var screen = cm.m_CustomerTradeCardScreen;
            if (screen == null || screen.IsScreenOpened())
            {
                Result("the host has the trade screen open", OutcomeBlocked, idx);
                return;
            }
            float preAsk = data.m_SellCardAskPrice;
            int preDecline = data.m_DeclineCount;
            bool accepted;
            float postAsk;
            int postDecline;
            try
            {
                screen.SetCustomer(cust, data);
                if (!data.m_IsTrading)
                    FiScrPriceSet?.SetValue(screen, bid);
                CoopPlugin.Log.LogInfo($"TradeServe host: applying vanilla accept ({(data.m_IsTrading ? "trade" : $"bid {Price(bid)} vs ask {Price(preAsk)}")})");
                screen.OnPressAccept();
                accepted = FiScrAccepted?.GetValue(screen) is bool ok && ok;
                postAsk = FiScrAsk?.GetValue(screen) is float pa ? pa : preAsk;
                postDecline = FiScrDecline?.GetValue(screen) is int pd ? pd : preDecline;
                if (!accepted)
                {
                    // persist the haggled screen state back into the vanilla "Let Me
                    // Think" slot (OnPressLetMeThink's recipe) so the next attempt and
                    // the broadcast digest both see the moved ask/decline counters
                    data.m_PriceSet = FiScrPriceSet?.GetValue(screen) is float ps ? ps : bid;
                    data.m_LastPriceSet = FiScrLastPrice?.GetValue(screen) is float lp ? lp : bid;
                    data.m_SellCardAskPrice = postAsk;
                    data.m_MaxDeclineCount = FiScrMaxDecline?.GetValue(screen) is int md ? md : data.m_MaxDeclineCount;
                    data.m_DeclineCount = postDecline;
                    FiTradeData?.SetValue(cust, data);
                }
            }
            finally
            {
                try
                {
                    cm.m_IsPlayerTrading = false;
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }

            if (accepted)
            {
                _preRollFailUntil.Remove(cust.GetInstanceID()); // pooled id reuse must not carry backoff
                FinishCustomer(cust, counter);
                Result(data.m_IsTrading
                    ? $"traded {CardName(data.m_CardData_R)} for {CardName(data.m_CardData_L)}"
                    : $"bought {CardName(data.m_CardData_L)} for {Price(bid)}", OutcomeAccepted, idx);
                return;
            }
            if (data.m_IsTrading)
            {
                // both trading branches either accept or hit the have-no-card popup,
                // which the pre-check above already covers - belt-and-braces report
                Result("the trade fell through - the host must serve this one", OutcomeBlocked, idx);
                return;
            }
            if (postDecline == preDecline)
            {
                // neither the haggle nor the plain-refusal branch ran: vanilla's final
                // else (out of patience, its CloseScreen no-ops on the closed screen) -
                // the customer walks, exactly like its own resolution would
                _preRollFailUntil.Remove(cust.GetInstanceID()); // pooled id reuse must not carry backoff
                FinishCustomer(cust, counter);
                Result("the customer lost patience and left", OutcomeWalkOff, idx);
                return;
            }
            bool askMoved = Mathf.Abs(postAsk - preAsk) > 0.005f;
            Result(askMoved
                ? $"they refuse {Price(bid)} - now asking {Price(postAsk)}"
                : $"they refuse {Price(bid)} - try higher",
                askMoved ? OutcomeRefusedAsk : OutcomeRefusedSame, idx);
        }

        /// <summary>Host: resolve the waiting customer exactly like the vanilla 60s
        /// timeout (Customer.cs:3639-3660) - the only UI-free resolution path in the
        /// game. The customer stows the exclamation mark, frees the counter's trade
        /// slot and goes back to shopping.</summary>
        private static void FinishCustomer(Customer cust, InteractableCashierCounter counter)
        {
            FiCustTimer?.SetValue(cust, 0f);
            FiCustTimerMax?.SetValue(cust, 0f);
            FiTradeData?.SetValue(cust, null);
            FiPausing?.SetValue(cust, false);
            try
            {
                if (cust.m_ExclaimationMesh != null)
                    cust.m_ExclaimationMesh.SetActive(false);
                if (cust.m_InteractCollider != null)
                    cust.m_InteractCollider.SetActive(false);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            FiHasTraded?.SetValue(cust, true);
            try
            {
                counter?.CustomerFinishTradingCard();
            }
            catch (System.Exception e) { Swallow.Log(e); }
            FiTradeCounter?.SetValue(cust, null);
            try
            {
                MiDetermine?.Invoke(cust, null);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe finish: " + e.Message); }
        }

        // ---------------- client ----------------

        public void ClientApplyState(TradeStateMessage message)
        {
            _hostBusy = message.HostBusy;
            byte seq = message.ResultSeq;
            string result = message.Result;
            int count = message.Offers.Count;
            _offers.Clear();
            for (int i = 0; i < count; i++)
            {
                var e = message.Offers[i];
                var o = new Offer
                {
                    CounterIdx = e.CounterIdx,
                    Known = e.Known,
                    Trading = e.Trading,
                    CustomerIndex = e.CustomerIndex,
                    CustomerGeneration = e.CustomerGeneration,
                    Position = e.Position,
                    Yaw = e.Yaw,
                    PriceSet = e.PriceSet,
                    LastPriceSet = e.LastPriceSet,
                    MaxDeclineCount = e.MaxDeclineCount,
                    DeclineCount = e.DeclineCount,
                    CardL = e.Known ? e.CardL : null,
                    CardR = (e.Known && e.Trading) ? e.CardR : null,
                    Price = (e.Known && !e.Trading) ? e.Price : 0f,
                    Remaining = e.Remaining,
                };
                _offers[o.CounterIdx] = o;
                if (o.Known)
                    PrepareCarrier(o);
                else
                    ReleaseCarrier(o.CounterIdx);
            }
            var staleCarriers = new List<int>();
            foreach (var kv in _carriers)
                if (!_offers.ContainsKey(kv.Key) && kv.Key != _pendingCounter)
                    staleCarriers.Add(kv.Key);
            for (int i = 0; i < staleCarriers.Count; i++)
                ReleaseCarrier(staleCarriers[i]);
            _staleTimer = 0f;
            if (count != _lastOfferCount) // change-only, so the 6s heal doesn't spam
            {
                _lastOfferCount = count;
                CoopPlugin.Log.LogInfo($"TradeServe client: state received, {count} live offer(s)");
            }

            if (seq != _seenSeq)
            {
                _seenSeq = seq;
                byte outcome = message.ResultOutcome;
                byte rc = message.ResultCounter;
                if (rc != NoCounter && rc == _pendingCounter)
                {
                    // the host confirmed what happened at the counter whose screen we hold:
                    // drive the REAL screen to the state single-player would have reached
                    ApplyOutcome(outcome, rc);
                    if (outcome == OutcomeBlocked && result.Length > 0 && CoopCore.Instance != null)
                    {
                        CoopCore.Instance.RegisterLine = result;
                        CoopCore.Instance.RegisterLineTimer = 4f;
                    }
                }
                else if (result.Length > 0 && CoopCore.Instance != null
                    && (outcome == OutcomeBlocked || outcome == OutcomeNone))
                {
                    CoopCore.Instance.RegisterLine = result;
                    CoopCore.Instance.RegisterLineTimer = 4f;
                }
            }
        }

        /// <summary>Client: replay the host-confirmed outcome on the open native screen.
        /// The host owns the haggle RNG and the economy; this only drives the screen's own
        /// objects (text lists, animation, buttons, price labels) to the same state vanilla
        /// would reach, so the guest sees the single-player flow.</summary>
        private void ApplyOutcome(byte outcome, int counter)
        {
            var cm = Cm();
            var screen = cm != null ? cm.m_CustomerTradeCardScreen : null;
            if (screen == null)
                return;
            if (!screen.IsScreenOpened())
            {
                // e.g. the guest already pressed Done/Esc; there is nothing left to present
                _awaitingResult = false;
                return;
            }
            switch (outcome)
            {
                case OutcomeAccepted:
                    PresentAccepted(screen);
                    break;
                case OutcomeRefusedAsk:
                    _awaitingResult = false;
                    if (_offers.TryGetValue(counter, out var askOffer))
                        PresentRefusal(screen, askOffer, haggle: true);
                    break;
                case OutcomeRefusedSame:
                    _awaitingResult = false;
                    if (_offers.TryGetValue(counter, out var sameOffer))
                        PresentRefusal(screen, sameOffer, haggle: false);
                    break;
                case OutcomeWalkOff:
                    CloseFromRemote(screen);
                    break;
                case OutcomeInsufficientMoney:
                    _awaitingResult = false;
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.Money);
                    break;
                case OutcomeMissingCard:
                    _awaitingResult = false;
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.DontHaveTheCard);
                    break;
                default: // Blocked/None: keep the screen open so the guest can try again
                    _awaitingResult = false;
                    break;
            }
        }

        /// <summary>Client: the host accepted. Reproduce the vanilla accepted presentation
        /// (top text + animation, Accept/Cancel/LetMeThink hidden, Done shown, m_HasAccepted
        /// set) so the guest's Done press closes through the real OnPressAccept path. No
        /// economy runs here - the host already applied it and the existing mirrors carry it.</summary>
        private void PresentAccepted(CustomerTradeCardScreen screen)
        {
            _awaitingResult = false;
            _acceptedShown = true;
            try
            {
                var list = screen.m_CustomerAcceptedTextList;
                string key = list != null && list.Count > 0 ? list[UnityEngine.Random.Range(0, list.Count)] : null;
                if (key != null && screen.m_CustomerTopText != null)
                    screen.m_CustomerTopText.text = LocalizationManager.GetTranslation("CustomerTrade/" + key);
                if (screen.m_CustomerTopTextAnim != null)
                {
                    screen.m_CustomerTopTextAnim.Rewind();
                    screen.m_CustomerTopTextAnim.Play();
                }
                if (screen.m_AcceptBtn != null)
                    screen.m_AcceptBtn.SetActive(false);
                if (screen.m_CancelBtn != null)
                    screen.m_CancelBtn.SetActive(false);
                if (screen.m_LetMeThinkBtn != null)
                    screen.m_LetMeThinkBtn.SetActive(false);
                if (screen.m_DoneBtn != null)
                    screen.m_DoneBtn.SetActive(true);
                FiScrAccepted?.SetValue(screen, true);
                CoopPlugin.Log.LogInfo("TradeServe client: host accepted - showing the vanilla accepted state (Done)");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("TradeServe client: accepted presentation: " + e.Message);
            }
        }

        /// <summary>Client: the host refused the bid. Reproduce the vanilla feedback for the
        /// branch the host took: apply its post-haggle ask/decline values and show the
        /// haggle (ask moved) or decline (ask held) text, so the guest can bid again.</summary>
        private void PresentRefusal(CustomerTradeCardScreen screen, Offer offer, bool haggle)
        {
            _awaitingResult = false;
            try
            {
                float ask = offer.Known && !offer.Trading
                    ? offer.Price
                    : (FiScrAsk?.GetValue(screen) is float fa ? fa : 0f);
                FiScrAsk?.SetValue(screen, ask);
                FiScrLastPrice?.SetValue(screen, offer.LastPriceSet);
                FiScrDecline?.SetValue(screen, offer.DeclineCount);
                FiScrMaxDecline?.SetValue(screen, offer.MaxDeclineCount);
                float market = FiScrMarket?.GetValue(screen) is float mp ? mp : 0f;
                if (haggle && screen.m_MarketPrice_L != null)
                {
                    // exactly the two lines vanilla rewrites in its haggle branch
                    screen.m_MarketPrice_L.text = LocalizationManager.GetTranslation("Ask Price")
                        + " : " + Price(ask) + "\n" + LocalizationManager.GetTranslation("Market Price")
                        + " : " + Price(market);
                }
                var list = haggle ? screen.m_CustomerHaggleTextList : screen.m_CustomerDeclineTextList;
                string key = list != null && list.Count > 0 ? list[UnityEngine.Random.Range(0, list.Count)] : null;
                if (key != null && screen.m_CustomerTopText != null)
                    screen.m_CustomerTopText.text = LocalizationManager.GetTranslation("CustomerTrade/" + key);
                if (screen.m_CustomerTopTextAnim != null)
                {
                    screen.m_CustomerTopTextAnim.Rewind();
                    screen.m_CustomerTopTextAnim.Play();
                }
                CoopPlugin.Log.LogInfo($"TradeServe client: host {(haggle ? "haggled" : "declined")} - showing the vanilla feedback (ask {Price(ask)})");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("TradeServe client: refusal presentation: " + e.Message);
            }
        }

        private void PrepareCarrier(Offer offer)
        {
            var cm = Cm();
            if (cm == null)
                return;
            var list = cm.GetCustomerList();
            if (offer.CustomerIndex >= list.Count || list[offer.CustomerIndex] == null)
                return;
            var carrier = list[offer.CustomerIndex];
            if (_carriers.TryGetValue(offer.CounterIdx, out var old) && !ReferenceEquals(old, carrier))
                ReleaseCarrier(offer.CounterIdx);
            _carriers[offer.CounterIdx] = carrier;
            _carrierSource[offer.CounterIdx] = offer.CustomerIndex;
            _carrierGeneration[offer.CounterIdx] = offer.CustomerGeneration;
            bool carrierVisualsCaptured = _carrierRenderers.ContainsKey(offer.CounterIdx);
            RegisterSync.AllowClientCustomerLifecycle = true;
            try
            {
                carrier.ActivateCustomer(false, false);
            }
            finally { RegisterSync.AllowClientCustomerLifecycle = false; }
            var sm = Sm();
            var counter = sm != null && offer.CounterIdx < sm.m_CashierCounterList.Count
                ? sm.m_CashierCounterList[offer.CounterIdx] : null;
            if (counter == null)
                return;
            FiTradeCounter?.SetValue(carrier, counter);
            FiTradeData?.SetValue(carrier, new CustomerTradeData
            {
                m_IsTrading = offer.Trading,
                m_CardData_L = offer.CardL,
                m_CardData_R = offer.CardR,
                m_SellCardAskPrice = offer.Price,
                m_PriceSet = offer.Trading ? 0f : offer.PriceSet,
                m_LastPriceSet = offer.Trading ? 0f : offer.LastPriceSet,
                m_MaxDeclineCount = offer.MaxDeclineCount,
                m_DeclineCount = offer.DeclineCount,
            });
            carrier.transform.position = offer.Position;
            carrier.transform.rotation = Quaternion.Euler(0f, offer.Yaw, 0f);
            carrier.gameObject.SetActive(true);
            if (carrier.m_ExclaimationMesh != null)
                carrier.m_ExclaimationMesh.SetActive(true);
            if (carrier.m_InteractCollider != null)
                carrier.m_InteractCollider.SetActive(true);
            if (!carrierVisualsCaptured)
            {
                var renderers = carrier.GetComponentsInChildren<Renderer>(true);
                var states = new bool[renderers.Length];
                for (int i = 0; i < renderers.Length; i++)
                {
                    states[i] = renderers[i] != null && renderers[i].enabled;
                    if (renderers[i] != null)
                        renderers[i].enabled = false;
                }
                _carrierRenderers[offer.CounterIdx] = renderers;
                _carrierRendererStates[offer.CounterIdx] = states;
            }
            else if (_carrierRenderers.TryGetValue(offer.CounterIdx, out var existingRenderers))
            {
                for (int i = 0; i < existingRenderers.Length; i++)
                    if (existingRenderers[i] != null)
                        existingRenderers[i].enabled = false;
            }
            NpcSync.SuppressedCustomer.Add(offer.CustomerIndex);
            NpcSync.AttachExistingCustomer(offer.CustomerIndex, offer.CustomerGeneration, carrier, keepPuppetVisible: true);
        }

        private void ReleaseCarrier(int counterIdx)
        {
            if (!_carriers.TryGetValue(counterIdx, out var carrier))
                return;
            if (_carrierRenderers.TryGetValue(counterIdx, out var renderers)
                && _carrierRendererStates.TryGetValue(counterIdx, out var states))
            {
                for (int i = 0; i < renderers.Length && i < states.Length; i++)
                    if (renderers[i] != null)
                        renderers[i].enabled = states[i];
            }
            if (_carrierSource.TryGetValue(counterIdx, out var source))
                NpcSync.DetachExistingCustomer(source, carrier);
            carrier.gameObject.SetActive(false);
            _carriers.Remove(counterIdx);
            _carrierSource.Remove(counterIdx);
            _carrierGeneration.Remove(counterIdx);
            _carrierRenderers.Remove(counterIdx);
            _carrierRendererStates.Remove(counterIdx);
        }

        private void TeardownCarriers()
        {
            var keys = new List<int>(_carriers.Keys);
            for (int i = 0; i < keys.Count; i++)
                ReleaseCarrier(keys[i]);
            _carriers.Clear();
            _carrierSource.Clear();
            _carrierGeneration.Clear();
            _carrierRenderers.Clear();
            _carrierRendererStates.Clear();
        }

        /// <summary>True while a trade/sell-in offer is live at this counter.</summary>
        public bool HasOffer(int counterIdx)
        {
            return counterIdx >= 0 && _offers.ContainsKey(counterIdx);
        }

        /// <summary>Client: the joiner's MOVING body. InteractionPlayerController sits on
        /// a stationary manager object whose transform never moves (the frozen-avatar
        /// bug); the walking body is its public m_WalkerCtrl, same as CoopCore's
        /// ResolvePlayer. Measuring reach from ipc.transform was why the old accept/
        /// decline keys never fired: the manager is never within 3.5m of a counter.</summary>
        private Transform PlayerBody()
        {
            if (_playerTf != null)
                return _playerTf;
            var ipc = Ipc();
            if (ipc == null)
                return null;
            _playerTf = ipc.m_WalkerCtrl != null ? ipc.m_WalkerCtrl.transform : ipc.transform;
            return _playerTf;
        }

        /// <summary>Client: send one op for a counter with local feedback. Accept keeps the
        /// offer so ApplyOutcome can read the host's post-haggle fields; decline clears it
        /// optimistically (its vanilla body closes the screen immediately).</summary>
        private bool SendOpFor(byte op, int idx, float price, string line, bool clearOffer = true)
        {
            _opThrottle = 0.5f;
            CoopPlugin.Log.LogInfo($"TradeServe client: sending {(op == OpAccept ? "accept" : "decline")} @ counter {idx}, price {price:F2}");
            if (SendOp == null)
                return false;
            SendOp.Invoke(new TradeOpMessage { Op = op, CounterIdx = (byte)idx, Price = price });
            if (clearOffer)
                _offers.Remove(idx);
            if (!string.IsNullOrEmpty(line) && CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = line;
                CoopCore.Instance.RegisterLineTimer = 2f;
            }
            return true;
        }

        /// <summary>Client (S1): an accept/decline press landed on a screen with no bound
        /// counter (_pendingCounter == -1) - the screen was ORPHANED (the vanish-close race
        /// left it visually open, or a native close swallowed a throw before it could clear
        /// the binding). Rather than silently drop the press, try to rebind to the live
        /// offer at whatever counter the guest is actually standing at. Returns the rebound
        /// counter index (also adopting it as _pendingCounter so the close path is consistent),
        /// or -1 if nothing resolves - in which case the caller lets the vanilla CloseScreen
        /// run so the dead screen goes away.</summary>
        private int RebindOrphanedScreen(string action)
        {
            try
            {
                var body = PlayerBody();
                if (body != null)
                {
                    int near = RegisterServe.FindNearestCounter(body.position, Reach, quiet: true);
                    if (near >= 0 && _offers.TryGetValue(near, out var o) && o.Known)
                    {
                        _pendingCounter = near;
                        CoopPlugin.Log.LogInfo($"TradeServe client: rebound orphaned trade screen to counter {near}");
                        return near;
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe client: rebind: " + e.Message); }
            // no counter to bind and no resolvable offer - this is a genuinely dead screen;
            // WARN (not Info) so the swallowed press is diagnosable, and let the caller's
            // CloseScreen tear it down
            CoopPlugin.Log.LogWarning($"TradeServe client: {action} press had no bound counter and no resolvable offer nearby - closing the dead screen");
            return -1;
        }

        /// <summary>Client per-frame: local countdown/staleness, the native screen's
        /// lifecycle, and the serve/decline keys (KeyDown, TextFieldFocused guard,
        /// short throttle, 3.5m reach from the WALKER body).</summary>
        public void ClientTick(float dt, bool inGame)
        {
            if (_opThrottle > 0f)
                _opThrottle -= dt;

            if (_offers.Count > 0)
            {
                _staleTimer += dt;
                if (_staleTimer > StaleAfter)
                {
                    _offers.Clear();
                }
                else
                {
                    // count down locally; an expired offer means the customer walked off
                    _keyBuf.Clear();
                    foreach (var kv in _offers)
                        _keyBuf.Add(kv.Key);
                    for (int i = 0; i < _keyBuf.Count; i++)
                    {
                        // the counter whose native screen the guest currently holds must NOT
                        // be locally timed out mid-interaction - defer to the host, which
                        // drops the offer from its broadcast when the customer truly leaves
                        if (_keyBuf[i] == _pendingCounter)
                            continue;
                        var o = _offers[_keyBuf[i]];
                        o.Remaining -= dt;
                        if (o.Remaining <= 0f)
                            _offers.Remove(_keyBuf[i]);
                        else
                            _offers[_keyBuf[i]] = o;
                    }
                }
            }

            // while OUR native screen is up its buttons are the input; drive its lifecycle
            // from the host's confirmed state.
            if (_pendingCounter >= 0)
            {
                var cm = Cm();
                var screen = cm != null ? cm.m_CustomerTradeCardScreen : null;
                if (screen == null || !screen.IsScreenOpened())
                {
                    _pendingCounter = -1; // closed by Esc/back/Done; no op = offer stays live
                    _awaitingResult = false;
                    _acceptedShown = false;
                    return;
                }

                // renew the open-screen claim so the host can't open the SAME customer's
                // screen ("trades popping up on both screens" report); renewal-based so any
                // close path (Esc, crash, disconnect) simply lets it expire host-side. Once
                // the host has answered, stop: the customer is resolved and re-claiming a
                // finished counter would only pin it host-side.
                if (!_awaitingResult && !_acceptedShown)
                {
                    _claimTimer += dt;
                    if (_claimTimer >= 2f)
                    {
                        _claimTimer = 0f;
                        int pc = _pendingCounter;
                        SendOp?.Invoke(new TradeOpMessage { Op = OpScreen, CounterIdx = (byte)pc, Price = 0f });
                    }
                }

                // close only if the offer died and we are NOT showing a confirmed accept:
                // after an accept the offer is gone on purpose and Done owns the close
                if (!_acceptedShown && !_offers.ContainsKey(_pendingCounter))
                    CloseFromRemote(screen);
                return;
            }
            _awaitingResult = false;
            _acceptedShown = false;
        }

        /// <summary>Client: tear down the native trade screen exactly like the vanilla close
        /// chain (CloseScreen -> OnCloseScreen -> OnPressStopInteract), with the S2
        /// hard-teardown fallback if a link in that chain threw and left the screen visible.</summary>
        private void CloseFromRemote(CustomerTradeCardScreen screen)
        {
            _awaitingResult = false;
            _acceptedShown = false;
            CoopPlugin.Log.LogInfo("TradeServe client: closing the native trade screen (customer resolved/left)");
            try
            {
                screen.CloseScreen();
            }
            catch (System.Exception e) { Swallow.Log(e); }
            // S2: CloseScreen -> OnCloseScreen -> m_CurrentCustomer.OnPressStopInteract
            // runs BEFORE base.OnCloseScreen (which is what actually hides the screen -
            // UIScreenBase.OnCloseScreen sets m_IsScreenOpen=false + m_ScreenGroup
            // inactive). If any link in that chain threw, the swallow-all catch above
            // ate it and the screen stayed VISIBLE while _pendingCounter got zeroed -
            // the ORPHANED screen at the root of this bug (every later accept/decline
            // hit the idx<0 guard and was dropped). Verify the close actually took;
            // if not, hard-tear-down by mirroring base.OnCloseScreen via reflection
            // and restoring the player ourselves.
            if (screen.IsScreenOpened())
            {
                CoopPlugin.Log.LogWarning("TradeServe client: vanilla close chain left the trade screen open - forcing hard teardown");
                try
                {
                    FiScrIsOpen?.SetValue(screen, false);
                    if (FiScrGroup?.GetValue(screen) is GameObject group)
                        group.SetActive(false);
                    RestorePlayerFromUiMode();
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe client: hard teardown: " + e.Message); }
            }
            _pendingCounter = -1;
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "the customer left";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
        }
    }
}
