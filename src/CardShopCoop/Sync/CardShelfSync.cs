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
    public class CardShelfSync
    {
        public struct Entry
        {
            public int Key; // kind<<24 | shelfIdx<<8 | compIdx  (kind: 2 card shelf, 3 combi)
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
                if (c == null) return default;
                return new SlotState
                {
                    Occupied = true,
                    Monster = (int)c.monsterType, Expansion = (int)c.expansionType,
                    Border = (int)c.borderType, Grade = c.cardGrade,
                    GradedIdx = c.gradedCardIndex, Foil = c.isFoil,
                    Destiny = c.isDestiny, Champion = c.isChampionCard,
                };
            }
        }

        private readonly Dictionary<int, SlotState> _last = new Dictionary<int, SlotState>();
        private readonly Dictionary<int, double> _locallyChanged = new Dictionary<int, double>();
        private float _timer;
        private ShelfManager _sm;

        public Action<List<Entry>> OnLocalChanges;

        /// <summary>Client role: adopt unknown slots silently instead of reporting them
        /// (a joiner only reports transitions it witnessed against a known baseline -
        /// reporting its own stale/empty view is how the host's stands got wiped), and
        /// protect fresh local edits from stale host echoes.</summary>
        public bool IsClientRole;

        public void Reset()
        {
            _last.Clear();
            _locallyChanged.Clear();
            _timer = 0.1f; // staggered phase vs the other snapshot engines
            _sm = null;
        }

        /// <summary>The local display structure changed under us (population repair
        /// respawned a shelf): the baseline is meaningless now. Client mode silently
        /// re-adopts; the host's periodic full resync repaints whatever got wiped.</summary>
        public void InvalidateBaseline()
        {
            _last.Clear();
        }

        private ShelfManager Sm()
        {
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        public void Tick(float dt, bool active)
        {
            if (!active) return;
            _timer += dt;
            if (_timer < 0.9f) return;
            _timer -= 0.9f;

            List<Entry> changes = null;
            try
            {
                var sm = Sm();
                if (sm == null) return;
                Walk(sm.m_CardShelfList, 2, ref changes);
                Walk(sm.m_CardItemCombiShelfList, 3, ref changes);
                Walk(sm.m_TournamentPrizeShelfList, 14, ref changes); // prize cards on display
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("CardShelfSync snapshot: " + e.Message);
                return;
            }
            if (changes != null && changes.Count > 0)
                OnLocalChanges?.Invoke(changes);
        }

        private void Walk<T>(List<T> shelves, int kind, ref List<Entry> changes) where T : CardShelf
        {
            for (int i = 0; i < shelves.Count; i++)
            {
                var shelf = shelves[i];
                if (shelf == null || !shelf.gameObject.activeInHierarchy) continue; // boxed/carried
                var comps = shelf.GetCardCompartmentList();
                for (int j = 0; j < comps.Count; j++)
                {
                    var comp = comps[j];
                    if (comp == null) continue;
                    int key = (kind << 24) | ((i & 0xFFFF) << 8) | (j & 0xFF);
                    // unreadable (a card is there but its pooled UI is culled/detached)
                    // is NOT empty - misreporting it as empty wipes the other side
                    if (!TryReadSlot(comp, out CardData card)) continue;
                    bool occupied = card != null;

                    if (_last.TryGetValue(key, out var st))
                    {
                        if (st.Occupied == occupied && (!occupied || st.Matches(card)))
                            continue;
                    }
                    else if (IsClientRole)
                    {
                        _last[key] = SlotState.From(card); // adopt silently, never report
                        continue;
                    }

                    if (changes == null) changes = new List<Entry>();
                    if (changes.Count >= 128) return; // rest next tick
                    _last[key] = SlotState.From(card);
                    if (IsClientRole) _locallyChanged[key] = Time.realtimeSinceStartupAsDouble;
                    changes.Add(new Entry { Key = key, Occupied = occupied, Card = card });
                }
            }
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
            if (comp.m_StoredCardList.Count == 0) return true; // genuinely empty
            var card3d = comp.m_StoredCardList[0];
            if (card3d == null || card3d.m_Card3dUI == null || card3d.m_Card3dUI.m_CardUI == null)
                return false;
            card = card3d.m_Card3dUI.m_CardUI.GetCardData();
            return card != null;
        }

        public void ApplyRemote(List<Entry> entries)
        {
            var sm = Sm();
            if (sm == null) return;
            foreach (var e in entries)
            {
                try
                {
                    // my own fresh edit is still round-tripping to the host; a stale
                    // echo (or the periodic full resync) must not stomp it
                    if (IsClientRole && _locallyChanged.TryGetValue(e.Key, out double t)
                        && Time.realtimeSinceStartupAsDouble - t < 6.0)
                        continue;
                    var comp = Resolve(sm, e.Key);
                    if (comp == null) continue;
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

        private static InteractableCardCompartment Resolve(ShelfManager sm, int key)
        {
            int kind = key >> 24;
            int shelfIdx = (key >> 8) & 0xFFFF;
            int compIdx = key & 0xFF;
            CardShelf shelf = null;
            if (kind == 2 && shelfIdx < sm.m_CardShelfList.Count) shelf = sm.m_CardShelfList[shelfIdx];
            else if (kind == 3 && shelfIdx < sm.m_CardItemCombiShelfList.Count) shelf = sm.m_CardItemCombiShelfList[shelfIdx];
            else if (kind == 14 && shelfIdx < sm.m_TournamentPrizeShelfList.Count) shelf = sm.m_TournamentPrizeShelfList[shelfIdx];
            if (shelf == null) return null;
            var comps = shelf.GetCardCompartmentList();
            return compIdx < comps.Count ? comps[compIdx] : null;
        }

        private static void ApplySlot(InteractableCardCompartment comp, Entry e)
        {
            bool hasCard = comp.m_StoredCardList.Count > 0;
            if (!e.Occupied)
            {
                if (hasCard) ClearSlot(comp);
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
            if (sm == null) return full;
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
                if (shelf == null || !shelf.gameObject.activeInHierarchy) continue;
                var comps = shelf.GetCardCompartmentList();
                for (int j = 0; j < comps.Count; j++)
                {
                    var comp = comps[j];
                    if (comp == null || !TryReadSlot(comp, out CardData card)) continue;
                    into.Add(new Entry
                    {
                        Key = (kind << 24) | ((i & 0xFFFF) << 8) | (j & 0xFF),
                        Occupied = card != null,
                        Card = card,
                    });
                }
            }
        }

        // ---- wire format ----

        public static void WriteEntries(BinaryWriter bw, List<Entry> entries)
        {
            bw.Write((ushort)entries.Count);
            foreach (var e in entries)
            {
                bw.Write(e.Key);
                bw.Write(e.Occupied);
                if (e.Occupied) Msg.WriteCard(bw, e.Card);
            }
        }

        public static List<Entry> ReadEntries(BinaryReader br)
        {
            int n = br.ReadUInt16();
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new Entry { Key = br.ReadInt32(), Occupied = br.ReadBoolean() };
                if (e.Occupied) e.Card = Msg.ReadCard(br);
                list.Add(e);
            }
            return list;
        }
    }
}
