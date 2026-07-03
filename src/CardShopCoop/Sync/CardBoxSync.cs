using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CardShopCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors graded-card RETURN boxes (InteractablePackagingBox_Card) between host
    /// and client. Grading matures host-side only (GradingSync blocks the client's
    /// RestockManager.OnDayStarted), and the result box lands in RestockManager's
    /// m_CardPackagingBoxList - a separate list from the item boxes BoxSync mirrors -
    /// so without this module the joiner never sees his graded cards come back.
    ///
    /// Architecture is BoxSync's: the host list is the truth, broadcast by index every
    /// 1.5s (hash-gated, slow heal); the client reconciles its live list to match,
    /// spawning through RestockManager.SpawnPackageBoxCard - which takes the exact
    /// CardData list and never re-rolls grades (rolls happen earlier, in OnDayStarted) -
    /// and despawning through the box's own OnDestroyed. Box contents are immutable
    /// after spawn, so the client only ever reports carried transitions and positions.
    ///
    /// Joiner collect: the vanilla InteractablePackagingBox_Card.OnPressOpenBox hands
    /// the card3ds to the player (AddHoldCard) and the cards reach the binder later,
    /// when the held cards are stored (InteractionPlayerController's store path calls
    /// CPlayerData.AddCard per card). Letting that run locally would AddCard on the
    /// joiner AND mirror through CardDelta AND leave the host's real box alive - a
    /// guaranteed duplicate. So the joiner's open is blocked BEFORE anything is handed
    /// over and forwarded as a collect op; the host AddCards each stored card itself
    /// (grade-10 report/achievement bookkeeping included, replicating OnPressOpenBox),
    /// which the existing CardDelta mirror carries into BOTH binders, then despawns
    /// its real box; the mirror follows on the next snapshot. Reveal choice: the
    /// vanilla reveal is hold-card mode, and hold-card mode's only exit paths call
    /// AddCard - there is no read-only way to invoke it - so the joiner gets the box
    /// "Open" animation + SFX locally plus a register toast instead.
    /// </summary>
    public class CardBoxSync
    {
        public struct Entry
        {
            public List<CardData> Cards; // immutable identity of the box
            public bool Carried;         // in someone's hands: position is transient
            public Vector3 Pos;
            public float Yaw;
        }

        private const int MaxBoxes = 16; // vanilla caps pending grading sets at 4
        private const int MaxCards = 16; // vanilla sets carry up to 8 cards

        /// <summary>The live module instance, for the static Harmony patches.</summary>
        public static CardBoxSync Instance;

        /// <summary>Set by CoopCore: is this box currently in the LOCAL player's hands?
        /// (InteractionPlayerController.m_CurrentHoldingBoxCard / m_CurrentHoldingBox)</summary>
        public static Func<InteractablePackagingBox_Card, bool> IsLocallyCarried = _ => false;

        /// <summary>Set by CoopCore: client -> host op (MsgType.CardBoxOp).</summary>
        public Action<Action<BinaryWriter>> SendOp;

        /// <summary>Set by CoopCore: host -> clients state (MsgType.CardBoxState).</summary>
        public Action<Action<BinaryWriter>> BroadcastState;

        /// <summary>True while sync code itself destroys/spawns boxes, so the
        /// OnDestroyed patch doesn't mistake reconciliation for player action.</summary>
        public static bool ApplyingRemote;

        // op kinds on the CardBoxOp wire
        private const byte OpReport = 0;  // carried flags + positions, index-aligned
        private const byte OpCollect = 1; // joiner opened a box: index + identity hash
        private const byte OpRemoved = 2; // joiner's local copy died outside sync

        // m_StoredCardList is private; needed to hide the card3d UI followers along
        // with the box (Card3dUIGroup only tracks its card in LateUpdate, so a plain
        // SetActive(false) on the box would strand the card faces in mid-air)
        private static readonly FieldInfo FiStoredCards =
            AccessTools.Field(typeof(InteractablePackagingBox_Card), "m_StoredCardList");

        private readonly List<Entry> _lastApplied = new List<Entry>(); // client: host truth
        private readonly HashSet<int> _carriedLastTick = new HashSet<int>();
        private readonly HashSet<int> _remoteCarried = new HashSet<int>();   // host: client-held boxes
        private readonly Dictionary<int, double> _recentlyReleased = new Dictionary<int, double>(); // client: ignore stale carried echoes
        private readonly Dictionary<int, double> _locallyTouched = new Dictionary<int, double>();   // client: my recent moves beat stale echoes
        private readonly Dictionary<int, double> _recentlyCollected = new Dictionary<int, double>(); // client: cardsHash -> time; stale pre-collect snapshots must not resurrect the box
        private float _timer;
        private int _lastHostHash;
        private float _hostHeal;
        private RestockManager _rm;
        private Transform _spawnAnchor; // reusable Transform for SpawnPackageBoxCard

        public CardBoxSync()
        {
            Instance = this;
        }

        public void Reset()
        {
            _lastApplied.Clear();
            _carriedLastTick.Clear();
            _remoteCarried.Clear();
            _recentlyReleased.Clear();
            _locallyTouched.Clear();
            _recentlyCollected.Clear();
            _timer = -8.4f; // staggered phase vs the other snapshot engines
            _lastHostHash = 0;
            _hostHeal = 0f;
            _rm = null;
        }

        public void ForceResend()
        {
            _lastHostHash = 0;
            _hostHeal = 999f; // beats the hash gate even if the real hash is 0
        }

        private RestockManager Rm()
        {
            if (_rm == null) _rm = UnityEngine.Object.FindObjectOfType<RestockManager>();
            return _rm;
        }

        private static List<InteractablePackagingBox_Card> LiveBoxes()
        {
            return RestockManager.GetCardPackagingBoxList();
        }

        private static bool InGameLevel()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // The joiner's collect: block BEFORE the card3ds are handed to the player
            // (the hold-card store path would AddCard locally = duplication).
            Try(h, typeof(InteractablePackagingBox_Card), "OnPressOpenBox",
                prefix: new HarmonyMethod(typeof(CardBoxSync), nameof(OpenBoxPrefix)));

            // OnDestroyed IS an override on InteractablePackagingBox_Card (it clears
            // its card3ds and calls RestockManager.RemoveCardPackageBox), so patching
            // it here catches ONLY card boxes - item boxes keep BoxSync's own patch.
            Try(h, typeof(InteractablePackagingBox_Card), "OnDestroyed",
                prefix: new HarmonyMethod(typeof(CardBoxSync), nameof(DestroyedPrefix)));
        }

        public static bool OpenBoxPrefix(InteractablePackagingBox_Card __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            try { Instance?.ClientCollect(__instance); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync collect: " + e.Message); }
            return false; // never vanilla on the client (see class comment)
        }

        public static bool DestroyedPrefix(InteractablePackagingBox_Card __instance)
        {
            // world-(re)load cleanup destroys are not player actions
            if (!ApplyingRemote && !CoopCore.ClientReloading) Instance?.OnLocalDestroyed(__instance);
            return true;
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

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || Rm() == null) return;
            _timer += dt;
            if (_timer < 1.5f) return;
            _timer -= 1.5f;
            try
            {
                var boxes = LiveBoxes();
                var list = new List<Entry>(Mathf.Min(boxes.Count, MaxBoxes));
                for (int i = 0; i < boxes.Count && list.Count < MaxBoxes; i++)
                {
                    if (boxes[i] == null) continue;
                    var e = Snapshot(boxes[i]);
                    if (_remoteCarried.Contains(i)) e.Carried = true; // a client holds it
                    list.Add(e);
                }
                int hash = 17;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    hash = hash * 31 + HashCards(e.Cards);
                    hash = hash * 31 + (e.Carried ? 1 : 0);
                    hash = hash * 31 + (int)(e.Pos.x * 8f);
                    hash = hash * 31 + (int)(e.Pos.z * 8f);
                }
                _hostHeal += 1.5f;
                if (hash == _lastHostHash && _hostHeal < 10f) return;
                _lastHostHash = hash;
                _hostHeal = 0f;
                var snap = list;
                BroadcastState?.Invoke(bw => WriteEntries(bw, snap));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync host: " + e.Message); }
        }

        /// <summary>Host: dispatch a client op (report / collect / removed).</summary>
        public void HostApplyOp(BinaryReader br, int connId)
        {
            if (CoopCore.Role != CoopRole.Host) return;
            byte kind = br.ReadByte();
            switch (kind)
            {
                case OpReport: HostApplyReport(br); break;
                case OpCollect: HostApplyCollect(br); break;
                case OpRemoved: HostApplyRemoved(br, connId); break;
                default:
                    CoopPlugin.Log.LogWarning($"CardBoxSync: unknown op {kind}");
                    break;
            }
        }

        private void HostApplyReport(BinaryReader br)
        {
            int n = Mathf.Min(br.ReadByte(), MaxBoxes);
            var boxes = LiveBoxes();
            for (int i = 0; i < n; i++)
            {
                int cardCount = br.ReadByte();
                bool carried = br.ReadBoolean();
                var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                float yaw = br.ReadSingle();
                if (i >= boxes.Count || boxes[i] == null) continue;
                var box = boxes[i];
                // identity must match: indices may have shifted between snapshot and report
                if (SafeCards(box).Count != cardCount) continue;
                if (IsLocallyCarried(box)) continue; // never stomp a box in the host's hands
                if (carried) _remoteCarried.Add(i);
                else _remoteCarried.Remove(i);
                ApplyToBox(box, new Entry { Cards = null, Carried = carried, Pos = pos, Yaw = yaw });
            }
        }

        /// <summary>Host: the joiner opened a graded-returns box. Replicates the
        /// collect side of InteractablePackagingBox_Card.OnPressOpenBox WITHOUT the
        /// hold-card step (cards can't be put in the host player's hands): AddCard
        /// per stored card - AddCard routes graded cards into the graded collection
        /// itself, and the CardDelta mirror carries every AddCard to the joiner, so
        /// BOTH binders receive them - plus the grade-10 report counter and the two
        /// achievement checks, then the box despawns and the mirror follows.</summary>
        private void HostApplyCollect(BinaryReader br)
        {
            int index = br.ReadByte();
            int cardCount = br.ReadByte();
            int cardsHash = br.ReadInt32();

            var box = FindBox(index, cardCount, cardsHash);
            if (box == null)
            {
                CoopPlugin.Log.LogWarning("CardBoxSync: collect for unknown/mismatched box - ignored");
                return; // nothing consumed; the next snapshot re-aligns the joiner
            }
            if (IsLocallyCarried(box)) return; // host is holding it: let him open it himself

            try
            {
                var cards = SafeCards(box);
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i] == null) continue;
                    CPlayerData.AddCard(cards[i], 1); // mirrored by CardDelta
                    if (cards[i].cardGrade == 10)
                        CPlayerData.m_GameReportDataCollectPermanent.gemMintCardObtained++;
                }
                AchievementManager.OnCheckGemMintCardCount(CPlayerData.m_GameReportDataCollectPermanent.gemMintCardObtained);
                AchievementManager.OnCheckCollectedGradedCardSet();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync collect apply: " + e.Message); }

            ApplyingRemote = true;
            try { box.OnDestroyed(); } // destroys its card3ds + RemoveCardPackageBox
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync collect despawn: " + e.Message); }
            finally { ApplyingRemote = false; }
            _remoteCarried.Clear(); // indices shifted; holds re-assert within a tick
            ForceResend();          // the joiner's mirror updates on the next tick
        }

        private void HostApplyRemoved(BinaryReader br, int connId)
        {
            int index = br.ReadByte();
            int cardCount = br.ReadByte();
            int cardsHash = br.ReadInt32();
            // shared budget with item/furn boxes: a reloading client's world-teardown
            // echoes ALL THREE box lists as removals in one burst
            if (BoxSync.RemovalFlooded(connId, "card-box")) return;
            var box = FindBox(index, cardCount, cardsHash);
            if (box == null || IsLocallyCarried(box)) return;
            ApplyingRemote = true;
            try { box.OnDestroyed(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync removal: " + e.Message); }
            finally { ApplyingRemote = false; }
            _remoteCarried.Clear();
            ForceResend();
        }

        /// <summary>Resolve a box by index, falling back to an identity scan - the
        /// index may have shifted between the client's snapshot and his op.</summary>
        private static InteractablePackagingBox_Card FindBox(int index, int cardCount, int cardsHash)
        {
            var boxes = LiveBoxes();
            if (index >= 0 && index < boxes.Count && boxes[index] != null)
            {
                var cards = SafeCards(boxes[index]);
                if (cards.Count == cardCount && HashCards(cards) == cardsHash)
                    return boxes[index];
            }
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i] == null) continue;
                var cards = SafeCards(boxes[i]);
                if (cards.Count == cardCount && HashCards(cards) == cardsHash)
                    return boxes[i];
            }
            return null;
        }

        // ---------------- client ----------------

        /// <summary>Client: reconcile the live card-box population to the host's
        /// snapshot. Spawns go through RestockManager.SpawnPackageBoxCard with the
        /// EXACT broadcast CardData list (UpdateCardData stores it as-is; grades were
        /// rolled host-side in OnDayStarted, never here).</summary>
        public void ClientApplyState(BinaryReader br)
        {
            var hostList = ReadEntries(br);
            ApplyingRemote = true;
            try { ClientApplyInner(hostList); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(List<Entry> hostList)
        {
            if (Rm() == null) return;
            var boxes = LiveBoxes();
            double now = Time.realtimeSinceStartupAsDouble;

            // stale-snapshot guard: a snapshot generated BEFORE the host processed my
            // collect still contains the box I just despawned locally - spawning it
            // back for one tick would offer a ghost double-collect. Skip the whole
            // pass; the post-collect snapshot follows within 1.5s.
            for (int i = 0; i < hostList.Count; i++)
            {
                bool present = i < boxes.Count && boxes[i] != null
                    && SameCards(SafeCards(boxes[i]), hostList[i].Cards);
                if (!present && _recentlyCollected.TryGetValue(HashCards(hostList[i].Cards), out double tc)
                    && now - tc < 6.0)
                    return;
            }
            // expire old collect markers so a genuinely re-appearing box can heal
            if (_recentlyCollected.Count > 0)
            {
                List<int> dead = null;
                foreach (var kv in _recentlyCollected)
                    if (now - kv.Value > 12.0) (dead ?? (dead = new List<int>())).Add(kv.Key);
                if (dead != null) foreach (int k in dead) _recentlyCollected.Remove(k);
            }

            // shrink extras (from the end, so indices stay aligned)
            for (int i = boxes.Count - 1; i >= hostList.Count; i--)
            {
                try { if (boxes[i] != null) boxes[i].OnDestroyed(); } catch { }
            }
            // grow / fix / update
            for (int i = 0; i < hostList.Count; i++)
            {
                var want = hostList[i];
                InteractablePackagingBox_Card box = i < boxes.Count ? boxes[i] : null;
                if (box != null && !SameCards(SafeCards(box), want.Cards))
                {
                    try { box.OnDestroyed(); } catch { }
                    box = null;
                    boxes = LiveBoxes(); // list mutated
                }
                if (box == null)
                {
                    try
                    {
                        // UpdateCardData KEEPS the list reference (and prunes it), so
                        // hand it a private copy, never the applied-state one
                        box = RestockManager.SpawnPackageBoxCard(
                            new List<CardData>(want.Cards), SpawnAnchor(want.Pos, want.Yaw));
                        boxes = LiveBoxes();
                    }
                    catch (Exception e)
                    {
                        CoopPlugin.Log.LogWarning("CardBoxSync spawn: " + e.Message);
                        continue;
                    }
                }
                // a box in MY hands is mine until I put it down; a box in the HOST's
                // hands has a transient position we don't copy
                if (IsLocallyCarried(box)) continue;
                // a stale "carried" echo about a box I JUST released must not hide it
                if (want.Carried && _recentlyReleased.TryGetValue(i, out double t) && now - t < 6.0)
                    continue;
                // my own recent moves win over stale echoes; my report reaches the
                // host and the next echo agrees
                if (_locallyTouched.TryGetValue(i, out double touched) && now - touched < 6.0)
                    continue;
                ApplyToBox(box, want);
            }
            // remember the applied truth for local-change detection
            _lastApplied.Clear();
            _lastApplied.AddRange(hostList);
        }

        /// <summary>Client: detect the local player's carried transitions and box
        /// moves (contents are immutable client-side - collect is forwarded, never
        /// applied locally) and report them to the host.</summary>
        public void ClientTick(float dt, bool inGame)
        {
            if (!inGame || Rm() == null || _lastApplied.Count == 0) return;
            _timer += dt;
            if (_timer < 1.5f) return;
            _timer -= 1.5f;
            try
            {
                var boxes = LiveBoxes();
                bool changed = false;
                var list = new List<Entry>(_lastApplied.Count);
                for (int i = 0; i < _lastApplied.Count; i++)
                {
                    if (i < boxes.Count && boxes[i] != null)
                    {
                        // while I'M carrying it: tell the host (so his copy hides) but
                        // keep reporting the last settled position
                        if (IsLocallyCarried(boxes[i]))
                        {
                            var held = _lastApplied[i];
                            held.Carried = true;
                            if (_carriedLastTick.Add(i)) changed = true; // pickup transition
                            list.Add(held);
                            continue;
                        }
                        if (_carriedLastTick.Remove(i))
                        {
                            changed = true; // set-down transition
                            _recentlyReleased[i] = Time.realtimeSinceStartupAsDouble;
                        }
                        var nowSnap = Snapshot(boxes[i]);
                        var last = _lastApplied[i];
                        if ((nowSnap.Pos - last.Pos).sqrMagnitude > 0.01f
                            || Mathf.Abs(Mathf.DeltaAngle(nowSnap.Yaw, last.Yaw)) > 3f)
                        {
                            changed = true;
                            _locallyTouched[i] = Time.realtimeSinceStartupAsDouble;
                        }
                        nowSnap.Cards = last.Cards; // identity is the applied truth
                        list.Add(nowSnap);
                    }
                    else
                    {
                        // list shrank without OnDestroyed telling us - stop and let
                        // the next host snapshot re-align
                        break;
                    }
                }
                if (changed && SendOp != null)
                {
                    SendOp(bw =>
                    {
                        bw.Write(OpReport);
                        bw.Write((byte)Mathf.Min(list.Count, MaxBoxes));
                        for (int i = 0; i < list.Count && i < MaxBoxes; i++)
                        {
                            var e = list[i];
                            bw.Write((byte)Mathf.Min(e.Cards != null ? e.Cards.Count : 0, MaxCards));
                            bw.Write(e.Carried);
                            bw.Write(e.Pos.x); bw.Write(e.Pos.y); bw.Write(e.Pos.z);
                            bw.Write(e.Yaw);
                        }
                    });
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync client: " + e.Message); }
        }

        /// <summary>Client: the joiner pressed open on a mirrored box. Forward the
        /// collect (identity-hashed, so the host never collects the wrong box), keep
        /// the open moment locally (anim + SFX + toast), and retire the local mirror
        /// on the vanilla 0.85s delay.</summary>
        private void ClientCollect(InteractablePackagingBox_Card box)
        {
            int idx = LiveBoxes().IndexOf(box);
            if (idx < 0) return;
            var cards = SafeCards(box);

            if (SendOp == null)
            {
                CoopPlugin.Log.LogWarning("CardBoxSync: no host link, open ignored");
                return;
            }
            int hash = HashCards(cards);
            int count = cards.Count;
            SendOp(bw =>
            {
                bw.Write(OpCollect);
                bw.Write((byte)Mathf.Clamp(idx, 0, 255));
                bw.Write((byte)Mathf.Min(count, MaxCards));
                bw.Write(hash);
            });
            _recentlyCollected[hash] = Time.realtimeSinceStartupAsDouble;

            // local index bookkeeping shifts once the mirror despawns
            if (idx < _lastApplied.Count) _lastApplied.RemoveAt(idx);
            _carriedLastTick.Clear();   // index-keyed trackers all shifted;
            _locallyTouched.Clear();    // they re-establish within a tick
            _recentlyReleased.Clear();

            // the reveal moment, minus the hold-card handout (see class comment)
            try { CSingleton<InteractionPlayerController>.Instance.OnExitHoldBoxMode(); } catch { }
            try { box.m_BoxAnim.Play("Open"); } catch { }
            try { SoundManager.PlayAudio("SFX_BoxOpen", 0.5f); } catch { }
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "graded cards collected - check the binder";
                CoopCore.Instance.RegisterLineTimer = 4f;
            }
            try { box.StartCoroutine(CollectDespawn(box)); }
            catch { DespawnNow(box); }
        }

        private static IEnumerator CollectDespawn(InteractablePackagingBox_Card box)
        {
            yield return new WaitForSeconds(0.85f); // vanilla DelayResetOpenBox timing
            DespawnNow(box);
        }

        private static void DespawnNow(InteractablePackagingBox_Card box)
        {
            if (box == null) return;
            ApplyingRemote = true;
            try
            {
                box.gameObject.SetActive(false);
                box.OnDestroyed();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync local despawn: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        /// <summary>A card box died to LOCAL gameplay, not reconciliation. On the host
        /// the broadcast handles it (index-keyed client holds just reset); the client
        /// tells the host so the real box dies too and no echo resurrects it.</summary>
        private void OnLocalDestroyed(InteractablePackagingBox_Card box)
        {
            if (!InGameLevel()) return;
            if (CoopCore.Role == CoopRole.Host)
            {
                _remoteCarried.Clear(); // indices shifted; holds re-assert within a tick
                return;
            }
            if (CoopCore.Role != CoopRole.Client) return;
            int idx = LiveBoxes().IndexOf(box);
            if (idx < 0) return;
            var cards = SafeCards(box);
            int hash = HashCards(cards);
            int count = cards.Count;
            if (idx < _lastApplied.Count) _lastApplied.RemoveAt(idx);
            _carriedLastTick.Clear();
            _locallyTouched.Clear();
            _recentlyReleased.Clear();
            _recentlyCollected[hash] = Time.realtimeSinceStartupAsDouble;
            int sendIdx = idx;
            SendOp?.Invoke(bw =>
            {
                bw.Write(OpRemoved);
                bw.Write((byte)Mathf.Clamp(sendIdx, 0, 255));
                bw.Write((byte)Mathf.Min(count, MaxCards));
                bw.Write(hash);
            });
        }

        // ---------------- shared apply ----------------

        private static Entry Snapshot(InteractablePackagingBox_Card box)
        {
            return new Entry
            {
                Cards = SafeCards(box),
                Carried = IsLocallyCarried(box),
                Pos = box.transform.position,
                Yaw = box.transform.eulerAngles.y,
            };
        }

        private static List<CardData> SafeCards(InteractablePackagingBox_Card box)
        {
            try { return box.GetCardDataList() ?? EmptyCards; }
            catch { return EmptyCards; }
        }

        private static readonly List<CardData> EmptyCards = new List<CardData>();

        /// <summary>Apply carried visibility + position. Entry.Cards is ignored here
        /// (contents are spawn-time identity, never edited in place).</summary>
        private static void ApplyToBox(InteractablePackagingBox_Card box, Entry want)
        {
            try
            {
                // someone (remote) is carrying it: their avatar shows the box in hand,
                // so the world copy disappears until it's set down - including the
                // Card3dUIGroup followers, which don't live under the box transform
                // (they track their card3d in LateUpdate and would freeze in mid-air)
                if (want.Carried)
                {
                    SetBoxVisible(box, false);
                    return;
                }
                SetBoxVisible(box, true);
                var t = box.transform;
                if ((t.position - want.Pos).sqrMagnitude > 0.01f
                    || Mathf.Abs(Mathf.DeltaAngle(t.eulerAngles.y, want.Yaw)) > 3f)
                {
                    // card boxes have no price tag group (SpawnPriceTag is overridden
                    // empty), so a plain transform move carries everything: the stored
                    // card3ds are parented under m_StoredCardPosListGrp inside the box
                    t.SetPositionAndRotation(want.Pos, Quaternion.Euler(0f, want.Yaw, 0f));
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync apply: " + e.Message); }
        }

        private static void SetBoxVisible(InteractablePackagingBox_Card box, bool visible)
        {
            if (box.gameObject.activeSelf == visible) return;
            box.gameObject.SetActive(visible);
            try
            {
                if (FiStoredCards?.GetValue(box) is List<InteractableCard3d> card3ds)
                {
                    for (int i = 0; i < card3ds.Count; i++)
                    {
                        var c = card3ds[i];
                        if (c != null && c.m_Card3dUI != null)
                            c.m_Card3dUI.gameObject.SetActive(visible);
                    }
                }
            }
            catch { }
        }

        private Transform SpawnAnchor(Vector3 pos, float yaw)
        {
            if (_spawnAnchor == null)
                _spawnAnchor = new GameObject("CoopCardBoxSpawnAnchor").transform;
            _spawnAnchor.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            return _spawnAnchor;
        }

        // ---------------- wire / hash ----------------

        private static void WriteEntries(BinaryWriter bw, List<Entry> entries)
        {
            bw.Write((byte)Mathf.Min(entries.Count, MaxBoxes));
            for (int i = 0; i < entries.Count && i < MaxBoxes; i++)
            {
                var e = entries[i];
                int n = e.Cards != null ? Mathf.Min(e.Cards.Count, MaxCards) : 0;
                bw.Write((byte)n);
                for (int j = 0; j < n; j++)
                    Msg.WriteCard(bw, e.Cards[j] ?? new CardData());
                bw.Write(e.Pos.x); bw.Write(e.Pos.y); bw.Write(e.Pos.z);
                bw.Write(e.Yaw);
                bw.Write(e.Carried);
            }
        }

        private static List<Entry> ReadEntries(BinaryReader br)
        {
            int count = Mathf.Min(br.ReadByte(), MaxBoxes);
            var list = new List<Entry>(count);
            for (int i = 0; i < count; i++)
            {
                int n = Mathf.Min(br.ReadByte(), MaxCards);
                var cards = new List<CardData>(n);
                for (int j = 0; j < n; j++) cards.Add(Msg.ReadCard(br));
                var e = new Entry
                {
                    Cards = cards,
                    Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    Yaw = br.ReadSingle(),
                    Carried = br.ReadBoolean(),
                };
                list.Add(e);
            }
            return list;
        }

        /// <summary>Identity hash of a box's contents. Deliberately excludes isNew and
        /// gradedCardIndex (volatile bookkeeping the two sides may disagree on).</summary>
        private static int HashCards(List<CardData> cards)
        {
            int h = 17;
            if (cards == null) return h;
            h = h * 31 + cards.Count;
            for (int i = 0; i < cards.Count && i < MaxCards; i++)
            {
                var c = cards[i];
                if (c == null) continue;
                h = h * 31 + (int)c.monsterType;
                h = h * 31 + (int)c.expansionType;
                h = h * 31 + (int)c.borderType;
                h = h * 31 + ((c.isFoil ? 1 : 0) | (c.isDestiny ? 2 : 0) | (c.isChampionCard ? 4 : 0));
                h = h * 31 + c.cardGrade;
            }
            return h;
        }

        private static bool SameCards(List<CardData> a, List<CardData> b)
        {
            int ca = a != null ? a.Count : 0;
            int cb = b != null ? b.Count : 0;
            if (ca != cb) return false;
            return HashCards(a) == HashCards(b);
        }
    }
}
