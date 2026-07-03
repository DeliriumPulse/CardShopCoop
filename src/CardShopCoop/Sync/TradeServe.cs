using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CardShopCoop.Net;
using HarmonyLib;
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
    ///  - JOINER UX: the prompt line announces the offer; the serve key opens the
    ///    game's REAL CustomerTradeCardScreen through the host click-flow's own calls
    ///    (Customer.OnMousePress:236-250 - EnterWorkerInteractMode/EnterUIMode/
    ///    EnterLockMoveMode + tooltip/GameUI hides - minus the customer look-at). A
    ///    DEACTIVATED puppet customer from CustomerManager.GetCustomerList() (the pool
    ///    NpcSweep suppresses) is the data carrier: SetCustomer(puppet, digestData)
    ///    reads NOTHING from the customer beyond storing m_CurrentCustomer, and
    ///    data != null skips every local RNG roll. The screen's own buttons are then
    ///    remote controls: client-role prefixes on OnPressAccept / OnPressDecline
    ///    forward TradeOp{op, counterIdx, m_PriceSet} to the host and close through
    ///    CloseScreen -> OnCloseScreen -> Customer.OnPressStopInteract, whose client
    ///    prefix replays only the player-restore half (the vanilla tail would run
    ///    DetermineShopAction on an INACTIVE puppet - StartCoroutine throws there).
    ///  - Accept paths (CustomerTradeCardScreen.OnPressAccept): trading -> pure card
    ///    swap CPlayerData.AddCard(L,1) + ReduceCard(R,1) / RemoveGradedCard(R);
    ///    selling -> haggle RNG on m_PriceSet (m_PriceSet >= m_SellCardAskPrice-0.01
    ///    forces a 100% accept), then the REAL money+card path:
    ///    PriceChangeManager.AddTransaction, CEventPlayer_ReduceCoin(m_PriceSet),
    ///    CPlayerData.AddCard(L,1), customer.SetSoldCard(L). The host pins m_PriceSet
    ///    to the joiner's FORWARDED price, so lowballs haggle exactly like vanilla:
    ///    the not-accepted branches move m_SellCardAskPrice / the decline counters,
    ///    which we persist back into m_CustomerTradeData ("Let Me Think" recipe) and
    ///    re-broadcast; the out-of-patience final else (no counter change) walks the
    ///    customer off.
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
    /// CardDelta mirror). The client never runs any vanilla trade code: OnMousePress
    /// and the screen's mutating buttons are blocked/forwarded client-side below.
    /// </summary>
    public class TradeServe
    {
        private const float Cadence = 0.5f;      // host scan/broadcast gate
        private const float HealInterval = 6f;   // unchanged-state re-broadcast
        private const float StaleAfter = 13f;    // client: > 2x heal + margin
        private static float Reach => CoopPlugin.ServeReach.Value; // same reach as RegisterServe
        private const float VanillaWait = 60f;   // Customer.cs WaitingToTradeCard timeout
        private const int MaxOffers = 32;

        // joiner's decline key. Kept hardcoded (adding a ConfigEntry would mean
        // editing CoopPlugin, outside this module's file); it only fires while a
        // live offer prompt is showing within reach, so a stray overlap with a
        // vanilla bind is harmless. Answer/accept rides the existing ServeKey (V).
        private const KeyCode DeclineKey = KeyCode.B;

        /// <summary>Set by CoopCore: client -> host op (MsgType.TradeOp).</summary>
        public Action<Action<BinaryWriter>> SendOp;
        /// <summary>Set by CoopCore: host -> clients state (MsgType.TradeState).</summary>
        public Action<Action<BinaryWriter>> BroadcastState;

        private const byte OpAccept = 1;
        private const byte OpDecline = 2;

        // harmony prefixes are static; CoopCore owns the single instance
        private static TradeServe _live;

        public TradeServe() { _live = this; }

        // ---- reflection: Customer privates (verified against decompiled/Customer.cs)
        private static readonly FieldInfo FiTradeData = AccessTools.Field(typeof(Customer), "m_CustomerTradeData");
        private static readonly FieldInfo FiTradeCounter = AccessTools.Field(typeof(Customer), "m_CurrentTradeCardCashierCounter");
        private static readonly FieldInfo FiHasTraded = AccessTools.Field(typeof(Customer), "m_HasTradedCard");
        private static readonly FieldInfo FiCustTimer = AccessTools.Field(typeof(Customer), "m_Timer");
        private static readonly FieldInfo FiCustTimerMax = AccessTools.Field(typeof(Customer), "m_TimerMax");
        private static readonly FieldInfo FiPausing = AccessTools.Field(typeof(Customer), "m_IsPausingAction");
        private static readonly MethodInfo MiDetermine = AccessTools.Method(typeof(Customer), "DetermineShopAction");

        // ---- reflection: CustomerTradeCardScreen privates
        private static readonly FieldInfo FiScrTrading = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_IsTrading");
        private static readonly FieldInfo FiScrAccepted = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_HasAccepted");
        private static readonly FieldInfo FiScrPriceSet = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_PriceSet");
        private static readonly FieldInfo FiScrLastPrice = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_LastPriceSet");
        private static readonly FieldInfo FiScrAsk = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_SellCardAskPrice");
        private static readonly FieldInfo FiScrMarket = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_SellCardMarketPrice");
        private static readonly FieldInfo FiScrMaxDecline = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_MaxDeclineCount");
        private static readonly FieldInfo FiScrDecline = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_DeclineCount");
        private static readonly FieldInfo FiScrCardL = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_CardData_L");
        private static readonly FieldInfo FiScrCardR = AccessTools.Field(typeof(CustomerTradeCardScreen), "m_CardData_R");

        private struct Offer
        {
            public byte CounterIdx;
            public bool Known;    // pre-roll succeeded: card/price fields are valid
            public bool Trading;  // true = card-for-card trade, false = sell-in for money
            public CardData CardL;   // what the customer offers
            public CardData CardR;   // what the customer wants back (Trading only)
            public float Price;      // asking price (sell-in only)
            public float Remaining;  // seconds before the customer gives up
        }

        // host
        private float _timer;
        private int _lastHash;
        private float _heal;
        private byte _resultSeq;
        private string _result = "";
        // NEVER CSingleton<>.Instance for these three: touched while no real manager
        // exists (client reload loading screen, host mid-session save load) the getter
        // fabricates a fake empty DontDestroyOnLoad manager that shadows the real one
        // for the rest of the run (see WorldSync.ResolveShelfManager). Cached; Unity
        // fake-null re-resolves after scene loads.
        private ShelfManager _sm;
        private CustomerManager _cm;
        private InteractionPlayerController _ipc;
        private readonly List<Offer> _hostBuf = new List<Offer>();
        private readonly HashSet<int> _preRollFailed = new HashSet<int>();   // customer ids; warn once
        private readonly HashSet<int> _unknownLogged = new HashSet<int>();   // customer ids; log known=false once

        // client
        private readonly Dictionary<int, Offer> _offers = new Dictionary<int, Offer>();
        private readonly List<int> _keyBuf = new List<int>();
        private float _staleTimer;
        private float _opThrottle;
        private int _seenSeq = -1;
        private int _lastOfferCount = -1; // for change-only receive logging
        private int _pendingCounter = -1; // counter our native screen is answering
        private bool _nativeBroken;       // native screen threw once: prompt+keys fallback
        private Transform _playerTf;      // the joiner's MOVING body (ipc.m_WalkerCtrl)

        public void Reset()
        {
            _timer = -0.83f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            _resultSeq = 0;
            _result = "";
            _sm = null;
            _cm = null;
            _ipc = null;
            _hostBuf.Clear();
            _preRollFailed.Clear();
            _unknownLogged.Clear();
            _offers.Clear();
            _staleTimer = 0f;
            _opThrottle = 0f;
            _seenSeq = -1;
            _lastOfferCount = -1;
            _pendingCounter = -1;
            _nativeBroken = false;
            _playerTf = null;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = 999f; // beats the hash gate even if the real hash is 0
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
            // The native-screen remote controls: on the client the screen's buttons
            // forward a TradeOp to the host instead of resolving locally, then close
            // through the screen's own path. The host role passes every prefix through.
            Try(h, typeof(CustomerTradeCardScreen), "OnPressAccept",
                prefix: new HarmonyMethod(typeof(TradeServe), nameof(ClientAcceptPrefix)));
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

        public static bool ClientTradeBlockPrefix()
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = $"stand at the counter and press {CoopPlugin.ServeKey.Value} to answer the trade";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false;
        }

        /// <summary>Client: the screen's Accept button = forward the CURRENT price field
        /// to the host (the haggle RNG runs there) and close. Vanilla must never run -
        /// it would move the mirrored wallet/binder locally.</summary>
        public static bool ClientAcceptPrefix(CustomerTradeCardScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            var t = _live;
            if (t == null) return false;
            int idx = t._pendingCounter;
            float price = FiScrPriceSet?.GetValue(__instance) is float p ? p : 0f;
            CoopPlugin.Log.LogInfo($"TradeServe client: accept pressed on native screen (counter {idx}, price {price:F2})");
            if (idx >= 0)
                t.SendOpFor(OpAccept, idx, price, "answering the customer...");
            try { __instance.CloseScreen(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe client: screen close: " + e.Message); }
            return false;
        }

        /// <summary>Client: the screen's Decline button = forward the decline, then let
        /// the vanilla body run (it only plays a sound and closes the screen).</summary>
        public static bool ClientDeclinePrefix()
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            var t = _live;
            if (t != null && t._pendingCounter >= 0)
            {
                CoopPlugin.Log.LogInfo($"TradeServe client: decline pressed on native screen (counter {t._pendingCounter})");
                t.SendOpFor(OpDecline, t._pendingCounter, 0f, "declining...");
            }
            return true;
        }

        /// <summary>Client: "Let Me Think" = close without answering; the offer stays
        /// live on the host. Vanilla would call OnPressRefreshInteract on the puppet
        /// (re-enabling its "!" mesh and touching player state twice) - skip it.</summary>
        public static bool ClientLetMeThinkPrefix(CustomerTradeCardScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            try { __instance.CloseScreen(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe client: screen close: " + e.Message); }
            return false;
        }

        /// <summary>Client: the player-restore half of Customer.OnPressStopInteract
        /// (Customer.cs:255-267) without the shop-AI tail - the carrier puppet is
        /// deactivated, so DetermineShopAction/StartCoroutine would throw on it.</summary>
        public static bool ClientStopInteractPrefix(Customer __instance)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            FiTradeData?.SetValue(__instance, null);
            FiPausing?.SetValue(__instance, false);
            try
            {
                var ipc = _live != null ? _live.Ipc() : null;
                if (ipc != null)
                {
                    ipc.ExitWorkerInteractMode();
                    ipc.StopAimLookAt();
                    if (ipc.m_WalkerCtrl != null) ipc.m_WalkerCtrl.SetStopMovement(isStop: false);
                    ipc.ExitUIMode();
                }
                GameUIScreen.ResetToolTipVisibility();
                GameUIScreen.ResetEnterGoNextDayIndicatorVisible();
                TutorialManager.SetGameUIVisible(isVisible: true);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe client: UI restore: " + e.Message); }
            if (_live != null) _live._pendingCounter = -1;
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
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        private CustomerManager Cm()
        {
            if (_cm == null) _cm = UnityEngine.Object.FindObjectOfType<CustomerManager>();
            return _cm;
        }

        private InteractionPlayerController Ipc()
        {
            if (_ipc == null) _ipc = UnityEngine.Object.FindObjectOfType<InteractionPlayerController>();
            return _ipc;
        }

        private static string CardName(CardData c)
        {
            if (c == null) return "a card";
            string name = null;
            try
            {
                var md = InventoryBase.GetMonsterData(c.monsterType);
                if (md != null) name = md.GetName();
            }
            catch { }
            if (string.IsNullOrEmpty(name)) name = c.monsterType.ToString();
            if (c.isFoil) name += " (foil)";
            if (c.cardGrade > 0) name += $" [grade {c.cardGrade}]";
            return name;
        }

        private static string Price(float p)
        {
            try { return GameInstance.GetPriceString(p); }
            catch { return "$" + p.ToString("F2"); }
        }

        private static int CardHash(CardData c)
        {
            if (c == null) return 0;
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
            if (!inGame) return;
            _timer += dt;
            if (_timer < Cadence) return;
            _timer -= Cadence;
            try
            {
                var cm = Cm();
                var sm = Sm();
                if (cm == null || sm == null) return;

                _hostBuf.Clear();
                var customers = cm.GetCustomerList();
                for (int i = 0; i < customers.Count && _hostBuf.Count < MaxOffers; i++)
                {
                    var cust = customers[i];
                    if (cust == null || !cust.m_IsActive) continue;
                    if (cust.m_CurrentState != ECustomerState.WaitingToTradeCard) continue;

                    var counter = FiTradeCounter?.GetValue(cust) as InteractableCashierCounter;
                    if (counter == null) continue;
                    int idx = sm.m_CashierCounterList.IndexOf(counter);
                    if (idx < 0 || idx > 250) continue;

                    var data = FiTradeData?.GetValue(cust) as CustomerTradeData;
                    if (data == null) data = PreRoll(cm, cust);

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
                    };
                    if (known)
                    {
                        offer.Trading = data.m_IsTrading;
                        offer.CardL = data.m_CardData_L;
                        offer.CardR = data.m_CardData_R;
                        offer.Price = data.m_SellCardAskPrice;
                    }
                    _hostBuf.Add(offer);
                }

                // hash skips Remaining (it changes every tick); the heal broadcast
                // keeps the client's local countdown honest enough
                int hash = 17;
                hash = hash * 31 + _resultSeq; // a fresh op result always broadcasts
                for (int i = 0; i < _hostBuf.Count; i++)
                {
                    var o = _hostBuf[i];
                    hash = hash * 31 + o.CounterIdx;
                    hash = hash * 31 + ((o.Known ? 1 : 0) | (o.Trading ? 2 : 0));
                    hash = hash * 31 + CardHash(o.CardL);
                    hash = hash * 31 + CardHash(o.CardR);
                    hash = hash * 31 + (int)(o.Price * 100f);
                }

                _heal += Cadence;
                bool changed = hash != _lastHash;
                if (!changed && _heal < HealInterval) return;
                _lastHash = hash;
                _heal = 0f;
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
                BroadcastState?.Invoke(WriteState);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe host: " + e.Message); }
        }

        /// <summary>Host: roll the customer's offer WITHOUT opening the screen, using the
        /// game's own "Let Me Think" persistence slot. SetCustomer is safe on the closed
        /// screen: it only writes inspector-assigned UI children (legal while inactive;
        /// Animation.Play on an inactive object is a no-op) and rolls the offer from
        /// CPlayerData. The stored CustomerTradeData is replayed verbatim by any later
        /// SetCustomer call - so the host clicking the customer sees THIS same offer.</summary>
        private CustomerTradeData PreRoll(CustomerManager cm, Customer cust)
        {
            // without the storage field every roll would be lost and re-rolled (and
            // re-broadcast) each tick - better to leave the offer host-served
            if (FiTradeData == null) return null;
            var screen = cm.m_CustomerTradeCardScreen;
            // never stomp a live trade the host is running in the real UI
            if (screen == null || cm.m_IsPlayerTrading || screen.IsScreenOpened()) return null;
            int id = cust.GetInstanceID();
            if (_preRollFailed.Contains(id)) return null;
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
                    m_CardData_L = FiScrCardL?.GetValue(screen) as CardData,
                    m_CardData_R = FiScrCardR?.GetValue(screen) as CardData,
                };
                if (data.m_CardData_L == null)
                {
                    _preRollFailed.Add(id);
                    CoopPlugin.Log.LogWarning("TradeServe: pre-roll produced no card; offer left for the host to serve");
                    return null;
                }
                FiTradeData?.SetValue(cust, data);
                CoopPlugin.Log.LogInfo($"TradeServe host: pre-rolled {(data.m_IsTrading ? $"trade {CardName(data.m_CardData_L)} for {CardName(data.m_CardData_R)}" : $"sell-in {CardName(data.m_CardData_L)} @ {Price(data.m_SellCardAskPrice)}")}");
                return data;
            }
            catch (Exception e)
            {
                _preRollFailed.Add(id);
                CoopPlugin.Log.LogWarning("TradeServe pre-roll: " + e.Message);
                return null;
            }
            finally
            {
                try { cm.m_IsPlayerTrading = false; } catch { }
            }
        }

        private void WriteState(BinaryWriter bw)
        {
            bw.Write(_resultSeq);
            bw.Write(_result ?? "");
            bw.Write((byte)_hostBuf.Count);
            for (int i = 0; i < _hostBuf.Count; i++)
            {
                var o = _hostBuf[i];
                bw.Write(o.CounterIdx);
                bw.Write((byte)((o.Known ? 1 : 0) | (o.Trading ? 2 : 0)));
                if (o.Known)
                {
                    Msg.WriteCard(bw, o.CardL);
                    if (o.Trading) Msg.WriteCard(bw, o.CardR);
                    else bw.Write(o.Price);
                }
                bw.Write(o.Remaining);
            }
        }

        private void Result(string text)
        {
            _result = text;
            _resultSeq++;           // seq is hashed, so the next tick must broadcast
            ForceResend();
            CoopPlugin.Log.LogInfo("TradeServe: " + text);
        }

        /// <summary>Host: a joiner answered a counter offer (accept carries the price
        /// the joiner's screen had set; decline/trade ops carry 0).</summary>
        public void HostApplyOp(BinaryReader br)
        {
            byte op = br.ReadByte();
            int idx = br.ReadByte();
            float price = br.ReadSingle();
            CoopPlugin.Log.LogInfo($"TradeServe host: received {(op == OpAccept ? "accept" : op == OpDecline ? "decline" : "op " + op)} @ counter {idx}, price {price:F2}");
            try { HostApplyOpInner(op, idx, price); }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("TradeServe op: " + e);
                Result("trade failed - ask the host to serve them");
            }
        }

        private void HostApplyOpInner(byte op, int idx, float price)
        {
            var cm = Cm();
            var sm = Sm();
            if (cm == null || sm == null || idx < 0 || idx >= sm.m_CashierCounterList.Count)
            {
                Result("no counter there");
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
                Result("the customer already left");
                return;
            }
            if (cm.m_IsPlayerTrading)
            {
                Result("the host is talking to that customer right now");
                return;
            }

            if (op == OpDecline)
            {
                FinishCustomer(cust, counter);
                Result("trade declined - the customer moves on");
                return;
            }
            if (op != OpAccept) return;

            var data = FiTradeData?.GetValue(cust) as CustomerTradeData;
            if (data == null) data = PreRoll(cm, cust);
            if (data == null || data.m_CardData_L == null)
            {
                Result("couldn't read the offer - the host must serve this one");
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
                    Result($"the binder no longer has {CardName(r)} to trade");
                    return;
                }
            }
            else if (CPlayerData.m_CoinAmountDouble < (double)bid)
            {
                Result($"not enough money to pay {Price(bid)}");
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
                Result("the host has the trade screen open");
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
                try { cm.m_IsPlayerTrading = false; } catch { }
            }

            if (accepted)
            {
                FinishCustomer(cust, counter);
                Result(data.m_IsTrading
                    ? $"traded {CardName(data.m_CardData_R)} for {CardName(data.m_CardData_L)}"
                    : $"bought {CardName(data.m_CardData_L)} for {Price(bid)}");
                return;
            }
            if (data.m_IsTrading)
            {
                // both trading branches either accept or hit the have-no-card popup,
                // which the pre-check above already covers - belt-and-braces report
                Result("the trade fell through - the host must serve this one");
                return;
            }
            if (postDecline == preDecline)
            {
                // neither the haggle nor the plain-refusal branch ran: vanilla's final
                // else (out of patience, its CloseScreen no-ops on the closed screen) -
                // the customer walks, exactly like its own resolution would
                FinishCustomer(cust, counter);
                Result("the customer lost patience and left");
                return;
            }
            Result(Mathf.Abs(postAsk - preAsk) > 0.005f
                ? $"they refuse {Price(bid)} - now asking {Price(postAsk)}"
                : $"they refuse {Price(bid)} - try higher");
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
                if (cust.m_ExclaimationMesh != null) cust.m_ExclaimationMesh.SetActive(false);
                if (cust.m_InteractCollider != null) cust.m_InteractCollider.SetActive(false);
            }
            catch { }
            FiHasTraded?.SetValue(cust, true);
            try { counter?.CustomerFinishTradingCard(); } catch { }
            FiTradeCounter?.SetValue(cust, null);
            try { MiDetermine?.Invoke(cust, null); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeServe finish: " + e.Message); }
        }

        // ---------------- client ----------------

        public void ClientApplyState(BinaryReader br)
        {
            byte seq = br.ReadByte();
            string result = br.ReadString();
            int count = br.ReadByte();
            _offers.Clear();
            for (int i = 0; i < count; i++)
            {
                var o = new Offer { CounterIdx = br.ReadByte() };
                byte flags = br.ReadByte();
                o.Known = (flags & 1) != 0;
                o.Trading = (flags & 2) != 0;
                if (o.Known)
                {
                    o.CardL = Msg.ReadCard(br);
                    if (o.Trading) o.CardR = Msg.ReadCard(br);
                    else o.Price = br.ReadSingle();
                }
                o.Remaining = br.ReadSingle();
                _offers[o.CounterIdx] = o;
            }
            _staleTimer = 0f;
            if (count != _lastOfferCount) // change-only, so the 6s heal doesn't spam
            {
                _lastOfferCount = count;
                CoopPlugin.Log.LogInfo($"TradeServe client: state received, {count} live offer(s)");
            }

            if (seq != _seenSeq)
            {
                _seenSeq = seq;
                if (result.Length > 0 && CoopCore.Instance != null)
                {
                    CoopCore.Instance.RegisterLine = result;
                    CoopCore.Instance.RegisterLineTimer = 4f;
                }
            }
        }

        /// <summary>True while a trade/sell-in offer is live at this counter - CoopCore's
        /// serve-key path must skip ServeRequest for it (this module answers instead).</summary>
        public bool HasOffer(int counterIdx)
        {
            return counterIdx >= 0 && _offers.ContainsKey(counterIdx);
        }

        /// <summary>Client: prompt for the nearest counter's live offer, or null
        /// (composes with RegisterMirror.PromptFor in CoopCore).</summary>
        public string PromptFor(int nearestCounter)
        {
            if (nearestCounter < 0 || !_offers.TryGetValue(nearestCounter, out var o)) return null;
            if (!o.Known)
                return "a customer wants to trade - the host must serve them";
            string keys = _nativeBroken
                ? $"{CoopPlugin.ServeKey.Value} accept, {DeclineKey} decline" // prompt+key fallback
                : $"{CoopPlugin.ServeKey.Value} answer, {DeclineKey} decline";
            if (o.Trading)
                return $"trade: their {CardName(o.CardL)} for your {CardName(o.CardR)} - {keys}";
            return $"sell-in: {CardName(o.CardL)} for {Price(o.Price)} - {keys}";
        }

        /// <summary>Client: the joiner's MOVING body. InteractionPlayerController sits on
        /// a stationary manager object whose transform never moves (the frozen-avatar
        /// bug); the walking body is its public m_WalkerCtrl, same as CoopCore's
        /// ResolvePlayer. Measuring reach from ipc.transform was why the old accept/
        /// decline keys never fired: the manager is never within 3.5m of a counter.</summary>
        private Transform PlayerBody()
        {
            if (_playerTf != null) return _playerTf;
            var ipc = Ipc();
            if (ipc == null) return null;
            _playerTf = ipc.m_WalkerCtrl != null ? ipc.m_WalkerCtrl.transform : ipc.transform;
            return _playerTf;
        }

        /// <summary>Client: send one op for a counter with local feedback; the offer is
        /// cleared optimistically (the host's forced echo re-syncs the truth).</summary>
        private void SendOpFor(byte op, int idx, float price, string line)
        {
            _opThrottle = 0.5f;
            CoopPlugin.Log.LogInfo($"TradeServe client: sending {(op == OpAccept ? "accept" : "decline")} @ counter {idx}, price {price:F2}");
            SendOp?.Invoke(bw => { bw.Write(op); bw.Write((byte)idx); bw.Write(price); });
            _offers.Remove(idx);
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = line;
                CoopCore.Instance.RegisterLineTimer = 2f;
            }
        }

        /// <summary>Client: open the game's REAL CustomerTradeCardScreen filled with the
        /// host's offer. A deactivated puppet from CustomerManager's pool carries a
        /// CustomerTradeData built from the digest (SetCustomer with non-null data
        /// replays it verbatim and reads nothing else from the customer); the screen
        /// opens through the host click-flow's own calls (Customer.OnMousePress:236-250,
        /// minus the customer look-at) so input mode/cursor behave exactly like a host
        /// trade. Market price is recomputed locally by SetCustomer from the mirrored
        /// market data.</summary>
        private void OpenNativeScreen(int idx, Offer offer)
        {
            var cm = Cm();
            var screen = cm != null ? cm.m_CustomerTradeCardScreen : null;
            if (screen == null) throw new InvalidOperationException("no CustomerTradeCardScreen");
            if (screen.IsScreenOpened()) return;
            Customer carrier = null;
            var list = cm.GetCustomerList();
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) { carrier = list[i]; break; }
            if (carrier == null) throw new InvalidOperationException("no carrier customer in the pool");
            var data = new CustomerTradeData
            {
                m_IsTrading = offer.Trading,
                m_CardData_L = offer.CardL,
                m_CardData_R = offer.CardR,
                m_SellCardAskPrice = offer.Price,
                m_SellCardMarketPrice = 0f, // SetCustomer recomputes from GetCardMarketPrice
                m_PriceSet = 0f,
                m_LastPriceSet = 0f,
                m_MaxDeclineCount = 0, // the haggle RNG never runs here (accept is forwarded)
                m_DeclineCount = 0,
            };
            // fill BEFORE touching input state: a throw here leaves nothing to unwind
            // (bar m_IsPlayerTrading, which the caller's catch resets)
            screen.SetCustomer(carrier, data);
            var ipc = Ipc();
            if (ipc == null) throw new InvalidOperationException("no InteractionPlayerController");
            ipc.EnterWorkerInteractMode();
            ipc.EnterUIMode();
            ipc.EnterLockMoveMode();
            GameUIScreen.HideToolTip();
            GameUIScreen.HideEnterGoNextDayIndicatorVisible();
            TutorialManager.SetGameUIVisible(isVisible: false);
            screen.OpenScreen();
            _pendingCounter = idx;
            CoopPlugin.Log.LogInfo($"TradeServe client: opened native trade screen for counter {idx}");
        }

        /// <summary>Client per-frame: local countdown/staleness, the native screen's
        /// lifecycle, and the serve/decline keys (KeyDown, TextFieldFocused guard,
        /// short throttle, 3.5m reach from the WALKER body).</summary>
        public void ClientTick(float dt, bool inGame)
        {
            if (_opThrottle > 0f) _opThrottle -= dt;

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
                    foreach (var kv in _offers) _keyBuf.Add(kv.Key);
                    for (int i = 0; i < _keyBuf.Count; i++)
                    {
                        var o = _offers[_keyBuf[i]];
                        o.Remaining -= dt;
                        if (o.Remaining <= 0f) _offers.Remove(_keyBuf[i]);
                        else _offers[_keyBuf[i]] = o;
                    }
                }
            }

            // while OUR native screen is up its buttons are the input (V/B must not
            // leak through); close it if the offer died underneath (timeout, host
            // served them, walk-off)
            if (_pendingCounter >= 0)
            {
                var cm = Cm();
                var screen = cm != null ? cm.m_CustomerTradeCardScreen : null;
                if (screen == null || !screen.IsScreenOpened())
                {
                    _pendingCounter = -1; // closed by Esc/back; no op = offer stays live
                }
                else if (!_offers.ContainsKey(_pendingCounter))
                {
                    CoopPlugin.Log.LogInfo("TradeServe client: offer vanished while the screen was open - closing it");
                    try { screen.CloseScreen(); } catch { }
                    _pendingCounter = -1;
                    if (CoopCore.Instance != null)
                    {
                        CoopCore.Instance.RegisterLine = "the customer left";
                        CoopCore.Instance.RegisterLineTimer = 3f;
                    }
                }
                return;
            }

            if (!inGame || _offers.Count == 0 || _opThrottle > 0f || UI.CoopUI.TextFieldFocused) return;
            bool accept = Input.GetKeyDown(CoopPlugin.ServeKey.Value);
            bool decline = Input.GetKeyDown(DeclineKey);
            if (!accept && !decline) return;

            var body = PlayerBody();
            if (body == null)
            {
                CoopPlugin.Log.LogInfo("TradeServe client: key pressed but no player body resolved");
                return;
            }
            int near = RegisterServe.FindNearestCounter(body.position, Reach, quiet: true);
            if (near < 0 || !_offers.TryGetValue(near, out var offer))
            {
                // THE old silent gate that ate every keypress when the distance source
                // was wrong - log it so a dead key is diagnosable from the console
                CoopPlugin.Log.LogInfo($"TradeServe client: {(accept ? "serve" : "decline")} key ignored (nearest counter {near}, offers at [{string.Join(",", _offers.Keys)}])");
                return;
            }
            if (!offer.Known)
            {
                _opThrottle = 0.5f;
                CoopPlugin.Log.LogInfo($"TradeServe client: offer at counter {near} is host-only (pre-roll failed on the host)");
                if (CoopCore.Instance != null)
                {
                    CoopCore.Instance.RegisterLine = "the host must serve this one";
                    CoopCore.Instance.RegisterLineTimer = 3f;
                }
                return;
            }

            if (decline)
            {
                SendOpFor(OpDecline, near, 0f, "declining...");
                return;
            }

            // serve key: the native screen is the UX; prompt+direct-accept is the fallback
            if (!_nativeBroken)
            {
                _opThrottle = 0.3f;
                try
                {
                    OpenNativeScreen(near, offer);
                    return;
                }
                catch (Exception e)
                {
                    _nativeBroken = true;
                    _pendingCounter = -1;
                    try
                    {
                        var cm = Cm();
                        if (cm != null) cm.m_IsPlayerTrading = false; // SetCustomer may have set it
                    }
                    catch { }
                    CoopPlugin.Log.LogWarning("TradeServe client: native trade screen failed, falling back to prompt keys: " + e);
                    if (CoopCore.Instance != null)
                    {
                        CoopCore.Instance.RegisterLine = $"trade screen unavailable - {CoopPlugin.ServeKey.Value} accepts at asking price, {DeclineKey} declines";
                        CoopCore.Instance.RegisterLineTimer = 4f;
                    }
                    return; // the failed press is spent; the next one uses the fallback
                }
            }
            // fallback accept: -1 price = "at the asking price" (guaranteed branch)
            SendOpFor(OpAccept, near, -1f, "answering the customer...");
        }
    }
}
