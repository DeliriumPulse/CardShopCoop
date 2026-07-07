using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors the CONTENTS of the placed container stations - card storage shelves,
    /// bulk donation boxes, auto pack openers, empty box storages and auto cleansers -
    /// which PopulationSync only mirrors as physical objects. All of their internals
    /// (card lists, pack queues, box counts, spray cans) are per-client in vanilla, so
    /// cards the joiner donated landed in a box the host saw as empty and the box
    /// station literally ate the joiner's boxes.
    ///
    /// Host-authoritative, keyed by PopulationSync (kind, index): the host broadcasts
    /// each container's state hash-gated (~1 Hz while anything changes, full heal every
    /// 15s); joiner actions are blocked-and-forwarded as ops the host applies through
    /// the vanilla methods, and the next broadcast is the echo. Client edits carry a 6s
    /// locally-touched guard so a stale echo can't undo what the player just did.
    ///
    /// Pack opener specifics: the client machine's m_StoredItemList is kept EMPTY so
    /// its own Update() can never run the RNG pack-opening sim (which would invent
    /// cards the host never rolled) - the progress UI is driven from the host state
    /// instead. Collect runs the reveal UI on the COLLECTOR (its AddCard calls travel
    /// through the existing CardDelta mirror into the shared binder), while the host
    /// clears the machine and banks the report counters WITHOUT re-adding the cards.
    /// No coin moves through this module, so the double-charge question never arises.
    /// </summary>
    public class ContainerSync
    {
        public static ContainerSync Instance;

        // PopulationSync kind numbering - shared with ObjMoveSync so "kind 11 index 2"
        // means the same machine on every peer
        private const int KindCardStorage = 9;
        private const int KindCleanser = 10;
        private const int KindPackOpener = 11;
        private const int KindBoxStorage = 12;
        private const int KindDonation = 13;

        // client -> host op codes (first byte of a ContainerOp payload)
        private const byte OpContentSet = 1;
        private const byte OpPackInsert = 2;
        private const byte OpPackTurnOn = 3;
        private const byte OpPackCollect = 4;
        private const byte OpBoxTake = 5;
        private const byte OpBoxStore = 6;
        private const byte OpCleanserToggle = 7;
        private const byte OpCleanserRefill = 8;
        private const byte OpWorkerTakeFlag = 9;

        private const float TickInterval = 1f;
        private const float HealInterval = 15f;
        private const double TouchedGuard = 6.0;

        /// <summary>Set by CoopCore: client -> host op (MsgType.ContainerOp).</summary>
        public Action<Action<BinaryWriter>> SendOp;
        /// <summary>Set by CoopCore: host -> clients state (MsgType.ContainerState).</summary>
        public Action<Action<BinaryWriter>> BroadcastState;
        /// <summary>Set by CoopCore: ask BoxSync to broadcast the loose-box population on the
        /// next tick, so a freshly dispensed empty box appears on the guest within one tick
        /// instead of up to ~1.5s.</summary>
        public Action RequestBoxResync;

        /// <summary>True while sync code itself mutates a container, so the forwarding
        /// patches don't mistake an applied echo for a local player action.</summary>
        public static bool ApplyingRemote;

        // private game state this module must read/write (no public accessors exist)
        private static readonly FieldInfo FiPoIsProcessing =
            AccessTools.Field(typeof(InteractableAutoPackOpener), "m_IsProcessing");
        private static readonly FieldInfo FiPoOpenTimer =
            AccessTools.Field(typeof(InteractableAutoPackOpener), "m_PackOpenTimer");
        private static readonly FieldInfo FiPoOpenedCount =
            AccessTools.Field(typeof(InteractableAutoPackOpener), "m_PackOpenedCount");
        private static readonly FieldInfo FiPoUI =
            AccessTools.Field(typeof(InteractableAutoPackOpener), "m_AutoCardOpenerUI");
        private static readonly FieldInfo FiEbCount =
            AccessTools.Field(typeof(InteractableEmptyBoxStorage), "m_StoredBoxCount");
        private static readonly FieldInfo FiEbMax =
            AccessTools.Field(typeof(InteractableEmptyBoxStorage), "m_MaxStoredBoxCount");
        private static readonly MethodInfo MiEbEval =
            AccessTools.Method(typeof(InteractableEmptyBoxStorage), "EvaluateStoredBoxStackHeight");
        private static readonly FieldInfo FiClTurnedOn =
            AccessTools.Field(typeof(InteractableAutoCleanser), "m_IsTurnedOn");
        private static readonly FieldInfo FiClNeedRefill =
            AccessTools.Field(typeof(InteractableAutoCleanser), "m_IsNeedRefill");
        private static readonly FieldInfo FiClCooldown =
            AccessTools.Field(typeof(InteractableAutoCleanser), "m_IsSprayOnCooldown");
        private static readonly FieldInfo FiClTimer =
            AccessTools.Field(typeof(InteractableAutoCleanser), "m_Timer");

        /// <summary>Client's copy of a pack opener's host-side truth. Kept OUTSIDE the
        /// game object because the machine's own fields must stay inert (see class doc).</summary>
        private class PackMirror
        {
            public int StoredCount;
            public List<int> StoredTypes = new List<int>();
            public bool Processing;
            public float Timer;
            public int OpenedCount;
            public List<CompactCardDataAmount> Output = new List<CompactCardDataAmount>();
        }

        private ShelfManager _sm;
        private float _timer;
        private float _heal;
        private readonly Dictionary<int, int> _lastHash = new Dictionary<int, int>();      // host
        private readonly List<int> _dirty = new List<int>();                               // host
        private readonly Dictionary<int, double> _touched = new Dictionary<int, double>(); // client
        private readonly Dictionary<int, PackMirror> _packMirrors = new Dictionary<int, PackMirror>();

        // hash delegates cached once: a fresh closure per kind per tick would be a
        // steady GC drip for the whole session (same reasoning as CoopCore's stages)
        private readonly Func<object, int> _hashCardStorage, _hashDonation, _hashPackOpener,
            _hashBoxStorage, _hashCleanser;

        public ContainerSync()
        {
            Instance = this;
            _hashCardStorage = obj =>
            {
                var s = (InteractableCardStorageShelf)obj;
                return HashCards(s.GetCompactCardDataAmountList()) * 31 + (s.CanWorkerTake() ? 1 : 0);
            };
            _hashDonation = obj =>
                HashCards(((InteractableBulkDonationBox)obj).GetCompactCardDataAmountList());
            _hashPackOpener = obj =>
            {
                var p = (InteractableAutoPackOpener)obj;
                int h = 17;
                var stored = p.GetStoredItemList();
                h = h * 31 + (stored?.Count ?? 0);
                if (stored != null)
                    for (int i = 0; i < stored.Count; i++)
                        if (stored[i] != null) h = h * 31 + (int)stored[i].GetItemType();
                h = h * 31 + (p.GetIsProcessing() ? 1 : 0);
                // quantized so a processing machine re-broadcasts ~1 Hz, an idle one never
                h = h * 31 + (int)((FiPoOpenTimer?.GetValue(p) as float? ?? 0f) * 2f);
                h = h * 31 + p.GetPackOpenedCount();
                h = h * 31 + HashCards(p.GetCompactCardDataAmountList());
                return h;
            };
            _hashBoxStorage = obj => ((InteractableEmptyBoxStorage)obj).GetBoxStoredCount();
            _hashCleanser = obj =>
            {
                var c = (InteractableAutoCleanser)obj;
                int h = 17;
                h = h * 31 + (c.IsTurnedOn() ? 1 : 0);
                h = h * 31 + (c.IsNeedRefill() ? 2 : 0);
                var stored = c.GetStoredItemList();
                h = h * 31 + (stored?.Count ?? 0);
                if (stored != null)
                    for (int i = 0; i < stored.Count; i++)
                        if (stored[i] != null) h = h * 31 + (int)(stored[i].GetContentFill() * 100f);
                return h;
            };
        }

        public void Reset()
        {
            _sm = null;
            _timer = -5.3f; // staggered phase vs the other snapshot engines
            _heal = 0f;
            _lastHash.Clear();
            _dirty.Clear();
            _touched.Clear();
            _packMirrors.Clear();
        }

        public void ForceResend()
        {
            // forgetting every hash makes the next HostTick rebroadcast the world -
            // the on-join snapshot for containers
            _lastHash.Clear();
            _heal = 0f;
        }

        private ShelfManager Sm()
        {
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        private int IndexOf(int kind, object obj)
        {
            var sm = Sm();
            if (sm == null) return -1;
            var list = PopulationSync.GetList(sm, kind);
            if (list == null) return -1;
            int idx = list.IndexOf(obj);
            return idx < 250 ? idx : -1; // wire index is a byte (matches PopulationSync's cap)
        }

        private T Get<T>(int kind, int idx) where T : class
        {
            var sm = Sm();
            if (sm == null) return null;
            var list = PopulationSync.GetList(sm, kind);
            if (list == null || idx < 0 || idx >= list.Count) return null;
            return list[idx] as T;
        }

        private void Touch(int kind, int idx)
        {
            _touched[(kind << 8) | idx] = Time.realtimeSinceStartupAsDouble;
        }

        private bool IsTouched(int kind, int idx)
        {
            return _touched.TryGetValue((kind << 8) | idx, out double t)
                && Time.realtimeSinceStartupAsDouble - t < TouchedGuard;
        }

        // ---------------- host: hash-gated broadcast ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame) return;
            _timer += dt;
            if (_timer < TickInterval) return;
            _timer -= TickInterval;
            if (_timer > TickInterval) _timer = TickInterval; // clamp debt after a hitch
            try
            {
                var sm = Sm();
                if (sm == null) return;
                _heal += TickInterval;
                if (_heal >= HealInterval)
                {
                    // periodic full rebroadcast repairs any client that missed an echo
                    _heal = 0f;
                    _lastHash.Clear();
                }
                _dirty.Clear();
                CollectKind(sm, KindCardStorage, _hashCardStorage);
                CollectKind(sm, KindDonation, _hashDonation);
                CollectKind(sm, KindPackOpener, _hashPackOpener);
                CollectKind(sm, KindBoxStorage, _hashBoxStorage);
                CollectKind(sm, KindCleanser, _hashCleanser);
                if (_dirty.Count == 0) return;
                var dirty = new List<int>(_dirty); // snapshot for the closure
                BroadcastState?.Invoke(bw =>
                {
                    bw.Write((ushort)dirty.Count);
                    for (int i = 0; i < dirty.Count; i++)
                        WriteRecord(bw, dirty[i] >> 8, dirty[i] & 0xFF);
                });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ContainerSync host: " + e.Message); }
        }

        private void CollectKind(ShelfManager sm, int kind, Func<object, int> hashFn)
        {
            var list = PopulationSync.GetList(sm, kind);
            if (list == null) return;
            for (int i = 0; i < list.Count && i < 250; i++)
            {
                if (list[i] == null) continue;
                int h;
                try { h = hashFn(list[i]); }
                catch { continue; }
                int key = (kind << 8) | i;
                if (_lastHash.TryGetValue(key, out int prev) && prev == h) continue;
                _lastHash[key] = h;
                _dirty.Add(key);
            }
        }

        private void WriteRecord(BinaryWriter bw, int kind, int idx)
        {
            bw.Write((byte)kind);
            bw.Write((byte)idx);
            switch (kind)
            {
                case KindCardStorage:
                {
                    var s = Get<InteractableCardStorageShelf>(kind, idx);
                    bw.Write(s == null || s.CanWorkerTake());
                    WriteCards(bw, s?.GetCompactCardDataAmountList());
                    break;
                }
                case KindDonation:
                {
                    var b = Get<InteractableBulkDonationBox>(kind, idx);
                    WriteCards(bw, b?.GetCompactCardDataAmountList());
                    break;
                }
                case KindPackOpener:
                {
                    var p = Get<InteractableAutoPackOpener>(kind, idx);
                    var stored = p?.GetStoredItemList();
                    int n = Mathf.Min(stored?.Count ?? 0, 250);
                    bw.Write((byte)n);
                    for (int i = 0; i < n; i++)
                        bw.Write(stored[i] != null ? (int)stored[i].GetItemType() : 0);
                    bw.Write(p != null && p.GetIsProcessing());
                    bw.Write(p != null ? (FiPoOpenTimer?.GetValue(p) as float? ?? 0f) : 0f);
                    bw.Write(p != null ? p.GetPackOpenedCount() : 0);
                    WriteCards(bw, p?.GetCompactCardDataAmountList());
                    break;
                }
                case KindBoxStorage:
                {
                    var s = Get<InteractableEmptyBoxStorage>(kind, idx);
                    bw.Write(s != null ? s.GetBoxStoredCount() : 0);
                    break;
                }
                case KindCleanser:
                {
                    var c = Get<InteractableAutoCleanser>(kind, idx);
                    byte flags = 0;
                    if (c != null && c.IsTurnedOn()) flags |= 1;
                    if (c == null || c.IsNeedRefill()) flags |= 2;
                    bw.Write(flags);
                    var stored = c?.GetStoredItemList();
                    int n = Mathf.Min(stored?.Count ?? 0, 32);
                    bw.Write((byte)n);
                    for (int i = 0; i < n; i++)
                        bw.Write(stored[i] != null ? stored[i].GetContentFill() : 0f);
                    break;
                }
            }
        }

        // ---------------- host: apply client ops ----------------

        public void HostApplyOp(BinaryReader br)
        {
            byte op = br.ReadByte();
            try
            {
                switch (op)
                {
                    case OpContentSet:
                    {
                        int kind = br.ReadByte();
                        int idx = br.ReadByte();
                        bool canTake = br.ReadBoolean();
                        var cards = ReadCards(br);
                        // never drop this even if the host has the same UI open: the
                        // joiner's binder already paid these cards through the CardDelta
                        // mirror, so losing the list here would lose the cards for real
                        ApplyContent(kind, idx, cards, kind == KindCardStorage, canTake);
                        break;
                    }
                    case OpWorkerTakeFlag:
                    {
                        int idx = br.ReadByte();
                        bool canTake = br.ReadBoolean();
                        var s = Get<InteractableCardStorageShelf>(KindCardStorage, idx);
                        if (s == null) break;
                        ApplyingRemote = true;
                        try { s.SetCanWorkerTake(canTake); s.OnCardStorageShelfSettingDone(); }
                        finally { ApplyingRemote = false; }
                        break;
                    }
                    case OpPackInsert:
                    {
                        int idx = br.ReadByte();
                        var itemType = (EItemType)br.ReadInt32();
                        var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
                        if (p == null) break;
                        // apply unconditionally (like a worker refill would): dropping it
                        // would eat the pack the joiner's box already gave up
                        var item = SpawnItem(itemType, p.m_PosInside);
                        if (item == null) break;
                        ApplyingRemote = true;
                        try { p.AddItem(item, addToFront: true, isPlayer: false); }
                        finally { ApplyingRemote = false; }
                        break;
                    }
                    case OpPackTurnOn:
                    {
                        int idx = br.ReadByte();
                        var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
                        // the state check pins vanilla OnMouseButtonUp to its turn-on
                        // branch; anything else means the click raced and is stale
                        if (p != null && !p.GetIsProcessing() && p.GetStoredItemList().Count > 0)
                            p.OnMouseButtonUp();
                        break;
                    }
                    case OpPackCollect:
                    {
                        int idx = br.ReadByte();
                        var revealed = ReadCards(br);
                        HostApplyPackCollect(idx, revealed);
                        break;
                    }
                    case OpBoxTake:
                    {
                        int idx = br.ReadByte();
                        var reqPos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        HostApplyBoxTake(idx, reqPos);
                        // forget the cached count so the corrected value re-broadcasts on the
                        // NEXT tick instead of waiting up to ~15s for the heal (else the guest
                        // sees a stale-high count and each further click "can't pick up")
                        _lastHash.Remove((KindBoxStorage << 8) | idx);
                        break;
                    }
                    case OpBoxStore:
                    {
                        int idx = br.ReadByte();
                        var s = Get<InteractableEmptyBoxStorage>(KindBoxStorage, idx);
                        if (s == null) break;
                        int max = FiEbMax?.GetValue(s) as int? ?? 200;
                        if (s.GetBoxStoredCount() >= max) break;
                        FiEbCount?.SetValue(s, s.GetBoxStoredCount() + 1);
                        MiEbEval?.Invoke(s, null);
                        _lastHash.Remove((KindBoxStorage << 8) | idx);
                        break;
                    }
                    case OpCleanserToggle:
                    {
                        int idx = br.ReadByte();
                        bool on = br.ReadBoolean();
                        var c = Get<InteractableAutoCleanser>(KindCleanser, idx);
                        if (c == null) break;
                        // direct field write instead of vanilla OnMouseButtonUp: the
                        // vanilla path would flash tooltips/popups on the HOST's HUD
                        // for a button the host never touched
                        FiClTurnedOn?.SetValue(c, on);
                        if (!on)
                        {
                            FiClCooldown?.SetValue(c, true);
                            FiClTimer?.SetValue(c, 0f);
                        }
                        break;
                    }
                    case OpCleanserRefill:
                    {
                        int idx = br.ReadByte();
                        float fill = br.ReadSingle();
                        var c = Get<InteractableAutoCleanser>(KindCleanser, idx);
                        if (c == null || !c.HasEnoughSlot()) break;
                        var item = SpawnItem(EItemType.Deodorant, c.m_PosList[0], fill);
                        if (item == null) break;
                        ApplyingRemote = true;
                        try { c.AddItem(item, addToFront: true); }
                        finally { ApplyingRemote = false; }
                        break;
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"ContainerSync op {op}: {e.Message}"); }
        }

        private void HostApplyPackCollect(int idx, List<CompactCardDataAmount> revealed)
        {
            var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
            if (p == null) return;
            var output = p.GetCompactCardDataAmountList();
            // Even if there's nothing left to bank (a stale/duplicate collect, or the host
            // already collected), still force the machine idle below. The old early-return
            // left m_IsProcessing pinned TRUE, and the heal then rebroadcast processing=true
            // to the guest forever - so the guest could never add packs again ("no empty
            // slot") after the first collect, while the host was unaffected.
            if (output != null && output.Count > 0)
            {
                // the collector's reveal already put its snapshot of the cards into the
                // shared binder (via the AddCard mirror); only bank the DIFFERENCE - packs
                // that finished after the client's last state echo
                for (int i = 0; i < output.Count; i++)
                {
                    var o = output[i];
                    if (o == null) continue;
                    int rem = o.amount - AmountFor(revealed, o);
                    if (rem <= 0) continue;
                    var cd = CPlayerData.GetCardData(o.cardSaveIndex, o.expansionType, o.isDestiny);
                    if (cd != null) CPlayerData.AddCard(cd, rem); // mirrored back to the collector
                }
                int opened = p.GetPackOpenedCount();
                CPlayerData.m_GameReportDataCollect.cardPackOpened += opened;
                CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened += opened;
                AchievementManager.OnCardPackOpened(CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened);
                output.Clear();
            }
            FiPoOpenedCount?.SetValue(p, 0);
            FiPoIsProcessing?.SetValue(p, false);
            p.m_CurrentState = 0;
            if (FiPoUI?.GetValue(p) is AutoCardOpenerUI ui)
            {
                ui.SetUIState(0);
                ui.UpdatePackCountText(0, p.m_MaxPackCount);
            }
            _lastHash.Remove((KindPackOpener << 8) | idx); // force an idle rebroadcast next tick
        }

        private void HostApplyBoxTake(int idx, Vector3 reqPos)
        {
            var s = Get<InteractableEmptyBoxStorage>(KindBoxStorage, idx);
            if (s == null || s.GetBoxStoredCount() <= 0) return;
            // spawn the box officially (RestockManager registers it) so BoxSync mirrors
            // it back to the joiner at the storage's own hand-off spot; vanilla TakeBox
            // is unusable here because it force-holds the box in the HOST's hands
            var box = RestockManager.SpawnPackageBoxItem(EItemType.None, 0, isBigBox: true);
            if (box == null) return;
            var loc = s.m_EmptyBoxSpawnLoc;
            box.transform.position = loc != null ? loc.position : reqPos;
            if (loc != null) box.transform.rotation = loc.rotation;
            box.ForceSetOpenCloseInstant(isOpen: true);
            box.SetOpenCloseBox(isOpen: false, isPlayer: false);
            FiEbCount?.SetValue(s, s.GetBoxStoredCount() - 1);
            MiEbEval?.Invoke(s, null);
            // push the freshly spawned box to the guest promptly (else up to ~1.5s late)
            RequestBoxResync?.Invoke();
        }

        // ---------------- client: apply authoritative state ----------------

        public void ClientApplyState(BinaryReader br)
        {
            int n = br.ReadUInt16();
            for (int r = 0; r < n; r++)
            {
                int kind = br.ReadByte();
                int idx = br.ReadByte();
                try
                {
                    // always consume the record fully; the guards only skip the APPLY
                    switch (kind)
                    {
                        case KindCardStorage:
                        {
                            bool canTake = br.ReadBoolean();
                            var cards = ReadCards(br);
                            var s = Get<InteractableCardStorageShelf>(kind, idx);
                            // a container the player is editing right now (or edited in
                            // the last 6s) is his; the host hears about it via the op
                            if (s == null || s.IsEditingBulkBox() || IsTouched(kind, idx)) break;
                            ApplyContent(kind, idx, cards, true, canTake);
                            break;
                        }
                        case KindDonation:
                        {
                            var cards = ReadCards(br);
                            var b = Get<InteractableBulkDonationBox>(kind, idx);
                            if (b == null || b.IsEditingBulkBox() || IsTouched(kind, idx)) break;
                            ApplyContent(kind, idx, cards, false, false);
                            break;
                        }
                        case KindPackOpener:
                        {
                            int sc = br.ReadByte();
                            var types = new List<int>(sc);
                            for (int i = 0; i < sc; i++) types.Add(br.ReadInt32());
                            bool proc = br.ReadBoolean();
                            float timer = br.ReadSingle();
                            int opened = br.ReadInt32();
                            var output = ReadCards(br);
                            var p = Get<InteractableAutoPackOpener>(kind, idx);
                            if (p == null) break;
                            if (!_packMirrors.TryGetValue(idx, out var m))
                                _packMirrors[idx] = m = new PackMirror();
                            m.StoredCount = sc;
                            m.StoredTypes = types;
                            m.Processing = proc;
                            m.Timer = timer;
                            m.OpenedCount = opened;
                            m.Output = output;
                            // the pack opener was the only container kind with NO touch guard:
                            // a stale/heal "processing=true" echo arriving right after the
                            // guest's own local collect re-pinned m_IsProcessing and blocked
                            // any further insert ("no empty slot"). Skip a stale echo for ~6s
                            // after a local collect/insert (the record is already consumed).
                            if (IsTouched(kind, idx)) break;
                            ApplyPackMirrorToMachine(p, m);
                            break;
                        }
                        case KindBoxStorage:
                        {
                            int count = br.ReadInt32();
                            var s = Get<InteractableEmptyBoxStorage>(kind, idx);
                            // do NOT gate on IsTouched here: on a TAKE the guest suppresses
                            // its local count entirely (no local write to protect), so the
                            // touch-guard only stranded the authoritative count for ~15s and
                            // made further takes look like "can't pick up". The record is
                            // already consumed above, so stream position is safe.
                            if (s == null) break;
                            FiEbCount?.SetValue(s, count);
                            MiEbEval?.Invoke(s, null);
                            break;
                        }
                        case KindCleanser:
                        {
                            byte flags = br.ReadByte();
                            int cnt = br.ReadByte();
                            var fills = new List<float>(cnt);
                            for (int i = 0; i < cnt; i++) fills.Add(br.ReadSingle());
                            var c = Get<InteractableAutoCleanser>(kind, idx);
                            if (c == null || IsTouched(kind, idx)) break;
                            ApplyCleanserState(c, (flags & 1) != 0, (flags & 2) != 0, fills);
                            break;
                        }
                        default:
                            return; // unknown kind: cannot know its length, stop parsing
                    }
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning($"ContainerSync apply kind {kind}: {e.Message}");
                    return; // stream position is unreliable after a mid-record throw
                }
            }
        }

        private void ApplyContent(int kind, int idx, List<CompactCardDataAmount> cards,
            bool hasFlag, bool canWorkerTake)
        {
            ApplyingRemote = true;
            try
            {
                if (kind == KindCardStorage)
                {
                    var s = Get<InteractableCardStorageShelf>(kind, idx);
                    if (s == null) return;
                    s.SetCompactCardDataAmountList(cards);
                    if (hasFlag)
                    {
                        s.SetCanWorkerTake(canWorkerTake);
                        s.OnCardStorageShelfSettingDone();
                    }
                }
                else if (kind == KindDonation)
                {
                    var b = Get<InteractableBulkDonationBox>(kind, idx);
                    if (b == null) return;
                    b.SetCompactCardDataAmountList(cards);
                    b.UpdateFillPercent(Mathf.Clamp01(
                        (float)b.GetTotalCardAmount() / b.GetBoxTotalCardCountMax()));
                }
            }
            finally { ApplyingRemote = false; }
        }

        /// <summary>Client: push a pack mirror onto the machine's UI/tooltip surface.
        /// m_StoredItemList is drained, never filled - an empty list is what keeps the
        /// machine's own Update() from rolling packs the host never rolled.</summary>
        private void ApplyPackMirrorToMachine(InteractableAutoPackOpener p, PackMirror m)
        {
            ApplyingRemote = true;
            try
            {
                var stored = p.GetStoredItemList();
                if (stored != null && stored.Count > 0)
                {
                    // save-transferred packs from the join snapshot: real items that
                    // would let the local sim run - retire them, the host has the truth
                    for (int i = stored.Count - 1; i >= 0; i--)
                    {
                        var it = stored[i];
                        stored.RemoveAt(i);
                        if (it != null) try { ItemSpawnManager.DisableItem(it); } catch { }
                    }
                }
                FiPoIsProcessing?.SetValue(p, m.Processing); // drives the Collect tooltip
                p.m_CurrentState = m.Processing ? (m.StoredCount > 0 ? 1 : 2) : 0;
                if (FiPoUI?.GetValue(p) is AutoCardOpenerUI ui)
                {
                    if (m.Processing && m.StoredCount > 0)
                    {
                        ui.SetUIState(1);
                        ui.UpdateProcessingFillBar(1f - (float)m.StoredCount / p.m_MaxPackCount);
                        ui.UpdateProcessingTimeLeftText(p.m_PackOpenTime * m.StoredCount - m.Timer);
                    }
                    else if (m.Processing)
                    {
                        ui.SetUIState(2);
                    }
                    else
                    {
                        ui.SetUIState(0);
                        ui.UpdatePackCountText(m.StoredCount, p.m_MaxPackCount);
                    }
                }
            }
            finally { ApplyingRemote = false; }
        }

        private void ApplyCleanserState(InteractableAutoCleanser c, bool on, bool needRefill,
            List<float> fills)
        {
            ApplyingRemote = true;
            try
            {
                // reconcile the visible spray cans through the vanilla add/remove so
                // m_ItemAmount and the slot layout stay coherent; guards bound the loops
                // because RemoveItem itself can shed additional empty cans
                int guard = 12;
                while (c.GetItemCount() > fills.Count && guard-- > 0)
                {
                    var last = c.GetLastItem();
                    if (last == null) break;
                    c.RemoveItem(last);
                    try { ItemSpawnManager.DisableItem(last); } catch { }
                }
                guard = 12;
                while (c.GetItemCount() < fills.Count && guard-- > 0 && c.HasEnoughSlot())
                {
                    var item = SpawnItem(EItemType.Deodorant, c.m_PosList[0], 1f);
                    if (item == null) break;
                    c.AddItem(item, addToFront: true);
                }
                var stored = c.GetStoredItemList();
                if (stored != null)
                    for (int i = 0; i < stored.Count && i < fills.Count; i++)
                        if (stored[i] != null) stored[i].SetContentFill(fills[i]);
                // flags last: AddItem flips m_IsNeedRefill on its own
                FiClTurnedOn?.SetValue(c, on);
                FiClNeedRefill?.SetValue(c, needRefill);
            }
            finally { ApplyingRemote = false; }
        }

        // ---------------- client: forwarded actions (called from patches) ----------------

        private void ClientForwardContent(int kind, object container,
            List<CompactCardDataAmount> cards, bool canWorkerTake)
        {
            int idx = IndexOf(kind, container);
            if (idx < 0) return;
            Touch(kind, idx);
            SendOp?.Invoke(bw =>
            {
                bw.Write(OpContentSet);
                bw.Write((byte)kind);
                bw.Write((byte)idx);
                bw.Write(canWorkerTake);
                WriteCards(bw, cards);
            });
        }

        private void ClientPackOpenerClick(InteractableAutoPackOpener p)
        {
            int idx = IndexOf(KindPackOpener, p);
            if (idx < 0) return;
            _packMirrors.TryGetValue(idx, out var m);
            SoundManager.PlayAudio("SFX_ButtonLightTap", 0.6f, 0.5f);
            if (m == null || !m.Processing)
            {
                if (m != null && m.StoredCount > 0)
                    SendOp?.Invoke(bw => { bw.Write(OpPackTurnOn); bw.Write((byte)idx); });
                else
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.NoCardPackInMachine);
            }
            else if (m.Output.Count > 0 && m.StoredCount <= 0)
            {
                // the reveal (and its AddCard calls) run HERE, on the collector - the
                // CardDelta mirror carries the cards into the shared binder; the host's
                // OpPackCollect clears the machine and banks report credit WITHOUT
                // re-adding what we list as revealed
                var revealed = new List<CompactCardDataAmount>(m.Output);
                CPlayerData.m_GameReportDataCollect.cardPackOpened += m.OpenedCount;
                CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened += m.OpenedCount;
                AchievementManager.OnCardPackOpened(CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened);
                try
                {
                    CSingleton<InteractionPlayerController>.Instance
                        .m_ShowCardObtainedPage.ShowCardObtained(revealed);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("ContainerSync reveal: " + e.Message); }
                SendOp?.Invoke(bw =>
                {
                    bw.Write(OpPackCollect);
                    bw.Write((byte)idx);
                    WriteCards(bw, revealed);
                });
                Touch(KindPackOpener, idx); // protect the local "now idle" state from a stale echo
                m.Output.Clear();
                m.Processing = false;
                m.OpenedCount = 0;
                m.StoredCount = 0;
                ApplyPackMirrorToMachine(p, m);
                SoundManager.PlayAudio("SFX_PercStarJingle3", 0.6f);
                SoundManager.PlayAudio("SFX_Gift", 0.6f);
            }
            else
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.WaitAllCardPacksToBeProcessed);
            }
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // joiner edits the storage/donation UI: every mutation funnels through the
            // vanilla setter, so its postfix is the one seam that catches them all
            Try(h, typeof(InteractableCardStorageShelf), "SetCompactCardDataAmountList",
                postfix: new HarmonyMethod(typeof(ContainerSync), nameof(StorageContentPostfix)));
            Try(h, typeof(InteractableBulkDonationBox), "SetCompactCardDataAmountList",
                postfix: new HarmonyMethod(typeof(ContainerSync), nameof(DonationContentPostfix)));
            Try(h, typeof(InteractableCardStorageShelf), "SetCanWorkerTake",
                postfix: new HarmonyMethod(typeof(ContainerSync), nameof(WorkerTakePostfix)));

            // pack opener: the button and every item path are host-owned on the client
            Try(h, typeof(InteractableAutoPackOpener), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(ContainerSync), nameof(PackOpenerClickPrefix)));
            Try(h, typeof(InteractableAutoPackOpener), "AddItem",
                prefix: new HarmonyMethod(typeof(ContainerSync), nameof(PackOpenerAddItemPrefix)));
            Try(h, typeof(InteractableAutoPackOpener), "TakeItemToHand",
                prefix: new HarmonyMethod(typeof(ContainerSync), nameof(TakeItemBlockPrefix)));

            // empty box storage: TakeBox would spawn a client-local box that BoxSync's
            // reconciliation culls within seconds - the station 'eats' the box
            Try(h, typeof(InteractableEmptyBoxStorage), "TakeBox",
                prefix: new HarmonyMethod(typeof(ContainerSync), nameof(TakeBoxPrefix)));
            Try(h, typeof(InteractableEmptyBoxStorage), "StoreBox",
                prefix: new HarmonyMethod(typeof(ContainerSync), nameof(StoreBoxPrefix)),
                postfix: new HarmonyMethod(typeof(ContainerSync), nameof(StoreBoxPostfix)));

            // auto cleanser: the toggle applies instantly on the client (pure local
            // fields + UI, safe) and the op tells the host; refills are host-owned
            Try(h, typeof(InteractableAutoCleanser), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(ContainerSync), nameof(CleanserTogglePostfix)));
            Try(h, typeof(InteractableAutoCleanser), "AddItem",
                prefix: new HarmonyMethod(typeof(ContainerSync), nameof(CleanserAddItemPrefix)));
            Try(h, typeof(InteractableAutoCleanser), "TakeItemToHand",
                prefix: new HarmonyMethod(typeof(ContainerSync), nameof(TakeItemBlockPrefix)));
        }

        public static void StorageContentPostfix(InteractableCardStorageShelf __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return;
            Instance?.ClientForwardContent(KindCardStorage, __instance,
                __instance.GetCompactCardDataAmountList(), __instance.CanWorkerTake());
        }

        public static void DonationContentPostfix(InteractableBulkDonationBox __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return;
            Instance?.ClientForwardContent(KindDonation, __instance,
                __instance.GetCompactCardDataAmountList(), false);
        }

        public static void WorkerTakePostfix(InteractableCardStorageShelf __instance, bool canWorkerTake)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return;
            var self = Instance;
            if (self == null) return;
            int idx = self.IndexOf(KindCardStorage, __instance);
            if (idx < 0) return;
            self.Touch(KindCardStorage, idx);
            self.SendOp?.Invoke(bw =>
            {
                bw.Write(OpWorkerTakeFlag);
                bw.Write((byte)idx);
                bw.Write(canWorkerTake);
            });
        }

        public static bool PackOpenerClickPrefix(InteractableAutoPackOpener __instance)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            try { Instance?.ClientPackOpenerClick(__instance); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ContainerSync click: " + e.Message); }
            return false;
        }

        public static bool PackOpenerAddItemPrefix(InteractableAutoPackOpener __instance, Item item)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            // the guest's join world-load restores the host save via
            // InteractableAutoPackOpener.LoadData, which calls AddItem once per stored
            // pack (decompiled ~384). Those are NOT player inserts - forwarding each one
            // makes the host spawn a NEW pack it already has, duplicating every pack that
            // sat in an opener on every join/rejoin. Skip the op during the reload, same
            // as the symmetric destroy guard (CardBoxSync/FurnBoxSync DestroyedPrefix).
            // Still retire the item (as below) so it doesn't float - the host echoes truth.
            if (CoopCore.ClientReloading)
            {
                try { ItemSpawnManager.DisableItem(item); } catch { }
                return false;
            }
            var self = Instance;
            if (self == null) return true;
            int idx = self.IndexOf(KindPackOpener, __instance);
            if (idx >= 0)
            {
                int itemType = 0;
                try { itemType = (int)item.GetItemType(); } catch { }
                self.SendOp?.Invoke(bw =>
                {
                    bw.Write(OpPackInsert);
                    bw.Write((byte)idx);
                    bw.Write(itemType);
                });
                self.Touch(KindPackOpener, idx); // protect the local insert from a stale echo
            }
            // the caller strips the item out of its (synced) box either way; retire it
            // here so it doesn't float in the world - the host's insert echoes back
            try { ItemSpawnManager.DisableItem(item); } catch { }
            return false;
        }

        /// <summary>Shared client block for both machines' TakeItemToHand: pulling an
        /// item back OUT client-side would hand the joiner a phantom the host still
        /// counts as inside the machine.</summary>
        public static bool TakeItemBlockPrefix(ref Item __result)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            __result = null;
            return false;
        }

        public static bool TakeBoxPrefix(InteractableEmptyBoxStorage __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            var self = Instance;
            if (self == null) return false;
            int idx = self.IndexOf(KindBoxStorage, __instance);
            if (idx >= 0 && __instance.GetBoxStoredCount() > 0)
            {
                var loc = __instance.m_EmptyBoxSpawnLoc;
                Vector3 pos = loc != null ? loc.position : __instance.transform.position;
                self.Touch(KindBoxStorage, idx);
                self.SendOp?.Invoke(bw =>
                {
                    bw.Write(OpBoxTake);
                    bw.Write((byte)idx);
                    bw.Write(pos.x); bw.Write(pos.y); bw.Write(pos.z);
                });
            }
            return false;
        }

        public static void StoreBoxPrefix(InteractableEmptyBoxStorage __instance, out int __state)
        {
            __state = __instance.GetBoxStoredCount();
        }

        public static void StoreBoxPostfix(InteractableEmptyBoxStorage __instance, int __state)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return;
            // vanilla StoreBox has four rejection exits; only a grown count means the
            // box was really banked (its OnDestroyed already told the host to kill it)
            if (__instance.GetBoxStoredCount() <= __state) return;
            var self = Instance;
            if (self == null) return;
            int idx = self.IndexOf(KindBoxStorage, __instance);
            if (idx < 0) return;
            self.Touch(KindBoxStorage, idx);
            self.SendOp?.Invoke(bw => { bw.Write(OpBoxStore); bw.Write((byte)idx); });
        }

        public static void CleanserTogglePostfix(InteractableAutoCleanser __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return;
            var self = Instance;
            if (self == null) return;
            int idx = self.IndexOf(KindCleanser, __instance);
            if (idx < 0) return;
            bool on = __instance.IsTurnedOn(); // vanilla already flipped it locally
            self.Touch(KindCleanser, idx);
            self.SendOp?.Invoke(bw =>
            {
                bw.Write(OpCleanserToggle);
                bw.Write((byte)idx);
                bw.Write(on);
            });
        }

        public static bool CleanserAddItemPrefix(InteractableAutoCleanser __instance, Item item)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            // same join-LoadData dupe as PackOpenerAddItemPrefix: InteractableAutoCleanser
            // .LoadData calls AddItem once per saved spray can (decompiled ~396). Forwarding
            // each as OpCleanserRefill makes the host spawn extra deodorant cans on every
            // join/rejoin. Skip the op during the reload; still retire the local item.
            if (CoopCore.ClientReloading)
            {
                try { ItemSpawnManager.DisableItem(item); } catch { }
                return false;
            }
            var self = Instance;
            if (self == null) return true;
            int idx = self.IndexOf(KindCleanser, __instance);
            if (idx >= 0)
            {
                float fill = 1f;
                try { fill = item.GetContentFill(); } catch { }
                self.Touch(KindCleanser, idx);
                self.SendOp?.Invoke(bw =>
                {
                    bw.Write(OpCleanserRefill);
                    bw.Write((byte)idx);
                    bw.Write(fill);
                });
            }
            try { ItemSpawnManager.DisableItem(item); } catch { }
            return false;
        }

        // ---------------- shared helpers ----------------

        /// <summary>The game's own save-load recipe for materializing an Item by type.</summary>
        private static Item SpawnItem(EItemType itemType, Transform parent, float contentFill = -1f)
        {
            try
            {
                var meshData = InventoryBase.GetItemMeshData(itemType);
                var item = ItemSpawnManager.GetItem(parent);
                item.SetMesh(meshData.mesh, meshData.material, itemType,
                    meshData.meshSecondary, meshData.materialSecondary);
                item.transform.localPosition = Vector3.zero;
                item.transform.localRotation = Quaternion.identity;
                if (contentFill >= 0f) item.SetContentFill(contentFill);
                item.gameObject.SetActive(true);
                return item;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"ContainerSync spawn {itemType}: {e.Message}");
                return null;
            }
        }

        private static int AmountFor(List<CompactCardDataAmount> list, CompactCardDataAmount id)
        {
            if (list == null) return 0;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e != null && e.cardSaveIndex == id.cardSaveIndex
                    && e.expansionType == id.expansionType && e.isDestiny == id.isDestiny)
                    return e.amount;
            }
            return 0;
        }

        private static int HashCards(List<CompactCardDataAmount> list)
        {
            int h = 17;
            if (list == null) return h;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null) continue;
                h = h * 31 + e.cardSaveIndex;
                h = h * 31 + (int)e.expansionType;
                h = h * 31 + (e.isDestiny ? 1 : 0);
                h = h * 31 + e.amount;
                h = h * 31 + e.gradedCardIndex;
            }
            return h;
        }

        private static void WriteCards(BinaryWriter bw, List<CompactCardDataAmount> list)
        {
            int n = Mathf.Min(list?.Count ?? 0, 2000);
            bw.Write((ushort)n);
            for (int i = 0; i < n; i++)
            {
                var e = list[i] ?? new CompactCardDataAmount();
                bw.Write(e.cardSaveIndex);
                bw.Write((int)e.expansionType);
                bw.Write(e.isDestiny);
                bw.Write(e.amount);
                bw.Write(e.gradedCardIndex);
            }
        }

        private static List<CompactCardDataAmount> ReadCards(BinaryReader br)
        {
            int n = br.ReadUInt16();
            var list = new List<CompactCardDataAmount>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new CompactCardDataAmount();
                e.cardSaveIndex = br.ReadInt32();
                e.expansionType = (ECardExpansionType)br.ReadInt32();
                e.isDestiny = br.ReadBoolean();
                e.amount = br.ReadInt32();
                e.gradedCardIndex = br.ReadInt32();
                list.Add(e);
            }
            return list;
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
    }
}
