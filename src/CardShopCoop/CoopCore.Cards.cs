using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        // card/price mirrors that arrived during a scene load, flushed once in-game
        private struct PendingCard
        {
            public bool IsAdd; public int Amount; public CardData Card;
        }
        private readonly List<PendingCard> _pendingCardDeltas = new List<PendingCard>();
        private readonly List<KeyValuePair<CardData, float>> _pendingCardPrices = new List<KeyValuePair<CardData, float>>();

        // OUTGOING card deltas leave through a per-frame outbox instead of one reliable frame
        // each. A "collect all machines" click fires 300-1300 AddCard/ReduceCard calls in ONE
        // frame; that many individual CardDelta frames swamped the reliable lane (SteamNet
        // drops a frame Steam refuses 30 frames running) - which is exactly how a guest's card
        // price edit went missing. Flushed at the end of Update, and by the send helpers before
        // any OTHER message goes out so today's global ordering is preserved.
        private readonly List<PendingCard> _cardDeltaOutbox = new List<PendingCard>();
        private const int CardDeltaBatchMax = 200; // deltas per CardDeltaBatch frame
        private bool _flushingCardDeltas;          // re-entrancy guard for the send-helper hook
        private readonly List<PendingCard> _batchRelayBuf = new List<PendingCard>();

        // ONE binder relayout per frame, not per delta: with the book open RefreshOpenBinder
        // invokes the game's OnSortingMethodUpdated (O(N^2) re-sort + 72-slot UI rebuild +
        // album total recompute), which per delta is the reported 20-30s freeze.
        private static bool _binderRefreshPending;

        // The per-delta apply line is the field-log diagnosis for "cards didn't show up in the
        // binder", so it survives verbatim for ordinary changes (<=5 applied in a frame) and
        // folds into one summary line for a flood. Emitted by FlushFrameCardWork.
        private static readonly List<PendingCard> _deltaLogBuf = new List<PendingCard>();
        private static int _deltaAppliedThisFrame;

        /// <summary>A card price WE set locally that the other side has not confirmed yet.
        /// Card prices had NO ack and NO retry: a single dropped reliable frame stranded the
        /// edit, and the host's 3s price heal then broadcast its own stale value back over it.</summary>
        private struct MyCardPrice
        {
            public CardData Card;   // snapshot: the postfix restores the live object's grade
            public float Value;
            public bool Acked;
            public double LastSend;
            public int Attempts;
        }
        private readonly Dictionary<string, MyCardPrice> _myCardPrices = new Dictionary<string, MyCardPrice>();
        private readonly List<string> _cardPriceRetryKeys = new List<string>(); // scratch: no mutate-while-iterating
        private float _cardPriceRetryTimer;
        private const int MyCardPriceMax = 1024;
        private const int CardPriceMaxAttempts = 12;
        /// <summary>Price-equality tolerance. Strictly ABOVE half a display quantum (0.005)
        /// plus float error, and still far below the smallest price step anyone cares about.
        /// The game's price store legitimately rounds by up to exactly half a quantum, and the
        /// quantum depends on each machine's LOCAL currency setting (2dp vs 3dp) - so a
        /// cross-currency pair landed EXACTLY on the old 0.005 and the ack test at the
        /// CardPriceSet handler became deterministically unreachable: every edit burned all 12
        /// retries and then falsely surrendered.</summary>
        private const float CardPriceEpsilon = 0.0075f;

        /// <summary>An item price WE just set. The host's PriceList is a full-table overwrite
        /// built BEFORE our ItemPriceContrib landed, so for a few seconds it would repaint our
        /// fresh price back to the old one. Bulk host state, so a recency window is enough.</summary>
        private struct MyItemPrice
        {
            public float Value; public double At;
        }
        private readonly Dictionary<int, MyItemPrice> _myItemPriceEdits = new Dictionary<int, MyItemPrice>();
        private const int MyItemPriceMax = 256;
        private const double ItemPriceHoldSeconds = 6.0;

        private static InteractionPlayerController _deltaIpc; // NEVER CSingleton<>.Instance (fake-manager landmine)

        /// <summary>Returns true when the delta was actually applied - the host's relay to
        /// OTHER guests keys off this, so a delta this side REFUSED (corrupt grade, would-go-
        /// negative registry mismatch) is never propagated onward and can't spread divergence.
        /// <paramref name="relayAnyway"/> separates "THIS PC lacks the content" from "this delta
        /// is garbage", exactly as the price path does: identical registries do NOT imply
        /// identical installed data (EPL seeds enum ids from enum_values.json even for bundles
        /// that aren't installed), so the host can fully RESOLVE a card it has no data row for.
        /// Refusing it locally is right; swallowing it is not - a third player who DOES have the
        /// pack must still receive it, which is what the 3+ player regression was. True for the
        /// CardSetInstalledHere refusal, for the graded-remove album mismatch and for the
        /// suppressed follow-up add that pairs with one; false for a corrupt grade, a
        /// would-go-negative reduce, and (via the callers' catch) any throw.</summary>
        private static bool ApplyCardDelta(bool isAdd, int amount, CardData card, out bool relayAnyway)
        {
            relayAnyway = false;
            // A cardGrade > 10 is NOT corruption when Grading Overhaul is installed: it's an
            // ENCODED grade (company + 1-10 grade + cert serial). The old hard 1-10 drop-guard
            // discarded every real graded card. Only a >10 grade WITHOUT Grading Overhaul is
            // impossible/genuine corruption (vanilla only writes 1-10), so still refuse that.
            if (card.cardGrade != 0 && (card.cardGrade < 1 || card.cardGrade > 10) && !Util.GradingInterop.Present)
            {
                CoopPlugin.Log.LogWarning($"card delta: dropping corrupt graded card {CardIdent(card)} (grade {card.cardGrade}) - not applied (Grading Overhaul absent)");
                return false;
            }
            Patches.GamePatches.ApplyingRemoteCards = true;
            try
            {
                // UNKNOWN-CARD guard - the mirror of the negative-reduce guard below, and the
                // price of tolerating extra registry entries in the handshake (FIX C): a card
                // can now arrive for a content pack THIS PC doesn't have. Every branch below
                // resolves the card's slot through CPlayerData.GetCardSaveIndex, whose loop over
                // InventoryBase.GetShownMonsterList simply leaves the index at 0 when the monster
                // isn't in the list - and GetShownMonsterList itself falls back to the TETRAMON
                // list for an expansion outside the vanilla switch. So on the vanilla path an
                // unknown card doesn't error: it silently credits save index 0, i.e. the
                // receiver's FIRST Tetramon card, quietly inflating a real card's count (and,
                // for a graded one, filing a bogus entry in the graded album). Under EPL the same
                // lookup is an IndexOf that returns -1, and CardCountList[-1] THROWS.
                // Guards ALL THREE branches (it used to sit inside the add arm only, leaving the
                // graded-remove and ungraded-reduce paths to reach GetCardSaveIndex unguarded).
                // Returns false so the host's relay doesn't spread it.
                if (!CardSetInstalledHere(card))
                {
                    // RELAY ANYWAY: the delta is well-formed, this PC just has no data row for
                    // that card. Other peers may well have the pack, and before 1.0.37 this
                    // case reached the ungraded-reduce arm, returned true (a vanilla no-op) and
                    // so kept relaying - dropping the relay is what broke 3+ player sessions.
                    relayAnyway = true;
                    // Memoized per card key, like the price path: one host log showed 995
                    // identical lines. Printed through CardIdent because an ordinary modded id
                    // is a small ORDINAL: below ~122 the bare monsterType renders an unrelated
                    // VANILLA name, at 123+ it renders a bare number (EMonsterType has no
                    // members up there) - neither identifies the card without the expansion.
                    if (_priceWarnedKeys.Add("delta:" + CardPriceKey(card)))
                        CoopPlugin.Log.LogWarning($"card delta: {CardIdent(card)} is from a card set you don't have installed - skipped");
                    return false;
                }
                if (isAdd)
                {
                    // PAIRED-ADD SUPPRESSION. A graded remove this PC could not satisfy, followed
                    // seconds later by an add of the SAME key, is one gesture on the sender: the
                    // card came out of the album into their hand / a grading submit slot and went
                    // straight back (GradedCardSubmitSelectScreen.OnCloseScreen AddCards every
                    // occupied slot in ONE frame, decompiled :84-95). Their net change is ZERO -
                    // they still own exactly one copy. Applying only the ADD half therefore
                    // MANUFACTURES a copy here. It looked like a heal because sometimes the
                    // absence was a real deficit, but that is a coin flip on state neither side
                    // can see, and when the cert is already present here in mutated form GO's
                    // duplicate-cert sweep (decompiled-grading :8532-8582) answers the add by
                    // flagging BOTH rows FAKE - so the "heal" corrupts a card that was fine.
                    // Relay-anyway rather than a silent drop, by the same rule as the
                    // CardSetInstalledHere case above: a third peer that genuinely owns the pair
                    // must still receive it.
                    if (card.cardGrade > 0 && ConsumeGradedRemoveSkip(card))
                    {
                        CoopPlugin.Log.LogWarning($"graded add suppressed: {CardIdent(card)} (grade {card.cardGrade}) pairs with the remove this PC skipped moments ago - the sender only MOVED a card they still own, and this PC never had that copy, so applying the add alone would create one out of nothing");
                        relayAnyway = true;
                        return false;
                    }
                    // Register the host's cert with Grading Overhaul BEFORE AddCard, so its
                    // anti-cheat AddCard prefix sees the cert burned+bound and does NOT
                    // re-encode this card as FAKE (the ~20s changing-grade churn). BindCert
                    // is keyed by cardSaveIndex/expansion/isDestiny, so it survives AddCard's
                    // compaction into a fresh CompactCardDataAmount.
                    if (card.cardGrade > 10)
                        Util.GradingInterop.Remember(card);
                    CPlayerData.AddCard(card, amount);
                }
                else if (card.cardGrade > 0)
                {
                    // graded cards live in m_GradedCardInventoryList; ReduceCard would miss
                    // them and wrongly decrement the ungraded array. Route through
                    // RemoveGradedCard (the graded-remove mirror normally arrives as
                    // MsgType.GradedRemove; this defends the CardDelta path too).
                    // Count what actually came out: removing NOTHING (album never had it -
                    // the graded analog of the registry mismatch below) must report
                    // not-applied so the host relay doesn't propagate a remove we refused.
                    int removed = 0;
                    for (int i = 0; i < amount && CPlayerData.HasGradedCardInAlbum(card); i++)
                    {
                        CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
                        removed++;
                    }
                    if (removed == 0)
                    {
                        RecordGradedRemoveSkip(card);
                        CoopPlugin.Log.LogWarning($"graded remove: {CardIdent(card)} (grade {card.cardGrade}) not in this album - skipped (album mismatch?)");
                        // RELAY ANYWAY, by the same argument that gave CardSetInstalledHere its
                        // relay above: THIS album saying nothing about a card says nothing about
                        // a THIRD peer's album. Before this the GradedRemove handler broke without
                        // relaying and the third player kept a ghost copy forever. No effect on a
                        // 2-player session.
                        relayAnyway = true;
                        return false;
                    }
                    // A remove for this key SUCCEEDED, so whatever the album was missing it is not
                    // missing now: drop the skip memo, or the NEXT legitimate re-add of the same
                    // card would be suppressed on the strength of a stale one.
                    _gradedRemoveSkipped.Remove(CardPriceKey(card));
                }
                else
                {
                    // Negative-reduce guard: a remove that outruns what this side actually owns
                    // silently underflows the collected-count array (ReduceCard just subtracts),
                    // which is the slow "total value drifts" leak. GetCardAmount resolves the
                    // owned count through the SAME GetCardSaveIndex + per-expansion collected list
                    // that ReduceCard decrements, so it's the exact amount the apply would hit.
                    // (Graded cards - cardGrade > 10 - never reach here; they route through
                    // RemoveGradedCard above.)
                    // No null-collected-list arm here any more: CardSetInstalledHere above owns
                    // that case (it refuses when GetCardCollectedList is null) and relays it on.
                    int owned = CPlayerData.GetCardAmount(card);
                    if (owned < amount)
                    {
                        CoopPlugin.Log.LogWarning($"card delta would drive {CardIdent(card)} negative (have {owned}, remove {amount}) - skipped (card registry mismatch?)");
                        return false;
                    }
                    CPlayerData.ReduceCard(card, amount);
                }
            }
            finally { Patches.GamePatches.ApplyingRemoteCards = false; }
            // "cards didn't show up in the binder" reports were undiagnosable from the
            // receiving side - applies were completely silent. The line still goes out for an
            // ordinary change; a bulk collect (hundreds of deltas in one frame) folds into one
            // summary instead of its own log flood. Both are emitted by FlushFrameCardWork.
            _deltaAppliedThisFrame++;
            if (_deltaLogBuf.Count < 5)
                _deltaLogBuf.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = SnapshotCard(card) });
            // Deferred to the end of the frame: RefreshOpenBinder is O(N^2) re-sort + full UI
            // rebuild whenever the book is open, and running it per delta is the 20-30s freeze.
            _binderRefreshPending = true;
            return true;
        }

        /// <summary>Shared by the CardDelta and CardDeltaBatch handlers: hold the delta if a
        /// scene load is in flight (applying mid-load crashes into uninitialized card data;
        /// nothing is lost, FlushPendingCardWork replays it), otherwise apply it. Returns true
        /// only when it was actually applied - i.e. when it may be relayed onward - and reports
        /// through <paramref name="relayAnyway"/> the "this PC lacks the content, but the delta
        /// is fine" refusal that must still be forwarded (see ApplyCardDelta).</summary>
        private bool ApplyOrHoldCardDelta(bool isAdd, int amount, CardData card, out bool relayAnyway)
        {
            relayAnyway = false;
            if (!InGameLevel())
            {
                // HELD, not relayAnyway. Note this is NOT "it will relay later": the replay in
                // FlushPendingCardWork applies without relaying, so a delta held across a scene
                // load never reaches the other guests. That is pre-existing 1.0.36 behavior and
                // is deliberately left alone here - the relay-anyway work is about content this
                // PC lacks, not about the load window.
                _pendingCardDeltas.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = card });
                return false;
            }
            return ApplyCardDelta(isAdd, amount, card, out relayAnyway);
        }

        /// <summary>A private copy of exactly the nine fields the wire carries. Anything that
        /// DEFERS a send must snapshot: AddCardPostfix/SetCardPricePostfix temporarily write the
        /// ENCODED grade into the game's live cardData and restore it in a finally, so reading
        /// the same object a frame later would ship the bare 1-10 grade instead.</summary>
        private static CardData SnapshotCard(CardData c)
        {
            return new CardData
            {
                expansionType = c.expansionType,
                monsterType = c.monsterType,
                borderType = c.borderType,
                isFoil = c.isFoil,
                isDestiny = c.isDestiny,
                isChampionCard = c.isChampionCard,
                isNew = c.isNew,
                cardGrade = c.cardGrade,
                gradedCardIndex = c.gradedCardIndex,
            };
        }

        /// <summary>Canonical identity of a card's MARKED PRICE - everything the price store
        /// keys on and nothing else (gradedCardIndex is a per-copy serial, isNew is cosmetic).
        /// Used both as the in-flight-edit key and as the human-readable id in the price logs,
        /// which is why it is a string rather than a packed hash.</summary>
        private static string CardPriceKey(CardData card)
        {
            if (card == null)
                return null;
            return (int)card.expansionType + ":" + (int)card.monsterType + ":" + (int)card.borderType
                + ":" + (card.isFoil ? 1 : 0) + (card.isDestiny ? 1 : 0) + (card.isChampionCard ? 1 : 0)
                + ":" + card.cardGrade;
        }

        /// <summary>Per-expansion set of the monster ids that genuinely have a data row on THIS
        /// machine, taken from InventoryBase.GetShownMonsterList - the one list EPL prefixes, so
        /// it reports the expansion's real card keys on the modded path and the vanilla ones on
        /// the vanilla path. Built lazily and kept for the session (the shown lists are
        /// ScriptableObject content: they do not change while the game runs).</summary>
        private static readonly Dictionary<ECardExpansionType, HashSet<EMonsterType>> _shownMonsters =
            new Dictionary<ECardExpansionType, HashSet<EMonsterType>>();

        /// <summary>Drop the shown-monster cache. Called from Shutdown beside EnumMap.Clear():
        /// the next session may load a different save/content set, and a stale membership set
        /// would either refuse cards this install now has or admit ones it doesn't.</summary>
        internal static void ClearCardSetCache()
        {
            _shownMonsters.Clear();
        }

        /// <summary>Graded removes this PC could NOT satisfy, keyed exactly as the album matches
        /// (<see cref="CardPriceKey"/> already carries expansion, monster, border, foil, isDestiny
        /// and the encoded grade - a superset of RemoveGradedCard's predicate) and stamped with
        /// realtimeSinceStartup. Read once, by the add arm, to recognise the second half of a
        /// stage-then-abandon gesture. Small and short-lived on purpose: it is a pairing hint, not
        /// state.</summary>
        private static readonly Dictionary<string, float> _gradedRemoveSkipped = new Dictionary<string, float>();
        /// <summary>Observed pair gaps in the field log run 3.2 / 4.9 / 10.2 / 13 / 19s, so 20s is
        /// already too tight - and 60s is still nowhere near "took it off the shelf again later".</summary>
        private const float GradedSkipWindow = 60f;
        private const int GradedSkipMax = 64;

        /// <summary>Cleared beside <see cref="ClearCardSetCache"/> on disconnect and on every
        /// scene load: a pairing hint from a dead session (or a different world) describes an
        /// album that no longer exists, and acting on it would suppress a legitimate add.</summary>
        internal static void ClearGradedSkipMemory()
        {
            _gradedRemoveSkipped.Clear();
            Util.GradingInterop.Reset();
        }

        private static void RecordGradedRemoveSkip(CardData card)
        {
            string key = CardPriceKey(card);
            if (key == null)
                return;
            if (_gradedRemoveSkipped.Count >= GradedSkipMax && !_gradedRemoveSkipped.ContainsKey(key))
            {
                string oldest = null;
                float at = float.MaxValue;
                foreach (var kv in _gradedRemoveSkipped)
                    if (kv.Value < at)
                    {
                        at = kv.Value;
                        oldest = kv.Key;
                    }
                if (oldest != null)
                    _gradedRemoveSkipped.Remove(oldest);
            }
            _gradedRemoveSkipped[key] = Time.realtimeSinceStartup;
        }

        /// <summary>True when a skipped graded remove for this exact key is still inside the
        /// window. Always CONSUMES the memo (expired or not) - it has done its one job either
        /// way, and leaving stale keys behind would just burn the 64 slots.</summary>
        private static bool ConsumeGradedRemoveSkip(CardData card)
        {
            string key = CardPriceKey(card);
            if (key == null)
                return false;
            float at;
            if (!_gradedRemoveSkipped.TryGetValue(key, out at))
                return false;
            _gradedRemoveSkipped.Remove(key);
            return Time.realtimeSinceStartup - at <= GradedSkipWindow;
        }

        /// <summary>Membership test: does a data row for this monster exist under this expansion
        /// on this machine? Fills the cache on first ask, but NEVER caches a null-or-empty list -
        /// that means "InventoryBase isn't ready yet" (pre-load, or mid scene swap), and latching
        /// it would turn a timing miss into a permanent refusal for the rest of the session.</summary>
        private static bool MonsterHasDataRowHere(ECardExpansionType expansion, EMonsterType monster)
        {
            // FABRICATED-SINGLETON GATE. InventoryBase.GetShownMonsterList reads
            // CSingleton<InventoryBase>.Instance, and that getter does NOT return null when the
            // real inventory is absent (client reload window - InGameLevel() stays true there):
            // it FABRICATES one (new GameObject + AddComponent + DontDestroyOnLoad) and caches
            // it forever, so the fake permanently shadows the real inventory for the rest of the
            // run. Same house rule as everywhere else in this file - see the comment above Inv()
            // (~"NEVER CSingleton<>.Instance for scene-lifetime managers"). Asking Inv() first
            // (FindObjectOfType, fabricates nothing) both avoids that and makes the no-latch
            // not-ready refusal below actually reachable: without it this window threw an NRE
            // out of the fake's empty fields and landed in CardSetInstalledHere's catch.
            if (Inv() == null)
                return false; // not ready - do not latch, do not fabricate
            HashSet<EMonsterType> set;
            if (!_shownMonsters.TryGetValue(expansion, out set))
            {
                var shown = InventoryBase.GetShownMonsterList(expansion);
                if (shown == null || shown.Count == 0)
                    return false; // not ready - do not latch
                set = new HashSet<EMonsterType>(shown);
                _shownMonsters[expansion] = set;
            }
            return set.Contains(monster);
        }

        /// <summary>True when THIS install can actually place the card - i.e. a data row for
        /// (expansion, monster) really exists here. Anything else would mis-index through
        /// GetCardSaveIndex/GetShownMonsterList into save slot 0 (silent album corruption on the
        /// vanilla path) or into CardCountList[-1] (a throw on the EPL path), so it is refused.
        /// Errs toward REFUSING on any throw.
        ///
        /// TWO ORACLES THAT LOOK RIGHT AND ARE NOT - do not reinstate either:
        ///
        ///  1. Enum.IsDefined(typeof(EMonsterType), ...). EPL never MINTS EMonsterType members.
        ///     A modded expansion numbers its cards as plain ORDINALS - (EMonsterType)(index+1),
        ///     1..N - so the ids collide with whatever vanilla names happen to sit at those
        ///     numbers. That split the pack's own cards in two, which is what made the field
        ///     reports so confusing: ordinals 1..122 PASSED IsDefined by pure numeric collision
        ///     and synced SILENTLY (they never reached the refusal log at all, and they landed
        ///     in the save slot of the colliding vanilla card); ordinals 123 and up failed the
        ///     check on EVERY machine - including both players' - and were universally refused,
        ///     logging as BARE NUMBERS because EMonsterType simply has no members up there.
        ///     So "some of the modded cards work" was the collision half, and the missing cards
        ///     were the 123+ half. Note the ECardExpansionType half of the check IS legitimate -
        ///     expansions genuinely ARE EPL-minted enum members - which is exactly why the two
        ///     halves look symmetric and are not.
        ///
        ///  2. InventoryBase.GetMonsterData(...) != null. EPL does not patch that method; it
        ///     rewrites the game's own CALL SITES with a transpiler. A third-party caller like
        ///     this mod runs the ORIGINAL body, which for a modded id returns null or - worse -
        ///     the wrong vanilla monster's data by collision.
        ///
        /// The oracle that holds on both paths is per-expansion MEMBERSHIP in
        /// GetShownMonsterList, which EPL prefixes with the expansion's real card keys:
        /// membership means "a data row exists here", which is precisely the question.</summary>
        internal static bool CardSetInstalledHere(CardData card)
        {
            try
            {
                if (card == null)
                    return false;
                // Refuse the None sentinels explicitly. Since 1.0.37 an incoming modded id whose
                // NAME does not exist on this PC is translated to the game's own None member
                // (Util.EnumMap.FromWire) instead of arriving as a foreign number, and None is a
                // DEFINED member of both enums (ECardExpansionType.None = -1, EMonsterType.None
                // = 0). Neither value names a real card, so refusing them costs nothing.
                if (card.expansionType == ECardExpansionType.None)
                    return false;
                if (card.monsterType == EMonsterType.None)
                    return false;
                // Expansions ARE EPL-minted enum members, so IsDefined is the correct oracle
                // HERE (and only here - see the doc comment above).
                if (!Enum.IsDefined(typeof(ECardExpansionType), card.expansionType))
                    return false;
                // ...and the expansion must still be one this save actually has a collected list
                // for. Without EPL's interceptor woven in, GetCardCollectedList returns null for
                // an out-of-vocabulary expansion, and every downstream lookup would fall through
                // GetShownMonsterList's default arm onto the TETRAMON list - the slot-0 mis-index.
                if (CPlayerData.GetCardCollectedList(card.expansionType, card.isDestiny) == null)
                    return false;
                return MonsterHasDataRowHere(card.expansionType, card.monsterType);
            }
            catch (Exception e)
            {
                // MEMOIZED like every other per-card warning here. This catch used to be
                // effectively dead (the not-ready window NRE'd elsewhere); now that the
                // fabricated-singleton gate in MonsterHasDataRowHere returns cleanly, anything
                // that still throws here throws on EVERY card - and this path runs up to
                // CardDeltaBatchMax times per frame during a collect-all burst.
                if (_priceWarnedKeys.Add("check:" + CardPriceKey(card)))
                    CoopPlugin.Log.LogWarning("card set check: " + e.Message);
                return false;
            }
        }

        /// <summary>Human-readable card id for the log lines that can now carry MODDED cards.
        /// A modded expansion numbers its cards as plain ORDINALS (1..N), so for anything at or
        /// past the vanilla expansion range the bare monsterType renders a completely unrelated
        /// vanilla member NAME by numeric collision - which is worse than useless in a field log.
        /// Vanilla expansions keep the readable name; modded ones print Expansion#N.
        ///
        /// None (= -1, NOT a missing member) needs its own arm: it is numerically BELOW MAX, so
        /// the vanilla branch used to claim it and render whatever monster name collides with that
        /// ordinal - a confidently wrong card name on exactly the rows where the expansion is the
        /// thing that failed to resolve (a card off the wire from a pack this PC lacks, or one
        /// whose expansion id did not map). Naming the unknown beats naming the wrong card.</summary>
        private static string CardIdent(CardData c)
        {
            if (c == null)
                return "(null card)";
            if (c.expansionType == ECardExpansionType.None)
                return "unknown-pack card #" + (int)c.monsterType;
            if ((int)c.expansionType < (int)ECardExpansionType.MAX)
                return c.monsterType.ToString();
            return c.expansionType + "#" + (int)c.monsterType;
        }

        /// <summary>Shared "this PC can't process that card" warning for the sites that have no
        /// choice but to SKIP a card outright (grade-return, card-box collect) - unlike the delta
        /// path there is no relay to fall back on, so the card is genuinely lost here and a silent
        /// `continue` left zero trace in the field logs. Memoized per (context, card) on the same
        /// set the price/delta warnings use: a rejected 300-card grading submission would
        /// otherwise print 300 lines.</summary>
        internal static void WarnRefusedCard(CardData c, string context)
        {
            if (c == null)
                return;
            if (_priceWarnedKeys.Add(context + ":" + CardPriceKey(c)))
                CoopPlugin.Log.LogWarning($"{context}: {c.expansionType}#{(int)c.monsterType} is from a card set this PC doesn't have - the card could NOT be processed here");
        }

        /// <summary>Keys already warned about by ApplyRemoteCardPrice, once per session. The
        /// price heal rebroadcasts every displayed card at least every 30s, and one-sided
        /// content packs are ALLOWED (1.0.34) - without this memo an hours-long session logs
        /// the same "unknown card set" / "store did not accept" line thousands of times.</summary>
        private static readonly HashSet<string> _priceWarnedKeys = new HashSet<string>();

        /// <summary>Apply a received card price. For an ENCODED (>10) graded grade, register the
        /// card with Grading Overhaul first (so its price-store key matches) and let GO's own
        /// SetCardPrice patch route the write into its store; without GO, skip - the vanilla
        /// 10-slot price array can't index an encoded grade. Callers hold ApplyingRemotePrice.
        /// Returns true when the price is actually STORED here (the host's ack/echo keys off
        /// this), and reports through <paramref name="actual"/> the value the game's price store
        /// really ended up holding - vanilla SetCardPrice is a hardcoded six-expansion if-chain
        /// that silently no-ops for a modded expansion, so "applied" used to mean nothing.
        /// <paramref name="relayAnyway"/> separates "THIS machine can't hold this price" from
        /// "this price is garbage": the message is well-formed and other peers may well have
        /// the content pack / a working store, so the host must still forward it (and the
        /// sender still needs its ack). False only when the price itself is unusable.</summary>
        private static bool ApplyRemoteCardPrice(CardData card, float price, string from, out float actual, out bool relayAnyway)
        {
            actual = price;
            relayAnyway = false;
            if (card == null)
                return false; // nothing to relay
            string key = CardPriceKey(card);
            // RESOLVABILITY FIRST, on the same runtime-enum oracle the card-delta guard uses:
            // an id this process has never heard of mis-indexes through GetShownMonsterList's
            // Tetramon fallback and would price somebody ELSE'S card (save slot 0).
            if (!CardSetInstalledHere(card))
            {
                relayAnyway = true; // one-sided content pack: the OTHER peers may well have it
                if (_priceWarnedKeys.Add("set:" + key))
                    CoopPlugin.Log.LogWarning($"card price for unknown card set skipped - other side has a content pack this PC doesn't ({key}; further ones logged once each)");
                return false;
            }
            if (card.cardGrade > 10)
            {
                // modded grade, no grading mod HERE: can't price, but a peer that has Grading
                // Overhaul can, and the sender is still waiting on its ack
                if (!Util.GradingInterop.Present)
                {
                    relayAnyway = true;
                    return false;
                }
                Util.GradingInterop.Remember(card);             // bind cert so GO's price-store key matches
            }
            float before = float.NaN;
            try
            {
                before = CPlayerData.GetCardPrice(card);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            try
            {
                CPlayerData.SetCardPrice(card, price);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("card price apply: " + e.Message); return false; }
            // READ-BACK, the same GetCardPrice the host's price heal reads: truth on the wire
            // lets the sender either converge on it or surface its own give-up warning.
            try
            {
                actual = CPlayerData.GetCardPrice(card);
            }
            catch (Exception e)
            {
                // memoized like the other price warnings: a store whose GetCardPrice throws
                // throws EVERY heal beat, which used to spam this line forever
                if (_priceWarnedKeys.Add("read:" + key))
                    CoopPlugin.Log.LogWarning("card price read-back: " + e.Message);
                actual = price;
            }
            if (Math.Abs(actual - price) > CardPriceEpsilon)
            {
                // F4, not F2: a rounding-sized mismatch printed as two IDENTICAL strings, so
                // the one line that explains the failure read as nonsense
                if (_priceWarnedKeys.Add("store:" + key))
                    CoopPlugin.Log.LogWarning($"card price {key}: the game's price store did not accept {price:F4} (it holds {actual:F4}) - modded expansion? (logged once per card)");
                // The store REJECTED the value. Echoing the read-back would push this
                // machine's stale/zero price onto every other guest (the reported "prices
                // reset to 0"), so this is not an apply - but peers with a working store
                // should still get the original, and the sender still needs its ack.
                relayAnyway = true;
                return false;
            }
            if (float.IsNaN(before) || Math.Abs(before - actual) > CardPriceEpsilon)
                CoopPlugin.Log.LogInfo($"card price applied: {key} = {actual:F2} (from {from})"); // this path was invisible in field logs
            return true;
        }

        // NEVER CSingleton<>.Instance (fake-manager landmine); cached, Unity re-resolves.
        private static readonly System.Reflection.MethodInfo MiBinderResort =
            HarmonyLib.AccessTools.Method(typeof(CollectionBinderFlipAnimCtrl), "OnSortingMethodUpdated");
        private static readonly System.Reflection.FieldInfo FiBinderIsBookOpen =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_IsBookOpen");
        // Extra binder internals we read to recompute the OPEN album's total-value text after a
        // card delta - the open path (CollectionBinderFlipAnimCtrl.Update ~807-830) branches on
        // these to pick which SetTotalValue variant to call. All private, so AccessTools-cached.
        private static readonly System.Reflection.FieldInfo FiBinderUI =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_CollectionBinderUI");
        private static readonly System.Reflection.FieldInfo FiBinderIsGradedAlbum =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_IsGradedCardAlbum");
        private static readonly System.Reflection.FieldInfo FiBinderExpansionType =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_ExpansionType");

        /// <summary>Make an ALREADY-OPEN collection binder re-lay-out after a card change.
        /// SetCanUpdateSort alone only ARMS a gate the vanilla per-frame Update never
        /// consumes, so a traded/pulled card stayed invisible until the player flipped a
        /// page or reopened the binder. When the book is open we also invoke the game's own
        /// OnSortingMethodUpdated (backToFirstPage:false, keeps the current page) which
        /// rebuilds the sorted list + relays out all page groups, so the card appears now.</summary>
        private static void RefreshOpenBinder()
        {
            try
            {
                if (_deltaIpc == null)
                    _deltaIpc = FindObjectOfType<InteractionPlayerController>();
                var ctrl = _deltaIpc != null ? _deltaIpc.m_CollectionBinderFlipAnimCtrl : null;
                if (ctrl == null)
                    return;
                ctrl.SetCanUpdateSort(canSort: true);
                bool isOpen = FiBinderIsBookOpen != null && (bool)FiBinderIsBookOpen.GetValue(ctrl);
                if (isOpen && MiBinderResort != null)
                    MiBinderResort.Invoke(ctrl, new object[] { false }); // backToFirstPage:false

                // OnSortingMethodUpdated re-lays out the cards but NEVER touches the total-value
                // text - that write only happens in the binder OPEN path. So a traded/pulled card
                // showed up on the page but the "total value" header stayed stale (the reported
                // "total value differs"). While the book is open, mirror the exact SetTotalValue
                // call the open path (CollectionBinderFlipAnimCtrl.Update ~807-830) would make for
                // the CURRENTLY open album. Behind the isOpen guard so a delta with no binder up
                // costs nothing.
                if (isOpen && FiBinderUI != null)
                {
                    var ui = FiBinderUI.GetValue(ctrl) as CollectionBinderUI;
                    if (ui != null)
                    {
                        bool isGraded = FiBinderIsGradedAlbum != null && (bool)FiBinderIsGradedAlbum.GetValue(ctrl);
                        var expansion = FiBinderExpansionType != null
                            ? (ECardExpansionType)FiBinderExpansionType.GetValue(ctrl)
                            : ECardExpansionType.None;
                        if (isGraded)
                        {
                            // graded album: sum GetCardMarketPrice over the graded inventory - and
                            // DELIBERATELY NOT the way the open path's loop (~810-819) does it.
                            //
                            // DO NOT "MAKE THIS MATCH VANILLA" AGAIN. Vanilla's loop WRITES
                            // m_GradedCardInventoryList[i].amount = 10 on every row over 10.
                            // CompactCardDataAmount is a CLASS (decompiled/CompactCardDataAmount.cs:4)
                            // and that list is the LIVE save list CGameData hands to the serializer
                            // BY REFERENCE (decompiled/CGameData.cs:1119 -> SetLoadData's
                            // `data = loadData` at :1019), so the write is a write to the save. With
                            // Grading Overhaul installed `amount` is not an amount at all: it is the
                            // ENCODED grade, packing grading company + the real 1-10 + the certificate
                            // serial (CPlayerData.AddCard stores cardGrade straight into it,
                            // decompiled/CPlayerData.cs:1506). Clamping it replaces every certificate
                            // in the album with a bare 10, permanently and world-wide, and it is
                            // UNRECOVERABLE - GO's own repair sweeps all skip amount <= 10
                            // (decompiled-grading :8266, :8652, :8804, :8901) and its
                            // EncodedGradeRegistry is keyed by CardData REFERENCE (:5881) while
                            // GetGradedCardData mints a fresh CardData every call (:1689).
                            //
                            // Ours was strictly worse than vanilla's, which is why this had to
                            // diverge rather than be left alone: vanilla runs that loop only on the
                            // binder OPEN transition (m_OpenBinder && !m_IsBookOpen,
                            // decompiled/CollectionBinderFlipAnimCtrl.cs:766), whereas this method is
                            // armed on the success tail of every applied card delta (~1333) and by
                            // the graded-adopt path, then drained per frame - so it re-ran on an
                            // ALREADY-OPEN book. GradeDataLifeSaver, the community fix, transpiles
                            // vanilla's clamp away but patches CollectionBinderFlipAnimCtrl.Update,
                            // so it can never reach a copy compiled into CardShopCoop.dll.
                            //
                            // So: READ the row, never write it. GetGradedCardData returns a brand-new
                            // CardData every call (:1689-1701), so nothing done to that throwaway
                            // copy can reach the save.
                            //
                            // AND WITH GO PRESENT, HAND ITS GRADE TO GetCardMarketPrice ENCODED AND
                            // UNTOUCHED. Do NOT "decode it first so vanilla sees a real 1-10" -
                            // vanilla already does. GO's
                            // PricingPatch_MarketPrice_GetMarketPrice.Prefix takes `ref int cardGrade`
                            // and does `if (cardGrade > 10) cardGrade = Helper.GetActualGrade(cardGrade)`
                            // (decompiled-grading :13697-13708), so MarketPrice.GetMarketPrice's body
                            // runs on the decoded value either way. Pre-decoding buys nothing there
                            // and COSTS the whole company multiplier: GO's postfix on
                            // CPlayerData.GetCardMarketPrice - PricingFix_GetCardMarketPrice_UseRegistry
                            // (:15052-15069) - reads the ENCODED value back off the card and applies
                            // nothing unless Helper.TryGetCompanyFromGrade accepts it, and that
                            // returns FALSE for every value in 1-10 (:16026-16035). Note the registry
                            // read it does that through is keyed by CardData REFERENCE (:5881, :5929),
                            // so for a fresh copy like this one it can only ever return the field we
                            // just set - the value we pass IS the value that decides the multiplier.
                            // Dropping it is not a rounding error: Cardinals 10 is 3x, PSA 10 is
                            // 16.43x, Beckett 10 is 21x and a Beckett Black Label multiplies that by
                            // 5 again for 105x (tables :1867-1881, applied at :1925-1975). A decoded
                            // copy therefore understates a top-grade album by up to 105x - and
                            // OVERSTATES a low-grade one, since grades 1-6 multiply by less than 1.
                            // An earlier version of this comment claimed that cost "a few percent";
                            // that was false, and it is why the decode is gone.
                            //
                            // The clamp survives for the NO-GO case ONLY. With GO absent nothing
                            // decodes an encoded grade on the way in, and vanilla indexes
                            // `(index * 10 + (cardGrade - 1)) % list.Count` (:1415-1420 ->
                            // MarketPrice.cs:18) - the `%` means an encoded int WRAPS rather than
                            // throws, so it cannot crash, it just totals a meaningless slot. Present
                            // is "GO's assembly loaded and its API resolved" (Util/GradingInterop.cs:119),
                            // which is also the only condition under which anything on this PC could
                            // have written an encoded grade in the first place. It does NOT track
                            // GO's own ConfigSettings.EnableMod, which gates both patches above; a
                            // player who installs GO and then disables it in config gets the same
                            // meaningless-slot number here, for the same harmless reason.
                            bool goPresent = Util.GradingInterop.Present;
                            float total = 0f;
                            for (int i = 0; i < CPlayerData.m_GradedCardInventoryList.Count; i++)
                            {
                                var row = CPlayerData.m_GradedCardInventoryList[i];
                                if (row == null)
                                    continue;
                                // PER-ROW, never around the loop: one bad row must not abandon the
                                // rest of the total. Same hazard as the compact-row walk documented
                                // on Util/GradingInterop.AddAllCompact, and it is NOT the divide by
                                // zero an earlier draft of this comment claimed.
                                // GetCardAmountPerMonsterType initialises num = 6 BEFORE its switch,
                                // every case assigns 6 (or 1 for Ghost), and there is no default arm
                                // (decompiled/CPlayerData.cs:692-721, the init at :694), so it
                                // returns 6 or 12 for an expansion it has never heard of and cannot
                                // return 0.
                                //
                                // What can actually throw is an out-of-range INDEX, and BOTH calls
                                // inside this try can do it for a row whose card set this install
                                // does not have. GetGradedCardData resolves the monster through
                                // GetMonsterTypeFromCardSaveIndex, which indexes
                                // InventoryBase.GetShownMonsterList(exp)[cardSaveIndex / perType]
                                // (:790-793) - a list that falls back to TETRAMON's for an unknown
                                // expansion (decompiled/InventoryBase.cs:290-308). GetCardMarketPrice
                                // then indexes m_GenCardMarketPriceList[GetCardSaveIndex(card)]
                                // (:1415-1420), and that list is sized from THIS install's own
                                // GetCardCollectionDataCount() + 100 (:491), so a high enough index
                                // runs off the end. Skipping such a row costs its value in one
                                // header total, which is the trade this loop wants.
                                //
                                // Verified against vanilla and Grading Overhaul (GO's only reference
                                // to GetCardAmountPerMonsterType is a read, decompiled-grading
                                // :7132). EPL is not visible from this repo and could patch it, so
                                // the catch - not the invariant - is what makes this safe.
                                try
                                {
                                    var copy = CPlayerData.GetGradedCardData(row);
                                    if (!goPresent && copy.cardGrade > 10)
                                        copy.cardGrade = 10;
                                    total += CPlayerData.GetCardMarketPrice(copy);
                                }
                                catch { continue; }
                            }
                            ui.SetTotalValue(total);
                        }
                        else if (expansion == ECardExpansionType.Ghost)
                        {
                            // Ghost/dimension album sums both the normal and dimension halves (~824).
                            ui.SetTotalValue(CPlayerData.GetCardAlbumTotalValue(expansion, isDimensionCard: false)
                                + CPlayerData.GetCardAlbumTotalValue(expansion, isDimensionCard: true));
                        }
                        else
                        {
                            // normal expansion album (~829).
                            ui.SetTotalValue(CPlayerData.GetCardAlbumTotalValue(expansion, isDimensionCard: false));
                        }
                    }
                }
            }
            catch (System.Exception e) { CoopPlugin.Log.LogWarning($"binder relayout after card change failed: {e.Message}"); }
        }

        private void FlushPendingCardWork()
        {
            if (!InGameLevel() || (_pendingCardDeltas.Count == 0 && _pendingCardPrices.Count == 0))
                return;
            Guarded("pending-cards", () =>
            {
                // The relay-anyway flag is discarded here on purpose: this replay path has never
                // relayed anything (see ApplyOrHoldCardDelta's hold comment).
                foreach (var p in _pendingCardDeltas)
                    ApplyCardDelta(p.IsAdd, p.Amount, p.Card, out _);
                if (_pendingCardDeltas.Count > 0)
                    CoopPlugin.Log.LogInfo($"applied {_pendingCardDeltas.Count} card change(s) held during loading");
                _pendingCardDeltas.Clear();
                // The results are NOT discardable on the host: a price queued during a scene
                // load still owes its sender the same ack/relay the live CardPriceSet handler
                // gives it. Dropping them meant a guest that priced a card while the host was
                // loading retried 12 times and then falsely surrendered. Echoes are collected
                // and sent AFTER the flag is cleared, exactly like the live handler.
                bool echoing = Role == CoopRole.Host;
                var echoes = echoing ? new List<KeyValuePair<CardData, float>>() : null;
                Patches.GamePatches.ApplyingRemotePrice = true;
                try
                {
                    foreach (var p in _pendingCardPrices)
                    {
                        bool applied = ApplyRemoteCardPrice(p.Key, p.Value, "load queue", out float actual, out bool relayAnyway);
                        if (!echoing)
                            continue;                       // clients never echo
                        if (applied)
                            echoes.Add(new KeyValuePair<CardData, float>(p.Key, actual));
                        else if (relayAnyway)
                            echoes.Add(p);          // pure relay of the original
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("pending card price apply: " + e.Message); }
                finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                _pendingCardPrices.Clear();
                if (echoes != null)
                {
                    for (int i = 0; i < echoes.Count; i++)
                    {
                        var kv = echoes[i];
                        Broadcast(new CardPriceSetMessage { Card = kv.Key, Price = kv.Value });
                    }
                }
            });
        }

        /// <summary>Host-side raw fan-out: forward a message a guest sent us on to the OTHER
        /// guests, byte-for-byte. The collection is one shared inventory, so a card a 3rd+ player
        /// gains/loses must reach every peer, not just the host. We re-wrap the ORIGINAL payload
        /// bytes (not a re-serialized CardData) so an encoded graded grade - the >10 company/cert
        /// packing - survives verbatim; re-serializing would risk lossy round-trips. Same shape as
        /// the CardShelfRequest relay, generalized. No-op unless we're the host with >1 peer.</summary>
        /// <summary>Host: relay only the deltas of a CardDeltaBatch that THIS side accepted.
        /// Used when part of the batch was refused here (corrupt grade / uninstalled card set /
        /// would-go-negative): the whole-batch case relays the ORIGINAL bytes, but a delta we
        /// refused must never be propagated onward - exactly the guarantee the single-delta
        /// case has always had.</summary>
        /// <summary>Host-side DTO fan-out: forward a decoded DTO a guest sent us to the OTHER
        /// guests. The DTO carries the exact CardData (including the encoded graded grade), so
        /// re-serializing it reproduces the original wire bytes.</summary>
        private void RelayToOthers(int senderConn, INetMessage message)
        {
            if (Role != CoopRole.Host || _net == null || _net.ConnectionCount <= 1)
                return;
            FlushCardDeltaOutbox();
            foreach (int cid in _net.ConnIds())
                if (cid != senderConn)
                    _net.Send(cid, message);
        }

        private void RelayCardDeltaBatchToOthers(int senderConn, List<PendingCard> deltas)
        {
            if (Role != CoopRole.Host || _net == null || _net.ConnectionCount <= 1 || deltas.Count == 0)
                return;
            FlushCardDeltaOutbox(); // ordering: our own pending deltas leave first
            var relay = new CardDeltaBatchMessage();
            for (int i = 0; i < deltas.Count; i++)
                relay.Deltas.Add(new CardDeltaEntry
                {
                    IsAdd = deltas[i].IsAdd,
                    Amount = deltas[i].Amount,
                    Card = deltas[i].Card
                });
            foreach (int cid in _net.ConnIds())
                if (cid != senderConn)
                    _net.Send(cid, relay);
        }

        /// <summary>Send everything the card-delta outbox holds, at most CardDeltaBatchMax
        /// deltas per frame. Called at the end of Update AND by the send helpers before any
        /// other message goes out, so a game action that emits a delta and then a follow-up
        /// message (the graded-card flows) still puts them on the wire in that order.</summary>
        private void FlushCardDeltaOutbox()
        {
            if (_cardDeltaOutbox.Count == 0 || _flushingCardDeltas)
                return;
            if (_net == null)
            {
                _cardDeltaOutbox.Clear();
                return;
            }
            _flushingCardDeltas = true;
            try
            {
                int total = _cardDeltaOutbox.Count;
                int sent = 0;
                while (sent < total)
                {
                    int start = sent;
                    int n = Math.Min(CardDeltaBatchMax, total - start);
                    var batch = new CardDeltaBatchMessage();
                    for (int i = start; i < start + n; i++)
                    {
                        var d = _cardDeltaOutbox[i];
                        batch.Deltas.Add(new CardDeltaEntry { IsAdd = d.IsAdd, Amount = d.Amount, Card = d.Card });
                    }
                    Broadcast(batch);
                    sent += n;
                }
                if (total > CardDeltaBatchMax)
                    CoopPlugin.Log.LogInfo($"card deltas: {total} sent as {(total + CardDeltaBatchMax - 1) / CardDeltaBatchMax} batch(es)");
                _cardDeltaOutbox.Clear();
            }
            finally { _flushingCardDeltas = false; }
        }

        /// <summary>End of frame: the folded card-delta log line(s), ONE binder relayout for
        /// everything applied this frame, then the batched outbox. Runs after every stage that
        /// can apply or produce a delta (dispatch, the held-during-loading flush, HostTick).</summary>
        private void FlushFrameCardWork()
        {
            if (_deltaAppliedThisFrame > 0)
            {
                if (_deltaAppliedThisFrame <= 5)
                {
                    for (int i = 0; i < _deltaLogBuf.Count; i++)
                    {
                        var d = _deltaLogBuf[i];
                        CoopPlugin.Log.LogInfo($"card delta applied: {(d.IsAdd ? "+" : "-")}{d.Amount} {CardIdent(d.Card)}{(d.Card.cardGrade > 0 ? $" (grade {d.Card.cardGrade})" : d.Card.isFoil ? " (foil)" : "")}");
                    }
                }
                else
                    CoopPlugin.Log.LogInfo($"applied {_deltaAppliedThisFrame} card deltas");
                _deltaLogBuf.Clear();
                _deltaAppliedThisFrame = 0;
            }
            if (_binderRefreshPending)
            {
                _binderRefreshPending = false;
                RefreshOpenBinder();
            }
            FlushCardDeltaOutbox();
        }

    }
}
