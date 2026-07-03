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
    ///    SetCustomer call replays it verbatim. We exploit exactly that hook: when a
    ///    customer starts waiting, the host PRE-ROLLS the offer by calling SetCustomer
    ///    on the (closed) screen and stashing the result into m_CustomerTradeData, so
    ///    the digest we broadcast is the same offer the host would see on click.
    ///  - Accept paths (CustomerTradeCardScreen.OnPressAccept): trading -> pure card
    ///    swap CPlayerData.AddCard(L,1) + ReduceCard(R,1) / RemoveGradedCard(R);
    ///    selling -> haggle RNG on m_PriceSet, but m_PriceSet >= m_SellCardAskPrice-0.01
    ///    forces a 100% accept, which then runs the REAL money+card path:
    ///    PriceChangeManager.AddTransaction, CEventPlayer_ReduceCoin(m_PriceSet),
    ///    CPlayerData.AddCard(L,1), customer.SetSoldCard(L). We accept at the asking
    ///    price so the whole vanilla econ path fires deterministically.
    ///  - Headless resolution recipe: the 60s wait timeout (Customer.cs:3639-3660)
    ///    resolves a waiting customer with NO UI/InteractionPlayerController calls:
    ///    clear m_CustomerTradeData/m_Timer/m_IsPausingAction, hide mesh+collider,
    ///    m_HasTradedCard = true, counter.CustomerFinishTradingCard(),
    ///    DetermineShopAction(). We reuse it for both decline and post-accept cleanup,
    ///    so the host's screen NEVER opens and the host player is never yanked into
    ///    UI mode (OnPressStopInteract would call ExitUIMode etc. on the host).
    ///
    /// Money safety: the sell-in charge runs ONCE, host-side, through the vanilla
    /// OnPressAccept (CEventPlayer_ReduceCoin -> shared econ mirror; AddCard -> the
    /// CardDelta mirror). The client never runs any vanilla trade code: OnMousePress
    /// and OnPressAccept are blocked client-side below.
    /// </summary>
    public class TradeServe
    {
        private const float Cadence = 0.5f;      // host scan/broadcast gate
        private const float HealInterval = 6f;   // unchanged-state re-broadcast
        private const float StaleAfter = 13f;    // client: > 2x heal + margin
        private const float Reach = 3.5f;        // same reach as RegisterServe
        private const float VanillaWait = 60f;   // Customer.cs WaitingToTradeCard timeout
        private const int MaxOffers = 32;

        // joiner's decline key. Kept hardcoded (adding a ConfigEntry would mean
        // editing CoopPlugin, outside this module's file); it only fires while a
        // live offer prompt is showing within reach, so a stray overlap with a
        // vanilla bind is harmless. Accept rides the existing ServeKey (V).
        private const KeyCode DeclineKey = KeyCode.B;

        /// <summary>Set by CoopCore: client -> host op (MsgType.TradeOp).</summary>
        public Action<Action<BinaryWriter>> SendOp;
        /// <summary>Set by CoopCore: host -> clients state (MsgType.TradeState).</summary>
        public Action<Action<BinaryWriter>> BroadcastState;

        private const byte OpAccept = 1;
        private const byte OpDecline = 2;

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
        private ShelfManager _sm;
        private readonly List<Offer> _hostBuf = new List<Offer>();
        private readonly HashSet<int> _preRollFailed = new HashSet<int>(); // customer ids; warn once

        // client
        private readonly Dictionary<int, Offer> _offers = new Dictionary<int, Offer>();
        private readonly List<int> _keyBuf = new List<int>();
        private float _staleTimer;
        private float _opThrottle;
        private int _seenSeq = -1;

        public void Reset()
        {
            _timer = -0.83f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
            _resultSeq = 0;
            _result = "";
            _sm = null;
            _hostBuf.Clear();
            _preRollFailed.Clear();
            _offers.Clear();
            _staleTimer = 0f;
            _opThrottle = 0f;
            _seenSeq = -1;
        }

        public void ForceResend()
        {
            _lastHash = 0;
            _heal = 999f; // beats the hash gate even if the real hash is 0
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // The joiner's world has puppet customers only; a local trade screen would
            // mutate the mirrored wallet/binder outside the host's simulation (and the
            // puppet customer could never resolve). Block the entry point AND the
            // mutating button as belt-and-braces; the counter prompt is the joiner's UI.
            Try(h, typeof(Customer), "OnMousePress",
                prefix: new HarmonyMethod(typeof(TradeServe), nameof(ClientTradeBlockPrefix)));
            Try(h, typeof(CustomerTradeCardScreen), "OnPressAccept",
                prefix: new HarmonyMethod(typeof(TradeServe), nameof(ClientTradeBlockPrefix)));
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
                var cm = CSingleton<CustomerManager>.Instance;
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
                if (hash == _lastHash && _heal < HealInterval) return;
                _lastHash = hash;
                _heal = 0f;
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

        /// <summary>Host: a joiner pressed accept/decline at a counter.</summary>
        public void HostApplyOp(BinaryReader br)
        {
            byte op = br.ReadByte();
            int idx = br.ReadByte();
            try { HostApplyOpInner(op, idx); }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("TradeServe op: " + e);
                Result("trade failed - ask the host to serve them");
            }
        }

        private void HostApplyOpInner(byte op, int idx)
        {
            var cm = CSingleton<CustomerManager>.Instance;
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
            else if (CPlayerData.m_CoinAmountDouble < (double)data.m_SellCardAskPrice)
            {
                Result($"not enough money to pay {Price(data.m_SellCardAskPrice)}");
                return;
            }

            // headless vanilla accept: SetCustomer replays the stored offer onto the
            // CLOSED screen, we pin the price at the asking price (>= ask - 0.01 forces
            // the 100% accept branch), then the screen's own OnPressAccept moves the
            // money (CEventPlayer_ReduceCoin) and cards (AddCard/ReduceCard) so every
            // existing econ/CardDelta mirror carries the change. No UI ever opens.
            var screen = cm.m_CustomerTradeCardScreen;
            if (screen == null || screen.IsScreenOpened())
            {
                Result("the host has the trade screen open");
                return;
            }
            bool accepted;
            try
            {
                screen.SetCustomer(cust, data);
                if (!data.m_IsTrading)
                    FiScrPriceSet?.SetValue(screen, data.m_SellCardAskPrice);
                screen.OnPressAccept();
                accepted = FiScrAccepted?.GetValue(screen) is bool ok && ok;
            }
            finally
            {
                try { cm.m_IsPlayerTrading = false; } catch { }
            }

            if (!accepted)
            {
                Result("the trade fell through - the host must serve this one");
                return;
            }

            FinishCustomer(cust, counter);
            Result(data.m_IsTrading
                ? $"traded {CardName(data.m_CardData_R)} for {CardName(data.m_CardData_L)}"
                : $"bought {CardName(data.m_CardData_L)} for {Price(data.m_SellCardAskPrice)}");
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
            if (o.Trading)
                return $"trade: their {CardName(o.CardL)} for your {CardName(o.CardR)} - {CoopPlugin.ServeKey.Value} accept, {DeclineKey} decline";
            return $"sell-in: {CardName(o.CardL)} for {Price(o.Price)} - {CoopPlugin.ServeKey.Value} accept, {DeclineKey} decline";
        }

        /// <summary>Client per-frame: local countdown/staleness + the accept/decline keys
        /// (same conventions as CoopCore's serve key: KeyDown, TextFieldFocused guard,
        /// short throttle, 3.5m reach via RegisterServe.FindNearestCounter).</summary>
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

            if (!inGame || _offers.Count == 0 || _opThrottle > 0f || UI.CoopUI.TextFieldFocused) return;
            bool accept = Input.GetKeyDown(CoopPlugin.ServeKey.Value);
            bool decline = Input.GetKeyDown(DeclineKey);
            if (!accept && !decline) return;

            var ipc = CSingleton<InteractionPlayerController>.Instance;
            if (ipc == null) return;
            int near = RegisterServe.FindNearestCounter(ipc.transform.position, Reach, quiet: true);
            if (near < 0 || !_offers.TryGetValue(near, out var offer)) return;
            if (!offer.Known) return; // host-only offer: keys do nothing

            _opThrottle = 0.5f;
            byte op = accept ? OpAccept : OpDecline;
            int idx = near;
            SendOp?.Invoke(bw => { bw.Write(op); bw.Write((byte)idx); });
            _offers.Remove(near); // optimistic clear; the host's forced echo re-syncs
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = accept ? "answering the customer..." : "declining...";
                CoopCore.Instance.RegisterLineTimer = 2f;
            }
        }
    }
}
