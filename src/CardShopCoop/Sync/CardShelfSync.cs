using System;
using System.Collections.Generic;
using System.IO;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors single-card DISPLAY slots (CardShelf + the card side of CardItemCombiShelf)
    /// between host and client, same snapshot-diff scheme as the item shelves. A slot's
    /// state is (occupied, CardData identity); applying a remote placement replicates the
    /// game's own save-loader recipe (Card3dUISpawner + SpawnInteractableObject +
    /// SetCardOnShelf), and removal uses the compartment's own DisableAllCard cleanup.
    /// Collection accounting is NOT touched here - the CardDelta mirror already forwards
    /// the ReduceCard/AddCard the acting player's game performs.
    /// </summary>
    public class CardShelfSync : CoopModule
    {
        public struct Entry
        {
            public int Key; // kind<<24 | stableObjectId<<8 | compIdx
            public bool Occupied;
            public CardData Card; // valid when Occupied
        }

        private struct SlotState
        {
            public bool Occupied;
            public int Monster, Expansion, Border, Grade, GradedIdx;
            public bool Foil, Destiny, Champion;

            public bool Matches(CardData c)
            {
                return Occupied && c != null
                    && Monster == (int)c.monsterType && Expansion == (int)c.expansionType
                    && Border == (int)c.borderType && Grade == c.cardGrade
                    && GradedIdx == c.gradedCardIndex && Foil == c.isFoil
                    && Destiny == c.isDestiny && Champion == c.isChampionCard;
            }

            public static SlotState From(CardData c)
            {
                if (c == null)
                    return default;
                return new SlotState
                {
                    Occupied = true,
                    Monster = (int)c.monsterType,
                    Expansion = (int)c.expansionType,
                    Border = (int)c.borderType,
                    Grade = c.cardGrade,
                    GradedIdx = c.gradedCardIndex,
                    Foil = c.isFoil,
                    Destiny = c.isDestiny,
                    Champion = c.isChampionCard,
                };
            }
        }

        private readonly Dictionary<int, SlotState> _last = new Dictionary<int, SlotState>();
        private readonly Dictionary<int, double> _locallyChanged = new Dictionary<int, double>();
        private readonly HashSet<string> _snapshotErrors = new HashSet<string>();
        private float _timer;
        private const float BaseScanInterval = 0.9f;
        private const float MaxQuietScanInterval = 3.0f;
        private float _scanInterval = BaseScanInterval;
        private ShelfManager _sm;

        /// <summary>A display slot THIS client placed a card into and the host has not yet
        /// explicitly confirmed. The card left the shared collection the moment it was picked up
        /// from the binder, so while it sits in this state the local card3d is the ONLY copy. A
        /// snapshot-like empty entry must therefore never destroy it: the client re-sends the
        /// placement until the host echoes an authoritative result (see the two-arg
        /// <see cref="ApplyRemote"/>), and only an EXPLICIT empty echo banks the card back into
        /// the collection.</summary>
        private struct PendingPlacement
        {
            public CardData Card;
            public double LastSend;
            public int Attempts;
        }

        private readonly Dictionary<int, PendingPlacement> _pendingPlacements = new Dictionary<int, PendingPlacement>();
        private float _pendingResendTimer;
        private const float PendingResendInterval = 2.0f;
        private const int PendingResendMax = 15; // ~30s, then stop resending but keep the card flagged on the display

        /// <summary>Live instance, for the card-compartment placement patch. Mirrors
        /// PlayTableSync.Active; the module registry owns Start/Dispose.</summary>
        public static CardShelfSync Active;

        // Time-sliced scan state: the walk is spread over frames, budget in SHELVES.
        private readonly ListScanCursor _cursor = new ListScanCursor { Budget = 12 };
        private System.Collections.IList[] _groups;
        private bool _scanning;
        private List<Entry> _scanChanges;
        private bool _sawError;

        public Action<List<Entry>> OnLocalChanges;

        /// <summary>Client role: adopt unknown slots silently instead of reporting them
        /// (a joiner only reports transitions it witnessed against a known baseline -
        /// reporting its own stale/empty view is how the host's stands got wiped), and
        /// protect fresh local edits from stale host echoes.</summary>
        public bool IsClientRole;

        public override string Name => "card-shelves";

        public override void Start() => Active = this;

        public override void Dispose()
        {
            if (ReferenceEquals(Active, this))
                Active = null;
            base.Dispose();
        }

        public override void ForceResend() => ForceNextTick();

        public override void Reset()
        {
            if (_pendingPlacements.Count > 0)
            {
                // A teardown/world reload can arrive before the host confirms a local placement.
                // Banking during teardown is unsafe (the world may be replaced by the host's
                // snapshot in the same beat), so this is deliberately left as the one remaining
                // path where an unconfirmed placement can be lost - but it must never be silent.
                CoopPlugin.Log.LogWarning(
                    $"CardShelfSync: reset with {_pendingPlacements.Count} unconfirmed display placement(s) - the host never confirmed them");
            }
            _last.Clear();
            _locallyChanged.Clear();
            _pendingPlacements.Clear();
            _pendingResendTimer = 0f;
            _snapshotErrors.Clear();
            _timer = 0.1f; // staggered phase vs the other snapshot engines
            _scanInterval = BaseScanInterval;
            _sm = null;
            _scanning = false;
            _groups = null;
            _scanChanges = null;
            _cursor.Reset();
        }

        /// <summary>The local display structure changed under us (population repair
        /// respawned a shelf): the baseline is meaningless now. Client mode silently
        /// re-adopts; the host's periodic full resync repaints whatever got wiped.</summary>
        public void InvalidateBaseline()
        {
            _last.Clear();
        }

        public void ForceNextTick()
        {
            _scanInterval = BaseScanInterval;
            _timer = _scanInterval;
        }

        private ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        public void Tick(float dt, bool active)
        {
            if (!active)
                return;
            _timer += dt;
            TickPendingResend(dt);
            if (!_scanning)
            {
                if (_timer < _scanInterval)
                    return;
                _timer -= _scanInterval;
                var sm = Sm();
                if (sm == null)
                    return;
                if (_groups == null || _groups.Length != 3)
                    _groups = new System.Collections.IList[3];
                _groups[0] = sm.m_CardShelfList;
                _groups[1] = sm.m_CardItemCombiShelfList;
                _groups[2] = sm.m_TournamentPrizeShelfList;
                _cursor.Reset();
                _scanning = true;
                _scanChanges = null;
                _sawError = false;
            }
            try
            {
                _cursor.Scan(_groups, VisitCardShelf);
            }
            catch (Exception e)
            {
                _sawError = true;
                LogSnapshotError("snapshot", e);
                _scanning = false;
                return;
            }
            if (_cursor.Done)
            {
                _scanning = false;
                if (!_sawError && _scanChanges != null && _scanChanges.Count > 0)
                {
                    _scanInterval = BaseScanInterval;
                    OnLocalChanges?.Invoke(_scanChanges);
                }
                else if (!_sawError)
                {
                    _scanInterval = Math.Min(MaxQuietScanInterval, _scanInterval * 1.25f);
                }
            }
        }

        /// <summary>Visit one card shelf (kind 2, 3 or 14) and all of its card slots.</summary>
        private void VisitCardShelf(object item, int group, int index)
        {
            var shelf = item as CardShelf;
            if (shelf == null || !shelf.gameObject.activeInHierarchy)
                return; // boxed/carried
            int kind = group == 0 ? 2 : (group == 1 ? 3 : 14);
            List<InteractableCardCompartment> comps;
            try
            {
                comps = shelf.GetCardCompartmentList();
            }
            catch (Exception e) { _sawError = true; LogSnapshotError(kind + ":" + index, e); return; }
            for (int j = 0; j < comps.Count; j++)
            {
                try
                {
                    var comp = comps[j];
                    if (comp == null)
                        continue;
                    if (!PlacedObjectIdentity.TryMakeCompartmentKey(kind, shelf, j, out int key))
                        continue;
                    if (!TryReadSlot(comp, out CardData card))
                        continue;
                    bool occupied = card != null;
                    bool known = _last.TryGetValue(key, out var st);
                    bool pending = IsClientRole && _pendingPlacements.ContainsKey(key);
                    if (known && st.Occupied == occupied && (!occupied || st.Matches(card)))
                        continue;
                    if (!known && IsClientRole && !pending)
                    {
                        // A joiner's first sighting of a slot it did not touch is the host's
                        // world: adopt it silently. A slot THIS client just placed into is
                        // NOT adopted, or the placement would never be announced and the
                        // host's next authoritative snapshot would erase the only copy.
                        _last[key] = SlotState.From(card);
                        continue;
                    }
                    if (_scanChanges == null)
                        _scanChanges = new List<Entry>();
                    if (_scanChanges.Count >= 128)
                        continue; // leave un-recorded; picked up next scan
                    _last[key] = SlotState.From(card);
                    if (pending)
                    {
                        // Keep the pending record in step with what is actually on the shelf:
                        // an occupied change refreshes the card, an empty change means the
                        // player took it back (it is in hand now, no longer a placement).
                        if (occupied)
                            _pendingPlacements[key] = new PendingPlacement
                            {
                                Card = card,
                                LastSend = Time.realtimeSinceStartupAsDouble,
                                Attempts = 0,
                            };
                        else
                            _pendingPlacements.Remove(key);
                    }
                    if (IsClientRole)
                        _locallyChanged[key] = Time.realtimeSinceStartupAsDouble;
                    _scanChanges.Add(new Entry { Key = key, Occupied = occupied, Card = card });
                }
                catch (Exception e) { _sawError = true; LogSnapshotError(kind + ":" + index + ":" + j, e); }
            }
        }

        private void LogSnapshotError(string item, Exception e)
        {
            if (_snapshotErrors.Add(item))
                CoopPlugin.Log.LogWarning("CardShelfSync snapshot item " + item + ": " + e.Message);
        }

        /// <summary>Remove a displayed card the way the vanilla purchase path does:
        /// DisableAllCard alone despawns the card but leaves the slot's PRICE TAG
        /// showing the sold card's price forever (the "stuck tag" bug). Mirrors
        /// InteractableCardCompartment.RemoveCardFromShelf's tag bookkeeping.</summary>
        private static void ClearSlot(InteractableCardCompartment comp)
        {
            comp.DisableAllCard(); // game's own card3d cleanup
            try
            {
                comp.m_StoredCardList.Clear();
                for (int i = 0; i < comp.m_InteractablePriceTagList.Count; i++)
                    comp.m_InteractablePriceTagList[i].SetPriceChecked(isPriceSet: false);
                comp.SetPriceTagCardData(null);
                comp.SetPriceTagVisibility(isVisible: false);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("CardShelfSync tag clear: " + ex.Message);
            }
        }

        /// <summary>False = state unknown right now (a stored card's pooled UI is
        /// distance-culled or detached) - callers must NOT treat that as empty.</summary>
        private static bool TryReadSlot(InteractableCardCompartment comp, out CardData card)
        {
            card = null;
            if (comp.m_StoredCardList.Count == 0)
                return true; // genuinely empty
            var card3d = comp.m_StoredCardList[0];
            if (card3d == null || card3d.m_Card3dUI == null || card3d.m_Card3dUI.m_CardUI == null)
                return false;
            card = card3d.m_Card3dUI.m_CardUI.GetCardData();
            return card != null;
        }

        public void ApplyRemote(List<Entry> entries) => ApplyRemote(entries, false);

        /// <summary>Apply host-authoritative card-display state. <paramref name="echo"/> is true
        /// only on the host's direct reply to this client's own placement request. The distinction
        /// is load-bearing: a snapshot (echo false) that says a pending slot is empty may have been
        /// sent before the host ever saw our request, so it must NOT destroy the placement. Only an
        /// echo can resolve a pending placement - occupied (host has it) or empty (host rejected
        /// it, so we bank the card instead of losing it).</summary>
        public void ApplyRemote(List<Entry> entries, bool echo)
        {
            var sm = Sm();
            if (sm == null)
                return;
            foreach (var e in entries)
            {
                try
                {
                    if (IsClientRole && _pendingPlacements.TryGetValue(e.Key, out var pending))
                    {
                        bool isMine = e.Occupied && PendingMatches(pending.Card, e.Card);
                        if (isMine)
                        {
                            // the host holds this exact card on the slot: confirmed, whether it
                            // arrived as the direct echo or a later authoritative snapshot
                            _pendingPlacements.Remove(e.Key);
                            _locallyChanged.Remove(e.Key);
                        }
                        else if (echo)
                        {
                            // the host processed the request and the slot does NOT hold our card
                            // (it is empty, or another card replaced it). Our card left the
                            // collection at pickup, so bank it back rather than destroying it.
                            CoopPlugin.Log.LogWarning(
                                $"CardShelfSync: host placement result for {CardName(pending.Card)} at {e.Key:X} was '{CardName(e.Card)}' - returning our card to the binder instead of losing it");
                            ReturnPendingToBinder(pending.Card);
                            _pendingPlacements.Remove(e.Key);
                            _locallyChanged.Remove(e.Key);
                            // fall through: apply the host's authoritative state below
                        }
                        else
                        {
                            // snapshot that may predate the host seeing our request: keep the
                            // placement and wait for the echo rather than erase the only copy
                            continue;
                        }
                    }
                    // my own fresh edit is still round-tripping to the host; a stale
                    // echo (or the periodic full resync) must not stomp it
                    if (IsClientRole && _locallyChanged.TryGetValue(e.Key, out double t)
                        && Time.realtimeSinceStartupAsDouble - t < 6.0)
                        continue;
                    var comp = Resolve(sm, e.Key);
                    if (comp == null)
                        continue;
                    // a culled slot (card present, pooled UI detached) matching what we
                    // last knew is almost certainly correct - rebuilding it every heal
                    // broadcast was a mass destroy/respawn spike whenever the player
                    // stood far from a card wall
                    if (e.Occupied && comp.m_StoredCardList.Count > 0
                        && _last.TryGetValue(e.Key, out var known) && known.Matches(e.Card)
                        && !TryReadSlot(comp, out _))
                        continue;
                    ApplySlot(comp, e);
                    _last[e.Key] = SlotState.From(e.Occupied ? e.Card : null);
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning($"CardShelfSync apply {e.Key:X}: {ex.Message}");
                }
            }
        }

        /// <summary>Host: the authoritative post-apply state of the given slots, sent back as the
        /// echo. A key that does not resolve at all is reported EMPTY so the placing client can
        /// bank the card; an unreadable (culled) slot is OMITTED so the client keeps waiting
        /// rather than banking a card the host actually holds.</summary>
        public List<Entry> ReadEntries(List<Entry> requested)
        {
            var result = new List<Entry>();
            if (requested == null)
                return result;
            var sm = Sm();
            if (sm == null)
                return result;
            for (int i = 0; i < requested.Count; i++)
            {
                var e = requested[i];
                try
                {
                    var comp = Resolve(sm, e.Key);
                    if (comp == null)
                    {
                        result.Add(new Entry { Key = e.Key, Occupied = false });
                        continue;
                    }
                    if (!TryReadSlot(comp, out CardData card))
                        continue; // unreadable: say nothing, keep the client pending
                    result.Add(new Entry { Key = e.Key, Occupied = card != null, Card = card });
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning($"CardShelfSync read {e.Key:X}: {ex.Message}");
                }
            }
            return result;
        }

        private static bool PendingMatches(CardData pending, CardData remote)
        {
            return pending != null && remote != null && SlotState.From(pending).Matches(remote);
        }

        private static string CardName(CardData c)
        {
            if (c == null)
                return "(null)";
            return c.expansionType + "#" + (int)c.monsterType
                + (c.cardGrade > 0 ? " grade " + c.cardGrade : "");
        }

        private static void ReturnPendingToBinder(CardData card)
        {
            if (card == null)
                return;
            try
            {
                // A graded card must be registered with Grading Overhaul before AddCard or its
                // anti-cheat re-encodes the cert as fake (same rule as ApplyCardDelta).
                if (card.cardGrade > 10 && Util.GradingInterop.Present)
                    Util.GradingInterop.Remember(card);
                CPlayerData.AddCard(card, 1);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("CardShelfSync return-to-binder failed: " + e.Message);
            }
        }

        /// <summary>Client: remember that a card was placed into a display slot by THIS player, so
        /// the mirror never silently adopts over it and never destroys it before the host has
        /// explicitly answered. Called from the card-compartment placement patch.</summary>
        internal static void MarkLocalPlacement(InteractableCardCompartment comp)
        {
            var self = Active;
            if (self == null || !self.IsClientRole || comp == null)
                return;
            try
            {
                var shelf = comp.GetCardShelf();
                if (shelf == null || !self.TryKindOf(shelf, out int kind))
                    return;
                int compIdx = CompartmentIndex(shelf, comp);
                if (compIdx < 0)
                    return;
                if (!PlacedObjectIdentity.TryMakeCompartmentKey(kind, shelf, compIdx, out int key))
                {
                    CoopPlugin.Log.LogWarning(
                        "CardShelfSync: a card was placed on a display this client has not bound yet - the placement will not sync");
                    return;
                }
                if (!TryReadSlot(comp, out CardData card) || card == null)
                    return;
                self._pendingPlacements[key] = new PendingPlacement
                {
                    Card = card,
                    LastSend = Time.realtimeSinceStartupAsDouble,
                    Attempts = 0,
                };
                self._locallyChanged[key] = Time.realtimeSinceStartupAsDouble;
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        private void TickPendingResend(float dt)
        {
            if (!IsClientRole || _pendingPlacements.Count == 0)
                return;
            _pendingResendTimer += dt;
            if (_pendingResendTimer < PendingResendInterval)
                return;
            _pendingResendTimer = 0f;
            var entries = new List<Entry>();
            var keys = new List<int>(_pendingPlacements.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                int key = keys[i];
                var p = _pendingPlacements[key];
                if (p.Attempts >= PendingResendMax)
                {
                    if (p.Attempts == PendingResendMax)
                    {
                        // Stop resending but KEEP the placement tracked. Banking the card while it
                        // may already sit on the host's display would duplicate it; clearing the
                        // local slot could destroy the only copy. Instead notify once and leave it
                        // flagged so a later host echo still resolves it.
                        p.Attempts++;
                        _pendingPlacements[key] = p;
                        CoopPlugin.Log.LogWarning(
                            $"CardShelfSync: placement for {CardName(p.Card)} at {key:X} still unconfirmed after {PendingResendMax} attempts; keeping it on the display and waiting for the host");
                        CoopCore.Instance?.World?.RequestResync?.Invoke();
                        if (CoopCore.Instance != null)
                        {
                            CoopCore.Instance.RegisterLine = "a placed card is waiting for the host to confirm - it is safe on the display";
                            CoopCore.Instance.RegisterLineTimer = 6f;
                        }
                    }
                    continue;
                }
                p.Attempts++;
                _pendingPlacements[key] = p;
                _locallyChanged[key] = Time.realtimeSinceStartupAsDouble;
                entries.Add(new Entry { Key = key, Occupied = true, Card = p.Card });
            }
            if (entries.Count > 0)
                OnLocalChanges?.Invoke(entries);
        }

        private bool TryKindOf(CardShelf shelf, out int kind)
        {
            var sm = Sm();
            if (sm != null)
            {
                if (ContainsRef(sm.m_CardShelfList, shelf))
                {
                    kind = 2;
                    return true;
                }
                if (ContainsRef(sm.m_CardItemCombiShelfList, shelf))
                {
                    kind = 3;
                    return true;
                }
                if (ContainsRef(sm.m_TournamentPrizeShelfList, shelf))
                {
                    kind = 14;
                    return true;
                }
            }
            kind = 0;
            return false;
        }

        private static bool ContainsRef<T>(List<T> list, CardShelf shelf) where T : CardShelf
        {
            if (list == null)
                return false;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], shelf))
                    return true;
            return false;
        }

        private static int CompartmentIndex(CardShelf shelf, InteractableCardCompartment comp)
        {
            var comps = shelf.GetCardCompartmentList();
            for (int i = 0; i < comps.Count; i++)
                if (ReferenceEquals(comps[i], comp))
                    return i;
            return -1;
        }

        private static InteractableCardCompartment Resolve(ShelfManager sm, int key)
        {
            int kind = key >> 24;
            ushort objectId = PlacedObjectIdentity.ObjectIdFromCompartmentKey(key);
            int compIdx = key & 0xFF;
            if (!PlacedObjectIdentity.TryResolve(sm, kind, objectId, out var obj))
                return null;
            var shelf = obj as CardShelf;
            if (shelf == null)
                return null;
            var comps = shelf.GetCardCompartmentList();
            return compIdx < comps.Count ? comps[compIdx] : null;
        }

        private static void ApplySlot(InteractableCardCompartment comp, Entry e)
        {
            bool hasCard = comp.m_StoredCardList.Count > 0;
            if (!e.Occupied)
            {
                if (hasCard)
                    ClearSlot(comp);
                return;
            }
            if (hasCard)
            {
                // matching readable card: done; different or unreadable: replace clean
                // (spawning on top of an existing card3d would stack duplicates)
                if (TryReadSlot(comp, out CardData current) && current != null
                    && SlotState.From(current).Matches(e.Card))
                    return;
                ClearSlot(comp);
            }

            // The game's save-load recipe for putting a card on display (CardShelf.LoadCardCompartment)
            var cardUI = CSingleton<Card3dUISpawner>.Instance.GetCardUI();
            var card3d = ShelfManager.SpawnInteractableObject(EObjectType.Card3d).GetComponent<InteractableCard3d>();
            cardUI.m_IgnoreCulling = true;
            cardUI.m_CardUI.SetFoilCullListVisibility(isActive: true);
            cardUI.SetSimplifyCardDistanceCull(isCull: false);
            cardUI.m_CardUI.ResetFarDistanceCull();
            cardUI.m_CardUI.SetCardUI(e.Card);
            cardUI.transform.position = card3d.transform.position;
            cardUI.transform.rotation = card3d.transform.rotation;
            card3d.SetCardUIFollow(cardUI);
            card3d.SetEnableCollision(isEnable: false);
            comp.SetCardOnShelf(card3d);
            cardUI.m_IgnoreCulling = false;
        }

        /// <summary>Host: full authoritative slot state for the periodic heal broadcast.
        /// Unreadable slots (host's own culled cards) are OMITTED rather than guessed -
        /// absence means "no instruction", so clients keep what they have.</summary>
        public List<Entry> BuildFullState()
        {
            var full = new List<Entry>();
            var sm = Sm();
            if (sm == null)
                return full;
            Collect(sm.m_CardShelfList, 2, full);
            Collect(sm.m_CardItemCombiShelfList, 3, full);
            Collect(sm.m_TournamentPrizeShelfList, 14, full);
            return full;
        }

        private static void Collect<T>(List<T> shelves, int kind, List<Entry> into) where T : CardShelf
        {
            for (int i = 0; i < shelves.Count; i++)
            {
                var shelf = shelves[i];
                if (shelf == null || !shelf.gameObject.activeInHierarchy)
                    continue;
                var comps = shelf.GetCardCompartmentList();
                for (int j = 0; j < comps.Count; j++)
                {
                    var comp = comps[j];
                    if (comp == null || !TryReadSlot(comp, out CardData card))
                        continue;
                    if (PlacedObjectIdentity.TryMakeCompartmentKey(kind, shelf, j, out int key))
                        into.Add(new Entry
                        {
                            Key = key,
                            Occupied = card != null,
                            Card = card,
                        });
                }
            }
        }

        // ---- wire format ----
    }
}
