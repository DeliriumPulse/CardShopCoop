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
        private float _timer;
        private ShelfManager _sm;

        public Action<List<Entry>> OnLocalChanges;

        public void Reset()
        {
            _last.Clear();
            _timer = 0f;
            _sm = null;
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
            _timer = 0f;

            List<Entry> changes = null;
            try
            {
                var sm = Sm();
                if (sm == null) return;
                Walk(sm.m_CardShelfList, 2, ref changes);
                Walk(sm.m_CardItemCombiShelfList, 3, ref changes);
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
                if (shelf == null) continue;
                var comps = shelf.GetCardCompartmentList();
                for (int j = 0; j < comps.Count; j++)
                {
                    var comp = comps[j];
                    if (comp == null) continue;
                    int key = (kind << 24) | ((i & 0xFFFF) << 8) | (j & 0xFF);
                    CardData card = ReadSlot(comp);
                    bool occupied = card != null;

                    if (_last.TryGetValue(key, out var st)
                        && st.Occupied == occupied && (!occupied || st.Matches(card)))
                        continue;

                    if (changes == null) changes = new List<Entry>();
                    if (changes.Count >= 128) return; // rest next tick
                    _last[key] = SlotState.From(card);
                    changes.Add(new Entry { Key = key, Occupied = occupied, Card = card });
                }
            }
        }

        private static CardData ReadSlot(InteractableCardCompartment comp)
        {
            if (comp.m_StoredCardList.Count == 0) return null;
            var card3d = comp.m_StoredCardList[0];
            if (card3d == null || card3d.m_Card3dUI == null || card3d.m_Card3dUI.m_CardUI == null)
                return null;
            return card3d.m_Card3dUI.m_CardUI.GetCardData();
        }

        public void ApplyRemote(List<Entry> entries)
        {
            var sm = Sm();
            if (sm == null) return;
            foreach (var e in entries)
            {
                try
                {
                    var comp = Resolve(sm, e.Key);
                    if (comp == null) continue;
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
            if (shelf == null) return null;
            var comps = shelf.GetCardCompartmentList();
            return compIdx < comps.Count ? comps[compIdx] : null;
        }

        private static void ApplySlot(InteractableCardCompartment comp, Entry e)
        {
            var current = ReadSlot(comp);
            if (!e.Occupied)
            {
                if (current != null) comp.DisableAllCard(); // game's own cleanup path
                return;
            }
            if (current != null)
            {
                var st = SlotState.From(current);
                if (st.Matches(e.Card)) return; // already showing this exact card
                comp.DisableAllCard();
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
