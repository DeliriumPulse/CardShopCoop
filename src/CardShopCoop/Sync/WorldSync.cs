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
    /// Shelf-stock synchronization by explicit vanilla mutation events. Both sides load identical saves,
    /// so a compartment is identified by (shelfKind, shelfIndex, compartmentIndex) into
    /// ShelfManager's lists. Every 0.75s the world is snapshotted; whatever changed since
    /// the last snapshot is reported. On the host those diffs are authoritative broadcasts
    /// (they capture player actions, customers, workers - every mutation source, with no
    /// per-interaction patches). On the client, diffs against the last host-applied state
    /// are the local player's own actions and are sent to the host as requests.
    /// </summary>
    public class WorldSync : CoopModule
    {
        public struct Entry
        {
            public int Key;   // kind<<24 | stableObjectId<<8 | compIdx
            public int Type;  // EItemType
            public int Count;
            // A client's own take/restock is sent as a delta: the count it was based on and the
            // sequence the host echoes in a ShelfTransferResult. Snapshots/host deltas leave
            // BaseCount 0 and TransferSeq 0 (they are absolutes).
            public int BaseCount;
            public int TransferType; // EItemType actually moved, or -1
            public uint TransferSeq;
        }

        /// <summary>Client role only: when this machine last reported a change of its own for
        /// a compartment. Protects a fresh local edit from being rolled back by a host echo
        /// (or the 12s full-state heal) that was built BEFORE our request landed - mirrors
        /// CardShelfSync's guard.</summary>
        /// <summary>Memoized "can this machine actually build this item type" verdicts. The 12s
        /// full-state heal can carry every compartment in the shop, so the ItemData lookup must
        /// not be repeated per entry per heal.</summary>
        private readonly Dictionary<int, bool> _resolvable = new Dictionary<int, bool>();
        /// <summary>Compartments whose last rebuild could NOT hold everything that was asked
        /// for: same item type, fewer slots on this machine (EPL data packs are parity-exempt,
        /// so the same type id can carry a different itemDimension - and therefore a different
        /// m_MaxItemCount - on each PC). The read-back already stops us reporting the shortfall
        /// back, but the 12s heal's change gate is hashed over the WHOLE shop, so during open
        /// hours customers keep it moving and the same impossible entry arrives every beat -
        /// tearing that compartment down and rebuilding it forever. Remembering the request
        /// that clamped, plus what it clamped TO, lets ApplyRemote skip the identical rebuild
        /// while still reacting the moment either the request or the compartment changes.</summary>
        private struct ClampState
        {
            public int Type; public int Requested; public int Actual;
        }
        private readonly Dictionary<int, ClampState> _clamped = new Dictionary<int, ClampState>();
        /// <summary>Keys we've already named in the log: a clamp is silent by construction (the
        /// type IS resolvable), so say it once per compartment or a mismatched-pack shop is
        /// undiagnosable.</summary>
        private readonly HashSet<int> _clampWarned = new HashSet<int>();
        private readonly HashSet<string> _snapshotErrors = new HashSet<string>();
        private bool _applyingRemote;
        public bool ApplyingRemote => _applyingRemote;

        /// <summary>Fired with locally-originated changes (host: broadcast; client: request).</summary>
        public Action<List<Entry>> OnLocalChanges;
        /// <summary>Host: send the outcome of one client shelf transfer back to its sender so the
        /// requester can roll the unaccepted part out of its hand.</summary>
        public Action<ShelfTransferResultMessage, int> SendResult;
        public Action RequestResync;
        public Action<ShelfBoxPullMessage> SendBoxPull;
        private uint _boxPullSequence;
        private readonly ShelfBoxPull _boxPulls = new ShelfBoxPull();
        private bool _resyncRequested;
        private float _lastResyncRequestAt = -999f;
        private const float ResyncCooldownSeconds = 2f;

        private readonly PendingTransferLedger<int> _transfers = new PendingTransferLedger<int>();
        /// <summary>Local mutations held while an earlier transfer for the same key is
        /// outstanding. EscrowTake records whether the mutation was a hand take (must be
        /// reconciled out of the hand) or a container/restock edit (must not touch the hand).</summary>
        private struct QueuedMutation
        {
            public Entry Entry; public bool EscrowTake;
        }
        private readonly Dictionary<int, Queue<QueuedMutation>> _queued = new Dictionary<int, Queue<QueuedMutation>>();
        private struct DirtyTake
        {
            public ShelfCompartment Comp; public int Base; public int Type; public Item Item;
        }
        private readonly List<DirtyTake> _dirtyTakes = new List<DirtyTake>();
        private readonly HostTransferAcks _hostAcks = new HostTransferAcks();

        public WorldSync()
        {
        }

        public void RequestResyncCoalesced() => _resyncRequested = true;

        // NEVER CSingleton<ShelfManager>.Instance: if touched before the game scene
        // exists (e.g. deltas arriving during the client's loading screen) it silently
        // creates and caches an empty fake manager that then shadows the real one for
        // the whole session, breaking the game's own shelf loading.
        private ShelfManager _sm;

        private ShelfManager ResolveShelfManager()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        private static bool TryKey(int kind, InteractableObject obj, int comp, out int key)
            => PlacedObjectIdentity.TryMakeCompartmentKey(kind, obj, comp, out key);

        public override string Name => "world";

        public override void ForceResend() => ForceNextTick();

        public override void Reset()
        {
            _resolvable.Clear(); // a different host/save can mean a different content-pack set
            _clamped.Clear();    // ...and different shelves, so a remembered clamp means nothing
            _clampWarned.Clear();
            _snapshotErrors.Clear();
            _transfers.Clear();
            _queued.Clear();
            _dirtyTakes.Clear();
            _resyncRequested = false;
            _lastResyncRequestAt = -999f;
            _hostAcks.Clear();
            _boxPulls.Clear();
            _boxPullSequence = 0;
            _sm = null;
        }

        /// <summary>Live structure change (a shelf/object was removed or spawned): every
        /// index-keyed baseline is stale, so re-read it fresh. Unlike <see cref="Reset"/> this
        /// keeps outstanding transfers and escrow intact - releasing a pending take here would
        /// drop its obligation and allow a duplicate.</summary>
        public void InvalidateBaseline()
        {
            _resolvable.Clear();
            _clamped.Clear();
            _clampWarned.Clear();
            _snapshotErrors.Clear();
            _sm = null;
            _queued.Clear();
            _dirtyTakes.Clear();
        }

        /// <summary>Request a same-frame scan after a vanilla inventory/shelf mutation.
        /// The normal timer remains as a recovery heal.</summary>
        public void ForceNextTick()
        {
        }

        public void Tick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            if (_dirtyTakes.Count > 0)
            {
                var pending = _dirtyTakes.ToArray();
                _dirtyTakes.Clear();
                for (int i = 0; i < pending.Length; i++)
                    LocalCompartmentMutation(pending[i].Comp, pending[i].Base, pending[i].Type, -1, pending[i].Item);
            }
            if (_resyncRequested && Time.time - _lastResyncRequestAt >= ResyncCooldownSeconds)
            {
                _resyncRequested = false;
                _lastResyncRequestAt = Time.time;
                RequestResync?.Invoke();
            }
        }

        private bool TryGetKey(ShelfCompartment comp, out int key)
        {
            key = 0;
            var sm = ResolveShelfManager();
            if (sm == null || comp == null)
                return false;
            // A warehouse rack compartment back-references its owning rack and its index within it
            // (set in WarehouseShelf.Init), so resolve kind 1 directly instead of scanning
            // m_WarehouseShelfList on every unresolved lookup (box-internal compartments hit this
            // path constantly during restocking).
            var warehouse = comp.GetWarehouseShelf();
            if (warehouse != null)
                return TryKey(1, warehouse, comp.GetIndex(), out key);
            for (int i = 0; i < sm.m_ShelfList.Count; i++)
            {
                var s = sm.m_ShelfList[i];
                var cs = s == null ? null : s.GetItemCompartmentList();
                if (cs != null && cs.Contains(comp))
                    return TryKey(0, s, cs.IndexOf(comp), out key);
            }
            for (int i = 0; i < sm.m_CardItemCombiShelfList.Count; i++)
            {
                var s = sm.m_CardItemCombiShelfList[i];
                var cs = s == null ? null : s.GetItemCompartmentList();
                if (cs != null && cs.Contains(comp))
                    return TryKey(3, s, cs.IndexOf(comp), out key);
            }
            for (int i = 0; i < sm.m_TournamentPrizeShelfList.Count; i++)
            {
                var s = sm.m_TournamentPrizeShelfList[i];
                var cs = s == null ? null : s.GetItemCompartmentList();
                if (cs != null && cs.Contains(comp))
                    return TryKey(14, s, cs.IndexOf(comp), out key);
            }
            return false;
        }

        /// <summary>Client or host: an explicit right-click label removal. Sent as a TYPE-ONLY
        /// entry (BaseCount -1) on BOTH roles, so the receiver's ApplyRemote never runs the
        /// loose-item clear-and-rebuild path - a compartment's label is independent of its
        /// contents. Used only for warehouse racks (see GamePatches.RemoveLabelPostfix), whose
        /// box contents are owned by ItemBoxFamily and whose type also changes during box
        /// add/remove; conflating a label edit with a box edit would corrupt the rack.</summary>
        internal void LocalLabelRemoval(ShelfCompartment comp)
        {
            if (_applyingRemote || comp == null || CoopCore.Role == CoopRole.None)
                return;
            if (!TryGetKey(comp, out int key))
            {
                // The rack's stable identity has not been bound yet (PopulationSync normally does
                // this within a roster tick). Fail loud rather than silently leaving the two
                // sides disagreeing about the label.
                CoopPlugin.Log.LogWarning(
                    "WorldSync: a warehouse rack label change could not be keyed (rack not bound yet) - it will not sync");
                return;
            }
            OnLocalChanges?.Invoke(new List<Entry>
            {
                new Entry
                {
                    Key = key,
                    Type = (int)comp.GetItemType(),
                    Count = comp.GetItemCount(),
                    BaseCount = -1,
                    TransferType = -1,
                }
            });
        }

        public bool TryGetShelfKey(ShelfCompartment comp, out int key)
            => TryGetKey(comp, out key);

        public void RequestBoxPull(InteractablePackagingBox_Item box, ShelfCompartment source, BoxEngine boxes)
        {
            if (CoopCore.Role != CoopRole.Client || box == null || source == null || boxes == null
                || !BoxVisuals.ReadOpen(box) || !source.m_CanPutItem || source.GetItemCount() <= 0
                || source.GetWarehouseShelf() != null || !TryGetKey(source, out int key)
                || !boxes.TryGetClientId(box, out ushort id) || SendBoxPull == null)
                return;
            // Let existing optimistic hand operations settle before asking to move this stock.
            if (_transfers.IsAddReserved(key) || _transfers.IsTakeReserved(key)
                || _dirtyTakes.Exists(t => t.Comp == source))
                return;
            SendBoxPull(new ShelfBoxPullMessage
            {
                ShelfKey = key,
                BoxId = id,
                ItemType = source.GetItemType(),
                Sequence = ++_boxPullSequence
            });
        }

        public void HostApplyBoxPull(ShelfBoxPullMessage message, int connId, BoxEngine boxes)
        {
            if (CoopCore.Role != CoopRole.Host || message == null || boxes == null)
                return;
            int kind = message.ShelfKey >> 24;
            if (kind != 0 && kind != 3 && kind != 14)
                return;
            var sm = ResolveShelfManager();
            if (sm == null || !boxes.TryGetHostBox(message.BoxId, out var rawBox)
                || !(rawBox is InteractablePackagingBox_Item box)
                || !boxes.HostBoxHeldByConnection(message.BoxId, connId))
                return;
            var source = Resolve(sm, message.ShelfKey);
            bool previous = _applyingRemote;
            _applyingRemote = true;
            try
            {
                _boxPulls.Apply(connId, message.Sequence, source, box, message.ItemType);
            }
            finally
            {
                _applyingRemote = previous;
                // Publish only after both inventories have been updated. Rejections also heal
                // stale mirrors, without refunding an item the guest was never given.
                boxes.MarkBoxDirty(box);
                boxes.ForceNextTick();
                if (source != null)
                    OnLocalChanges?.Invoke(new List<Entry>
                    {
                        new Entry { Key = message.ShelfKey, Type = (int)source.GetItemType(), Count = source.GetItemCount() }
                    });
            }
        }

        public void LocalCompartmentMutation(ShelfCompartment comp, int baseCount, int transferType, int delta)
            => LocalCompartmentMutation(comp, baseCount, transferType, delta, null);

        public void QueueTake(ShelfCompartment comp, int baseCount, Item item)
        {
            if (_applyingRemote || comp == null || item == null || CoopCore.Role == CoopRole.None
                || comp.GetWarehouseShelf() != null)
                return;
            _dirtyTakes.Add(new DirtyTake
            {
                Comp = comp,
                Base = baseCount,
                Type = (int)item.GetItemType(),
                Item = item
            });
        }

        private void LocalCompartmentMutation(ShelfCompartment comp, int baseCount, int transferType, int delta, Item takeItem)
        {
            if (_applyingRemote || comp == null || CoopCore.Role == CoopRole.None)
                return;
            // A warehouse rack compartment holds BOXES (owned by the box engine, ItemBoxFamily),
            // never loose items. Keep the loose-item transfer API from ever emitting a kind-1
            // entry even if a future/modded caller reaches it; only LocalLabelRemoval may key a
            // warehouse compartment.
            if (comp.GetWarehouseShelf() != null)
                return;
            if (!TryGetKey(comp, out int key))
                return;
            int type = (int)comp.GetItemType();
            var e = new Entry { Key = key, Type = type, Count = comp.GetItemCount(), BaseCount = baseCount, TransferType = transferType };
            if (CoopCore.Role == CoopRole.Client && delta != 0)
            {
                // Only a take that actually moved an item into the hand may escrow hand items.
                // A container-to-container removal (RemoveItem: box/shelf transfer) has no hand
                // item, so escrowing it would reserve - and on rejection destroy - an unrelated
                // hand item. GetShelfKey/QueueTake is the hand-take path (takeItem != null).
                bool escrowTake = delta < 0 && takeItem != null;
                if (_transfers.IsAddReserved(key) || _transfers.IsTakeReserved(key))
                {
                    if (!_queued.TryGetValue(key, out var q))
                        _queued[key] = q = new Queue<QueuedMutation>();
                    if (q.Count < PendingTransferLedger<int>.MaxOutstanding)
                        q.Enqueue(new QueuedMutation { Entry = e, EscrowTake = escrowTake });
                    else
                        CoopPlugin.Log.LogError($"WorldSync: queued-transfer cap reached key={key:X}; dropping the local edit and requesting resync");
                    return;
                }
                e.TransferSeq = _transfers.Begin(key, delta, transferType, out e.TransferType, escrowTake);
                if (e.TransferSeq == 0)
                {
                    FailClosedMutation(comp, key, delta, transferType, escrowTake);
                    return;
                }
            }
            else
            {
                e.BaseCount = (CoopCore.Role == CoopRole.Client && delta == 0) ? -1 : 0;
                e.TransferType = -1;
            }
            OnLocalChanges?.Invoke(new List<Entry> { e });
        }

        /// <summary>Fail-closed handling when a local transfer cannot be tracked: undo the local
        /// mutation so a later authoritative heal cannot duplicate it, then ask for truth. A
        /// refused restock is returned to the hand; a refused hand take is removed from the hand;
        /// a refused container move is left to the resync (its item is in another container, not
        /// the hand). Must never leave an unreported mutation in place.</summary>
        private void FailClosedMutation(ShelfCompartment comp, int key, int delta, int transferType, bool escrowTake)
        {
            _applyingRemote = true;
            try
            {
                if (delta < 0 && escrowTake)
                    HandEscrow.RollbackUnreservedTake(transferType, -delta);
                else if (delta > 0)
                {
                    int returned = comp != null ? HandEscrow.EscrowAdded(comp, transferType, delta) : 0;
                    if (returned != delta)
                        CoopPlugin.Log.LogError($"WorldSync: failed to return all {delta} untracked added items key={key:X}; returned {returned}");
                }
            }
            finally { _applyingRemote = false; }
            RequestResyncCoalesced();
        }

        private void FlushQueued(int key)
        {
            if (!_queued.TryGetValue(key, out var q) || q.Count == 0)
                return;
            var qe = q.Dequeue();
            if (q.Count == 0)
                _queued.Remove(key);
            var e = qe.Entry;
            int delta = e.Count - e.BaseCount;
            e.TransferSeq = _transfers.Begin(key, delta, e.TransferType, out e.TransferType, qe.EscrowTake);
            if (e.TransferSeq == 0)
            {
                // The remaining entries were derived against a baseline that this failed (and now
                // rolled-back) edit changed; drop them and let the authoritative resync re-derive.
                var comp = Resolve(ResolveShelfManager(), key);
                FailClosedMutation(comp, key, delta, e.TransferType, qe.EscrowTake);
                _queued.Remove(key);
                return;
            }
            OnLocalChanges?.Invoke(new List<Entry> { e });
        }

        /// <summary>Apply authoritative states (client) or requested states (host).</summary>
        public void ApplyRemote(List<Entry> entries)
        {
            var sm = ResolveShelfManager();
            if (sm == null)
                return;
            _applyingRemote = true;
            try
            {
                foreach (var e in entries)
                {
                    ShelfCompartment comp = null;
                    try
                    {
                        comp = Resolve(sm, e.Key);
                        if (comp == null)
                            continue;
                        // my own fresh edit is still round-tripping to the host; a stale
                        // echo (or the periodic full-state heal) must not stomp it
                        if (e.BaseCount == -1)
                        {
                            // Type-only labels are deliberately non-destructive. Never clear a
                            // live compartment merely because an empty local label was edited.
                            if (comp.GetItemCount() == 0)
                                comp.SetCompartmentItemType((EItemType)e.Type);
                            continue;
                        }
                        if (CoopCore.Role == CoopRole.Client
                            && (_transfers.IsAddReserved(e.Key) || _transfers.IsTakeReserved(e.Key)))
                            continue;
                        // PARTIAL-CLAMP SUPPRESSION: this exact request already ran and came up
                        // short, and the compartment still holds exactly what that rebuild left -
                        // so running it again can only produce the same result. Skipping saves the
                        // full teardown+respawn (N DisableItem + N GetItem + a price-tag refresh)
                        // every heal beat for the rest of the session. Both halves are checked
                        // against the LIVE compartment on purpose: the instant the host asks for
                        // something else, or anything (a local pull, a partial apply) moves the
                        // compartment off the clamped value, the memory stops matching and the
                        // normal apply resumes.
                        if (_clamped.TryGetValue(e.Key, out var cl)
                            && cl.Type == e.Type && cl.Requested == e.Count
                            && (int)comp.GetItemType() == e.Type && comp.GetItemCount() == cl.Actual)
                            continue;
                        // an item type this machine can't build is SKIPPED whole - never cleared.
                        // ApplyCompartment tears the compartment down before it discovers the type
                        // is unusable, so a missing content pack would silently empty the shelf
                        // here. (count == 0 needs no type at all - it's a pure Clear, and
                        // ApplyCompartment never touches SetCompartmentItemType on that path - so
                        // an "emptied" instruction is still honoured for an unknown type.)
                        // TYPE None WITH ITEMS ON IT is the id-translation miss, and it takes that
                        // same skip-don't-clear path: Msg.ReadItemType yields EItemType.None for a
                        // modded id whose name has no counterpart HERE, and Msg.WriteItemType does
                        // the same for one the RECEIVER lacks, so this one test covers a one-sided
                        // content pack in either direction. CanResolve can't decide it for us - it
                        // answers "yes" for None, which is right for a genuinely empty compartment
                        // but would send an unmappable type straight into the clear-and-rebuild.
                        if (e.Count == 0 || (e.Type != (int)EItemType.None && CanResolve(e.Type)))
                            ApplyCompartment(comp, e.Type, e.Count);
                    }
                    catch (Exception ex)
                    {
                        CoopPlugin.Log.LogWarning($"WorldSync apply {e.Key:X}: {ex.Message}");
                    }
                    if (comp == null)
                        continue; // guarded/unresolved: leave our baseline alone
                    try
                    {
                        // Baseline is ALWAYS what the compartment ACTUALLY holds now - never what
                        // was requested. A truncated apply (more items than the shelf has slots),
                        // a mid-apply throw, or a skipped unresolvable type would otherwise leave
                        // _last claiming the requested value; the next snapshot walk then reads the
                        // real (smaller) value as a LOCAL change and reports the shortfall back -
                        // a mirror that couldn't satisfy an apply wiping the side that could.
                        int actualType = (int)comp.GetItemType();
                        int actualCount = comp.GetItemCount();
                        // ...and remember an apply that could not be satisfied in full, so the next
                        // identical request skips the pointless rebuild (see the check above). Only
                        // a SHORTFALL of the requested type counts: anything else - an exact apply,
                        // a skipped unresolvable type, a compartment that ended up on some other
                        // type - clears the memory so no stale entry can suppress a real update.
                        if (actualType == e.Type && actualCount < e.Count)
                        {
                            _clamped[e.Key] = new ClampState { Type = e.Type, Requested = e.Count, Actual = actualCount };
                            if (_clampWarned.Add(e.Key))
                                CoopPlugin.Log.LogWarning($"WorldSync: compartment {e.Key:X} only holds {actualCount} of the {e.Count} item(s) the host has there (type {e.Type}) - your copy of that content pack gives the shelf fewer slots; it will stay short instead of rebuilding every heal");
                        }
                        else
                            _clamped.Remove(e.Key);
                    }
                    catch (Exception ex)
                    {
                        CoopPlugin.Log.LogWarning($"WorldSync read-back {e.Key:X}: {ex.Message}");
                    }
                }
            }
            finally { _applyingRemote = false; }
        }

        /// <summary>Host: apply a client's shelf change request. A real item move is a DELTA
        /// (TransferSeq != 0): merge it against the host's current count so concurrent takes and
        /// restocks compose instead of overwriting each other, then report exactly what was
        /// accepted so the requester can roll its hand back. Entries without a sequence stay
        /// absolute (label edits and other non-item settings) and are applied as before.
        /// Returns the host's authoritative state for each changed compartment, so other peers
        /// are told the merged truth rather than the requester's (possibly stale) absolute.</summary>
        public List<Entry> ApplyRequest(List<Entry> entries, int connId)
        {
            List<Entry> authoritative = null;
            foreach (var e in entries)
            {
                if (e.TransferSeq != 0)
                {
                    var actual = ApplyTransferRequest(e, connId);
                    if (actual.HasValue)
                        (authoritative ?? (authoritative = new List<Entry>())).Add(actual.Value);
                }
                else
                {
                    ApplyRemote(new List<Entry> { e });
                    (authoritative ?? (authoritative = new List<Entry>())).Add(e);
                }
            }
            return authoritative;
        }

        private Entry? ApplyTransferRequest(Entry e, int connId)
        {
            if (_hostAcks.TryGet(connId, e.TransferSeq, out int priorAccepted))
            {
                CoopPlugin.Log.LogInfo($"WorldSync: replaying deduplicated transfer conn={connId} seq={e.TransferSeq}");
                SendResult?.Invoke(new ShelfTransferResultMessage { Key = e.Key, TransferSeq = e.TransferSeq, AcceptedDelta = priorAccepted }, connId);
                return null;
            }
            int accepted = 0;
            Entry? actual = null;
            try
            {
                var sm = ResolveShelfManager();
                var comp = sm != null ? Resolve(sm, e.Key) : null;
                if (comp != null)
                {
                    int hostType = (int)comp.GetItemType();
                    int hostCount = comp.GetItemCount();
                    int requested = e.Count - e.BaseCount;
                    bool baseValid = e.BaseCount == hostCount;
                    if (!baseValid)
                    {
                        // Applying this delta would make a stale client baseline consume or
                        // create stock unrelated to its request.  Return host truth and reject;
                        // the client will reconcile its escrow and request a coalesced resync.
                        CoopPlugin.Log.LogWarning(
                            $"WorldSync: rejected transfer with stale base key={e.Key:X} base={e.BaseCount} host={hostCount}");
                        actual = new Entry { Key = e.Key, Type = hostType, Count = hostCount };
                        accepted = 0;
                    }
                    if (baseValid)
                    {
                        int applyType = hostType;
                        int applyCount = hostCount;
                        if (requested != 0)
                        {
                            // A one-sided content pack maps the moved type to None (-1) on the wire.
                            // Never merge that into the host's stock: it would build phantom None
                            // items or delete the wrong type. Refuse so the reporter keeps its item.
                            bool transferKnown = e.TransferType != (int)EItemType.None
                                && CanResolve(e.TransferType);
                            // An empty compartment is rebindable: vanilla's locked label
                            // (CGameManager.m_LockItemLabel) leaves a stale type after the last
                            // item leaves, and ShelfCompartment.CheckItemType rebinds a 0-count
                            // compartment to whatever arrives next. Only a non-empty compartment
                            // constrains the incoming type.
                            bool typeOk = transferKnown
                                && (hostCount <= 0 || hostType == (int)EItemType.None
                                    || hostType == e.TransferType);
                            if (typeOk && requested < 0)
                            {
                                accepted = -Mathf.Min(-requested, hostCount);
                                applyCount = hostCount + accepted;
                                applyType = applyCount <= 0 ? e.Type : hostType;
                            }
                            else if (typeOk)
                            {
                                // A shelf placed mid-session never ran CalculatePositionList, so its
                                // empty compartments report m_MaxItemCount == 0 (and an empty
                                // m_ItemPosList) until someone places the first item locally. Reading
                                // that as "no room" rejected every client restock of a brand-new
                                // shelf: capacity 0 accepted 0, so the host still stamped the item
                                // label (applyCount 0 with a type) but kept no items, and the client
                                // got its item bounced back to hand. Trust the add exactly like the
                                // box path (ItemBoxFamily.ApplyContent): ApplyCompartment below
                                // rebuilds through SetCompartmentItemType -> CalculatePositionList ->
                                // SpawnItem, which computes the real capacity and clamps to it, and
                                // the read-back then reports only what actually landed.
                                int capacity = comp.GetMaxItemCount();
                                if (capacity <= 0 || hostCount <= 0)
                                {
                                    // An empty compartment has no capacity for the incoming type
                                    // yet (a stale locked label, or a shelf placed mid-session
                                    // that never ran CalculatePositionList). Trust the add:
                                    // ApplyCompartment below binds the type, measures the real
                                    // capacity and clamps, and the read-back reports what landed.
                                    capacity = hostCount + requested;
                                    CoopPlugin.Log.LogDebug(
                                        $"WorldSync: compartment {e.Key:X} empty/capacity not built; trusting add host={hostCount} requested={requested}");
                                }
                                accepted = Mathf.Min(requested, Mathf.Max(0, capacity - hostCount));
                                applyCount = hostCount + accepted;
                                // A 0-count compartment rebinds to the incoming type (vanilla
                                // CheckItemType); a positive count with no label is an
                                // inconsistent state that heals to the incoming type too.
                                applyType = (hostCount <= 0 || hostType == (int)EItemType.None)
                                    && e.TransferType >= 0 ? e.TransferType : hostType;
                            }
                        }
                        if (applyCount < 0)
                            applyCount = 0;
                        bool resolvable = applyCount <= 0 || applyType == (int)EItemType.None || CanResolve(applyType);
                        if (resolvable)
                        {
                            _applyingRemote = true;
                            try
                            {
                                ApplyCompartment(comp, applyType, applyCount);
                            }
                            finally { _applyingRemote = false; }
                            int actualType = (int)comp.GetItemType();
                            int actualCount = comp.GetItemCount();
                            // Report what actually landed (the rebuild can clamp to this machine's
                            // real slot count), not what was requested.
                            accepted = actualCount - hostCount;
                            actual = new Entry { Key = e.Key, Type = actualType, Count = actualCount };
                        }
                        else
                        {
                            accepted = 0; // can't build this type here; refuse so the reporter keeps its item
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning($"WorldSync transfer {e.Key:X}: {ex.Message}");
                accepted = 0;
            }
            _hostAcks.Store(connId, e.TransferSeq, accepted);
            SendResult?.Invoke(new ShelfTransferResultMessage
            {
                Key = e.Key,
                TransferSeq = e.TransferSeq,
                AcceptedDelta = accepted,
            }, connId);
            return actual;
        }

        /// <summary>Client: the host resolved one of our shelf transfers. Anything it could not
        /// accept is rolled back out of our hand (take) or returned from the compartment to the
        /// hand (restock), so a clamped shelf can neither duplicate nor lose items.</summary>
        public void ApplyTransferResult(ShelfTransferResultMessage msg)
        {
            if (msg == null || msg.TransferSeq == 0)
                return;
            if (!_transfers.TryGet(msg.TransferSeq, out var pending))
                return;
            int rejected = pending.RequestedDelta - msg.AcceptedDelta;
            bool fault = false;
            bool escrowResolved = true;
            bool reconciliationAttempted = false;
            try
            {
                if (pending.RequestedDelta < 0)
                {
                    int accepted = Mathf.Max(0, -msg.AcceptedDelta);
                    reconciliationAttempted = true;
                    _applyingRemote = true;
                    try
                    {
                        escrowResolved = HandEscrow.ResolveTake(pending.EscrowToken, accepted);
                    }
                    finally { _applyingRemote = false; }
                    CoopPlugin.Log.LogInfo($"WorldSync transfer key={pending.Target:X} take token={pending.EscrowToken} accepted={accepted} rejected={Mathf.Max(0, -rejected)}");
                }
                else if (rejected > 0)
                {
                    var sm = ResolveShelfManager();
                    var comp = sm != null ? Resolve(sm, pending.Target) : null;
                    reconciliationAttempted = true;
                    // A local opposite move was observed while this transfer was in flight.  Do
                    // not remove an arbitrary current shelf item as this add's rollback; the
                    // current compartment is already at the post-move count.
                    int returned = 0;
                    _applyingRemote = true;
                    try
                    {
                        if (comp != null)
                            returned = HandEscrow.EscrowAdded(comp, pending.TransferType, rejected);
                    }
                    finally { _applyingRemote = false; }
                    if (comp != null)
                        CoopPlugin.Log.LogInfo($"WorldSync transfer key={pending.Target:X} add rejected={rejected} escrowed={returned}");
                }
            }
            catch (Exception ex)
            {
                fault = true;
                CoopPlugin.Log.LogError($"WorldSync: transfer result handler failed seq={msg.TransferSeq}: {ex}");
                try
                {
                    if (pending.RequestedDelta < 0 && !reconciliationAttempted)
                    {
                        try
                        {
                            _applyingRemote = true;
                            try
                            {
                                escrowResolved = HandEscrow.ResolveTake(
                                pending.EscrowToken, Mathf.Max(0, -msg.AcceptedDelta));
                            }
                            finally { _applyingRemote = false; }
                        }
                        catch (Exception reconcileEx)
                        {
                            CoopPlugin.Log.LogError($"WorldSync: take fault reconciliation failed seq={msg.TransferSeq}: {reconcileEx}");
                            HandEscrow.DeferRejectedTake(
                                pending.EscrowToken, Mathf.Max(0, -msg.AcceptedDelta));
                        }
                    }
                    else if (pending.RequestedDelta < 0)
                        HandEscrow.DeferRejectedTake(pending.EscrowToken, Mathf.Max(0, -msg.AcceptedDelta));
                    else if (pending.RequestedDelta > 0 && rejected > 0 && !reconciliationAttempted)
                    {
                        var sm = ResolveShelfManager();
                        var comp = sm != null ? Resolve(sm, pending.Target) : null;
                        reconciliationAttempted = true;
                        _applyingRemote = true;
                        try
                        {
                            HandEscrow.EscrowAdded(comp, pending.TransferType, Mathf.Max(0, rejected));
                        }
                        finally { _applyingRemote = false; }
                    }
                }
                catch (Exception reconcileEx)
                {
                    CoopPlugin.Log.LogError($"WorldSync: add fault reconciliation failed seq={msg.TransferSeq}: {reconcileEx}");
                }
            }
            if (fault)
            {
                _transfers.TryResolve(msg.TransferSeq, out _);
                RequestResyncCoalesced();
            }
            else if (!escrowResolved)
            {
                _transfers.TryResolve(msg.TransferSeq, out _);
                CoopPlugin.Log.LogError($"WorldSync: transfer result seq={msg.TransferSeq} could not remove rejected hand items; released container guard for deferred local cleanup");
                RequestResyncCoalesced();
            }
            else
                _transfers.TryResolve(msg.TransferSeq, out _);
            // Only a diverging (rejected/partial) resolution needs authoritative truth; a fully
            // accepted transfer must not trigger a full all-module resync per item.
            if (rejected != 0)
                RequestResyncCoalesced();
            FlushQueued(pending.Target);
        }

        public void HostReleaseConn(int connId)
        {
            _hostAcks.ReleaseConn(connId);
            _boxPulls.ReleaseConn(connId);
        }

        /// <summary>
        /// Can THIS machine actually build a compartment of this item type? A peer running a
        /// content pack we don't have sends type ids our ItemData table can't answer for, and
        /// SetCompartmentItemType would then hand CalculatePositionList a zero itemDimension -
        /// a divide that leaves the compartment with no usable slots, so the clear-and-rebuild
        /// clears and never rebuilds. Verdicts are memoized (the 12s full-state heal can carry
        /// every compartment in the shop) and the warning is emitted once per type.
        /// </summary>
        private bool CanResolve(int type)
        {
            // EItemType.None is the empty compartment - always applicable, and its ItemData is
            // a blank placeholder whose dimensions are legitimately zero.
            if (type == (int)EItemType.None)
                return true;
            if (_resolvable.TryGetValue(type, out bool known))
                return known;

            bool ok = false;
            try
            {
                var data = InventoryBase.GetItemData((EItemType)type); // throws on an id past our table
                if (data != null)
                {
                    var dim = data.itemDimension;
                    ok = dim.x > 0f && dim.y > 0f && dim.z > 0f;
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning($"WorldSync item type {type} lookup: {ex.Message}");
            }
            _resolvable[type] = ok;
            if (!ok)
                CoopPlugin.Log.LogWarning($"WorldSync: shelf item type {type} is from a content pack you don't have - that compartment will look empty for you");
            return ok;
        }

        private static ShelfCompartment Resolve(ShelfManager sm, int key)
        {
            int kind = key >> 24;
            ushort objectId = PlacedObjectIdentity.ObjectIdFromCompartmentKey(key);
            int compIdx = key & 0xFF;
            if (!PlacedObjectIdentity.TryResolve(sm, kind, objectId, out var obj))
                return null;
            // Warehouse racks store boxes in the same compartment type; GetWarehouseCompartment
            // is the public accessor and already bounds-checks the index.
            if (kind == 1)
                return (obj as WarehouseShelf)?.GetWarehouseCompartment(compIdx);
            List<ShelfCompartment> comps = null;
            if (kind == 0)
                comps = (obj as Shelf)?.GetItemCompartmentList();
            else if (kind == 3 || kind == 14)
                comps = (obj as CardItemCombiShelf)?.GetItemCompartmentList();
            return comps != null && compIdx < comps.Count ? comps[compIdx] : null;
        }

        private static readonly FieldInfo FiStoredItemList =
            ReflectionSurface.RequiredField(typeof(ShelfCompartment), "m_StoredItemList");

        /// <summary>
        /// Set a compartment to exactly (type, count). IMPORTANT: ShelfCompartment.SpawnItem
        /// is a LOADER, not an adder - it sets m_ItemAmount = amount and appends `amount`
        /// fresh items, assuming an empty compartment (that's how Shelf.LoadItemCompartment
        /// uses it at save load). Calling it incrementally corrupts the count (the
        /// "shelf wiped down to one item" bug). So every apply is an atomic
        /// clear-and-rebuild: synchronous within the frame, no flicker, and it self-heals
        /// compartments whose m_ItemAmount already disagrees with their real item list.
        /// </summary>
        private static void ApplyCompartment(ShelfCompartment comp, int type, int count)
        {
            int curType = (int)comp.GetItemType();
            int cur = comp.GetItemCount();
            // Truly identical: nothing to do. NOTE there is deliberately NO `|| count == 0`
            // here - an EMPTY compartment still carries a LABEL (m_ItemType, shown on the
            // tag even with 0 items), and clearing/setting that label is a real change the
            // mirror must express. Treating every count==0 as "already the same" was why a
            // coop player's right-click remove-label desynced: the far side was told
            // (None,0) and kept its label forever (and the player's own copy toggled).
            if (cur == count && curType == type)
                return;

            // same product, fewer items (a customer bought some): remove exactly the
            // difference - the full teardown/respawn for a 1-item sale was constant
            // visible churn on the client every 0.75s during open hours
            if (curType == type && count < cur && count > 0)
            {
                for (int k = cur - count; k > 0; k--)
                {
                    var item = comp.GetLastItem();
                    if (item == null)
                        break;
                    comp.RemoveItem(item);
                    ItemSpawnManager.DisableItem(item);
                }
                if (comp.GetItemCount() == count)
                    return;
                // count disagrees (corrupted m_ItemAmount): fall through and self-heal
            }

            Clear(comp);
            if (count > 0)
            {
                comp.SetCompartmentItemType((EItemType)type);
                comp.CalculatePositionList();
                comp.SpawnItem(count, spawnFromFront: true);
            }
            else if (curType != type)
            {
                // count 0 but the requested type differs: a LABEL change on an empty
                // compartment (the item name/image on the tag stays up regardless of how
                // many items are here). Clear/rebuild never touches m_ItemType, so set it
                // explicitly - None hides the tag, a real type re-shows it with the image.
                comp.SetCompartmentItemType((EItemType)type);
            }
        }

        private static void Clear(ShelfCompartment comp)
        {
            // Drain the REAL stored list (not m_ItemAmount, which may be corrupted).
            if (FiStoredItemList?.GetValue(comp) is List<Item> stored && stored.Count > 0)
            {
                foreach (var item in new List<Item>(stored))
                {
                    if (item == null)
                        continue;
                    comp.RemoveItem(item);
                    ItemSpawnManager.DisableItem(item);
                }
                stored.Clear();
            }
            else
            {
                for (int guard = 0; guard < 4096; guard++)
                {
                    var item = comp.GetLastItem();
                    if (item == null)
                        break;
                    comp.RemoveItem(item);
                    ItemSpawnManager.DisableItem(item);
                }
            }
            // RemoveItem decrements m_ItemAmount with no floor, so draining a compartment whose
            // field already undercounted its list could leave a negative that then sticks (the
            // count==0/same-type apply never calls SpawnItem to reset it) and gets broadcast.
            // Force the field back to a valid empty value; the caller sets the real count next.
            comp.PreSpawnItemUpdate(0);
        }

        // ---- wire format ----

        // ---- full-state heal (FIX D-latent) ----

        /// <summary>
        /// Host: serialize the COMPLETE item shelf-stock state - every compartment the delta
        /// walk visits (item shelves kind 0, card+item combi shelves kind 3, tournament prize
        /// shelves kind 14; warehouse racks kind 1 are excluded here exactly as in the delta
        /// walk, since their "count" is a stored-box tally, not loose items) - into the SAME
        /// Entry format the delta path uses (WriteEntries), so a full-state payload is
        /// byte-identical in shape to a ShelfDelta and rides the existing message type.
        ///
        /// WIRING CONTRACT (CoopCore owns this - not this file): the host should broadcast
        /// BuildFullState on a ~12s CHANGE-GATED cadence over MsgType.ShelfDelta (hash the
        /// entries, resend only on change, with a longer forced heal for a dropped packet),
        /// symmetric to the card-display full resync that reuses MsgType.CardShelfDelta on the
        /// same 12s beat (see CoopCore's _cardResyncTimer block). Item stock previously had NO
        /// periodic full-truth heal, so a client that adopted a slightly-wrong baseline at join
        /// (see Visit's silent adoption) stayed wrong until the host happened to mutate that
        /// compartment again; this closes that gap. The receiving client routes a ShelfDelta
        /// through ApplyRemote already, so no separate full-state flag byte is required - a
        /// full-state broadcast IS just a ShelfDelta carrying every compartment.
        /// </summary>
        public List<Entry> BuildFullState()
        {
            var all = new List<Entry>();
            try
            {
                var sm = ResolveShelfManager();
                if (sm != null)
                {
                    for (int i = 0; i < sm.m_ShelfList.Count; i++)
                    {
                        var shelf = sm.m_ShelfList[i];
                        CollectComps(all, shelf?.GetItemCompartmentList(), 0, shelf);
                    }
                    for (int i = 0; i < sm.m_CardItemCombiShelfList.Count; i++)
                    {
                        var combi = sm.m_CardItemCombiShelfList[i];
                        CollectComps(all, combi?.GetItemCompartmentList(), 3, combi);
                    }
                    for (int i = 0; i < sm.m_TournamentPrizeShelfList.Count; i++)
                    {
                        var prize = sm.m_TournamentPrizeShelfList[i];
                        CollectComps(all, prize?.GetItemCompartmentList(), 14, prize);
                    }
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("WorldSync full-state build: " + e.Message);
            }
            return all;
        }

        private static void CollectComps(List<Entry> into, List<ShelfCompartment> comps, int kind,
            InteractableObject shelf)
        {
            if (comps == null)
                return;
            for (int j = 0; j < comps.Count; j++)
            {
                var comp = comps[j];
                if (comp == null)
                    continue;
                if (TryKey(kind, shelf, j, out int key))
                    into.Add(new Entry
                    {
                        Key = key,
                        Type = (int)comp.GetItemType(),
                        Count = comp.GetItemCount(),
                    });
            }
        }

        /// <summary>
        /// Client: apply a full-state heal ABSOLUTELY. Each present entry drives its compartment
        /// to exactly (type, count) through the same ApplyRemote/ApplyCompartment clear-and-rebuild
        /// the delta path uses, and refreshes _last so the local baseline re-converges on host
        /// truth. The keyed Entry format can't encode "every other compartment is already fine",
        /// so - like CardShelfSync's full resync - this is a PARTIAL heal: compartments ABSENT from
        /// the payload keep whatever they have. In practice the host emits every live compartment,
        /// so an absent key only means an index the host doesn't have either. Reuses ReadEntries,
        /// so it decodes an identical wire shape to a ShelfDelta.
        /// </summary>
        public void ApplyFullState(List<Entry> entries)
        {
            ApplyRemote(entries);
        }
    }
}
