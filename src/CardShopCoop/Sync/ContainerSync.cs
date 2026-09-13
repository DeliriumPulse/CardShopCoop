using CardShopCoop.Util;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
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
    public class ContainerSync : TickableCoopModule
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
        private const byte OpPackClaim = 6;
        private const byte OpBoxTake = 5;
        private const byte OpBoxStoreAtomic = 10;
        private const byte OpCleanserToggle = 7;
        private const byte OpCleanserRefill = 8;
        private const byte OpWorkerTakeFlag = 9;

        private const float TickInterval = 1f;
        private const float HealInterval = 15f;
        private const double TouchedGuard = 6.0;

        /// <summary>Set by CoopCore: client -> host op (MsgType.ContainerOp).</summary>
        public Action<INetMessage> SendOp;
        /// <summary>Set by CoopCore: host -> clients state (MsgType.ContainerState).</summary>
        public Action<INetMessage> BroadcastState;
        /// <summary>Set by CoopCore: host -> the requesting client, used for the accepted
        /// empty-box take hand-off.</summary>
        public Action<int, INetMessage> SendToClient;
        /// <summary>Set by CoopCore: put a newly mirrored authoritative box into the local
        /// player's hands. Kept as a delegate so this module does not own player reflection.</summary>
        public Func<InteractablePackagingBox_Item, bool> HoldClientBox;
        /// <summary>Set by CoopCore: ask the box engine to broadcast the loose-box population on the
        /// next tick, so a freshly dispensed empty box appears on the guest within one tick
        /// instead of up to ~1.5s.</summary>
        public Action RequestBoxResync;

        /// <summary>True while sync code itself mutates a container, so the forwarding
        /// patches don't mistake an applied echo for a local player action.</summary>
        public static bool ApplyingRemote;

        // private game state this module must read/write (no public accessors exist)
        private static readonly FieldInfo FiPoIsProcessing =
            ReflectionSurface.RequiredField(typeof(InteractableAutoPackOpener), "m_IsProcessing");
        private static readonly FieldInfo FiPoOpenTimer =
            ReflectionSurface.RequiredField(typeof(InteractableAutoPackOpener), "m_PackOpenTimer");
        private static readonly FieldInfo FiPoOpenedCount =
            ReflectionSurface.RequiredField(typeof(InteractableAutoPackOpener), "m_PackOpenedCount");
        private static readonly FieldInfo FiPoUI =
            ReflectionSurface.RequiredField(typeof(InteractableAutoPackOpener), "m_AutoCardOpenerUI");
        private static readonly FieldInfo FiEbCount =
            ReflectionSurface.RequiredField(typeof(InteractableEmptyBoxStorage), "m_StoredBoxCount");
        private static readonly FieldInfo FiEbMax =
            ReflectionSurface.RequiredField(typeof(InteractableEmptyBoxStorage), "m_MaxStoredBoxCount");
        private static readonly MethodInfo MiEbEval =
            ReflectionSurface.RequiredMethod(typeof(InteractableEmptyBoxStorage), "EvaluateStoredBoxStackHeight");
        private static readonly FieldInfo FiClTurnedOn =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_IsTurnedOn");
        private static readonly FieldInfo FiClNeedRefill =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_IsNeedRefill");
        private static readonly FieldInfo FiClCooldown =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_IsSprayOnCooldown");
        private static readonly FieldInfo FiClTimer =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_Timer");
        // the cleanser is the only container whose count is stored independently of its
        // list (every other one derives from m_StoredItemList.Count), and vanilla indexes
        // the list with it - so the reconcile has to be able to force the two back into
        // agreement rather than trust either one. See ApplyCleanserState.
        private static readonly FieldInfo FiClItemAmount =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_ItemAmount");

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
            public int CurrentState;
            public bool CollectClaimed;
        }

        private ShelfManager _sm;
        private float _timer;
        private float _heal;
        private readonly Dictionary<int, int> _lastHash = new Dictionary<int, int>();      // host
        private readonly List<int> _dirty = new List<int>();                               // host
        private readonly Dictionary<int, double> _touched = new Dictionary<int, double>(); // client
        private readonly Dictionary<int, PackMirror> _packMirrors = new Dictionary<int, PackMirror>();
        private readonly Dictionary<int, int> _packClaimOwner = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _packClaimToken = new Dictionary<int, int>();
        private int _nextPackClaimToken = 1;
        private readonly HashSet<ushort> _pendingBoxTakes = new HashSet<ushort>();
        private readonly HashSet<int> _pendingBoxTakeSlots = new HashSet<int>();
        private readonly HashSet<InteractableEmptyBoxStorage> _waitingBoxTakeStorages =
            new HashSet<InteractableEmptyBoxStorage>();

        private struct StoreBoxState
        {
            public int Count;
            public ushort StorageId;
            public ushort BoxId;
            public int BoxType;
            public bool BoxBig;
            public bool Atomic;
            public InteractablePackagingBox_Item Box;
        }

        // StoreBox destroys the local mirror before the host accepts the operation. Suppress
        // the separate BoxRemoved message for the atomic path; the host consumes the same stable
        // id only after validating the storage slot.
        private static InteractablePackagingBox_Item _suppressedStorageDestroy;
        private static int _suppressedStorageDestroyFrame = -1;
        /// <summary>Client: cleanser reconcile-catch throttle, keyed PER CONTAINER INDEX. A single
        /// shared timestamp made the machines compete for one 10s window - the first cleanser to
        /// fault printed, and every other cleanser faulting in that window was silenced, so a shop
        /// with several of them reported one machine's symptom and hid the rest. That is the wrong
        /// thing to economise on: the point of the throttle is to stop ONE machine repeating
        /// itself, not to cap how many distinct machines can be heard from. Small by construction -
        /// one entry per cleanser that has actually faulted, never per apply.</summary>
        private readonly Dictionary<int, double> _lastCleanserWarn = new Dictionary<int, double>();

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
                        if (stored[i] != null)
                            h = h * 31 + (int)stored[i].GetItemType();
                h = h * 31 + (p.GetIsProcessing() ? 1 : 0);
                // quantized so a processing machine re-broadcasts ~1 Hz, an idle one never
                h = h * 31 + (int)((FiPoOpenTimer?.GetValue(p) as float? ?? 0f) * 2f);
                h = h * 31 + p.GetPackOpenedCount();
                h = h * 31 + HashCards(p.GetCompactCardDataAmountList());
                h = h * 31 + (_packClaimOwner.ContainsKey((KindPackOpener << 8) | IndexOf(KindPackOpener, p)) ? 1 : 0);
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
                        if (stored[i] != null)
                            h = h * 31 + (int)(stored[i].GetContentFill() * 100f);
                return h;
            };
        }

        public override string Name => "containers";

        protected override void OnHostTick(in SyncFrame frame)
        {
            HostTick(frame.Dt, frame.InGame);
        }

        protected override void OnClientTick(in SyncFrame frame)
        {
            ClientTick(frame.Dt, frame.InGame && !frame.PreloadHold);
        }

        /// <summary>Disable static Harmony hooks before session state is torn down.</summary>
        public static void ClearLive()
        {
            Instance = null;
            ApplyingRemote = false;
            _suppressedStorageDestroy = null;
            _suppressedStorageDestroyFrame = -1;
        }

        public static void ActivateLive(ContainerSync instance)
        {
            Instance = instance;
        }

        public override void Reset()
        {
            _sm = null;
            _timer = -5.3f; // staggered phase vs the other snapshot engines
            _heal = 0f;
            _lastHash.Clear();
            _dirty.Clear();
            _touched.Clear();
            _packMirrors.Clear();
            _packClaimOwner.Clear();
            _packClaimToken.Clear();
            _nextPackClaimToken = 1;
            _pendingBoxTakes.Clear();
            _pendingBoxTakeSlots.Clear();
            _waitingBoxTakeStorages.Clear();
            _suppressedStorageDestroy = null;
            _suppressedStorageDestroyFrame = -1;
            // container indices are re-derived per world, so a kept timestamp would throttle a
            // DIFFERENT machine in the next session
            _lastCleanserWarn.Clear();
        }

        public override void ForceResend()
        {
            // forgetting every hash makes the next HostTick rebroadcast the world -
            // the on-join snapshot for containers
            _lastHash.Clear();
            _heal = 0f;
        }

        /// <summary>Release a disconnected client's pack-opener claims so a failed
        /// collection cannot lock the machine until the whole session resets.</summary>
        public void HostReleaseConn(int connId)
        {
            var released = new List<int>();
            foreach (var pair in _packClaimOwner)
                if (pair.Value == connId)
                    released.Add(pair.Key);
            for (int i = 0; i < released.Count; i++)
            {
                int key = released[i];
                _packClaimOwner.Remove(key);
                _packClaimToken.Remove(key);
                _lastHash.Remove(key);
            }
            if (released.Count > 0)
            {
                CoopPlugin.Log.LogInfo($"ContainerSync: released {released.Count} pack claim(s) from disconnected client {connId}");
                _heal = 999f;
            }
        }

        private ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        private int IndexOf(int kind, object obj)
        {
            var sm = Sm();
            if (sm == null)
                return -1;
            var list = PopulationSync.GetList(sm, kind);
            if (list == null)
                return -1;
            int idx = list.IndexOf(obj);
            return idx < 250 ? idx : -1; // wire index is a byte (matches PopulationSync's cap)
        }

        private T Get<T>(int kind, int idx) where T : class
        {
            var sm = Sm();
            if (sm == null)
                return null;
            var list = PopulationSync.GetList(sm, kind);
            if (list == null || idx < 0 || idx >= list.Count)
                return null;
            return list[idx] as T;
        }

        /// <summary>Advance client-only presentation state and retry take requests clicked
        /// while PopulationSync was rebuilding the placed-object identity table. The pack
        /// opener's real stored-item list remains empty on clients, so its vanilla Update()
        /// cannot drive the display clock; the mirror needs to do that explicitly between
        /// authoritative host snapshots.</summary>
        public void ClientTick(float dt, bool inGame)
        {
            if (CoopCore.Role != CoopRole.Client)
                return;

            if (inGame && dt > 0f)
            {
                foreach (var pair in _packMirrors)
                {
                    var mirror = pair.Value;
                    if (mirror == null || !mirror.Processing || mirror.StoredCount <= 0)
                        continue;
                    var opener = Get<InteractableAutoPackOpener>(KindPackOpener, pair.Key);
                    if (opener == null)
                        continue;

                    // This is presentation-only. Never write m_PackOpenTimer or refill
                    // m_StoredItemList here: doing so would allow the client's vanilla
                    // Update() to roll packs independently of the host. The next host
                    // snapshot re-anchors the mirror to the authoritative timer/count.
                    mirror.Timer = Mathf.Min(
                        mirror.Timer + dt,
                        opener.m_PackOpenTime * mirror.StoredCount);
                    UpdatePackMirrorDisplay(opener, mirror);
                }
            }

            if (_waitingBoxTakeStorages.Count > 0)
            {
                var retry = new List<InteractableEmptyBoxStorage>(_waitingBoxTakeStorages);
                for (int i = 0; i < retry.Count; i++)
                {
                    var storage = retry[i];
                    if (storage == null)
                    {
                        _waitingBoxTakeStorages.Remove(storage);
                        continue;
                    }
                    if (!TryQueueBoxTake(storage))
                        continue;
                    _waitingBoxTakeStorages.Remove(storage);
                }
            }
        }

        private InteractableEmptyBoxStorage GetBoxStorageById(ushort id)
        {
            var sm = Sm();
            if (sm == null || id == PlacedObjectIdentity.Invalid)
                return null;
            var list = PopulationSync.GetList(sm, KindBoxStorage);
            if (list == null)
                return null;
            for (int i = 0; i < list.Count; i++)
            {
                var storage = list[i] as InteractableEmptyBoxStorage;
                if (storage != null && PlacedObjectIdentity.TryGet(storage, out ushort current)
                    && current == id)
                    return storage;
            }
            return null;
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
            if (!inGame)
                return;
            _timer += dt;
            if (_timer < TickInterval)
                return;
            _timer -= TickInterval;
            if (_timer > TickInterval)
                _timer = TickInterval; // clamp debt after a hitch
            try
            {
                var sm = Sm();
                if (sm == null)
                    return;
                bool sawError = false;
                _heal += TickInterval;
                if (_heal >= HealInterval)
                {
                    // periodic full rebroadcast repairs any client that missed an echo
                    _heal = 0f;
                    _lastHash.Clear();
                }
                _dirty.Clear();
                CollectKind(sm, KindCardStorage, _hashCardStorage, ref sawError);
                CollectKind(sm, KindDonation, _hashDonation, ref sawError);
                CollectKind(sm, KindPackOpener, _hashPackOpener, ref sawError);
                CollectKind(sm, KindBoxStorage, _hashBoxStorage, ref sawError);
                CollectKind(sm, KindCleanser, _hashCleanser, ref sawError);
                if (_dirty.Count == 0)
                {
                    if (sawError)
                        ForceResend();
                    return;
                }
                var dirty = new List<int>(_dirty); // snapshot for the closure
                var records = new List<ContainerRecord>(dirty.Count);
                for (int i = 0; i < dirty.Count; i++)
                {
                    int key = dirty[i];
                    try
                    {
                        records.Add(BuildRecord(key >> 8, key & 0xFF));
                    }
                    catch (Exception e)
                    {
                        sawError = true;
                        _lastHash.Remove(key); // retry this record on the next tick
                        CoopPlugin.Log.LogWarning($"ContainerSync snapshot record {key >> 8}:{key & 0xFF}: {e.Message}");
                    }
                }
                if (sawError)
                {
                    ForceResend();
                    return; // never label the remaining records as a complete snapshot
                }
                BroadcastState?.Invoke(new ContainerStateMessage { Records = records });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ContainerSync host: " + e.Message); }
        }

        private void CollectKind(ShelfManager sm, int kind, Func<object, int> hashFn, ref bool sawError)
        {
            var list = PopulationSync.GetList(sm, kind);
            if (list == null)
                return;
            for (int i = 0; i < list.Count && i < 250; i++)
            {
                if (list[i] == null)
                    continue;
                int h;
                try
                {
                    h = hashFn(list[i]);
                }
                catch (Exception e)
                {
                    sawError = true;
                    CoopPlugin.Log.LogWarning($"ContainerSync snapshot record {kind}:{i}: {e.Message}");
                    _lastHash.Remove((kind << 8) | i);
                    continue;
                }
                int key = (kind << 8) | i;
                if (_lastHash.TryGetValue(key, out int prev) && prev == h)
                    continue;
                _lastHash[key] = h;
                _dirty.Add(key);
            }
        }

        private ContainerRecord BuildRecord(int kind, int idx)
        {
            var rec = new ContainerRecord { Kind = (byte)kind, Index = (byte)idx };
            switch (kind)
            {
                case KindCardStorage:
                    {
                        var s = Get<InteractableCardStorageShelf>(kind, idx);
                        rec.CanWorkerTake = s == null || s.CanWorkerTake();
                        rec.Cards = s?.GetCompactCardDataAmountList() ?? new List<CompactCardDataAmount>();
                        break;
                    }
                case KindDonation:
                    {
                        var b = Get<InteractableBulkDonationBox>(kind, idx);
                        rec.Cards = b?.GetCompactCardDataAmountList() ?? new List<CompactCardDataAmount>();
                        break;
                    }
                case KindPackOpener:
                    {
                        var p = Get<InteractableAutoPackOpener>(kind, idx);
                        var stored = p?.GetStoredItemList();
                        int n = Mathf.Min(stored?.Count ?? 0, 250);
                        rec.StoredTypes = new List<EItemType>(n);
                        for (int i = 0; i < n; i++)
                            // ONE id convention for the whole container family: WriteItemType here,
                            // (int)ReadItemType on the far side, like every other EItemType on the
                            // wire. An empty slot goes out as EItemType.None rather than the old
                            // literal 0 - 0 is a REAL item type, so a null slot used to arrive
                            // indistinguishable from that item. INERT EITHER WAY: PackMirror.StoredTypes
                            // is written and never read by anything, so nothing observable changes;
                            // this exists so the field cannot become a bug the day something reads it.
                            rec.StoredTypes.Add(stored[i] != null ? stored[i].GetItemType() : EItemType.None);
                        rec.Processing = p != null && p.GetIsProcessing();
                        rec.Timer = p != null ? (FiPoOpenTimer?.GetValue(p) as float? ?? 0f) : 0f;
                        rec.OpenedCount = p != null ? p.GetPackOpenedCount() : 0;
                        rec.Cards = p?.GetCompactCardDataAmountList() ?? new List<CompactCardDataAmount>();
                        // Vanilla leaves m_CurrentState at 0 when a worker (or a full
                        // hopper) starts the machine through AddItem. The UI is still
                        // processing in that case, so advertise the effective state
                        // rather than the stale implementation detail.
                        rec.CurrentState = p != null && p.GetIsProcessing()
                            ? ((stored?.Count ?? 0) > 0 ? 1 : 2)
                            : 0;
                        rec.CollectClaimed = _packClaimOwner.ContainsKey((kind << 8) | idx);
                        break;
                    }
                case KindBoxStorage:
                    {
                        var s = Get<InteractableEmptyBoxStorage>(kind, idx);
                        rec.StorageId = s != null ? PlacedObjectIdentity.AssignHost(s) : PlacedObjectIdentity.Invalid;
                        rec.Count = s != null ? s.GetBoxStoredCount() : 0;
                        break;
                    }
                case KindCleanser:
                    {
                        var c = Get<InteractableAutoCleanser>(kind, idx);
                        byte flags = 0;
                        if (c != null && c.IsTurnedOn())
                            flags |= 1;
                        if (c == null || c.IsNeedRefill())
                            flags |= 2;
                        rec.Flags = flags;
                        var stored = c?.GetStoredItemList();
                        int n = Mathf.Min(stored?.Count ?? 0, 32);
                        rec.Fills = new List<float>(n);
                        for (int i = 0; i < n; i++)
                            rec.Fills.Add(stored[i] != null ? stored[i].GetContentFill() : 0f);
                        break;
                    }
            }
            return rec;
        }

        // ---------------- host: apply client ops ----------------

        public void HostApplyOp(ContainerOpMessage message, int connId)
        {
            byte op = message.Op;
            try
            {
                switch (op)
                {
                    case OpContentSet:
                        {
                            int kind = message.Kind;
                            int idx = message.Index;
                            bool canTake = message.CanWorkerTake;
                            var cards = message.Cards;
                            // never drop this even if the host has the same UI open: the
                            // joiner's binder already paid these cards through the CardDelta
                            // mirror, so losing the list here would lose the cards for real
                            ApplyContent(kind, idx, cards, kind == KindCardStorage, canTake);
                            break;
                        }
                    case OpWorkerTakeFlag:
                        {
                            int idx = message.Index;
                            bool canTake = message.CanWorkerTake;
                            var s = Get<InteractableCardStorageShelf>(KindCardStorage, idx);
                            if (s == null)
                                break;
                            ApplyingRemote = true;
                            try
                            {
                                s.SetCanWorkerTake(canTake);
                                s.OnCardStorageShelfSettingDone();
                            }
                            finally { ApplyingRemote = false; }
                            break;
                        }
                    case OpPackInsert:
                        {
                            int idx = message.Index;
                            var itemType = message.ItemType; // guest id -> ours; see PackOpenerAddItemPrefix
                            var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
                            if (p == null)
                                break;
                            // a pack from a content pack THIS PC does not have arrives as
                            // EItemType.None. Note WHY this has to be an explicit value test:
                            // GetItemMeshData(None) does not fail, it returns a BLANK but NON-NULL
                            // ItemMeshData, so SpawnItem would happily build a meshless prop the
                            // opener then holds forever. Skip explicitly and say so once.
                            if (itemType == EItemType.None)
                            {
                                CoopPlugin.Log.LogWarning(
                                    "ContainerSync pack insert: item type has no counterpart here (one-sided content pack) - skipped");
                                break;
                            }
                            // apply unconditionally (like a worker refill would): dropping it
                            // would eat the pack the joiner's box already gave up
                            var item = SpawnItem(itemType, p.m_PosInside);
                            if (item == null)
                                break;
                            ApplyingRemote = true;
                            try
                            {
                                p.AddItem(item, addToFront: true, isPlayer: false);
                            }
                            finally { ApplyingRemote = false; }
                            break;
                        }
                    case OpPackTurnOn:
                        {
                            int idx = message.Index;
                            var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
                            // the state check pins vanilla OnMouseButtonUp to its turn-on
                            // branch; anything else means the click raced and is stale
                            if (p != null && !p.GetIsProcessing() && p.GetStoredItemList().Count > 0)
                                p.OnMouseButtonUp();
                            break;
                        }
                    case OpPackClaim:
                        {
                            HostApplyPackClaim(message.Index, connId);
                            break;
                        }
                    case OpPackCollect:
                        {
                            int idx = message.Index;
                            var revealed = message.Cards;
                            HostApplyPackCollect(idx, message.ClaimToken, revealed, connId);
                            break;
                        }
                    case OpBoxTake:
                        {
                            ushort storageId = message.StorageId;
                            var reqPos = message.Position;
                            CoopPlugin.Log.LogInfo($"ContainerSync: empty-box take request from client {connId}, storage id {storageId}");
                            HostApplyBoxTake(storageId, reqPos, connId);
                            break;
                        }
                    case OpBoxStoreAtomic:
                        {
                            ushort storageId = message.StorageId;
                            ushort boxId = message.BoxId;
                            int boxType = (int)message.ItemType;
                            bool isBig = message.IsBig;
                            var s = GetBoxStorageById(storageId);
                            if (s == null)
                                break;
                            int max = FiEbMax?.GetValue(s) as int? ?? 200;
                            if (s.GetBoxStoredCount() >= max)
                            {
                                CoopPlugin.Log.LogInfo($"ContainerSync: rejected atomic empty-box store id {boxId} (storage id {storageId} full)");
                                break;
                            }
                            var engine = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
                            if (engine == null
                                || !engine.HostConsumeForEmptyBoxStorage(boxId, boxType, isBig))
                            {
                                CoopPlugin.Log.LogWarning(
                                    $"ContainerSync: rejected atomic empty-box store id {boxId} (box no longer valid)");
                                break;
                            }
                            FiEbCount?.SetValue(s, s.GetBoxStoredCount() + 1);
                            MiEbEval?.Invoke(s, null);
                            CoopPlugin.Log.LogInfo($"ContainerSync: accepted atomic empty-box store id {boxId} into storage id {storageId}");
                            break;
                        }
                    case OpCleanserToggle:
                        {
                            int idx = message.Index;
                            bool on = message.TurnedOn;
                            var c = Get<InteractableAutoCleanser>(KindCleanser, idx);
                            if (c == null)
                                break;
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
                            int idx = message.Index;
                            float fill = message.Fill;
                            var c = Get<InteractableAutoCleanser>(KindCleanser, idx);
                            if (c == null || !c.HasEnoughSlot())
                                break;
                            var item = SpawnItem(EItemType.Deodorant, c.m_PosList[0], fill);
                            if (item == null)
                                break;
                            ApplyingRemote = true;
                            try
                            {
                                c.AddItem(item, addToFront: true);
                            }
                            finally { ApplyingRemote = false; }
                            break;
                        }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"ContainerSync op {op}: {e.Message}"); }
        }

        private void HostApplyPackClaim(int idx, int connId)
        {
            var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
            var output = p?.GetCompactCardDataAmountList();
            int key = (KindPackOpener << 8) | idx;
            if (p == null || !p.GetIsProcessing() || p.GetStoredItemList().Count > 0
                || output == null || output.Count == 0 || _packClaimOwner.ContainsKey(key))
            {
                SendToClient?.Invoke(connId, new ContainerPackClaimMessage { Index = (byte)idx });
                return;
            }
            int token = _nextPackClaimToken++;
            if (token == 0)
                token = _nextPackClaimToken++;
            _packClaimOwner[key] = connId;
            _packClaimToken[key] = token;
            SendToClient?.Invoke(connId, new ContainerPackClaimMessage
            {
                Index = (byte)idx,
                Accepted = true,
                ClaimToken = token,
                Cards = new List<CompactCardDataAmount>(output),
            });
            _lastHash.Remove(key);
        }

        private void HostApplyPackCollect(int idx, int token,
            List<CompactCardDataAmount> revealed, int connId)
        {
            var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
            if (p == null)
                return;
            var output = p.GetCompactCardDataAmountList();
            int key = (KindPackOpener << 8) | idx;
            if (!_packClaimOwner.TryGetValue(key, out int owner) || owner != connId
                || !_packClaimToken.TryGetValue(key, out int expected) || expected != token
                || !SameCards(output, revealed))
            {
                CoopPlugin.Log.LogWarning($"ContainerSync: rejected pack collect opener={idx} client={connId}");
                return;
            }
            // Even if there's nothing left to bank (a stale/duplicate collect, or the host
            // already collected), still force the machine idle below. The old early-return
            // left m_IsProcessing pinned TRUE, and the heal then rebroadcast processing=true
            // to the guest forever - so the guest could never add packs again ("no empty
            // slot") after the first collect, while the host was unaffected.
            if (output != null && output.Count > 0)
            {
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
            _packClaimOwner.Remove(key);
            _packClaimToken.Remove(key);
        }

        private void HostApplyBoxTake(ushort storageId, Vector3 reqPos, int connId)
        {
            var s = GetBoxStorageById(storageId);
            if (s == null || s.GetBoxStoredCount() <= 0)
            {
                CoopPlugin.Log.LogInfo($"ContainerSync: rejected empty-box take from client {connId}, storage id {storageId} empty or missing");
                SendBoxTakeResult(connId, storageId, 0, s != null ? s.GetBoxStoredCount() : 0);
                return;
            }
            // spawn the box officially (RestockManager registers it) so the box engine mirrors
            // it back to the joiner at the storage's own hand-off spot; vanilla TakeBox
            // is unusable here because it force-holds the box in the HOST's hands
            var box = RestockManager.SpawnPackageBoxItem(EItemType.None, 0, isBigBox: true);
            if (box == null)
            {
                CoopPlugin.Log.LogWarning($"ContainerSync: rejected empty-box take from client {connId}, spawn failed (storage id {storageId})");
                SendBoxTakeResult(connId, storageId, 0, s.GetBoxStoredCount());
                return;
            }
            var loc = s.m_EmptyBoxSpawnLoc;
            var targetPos = loc != null ? loc.position : reqPos;
            var targetYaw = loc != null ? loc.rotation.eulerAngles.y : box.transform.eulerAngles.y;
            // The box engine snapshots the real Rigidbody, not only the visual Transform. Using the
            // same physics-pose helper prevents the immediate forced snapshot from advertising
            // the random RestockManager spawn location instead of the storage hand-off point.
            BoxPlacement.ApplyPhysicsPose(box, targetPos, targetYaw);
            box.ForceSetOpenCloseInstant(isOpen: true);
            box.SetOpenCloseBox(isOpen: false, isPlayer: false);
            var boxEngine = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            ushort boxId = boxEngine != null ? boxEngine.EnsureHostId(box) : (ushort)0;
            if (boxId == 0)
            {
                CoopPlugin.Log.LogWarning($"ContainerSync: rejected empty-box take from client {connId}, BoxEngine id assignment failed (storage id {storageId})");
                BoxShared.ApplyingRemote = true;
                try
                {
                    box.OnDestroyed();
                }
                catch (System.Exception caught) { Swallow.Log(caught); }
                finally { BoxShared.ApplyingRemote = false; }
                SendBoxTakeResult(connId, storageId, 0, s.GetBoxStoredCount());
                return;
            }
            FiEbCount?.SetValue(s, s.GetBoxStoredCount() - 1);
            MiEbEval?.Invoke(s, null);
            SendBoxTakeResult(connId, storageId, boxId, s.GetBoxStoredCount());
            CoopPlugin.Log.LogInfo($"ContainerSync: accepted empty-box take for client {connId}, storage id {storageId}, box id {boxId}");
            // push the freshly spawned box to the guest promptly (else up to ~1.5s late)
            RequestBoxResync?.Invoke();
        }

        private void SendBoxTakeResult(int connId, ushort storageId, ushort boxId, int remaining)
        {
            if (SendToClient == null)
                return;
            SendToClient(connId, new ContainerBoxTakeMessage
            {
                StorageId = storageId,
                BoxId = boxId,
                Remaining = Mathf.Max(0, remaining),
            });
        }

        // ---------------- client: apply authoritative state ----------------

        public void ClientApplyState(ContainerStateMessage message)
        {
            var records = message.Records;
            bool sawError = false;
            for (int r = 0; r < records.Count; r++)
            {
                var rec = records[r];
                int kind = rec.Kind;
                int idx = rec.Index;
                try
                {
                    // always consume the record fully; the guards only skip the APPLY
                    switch (kind)
                    {
                        case KindCardStorage:
                            {
                                bool canTake = rec.CanWorkerTake;
                                var cards = rec.Cards;
                                var s = Get<InteractableCardStorageShelf>(kind, idx);
                                // a container the player is editing right now (or edited in
                                // the last 6s) is his; the host hears about it via the op
                                if (s == null || s.IsEditingBulkBox() || IsTouched(kind, idx))
                                    break;
                                ApplyContent(kind, idx, cards, true, canTake);
                                break;
                            }
                        case KindDonation:
                            {
                                var cards = rec.Cards;
                                var b = Get<InteractableBulkDonationBox>(kind, idx);
                                if (b == null || b.IsEditingBulkBox() || IsTouched(kind, idx))
                                    break;
                                ApplyContent(kind, idx, cards, false, false);
                                break;
                            }
                        case KindPackOpener:
                            {
                                int sc = rec.StoredTypes != null ? rec.StoredTypes.Count : 0;
                                var types = new List<int>(sc);
                                // mirror of the write above: host id -> ours. An empty slot (or a
                                // pack from a content pack this PC lacks) lands as EItemType.None.
                                // Nothing reads StoredTypes today, so this is inert - it is here so
                                // the two ends can never drift into disagreeing about the convention.
                                for (int i = 0; i < sc; i++)
                                    types.Add((int)rec.StoredTypes[i]);
                                bool proc = rec.Processing;
                                float timer = rec.Timer;
                                int opened = rec.OpenedCount;
                                var output = rec.Cards;
                                var p = Get<InteractableAutoPackOpener>(kind, idx);
                                if (p == null)
                                    break;
                                if (!_packMirrors.TryGetValue(idx, out var m))
                                    _packMirrors[idx] = m = new PackMirror();
                                m.StoredCount = sc;
                                m.StoredTypes = types;
                                m.Processing = proc;
                                m.Timer = timer;
                                m.OpenedCount = opened;
                                m.Output = output;
                                m.CurrentState = rec.CurrentState;
                                m.CollectClaimed = rec.CollectClaimed;
                                // the pack opener was the only container kind with NO touch guard:
                                // a stale/heal "processing=true" echo arriving right after the
                                // guest's own local collect re-pinned m_IsProcessing and blocked
                                // any further insert ("no empty slot"). Skip a stale echo for ~6s
                                // after a local collect/insert (the record is already consumed).
                                if (IsTouched(kind, idx))
                                    break;
                                ApplyPackMirrorToMachine(p, m);
                                break;
                            }
                        case KindBoxStorage:
                            {
                                ushort storageId = rec.StorageId;
                                int count = rec.Count;
                                var s = GetBoxStorageById(storageId);
                                // do NOT gate on IsTouched here: on a TAKE the guest suppresses
                                // its local count entirely (no local write to protect), so the
                                // touch-guard only stranded the authoritative count for ~15s and
                                // made further takes look like "can't pick up". The record is
                                // already consumed above, so stream position is safe.
                                if (s == null)
                                    break;
                                FiEbCount?.SetValue(s, count);
                                MiEbEval?.Invoke(s, null);
                                break;
                            }
                        case KindCleanser:
                            {
                                byte flags = rec.Flags;
                                var fills = rec.Fills;
                                var c = Get<InteractableAutoCleanser>(kind, idx);
                                if (c == null || IsTouched(kind, idx))
                                    break;
                                ApplyCleanserState(idx, c, (flags & 1) != 0, (flags & 2) != 0, fills);
                                break;
                            }
                        default:
                            sawError = true;
                            CoopPlugin.Log.LogWarning($"ContainerSync apply unknown kind {kind} at record {r}");
                            continue;
                    }
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning($"ContainerSync apply kind {kind}: {e.Message}");
                    sawError = true;
                    continue; // records are already materialized; later records remain safe
                }
            }
            if (sawError)
                ForceResend();
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
                    if (s == null)
                        return;
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
                    if (b == null)
                        return;
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
                        if (it != null)
                            try
                            {
                                ItemSpawnManager.DisableItem(it);
                            }
                            catch (System.Exception caught) { Swallow.Log(caught); }
                    }
                }
                FiPoIsProcessing?.SetValue(p, m.Processing); // drives the Collect tooltip
                FiPoOpenedCount?.SetValue(p, m.OpenedCount);
                // Keep the client object coherent with the state that vanilla's UI
                // actually represents. In particular, AddItem auto-starts a full
                // machine without setting m_CurrentState to 1.
                p.m_CurrentState = EffectivePackState(m);
                UpdatePackMirrorDisplay(p, m);
            }
            finally { ApplyingRemote = false; }
        }

        /// <summary>Paint the opener UI from the inert client mirror. This is deliberately
        /// separate from ApplyPackMirrorToMachine so ClientTick can animate the countdown
        /// without touching any game simulation fields.</summary>
        private static void UpdatePackMirrorDisplay(InteractableAutoPackOpener p, PackMirror m)
        {
            if (p == null || m == null || !(FiPoUI?.GetValue(p) is AutoCardOpenerUI ui))
                return;
            int state = EffectivePackState(m);
            if (state == 1)
            {
                ui.SetUIState(1);
                ui.UpdateProcessingFillBar(1f - (float)m.StoredCount / p.m_MaxPackCount);
                ui.UpdateProcessingTimeLeftText(
                    Mathf.Max(0f, p.m_PackOpenTime * m.StoredCount - m.Timer));
            }
            else if (state == 2)
            {
                ui.SetUIState(2);
            }
            else
            {
                ui.SetUIState(0);
                ui.UpdatePackCountText(m.StoredCount, p.m_MaxPackCount);
            }
        }

        /// <summary>Returns the state represented by the authoritative machine fields.
        /// Vanilla auto-starts a full opener from AddItem without updating
        /// m_CurrentState, so that field cannot be used as the source of truth.</summary>
        private static int EffectivePackState(PackMirror mirror)
        {
            if (mirror == null || !mirror.Processing)
                return 0;
            return mirror.StoredCount > 0 ? 1 : 2;
        }

        private void ApplyCleanserState(int idx, InteractableAutoCleanser c, bool on, bool needRefill,
            List<float> fills)
        {
            ApplyingRemote = true;
            try
            {
                // Reconcile the visible spray cans through the vanilla add/remove so the
                // slot layout stays coherent - but drive the loops off the REAL stored list,
                // never off GetItemCount()/GetLastItem(). Vanilla keeps m_ItemAmount as a
                // second, independent count and indexes the list with it
                // (GetLastItem = m_StoredItemList[m_ItemAmount - 1], decompiled :357, behind
                // a guard that only tests the list's length), so any drift between the two
                // throws IndexOutOfRange straight out of this handler. Reading the list is
                // bounds-safe by construction, and the forced write at the bottom converges
                // the counter instead of merely dodging it.
                var stored = c.GetStoredItemList();
                // guards bound the loops because a vanilla remove can shed additional cans
                int guard = 12;
                while ((stored?.Count ?? 0) > fills.Count && guard-- > 0)
                {
                    // same element GetLastItem WOULD return once the two counts agree
                    var last = stored[stored.Count - 1];
                    if (last == null)
                    {
                        stored.RemoveAt(stored.Count - 1);
                        continue;
                    }
                    c.RemoveItem(last);
                    try
                    {
                        ItemSpawnManager.DisableItem(last);
                    }
                    catch (System.Exception caught) { Swallow.Log(caught); }
                }
                guard = 12;
                // keep HasEnoughSlot(): it is the m_PosList bound AddItem itself indexes with
                while ((stored?.Count ?? 0) < fills.Count && guard-- > 0 && c.HasEnoughSlot())
                {
                    var item = SpawnItem(EItemType.Deodorant, c.m_PosList[0], 1f);
                    if (item == null)
                        break;
                    c.AddItem(item, addToFront: true);
                }
                if (stored != null)
                    for (int i = 0; i < stored.Count && i < fills.Count; i++)
                        if (stored[i] != null)
                            stored[i].SetContentFill(fills[i]);
                // Force the counter into agreement with what the machine physically holds.
                // This is what makes the reconcile idempotent, and it is the only thing that
                // repairs a guest ALREADY diverged mid-session (LoadData never re-runs, so
                // the join-time fix in CleanserAddItemPrefix only helps from the next join).
                FiClItemAmount?.SetValue(c, stored?.Count ?? 0);
                // flags last: AddItem/RemoveItem flip m_IsNeedRefill on their own
                FiClTurnedOn?.SetValue(c, on);
                FiClNeedRefill?.SetValue(c, needRefill);
            }
            catch (Exception e)
            {
                // never let a cleanser reconcile escape into the record loop's catch: that
                // one returns and strands the rest of the batch. Re-force the counter so the
                // next apply starts from a coherent machine even if this one bailed midway.
                try
                {
                    FiClItemAmount?.SetValue(c, c.GetStoredItemList()?.Count ?? 0);
                }
                catch (System.Exception caught) { Swallow.Log(caught); }
                double now = Time.realtimeSinceStartupAsDouble;
                double last;
                if (!_lastCleanserWarn.TryGetValue(idx, out last) || now - last > 10.0)
                {
                    _lastCleanserWarn[idx] = now;
                    CoopPlugin.Log.LogWarning($"ContainerSync cleanser {idx} reconcile: " + e.Message);
                }
            }
            finally { ApplyingRemote = false; }
        }

        // ---------------- client: forwarded actions (called from patches) ----------------

        private void ClientForwardContent(int kind, object container,
            List<CompactCardDataAmount> cards, bool canWorkerTake)
        {
            int idx = IndexOf(kind, container);
            if (idx < 0)
                return;
            Touch(kind, idx);
            SendOp?.Invoke(new ContainerOpMessage
            {
                Op = OpContentSet,
                Kind = (byte)kind,
                Index = (byte)idx,
                CanWorkerTake = canWorkerTake,
                Cards = cards,
            });
        }

        private void ClientPackOpenerClick(InteractableAutoPackOpener p)
        {
            int idx = IndexOf(KindPackOpener, p);
            if (idx < 0)
                return;
            var m = GetOrCreatePackMirror(idx);
            SoundManager.PlayAudio("SFX_ButtonLightTap", 0.6f, 0.5f);
            if (m == null || !m.Processing)
            {
                if (m != null && m.StoredCount > 0)
                {
                    SendOp?.Invoke(new ContainerOpMessage { Op = OpPackTurnOn, Index = (byte)idx });
                    // Give the acting client immediate UI feedback. The next host snapshot
                    // remains authoritative and corrects a raced/rejected turn-on.
                    m.Processing = true;
                    m.CurrentState = 1;
                    ApplyPackMirrorToMachine(p, m);
                }
                else
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.NoCardPackInMachine);
            }
            else if (m.Output.Count > 0 && m.StoredCount <= 0)
            {
                if (m.CollectClaimed)
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.WaitAllCardPacksToBeProcessed);
                    return;
                }
                SendOp?.Invoke(new ContainerOpMessage { Op = OpPackClaim, Index = (byte)idx });
            }
            else
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.WaitAllCardPacksToBeProcessed);
            }
        }

        private PackMirror GetOrCreatePackMirror(int idx)
        {
            if (!_packMirrors.TryGetValue(idx, out var mirror))
                _packMirrors[idx] = mirror = new PackMirror();
            return mirror;
        }

        public void ClientApplyPackClaim(ContainerPackClaimMessage message)
        {
            var p = Get<InteractableAutoPackOpener>(KindPackOpener, message.Index);
            if (!message.Accepted || p == null || message.Cards == null || message.Cards.Count == 0)
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.WaitAllCardPacksToBeProcessed);
                return;
            }
            var revealed = new List<CompactCardDataAmount>(message.Cards);
            try
            {
                int opened = 0;
                if (_packMirrors.TryGetValue(message.Index, out var mirror))
                    opened = mirror.OpenedCount;
                CPlayerData.m_GameReportDataCollect.cardPackOpened += opened;
                CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened += opened;
                AchievementManager.OnCardPackOpened(CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened);
                // This is deliberately the game's complete claim workflow: it adds the
                // cards, builds the pages/new-card/value state, opens the modal, and queues
                // the normal XP event for when the player closes it.
                CSingleton<InteractionPlayerController>.Instance
                    .m_ShowCardObtainedPage.ShowCardObtained(revealed);
                SendOp?.Invoke(new ContainerOpMessage
                {
                    Op = OpPackCollect,
                    Index = message.Index,
                    ClaimToken = message.ClaimToken,
                    Cards = revealed,
                });
                if (_packMirrors.TryGetValue(message.Index, out var m))
                {
                    m.Output.Clear();
                    m.Processing = false;
                    m.CurrentState = 0;
                    m.OpenedCount = 0;
                    m.StoredCount = 0;
                    m.CollectClaimed = false;
                    ApplyPackMirrorToMachine(p, m);
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("ContainerSync claim reveal failed: " + e.Message);
            }
        }

        /// <summary>Client: the host accepted a take and assigned the authoritative box
        /// id. The normal BoxState follows on the same ordered lane; if it already arrived,
        /// claim the existing mirror immediately.</summary>
        public void ClientApplyTakeAccepted(ContainerBoxTakeMessage message)
        {
            ushort storageId = message.StorageId;
            ushort id = message.BoxId;
            int remaining = message.Remaining;
            _pendingBoxTakeSlots.Remove(storageId);
            var storage = GetBoxStorageById(storageId);
            if (storage != null)
            {
                FiEbCount?.SetValue(storage, remaining);
                MiEbEval?.Invoke(storage, null);
            }
            if (id == 0)
            {
                CoopPlugin.Log.LogInfo($"ContainerSync: empty-box take rejected for storage id {storageId}");
                return;
            }
            CoopPlugin.Log.LogInfo($"ContainerSync: empty-box take accepted for storage id {storageId}, box id {id}");
            _pendingBoxTakes.Add(id);
            var engine = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (engine != null && engine.TryGetClientBox(id, out var box))
                TryAutoHoldTakenBox(box);
        }

        /// <summary>Called when a host snapshot creates a local box, and when a take result
        /// names a box that already exists. Only an exact acknowledged id can trigger this
        /// path; position/type guessing is deliberately not used, so another player's take
        /// cannot be stolen by this client.</summary>
        public void TryAutoHoldTakenBox(InteractablePackagingBox box)
        {
            var item = box as InteractablePackagingBox_Item;
            if (CoopCore.Role != CoopRole.Client || item == null)
                return;
            var engine = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (engine == null || !engine.TryGetClientId(box, out ushort id))
                return;
            if (!_pendingBoxTakes.Contains(id))
                return;
            try
            {
                if ((int)item.m_ItemCompartment.GetItemType() != (int)EItemType.None
                    || item.m_ItemCompartment.GetItemCount() != 0
                    || !item.m_IsBigBox)
                    return;
                if (HoldClientBox != null && HoldClientBox(item))
                    _pendingBoxTakes.Remove(id);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ContainerSync take hand-off: " + e.Message); }
        }

        /// <summary>Called by GamePatches before the box engine's normal local-destroy forwarding.
        /// The atomic storage path owns the host-side consume operation instead.</summary>
        public static bool ConsumeSuppressedStorageDestroy(InteractablePackagingBox_Item box)
        {
            if (box == null || !ReferenceEquals(_suppressedStorageDestroy, box))
                return false;
            if (Time.frameCount != _suppressedStorageDestroyFrame)
            {
                CoopPlugin.Log.LogWarning($"ContainerSync: refused stale storage destroy suppression for {box.name}; armed frame {_suppressedStorageDestroyFrame}, current frame {Time.frameCount}");
                _suppressedStorageDestroy = null;
                _suppressedStorageDestroyFrame = -1;
                return false;
            }
            _suppressedStorageDestroy = null;
            _suppressedStorageDestroyFrame = -1;
            return true;
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

            // empty box storage: TakeBox would spawn a client-local box that the engine's
            // reconciliation culls within seconds - the station 'eats' the box
            Try(h, typeof(InteractableEmptyBoxStorage), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(ContainerSync), nameof(StorageMouseButtonPrefix)));
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

            // say so loudly rather than silently no-op the null-conditional: without this
            // field ApplyCleanserState can no longer force the counter back onto the list,
            // and the join-time drift would return unnoticed on a renamed game build
            if (FiClItemAmount == null)
                CoopPlugin.Log.LogWarning(
                    "Field missing: InteractableAutoCleanser.m_ItemAmount - cleanser can count cannot self-heal");
        }

        public static void StorageContentPostfix(InteractableCardStorageShelf __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return;
            Instance?.ClientForwardContent(KindCardStorage, __instance,
                __instance.GetCompactCardDataAmountList(), __instance.CanWorkerTake());
        }

        public static void DonationContentPostfix(InteractableBulkDonationBox __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return;
            Instance?.ClientForwardContent(KindDonation, __instance,
                __instance.GetCompactCardDataAmountList(), false);
        }

        public static void WorkerTakePostfix(InteractableCardStorageShelf __instance, bool canWorkerTake)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return;
            var self = Instance;
            if (self == null)
                return;
            int idx = self.IndexOf(KindCardStorage, __instance);
            if (idx < 0)
                return;
            self.Touch(KindCardStorage, idx);
            self.SendOp?.Invoke(new ContainerOpMessage
            {
                Op = OpWorkerTakeFlag,
                Index = (byte)idx,
                CanWorkerTake = canWorkerTake,
            });
        }

        public static bool PackOpenerClickPrefix(InteractableAutoPackOpener __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            try
            {
                Instance?.ClientPackOpenerClick(__instance);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ContainerSync click: " + e.Message); }
            return false;
        }

        public static bool PackOpenerAddItemPrefix(InteractableAutoPackOpener __instance, Item item)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            // the guest's join world-load restores the host save via
            // InteractableAutoPackOpener.LoadData, which calls AddItem once per stored
            // pack (decompiled ~384). Those are NOT player inserts - forwarding each one
            // makes the host spawn a NEW pack it already has, duplicating every pack that
            // sat in an opener on every join/rejoin. Skip the op during the reload, same
            // as the symmetric destroy guard (card/furniture DestroyedPrefix).
            // Still retire the item (as below) so it doesn't float - the host echoes truth.
            if (CoopCore.ClientReloading)
            {
                try
                {
                    ItemSpawnManager.DisableItem(item);
                }
                catch (System.Exception caught) { Swallow.Log(caught); }
                return false;
            }
            var self = Instance;
            if (self == null)
                return true;
            int idx = self.IndexOf(KindPackOpener, __instance);
            if (idx >= 0)
            {
                var mirror = self.GetOrCreatePackMirror(idx);
                int itemType = 0;
                try
                {
                    itemType = (int)item.GetItemType();
                }
                catch (System.Exception caught) { Swallow.Log(caught); }
                self.SendOp?.Invoke(new ContainerOpMessage
                {
                    Op = OpPackInsert,
                    Index = (byte)idx,
                    // the host spawns a real pack prefab from this, so a modded id minted in
                    // a different order here would insert the WRONG product on the host
                    ItemType = (EItemType)itemType,
                });
                // Reflect the player's action immediately. Unlike a normal content edit,
                // this is intentionally not touch-guarded: the next authoritative host
                // snapshot must be able to correct a full/raced/rejected insert promptly.
                mirror.StoredCount++;
                if (!mirror.Processing && mirror.StoredCount >= __instance.m_MaxPackCount)
                {
                    mirror.Processing = true;
                    mirror.CurrentState = 1;
                    mirror.Timer = 0f;
                }
                self.ApplyPackMirrorToMachine(__instance, mirror);
            }
            // the caller strips the item out of its (synced) box either way; retire it
            // here so it doesn't float in the world - the host's insert echoes back
            try
            {
                ItemSpawnManager.DisableItem(item);
            }
            catch (System.Exception caught) { Swallow.Log(caught); }
            return false;
        }

        /// <summary>Shared client block for both machines' TakeItemToHand: pulling an
        /// item back OUT client-side would hand the joiner a phantom the host still
        /// counts as inside the machine.</summary>
        public static bool TakeItemBlockPrefix(ref Item __result)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            __result = null;
            return false;
        }

        public static bool TakeBoxPrefix(InteractableEmptyBoxStorage __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            var self = Instance;
            if (self == null || self.SendOp == null)
                return false;
            if (!self.TryQueueBoxTake(__instance))
            {
                self._waitingBoxTakeStorages.Add(__instance);
                CoopPlugin.Log.LogWarning("ContainerSync: deferred empty-box take; storage has no population identity yet");
            }
            return false;
        }

        /// <summary>Intercept the station's actual click entry point on clients. Vanilla
        /// OnMouseButtonUp calls TakeBox internally, but patching this outer method avoids
        /// losing the interaction if the game's compiler/runtime has inlined or otherwise
        /// bypassed the nested call's Harmony patch.</summary>
        public static bool StorageMouseButtonPrefix(InteractableEmptyBoxStorage __instance)
        {
            CoopPlugin.Log.LogInfo($"ContainerSync: empty-box storage click (role={CoopCore.Role}, remote={ApplyingRemote})");
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            TakeBoxPrefix(__instance);
            return false;
        }

        private bool TryQueueBoxTake(InteractableEmptyBoxStorage storage)
        {
            if (storage == null || SendOp == null)
                return false;
            if (!PlacedObjectIdentity.TryGet(storage, out ushort storageId))
                return false;
            if (!_pendingBoxTakeSlots.Add(storageId))
                return true;
            var loc = storage.m_EmptyBoxSpawnLoc;
            Vector3 pos = loc != null ? loc.position : storage.transform.position;
            SendOp.Invoke(new ContainerOpMessage
            {
                Op = OpBoxTake,
                StorageId = storageId,
                Position = pos,
            });
            CoopPlugin.Log.LogInfo($"ContainerSync: sent empty-box take request for storage id {storageId}, local count {storage.GetBoxStoredCount()}");
            return true;
        }

        private static bool StoreBoxPrefix(InteractableEmptyBoxStorage __instance,
            InteractablePackagingBox_Item packagingBox, out StoreBoxState __state)
        {
            __state = new StoreBoxState
            {
                Count = __instance.GetBoxStoredCount(),
                StorageId = 0,
                BoxId = 0,
                BoxType = (int)EItemType.None,
                Box = packagingBox,
            };

            if (CoopCore.Role != CoopRole.Client || ApplyingRemote || packagingBox == null)
                return true;
            var self = Instance;
            var engine = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (self == null || self.SendOp == null || engine == null)
                return false;
            if (!PlacedObjectIdentity.TryGet(__instance, out ushort storageId))
                return false;

            // Match vanilla's rejection checks before arming the destroy suppression. If the
            // local call is going to reject, it must remain an ordinary no-op and must not
            // affect the subsequent BoxRemoved path.
            try
            {
                int max = FiEbMax?.GetValue(__instance) as int? ?? 200;
                if (packagingBox.IsTogglingOpenClose()
                    || packagingBox.m_ItemCompartment.GetItemCount() > 0
                    || !packagingBox.m_IsBigBox
                    || __instance.GetBoxStoredCount() >= max)
                    return true;
            }
            catch (System.Exception e) { Swallow.Log(e); return true; }

            if (!engine.TryGetClientId(packagingBox, out ushort boxId))
            {
                CoopPlugin.Log.LogWarning($"ContainerSync: blocked empty-box store at storage id {storageId}; box has no BoxEngine id");
                return false;
            }
            __state.StorageId = storageId;
            __state.BoxId = boxId;
            __state.BoxType = (int)packagingBox.m_ItemCompartment.GetItemType();
            __state.BoxBig = packagingBox.m_IsBigBox;
            __state.Atomic = true;
            _suppressedStorageDestroy = packagingBox;
            _suppressedStorageDestroyFrame = Time.frameCount;
            return true;
        }

        private static void StoreBoxPostfix(InteractableEmptyBoxStorage __instance, StoreBoxState __state)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
            {
                if (__state.Atomic && ReferenceEquals(_suppressedStorageDestroy, __state.Box))
                {
                    _suppressedStorageDestroy = null;
                    _suppressedStorageDestroyFrame = -1;
                }
                return;
            }
            // vanilla StoreBox has four rejection exits; only a grown count means the
            // box was really banked. Atomic stores suppress the separate BoxRemoved message;
            // the host consumes the stable id only after validating this storage slot.
            if (__instance.GetBoxStoredCount() <= __state.Count)
            {
                if (__state.Atomic && ReferenceEquals(_suppressedStorageDestroy, __state.Box))
                {
                    _suppressedStorageDestroy = null;
                    _suppressedStorageDestroyFrame = -1;
                }
                return;
            }
            var self = Instance;
            if (self == null)
                return;
            if (__state.Atomic)
            {
                self.SendOp?.Invoke(new ContainerOpMessage
                {
                    Op = OpBoxStoreAtomic,
                    StorageId = __state.StorageId,
                    BoxId = __state.BoxId,
                    ItemType = (EItemType)__state.BoxType,
                    IsBig = __state.BoxBig,
                });
            }
        }

        public static void CleanserTogglePostfix(InteractableAutoCleanser __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return;
            var self = Instance;
            if (self == null)
                return;
            int idx = self.IndexOf(KindCleanser, __instance);
            if (idx < 0)
                return;
            bool on = __instance.IsTurnedOn(); // vanilla already flipped it locally
            self.Touch(KindCleanser, idx);
            self.SendOp?.Invoke(new ContainerOpMessage
            {
                Op = OpCleanserToggle,
                Index = (byte)idx,
                TurnedOn = on,
            });
        }

        public static bool CleanserAddItemPrefix(InteractableAutoCleanser __instance, Item item)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            // same join-LoadData dupe as PackOpenerAddItemPrefix: InteractableAutoCleanser
            // .LoadData calls AddItem once per saved spray can (decompiled ~396). Forwarding
            // each as OpCleanserRefill makes the host spawn extra deodorant cans on every
            // join/rejoin, so the reload must not send an op.
            //
            // It must NOT suppress vanilla AddItem to do that, though: LoadData ends with an
            // unconditional `m_ItemAmount = saveData.itemAmount` (decompiled :398) that runs
            // whether or not the AddItem calls landed. Swallowing them left the guest with
            // m_ItemAmount = N and m_StoredItemList empty, and vanilla GetLastItem
            // (decompiled :353-357) guards on the LIST's length but subscripts with the
            // COUNTER - so every later reconcile shrink threw IndexOutOfRange, permanently,
            // for the rest of the session. Returning true lets AddItem move both together,
            // which is exactly what :398 then agrees with. Nothing is forwarded: the op is
            // written further down, past this early-out. The cans are inert local props -
            // the cleanser hash is collected host-side only and ApplyCleanserState reconciles
            // the guest's copy against it.
            //
            // Do NOT copy this to PackOpenerAddItemPrefix. That machine's suppression is
            // load-bearing for a different reason (an empty client m_StoredItemList is what
            // stops its own Update() rolling packs the host never rolled - see class doc),
            // and its LoadData derives everything from the list with no counter hard-set, so
            // it has no equivalent bug to fix.
            if (CoopCore.ClientReloading)
                return true;
            var self = Instance;
            if (self == null)
                return true;
            int idx = self.IndexOf(KindCleanser, __instance);
            if (idx >= 0)
            {
                float fill = 1f;
                try
                {
                    fill = item.GetContentFill();
                }
                catch (System.Exception caught) { Swallow.Log(caught); }
                self.Touch(KindCleanser, idx);
                self.SendOp?.Invoke(new ContainerOpMessage
                {
                    Op = OpCleanserRefill,
                    Index = (byte)idx,
                    Fill = fill,
                });
            }
            try
            {
                ItemSpawnManager.DisableItem(item);
            }
            catch (System.Exception caught) { Swallow.Log(caught); }
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
                if (contentFill >= 0f)
                    item.SetContentFill(contentFill);
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
            if (list == null)
                return 0;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e != null && e.cardSaveIndex == id.cardSaveIndex
                    && e.expansionType == id.expansionType && e.isDestiny == id.isDestiny)
                    return e.amount;
            }
            return 0;
        }

        private static bool SameCards(List<CompactCardDataAmount> a, List<CompactCardDataAmount> b)
        {
            if (a == null || b == null || a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                var e = a[i];
                if (e == null || AmountFor(b, e) != e.amount)
                    return false;
            }
            for (int i = 0; i < b.Count; i++)
            {
                var e = b[i];
                if (e == null || AmountFor(a, e) != e.amount)
                    return false;
            }
            return true;
        }

        private static int HashCards(List<CompactCardDataAmount> list)
        {
            int h = 17;
            if (list == null)
                return h;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null)
                    continue;
                h = h * 31 + e.cardSaveIndex;
                h = h * 31 + (int)e.expansionType;
                h = h * 31 + (e.isDestiny ? 1 : 0);
                h = h * 31 + e.amount;
                h = h * 31 + e.gradedCardIndex;
            }
            return h;
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
    }
}
