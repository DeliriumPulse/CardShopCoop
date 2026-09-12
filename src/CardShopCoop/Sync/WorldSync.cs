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
    /// Shelf-stock synchronization by snapshot diffing. Both sides load identical saves,
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

        private struct CompState
        {
            public int Type; public int Count;
        }

        private readonly Dictionary<int, CompState> _last = new Dictionary<int, CompState>();
        /// <summary>Client role only: when this machine last reported a change of its own for
        /// a compartment. Protects a fresh local edit from being rolled back by a host echo
        /// (or the 12s full-state heal) that was built BEFORE our request landed - mirrors
        /// CardShelfSync's guard.</summary>
        private readonly Dictionary<int, double> _locallyChanged = new Dictionary<int, double>();
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
        private readonly Dictionary<WarehouseShelf, List<ShelfCompartment>> _whComps
            = new Dictionary<WarehouseShelf, List<ShelfCompartment>>();
        private float _timer;
        private const float BaseScanInterval = 0.75f;
        private const float MaxQuietScanInterval = 3.0f;
        private float _scanInterval = BaseScanInterval;

        /// <summary>Fired with locally-originated changes (host: broadcast; client: request).</summary>
        public Action<List<Entry>> OnLocalChanges;
        /// <summary>Host: send the outcome of one client shelf transfer back to its sender so the
        /// requester can roll the unaccepted part out of its hand.</summary>
        public Action<ShelfTransferResultMessage, int> SendResult;
        public Action RequestResync;
        private bool _resyncRequested;
        private float _lastResyncRequestAt = -999f;
        private const float ResyncCooldownSeconds = 2f;

        private readonly PendingTransferLedger<int> _transfers = new PendingTransferLedger<int>();
        private readonly HostTransferAcks _hostAcks = new HostTransferAcks();

        public WorldSync()
        {
        }

        public void RequestResyncCoalesced() => _resyncRequested = true;

        private static readonly FieldInfo FiWarehouseComps =
            ReflectionSurface.RequiredField(typeof(WarehouseShelf), "m_ItemCompartmentList");

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

        // Time-sliced scan state: the whole-world walk is spread over frames. The budget is in
        // SHELVES; each shelf's (few) compartments ride with it.
        private readonly ListScanCursor _cursor = new ListScanCursor { Budget = 12 };
        private System.Collections.IList[] _groups;
        private bool _scanning;
        private List<Entry> _scanChanges;
        private bool _sawError;

        public override string Name => "world";

        public override void ForceResend() => ForceNextTick();

        public override void Reset()
        {
            _last.Clear();
            _locallyChanged.Clear();
            _resolvable.Clear(); // a different host/save can mean a different content-pack set
            _clamped.Clear();    // ...and different shelves, so a remembered clamp means nothing
            _clampWarned.Clear();
            _snapshotErrors.Clear();
            _whComps.Clear();
            _transfers.Clear();
            _resyncRequested = false;
            _lastResyncRequestAt = -999f;
            _hostAcks.Clear();
            _timer = 0.35f; // staggered phase: engines must not all walk on the same frame
            _scanInterval = BaseScanInterval;
            _sm = null;
            _scanning = false;
            _groups = null;
            _scanChanges = null;
            _cursor.Reset();
        }

        /// <summary>Live structure change (a shelf/object was removed or spawned): every
        /// index-keyed baseline is stale, so re-read it fresh. Unlike <see cref="Reset"/> this
        /// keeps outstanding transfers and escrow intact - releasing a pending take here would
        /// drop its obligation and allow a duplicate.</summary>
        public void InvalidateBaseline()
        {
            _last.Clear();
            _locallyChanged.Clear();
            _resolvable.Clear();
            _clamped.Clear();
            _clampWarned.Clear();
            _snapshotErrors.Clear();
            _whComps.Clear();
            _timer = 0.35f;
            _scanInterval = BaseScanInterval;
            _sm = null;
            _scanning = false;
            _groups = null;
            _scanChanges = null;
            _cursor.Reset();
        }

        /// <summary>Request a same-frame scan after a vanilla inventory/shelf mutation.
        /// The normal timer remains as a recovery heal.</summary>
        public void ForceNextTick()
        {
            _scanInterval = BaseScanInterval;
            _timer = _scanInterval;
        }

        public void Tick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            if (_resyncRequested && Time.time - _lastResyncRequestAt >= ResyncCooldownSeconds)
            {
                _resyncRequested = false;
                _lastResyncRequestAt = Time.time;
                RequestResync?.Invoke();
            }
            _timer += dt;
            if (!_scanning)
            {
                if (_timer < _scanInterval)
                    return;
                _timer -= _scanInterval; // keep the phase; reset-to-zero drifts back into alignment
                var sm = ResolveShelfManager();
                if (sm == null)
                    return;
                if (_groups == null || _groups.Length != 3)
                    _groups = new System.Collections.IList[3];
                _groups[0] = sm.m_ShelfList;
                _groups[1] = sm.m_CardItemCombiShelfList;
                _groups[2] = sm.m_TournamentPrizeShelfList;
                _cursor.Reset();
                _scanning = true;
                _scanChanges = null;
                _sawError = false;
            }
            try
            {
                _cursor.Scan(_groups, VisitShelf);
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

        /// <summary>Visit one shelf (kind 0, 3 or 14) and all of its item compartments.</summary>
        private void VisitShelf(object item, int group, int index)
        {
            var shelf = item as Shelf;
            if (shelf == null)
                return;
            int kind = group == 0 ? 0 : (group == 1 ? 3 : 14);
            // warehouse racks (kind 1) are deliberately NOT walked here: their compartment
            // "count" is a STORED-BOX tally (AddBox/RemoveBox), not loose items, and applying
            // it through the item path spawned phantom item meshes into the rack. The item box
            // family owns racks via stored entries.
            try
            {
                var comps = shelf.GetItemCompartmentList();
                for (int j = 0; j < comps.Count; j++)
                {
                    try
                    {
                        if (TryKey(kind, shelf, j, out int key))
                            Visit(key, comps[j]);
                    }
                    catch (Exception e) { _sawError = true; LogSnapshotError("shelf " + index + " compartment " + j, e); }
                }
            }
            catch (Exception e) { _sawError = true; LogSnapshotError("shelf " + index, e); }
        }

        private void LogSnapshotError(string item, Exception e)
        {
            if (_snapshotErrors.Add(item))
                CoopPlugin.Log.LogWarning("WorldSync snapshot item " + item + ": " + e.Message);
        }

        private void Visit(int key, ShelfCompartment comp)
        {
            if (comp == null)
                return;
            int type = (int)comp.GetItemType();
            int count = comp.GetItemCount();
            int prevType = type;
            int prevCount = count;
            bool hadBaseline = false;
            if (_last.TryGetValue(key, out var st))
            {
                if (st.Type == type && st.Count == count)
                    return; // unchanged since last snapshot
                prevType = st.Type;
                prevCount = st.Count;
                hadBaseline = true;
            }
            else if (CoopCore.Role == CoopRole.Client)
            {
                // FIRST SIGHTING on a joiner: adopt the host's live value SILENTLY instead of
                // reporting it as a change from an empty baseline. After Reset clears _last (a
                // join or scene load), the client's first walk sees every compartment as "new";
                // reporting those (often stale, or still-loading and therefore empty) reads back
                // to the host made the HOST apply them and wipe live stock for every peer - the
                // mid-day-join stock-wipe bug. A joiner must only report transitions it witnessed
                // against a KNOWN baseline. Mirrors CardShelfSync.Walk's client silent-adoption;
                // any slightly-wrong adopted value is repainted by the host's periodic full resync
                // (ApplyFullState below). Adoption is deliberately NOT subject to the 512 cap - it
                // emits nothing, it only records what we already see.
                _last[key] = new CompState { Type = type, Count = count };
                return;
            }
            if (_scanChanges == null)
                _scanChanges = new List<Entry>();
            if (_scanChanges.Count >= 512)
                return; // leave un-recorded; picked up next tick
            _last[key] = new CompState { Type = type, Count = count };
            var entry = new Entry { Key = key, Type = type, Count = count };
            // a guest's change is only a REQUEST: it has to round-trip to the host before it
            // comes back as truth. Stamp it so an in-flight host echo (or the 12s full-state
            // heal, built before our request landed) can't roll the placement back under us.
            if (CoopCore.Role == CoopRole.Client)
            {
                _locallyChanged[key] = Time.realtimeSinceStartupAsDouble;
                // A real item move is sent as a DELTA the host merges, so two concurrent takes
                // can't overwrite each other with stale absolutes. The host echoes TransferSeq
                // with what it accepted; we roll the rest back out of our hand.
                int delta = count - prevCount;
                if (hadBaseline && delta != 0)
                {
                    int localTransfer = delta < 0 ? prevType : type;
                    entry.BaseCount = prevCount;
                    entry.TransferSeq = _transfers.Begin(key, delta, localTransfer, out entry.TransferType);
                    if (entry.TransferSeq == 0)
                    {
                        int rolledBack = delta < 0
                            ? HandEscrow.RollbackUnreservedTake(localTransfer, -delta) : 0;
                        if (delta > 0)
                        {
                            var rollbackComp = Resolve(ResolveShelfManager(), key);
                            int returned = rollbackComp != null
                                ? HandEscrow.EscrowAdded(rollbackComp, localTransfer, delta) : 0;
                            if (returned != delta)
                                CoopPlugin.Log.LogError($"WorldSync: failed to return all {delta} untracked added items key={key:X}; returned {returned}");
                            if (rollbackComp != null)
                                _last[key] = new CompState
                                {
                                    Type = (int)rollbackComp.GetItemType(),
                                    Count = rollbackComp.GetItemCount(),
                                };
                        }
                        // Drop the local-edit guard, or the full-state answer to this resync is
                        // skipped by ApplyRemote for the very key we need restored.
                        _locallyChanged.Remove(key);
                        CoopPlugin.Log.LogError(
                            $"WorldSync: refused untracked shelf delta key={key:X}; rolled back {rolledBack} and requesting resync");
                        RequestResync?.Invoke();
                        return;
                    }
                }
            }
            _scanChanges.Add(entry);
        }

        /// <summary>Apply authoritative states (client) or requested states (host).</summary>
        public void ApplyRemote(List<Entry> entries)
        {
            var sm = ResolveShelfManager();
            if (sm == null)
                return;
            foreach (var e in entries)
            {
                ShelfCompartment comp = null;
                try
                {
                    // my own fresh edit is still round-tripping to the host; a stale
                    // echo (or the periodic full-state heal) must not stomp it
                    if (CoopCore.Role == CoopRole.Client && _locallyChanged.TryGetValue(e.Key, out double t)
                        && Time.realtimeSinceStartupAsDouble - t < 6.0)
                        continue;
                    if (CoopCore.Role == CoopRole.Client
                        && (_transfers.IsAddReserved(e.Key) || _transfers.IsTakeReserved(e.Key)))
                        continue;
                    comp = Resolve(sm, e.Key);
                    if (comp == null)
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
                    _last[e.Key] = new CompState { Type = actualType, Count = actualCount };
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
                    int applyType = hostType;
                    int applyCount = hostCount;
                    if (requested != 0)
                    {
                        // A one-sided content pack maps the moved type to None (-1) on the wire.
                        // Never merge that into the host's stock: it would build phantom None
                        // items or delete the wrong type. Refuse so the reporter keeps its item.
                        bool transferKnown = e.TransferType != (int)EItemType.None
                            && CanResolve(e.TransferType);
                        bool typeOk = transferKnown
                            && (hostType == (int)EItemType.None || hostType == e.TransferType);
                        if (typeOk && requested < 0)
                        {
                            accepted = -Mathf.Min(-requested, hostCount);
                            applyCount = hostCount + accepted;
                            applyType = applyCount <= 0 ? e.Type : hostType;
                        }
                        else if (typeOk)
                        {
                            int capacity = comp.GetMaxItemCount();
                            if (capacity <= 0)
                                capacity = hostCount + requested;
                            accepted = Mathf.Min(requested, Mathf.Max(0, capacity - hostCount));
                            applyCount = hostCount + accepted;
                            applyType = hostType == (int)EItemType.None && e.TransferType >= 0
                                ? e.TransferType : hostType;
                        }
                    }
                    if (applyCount < 0)
                        applyCount = 0;
                    bool resolvable = applyCount <= 0 || applyType == (int)EItemType.None || CanResolve(applyType);
                    if (resolvable)
                    {
                        ApplyCompartment(comp, applyType, applyCount);
                        int actualType = (int)comp.GetItemType();
                        int actualCount = comp.GetItemCount();
                        // Report what actually landed (the rebuild can clamp to this machine's
                        // real slot count), not what was requested.
                        accepted = actualCount - hostCount;
                        _last[e.Key] = new CompState { Type = actualType, Count = actualCount };
                        actual = new Entry { Key = e.Key, Type = actualType, Count = actualCount };
                    }
                    else
                    {
                        accepted = 0; // can't build this type here; refuse so the reporter keeps its item
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
                    escrowResolved = _transfers.ResolveTake(pending, msg.AcceptedDelta);
                    CoopPlugin.Log.LogInfo($"WorldSync transfer key={pending.Target:X} take token={pending.EscrowToken} accepted={accepted} rejected={Mathf.Max(0, -rejected)}");
                }
                else if (rejected > 0)
                {
                    var sm = ResolveShelfManager();
                    var comp = sm != null ? Resolve(sm, pending.Target) : null;
                    reconciliationAttempted = true;
                    int returned = comp != null ? HandEscrow.EscrowAdded(comp, pending.TransferType, rejected) : 0;
                    if (comp != null)
                        _last[pending.Target] = new CompState { Type = (int)comp.GetItemType(), Count = comp.GetItemCount() };
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
                            escrowResolved = HandEscrow.ResolveTake(
                                pending.EscrowToken, Mathf.Max(0, -msg.AcceptedDelta));
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
                        HandEscrow.EscrowAdded(comp, pending.TransferType, Mathf.Max(0, rejected));
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
            _locallyChanged.Remove(pending.Target);
            // Only a diverging (rejected/partial) resolution needs authoritative truth; a fully
            // accepted transfer must not trigger a full all-module resync per item.
            if (rejected != 0)
                RequestResyncCoalesced();
            else
                ForceNextTick();
        }

        public void HostReleaseConn(int connId) => _hostAcks.ReleaseConn(connId);

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
