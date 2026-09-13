using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The one box engine. It owns identity, the possession lease, the host snapshot,
    /// the client apply, and the local-possession reporting. It knows nothing about
    /// item/card/furniture content - that lives in the <see cref="IBoxFamily"/>.
    ///
    /// Protocol: a box is Free, Held, Placing, or Removed. A BoxUpdate is accepted iff
    /// the box is unowned or the sender already owns it. Held/Placing keep the lease;
    /// Free/Removed release it. The host owns Free pose/velocity and rebroadcasts it.
    ///
    /// Snapshots are partial by default: the host emits only the boxes whose state hash
    /// changed, plus an explicit Removed for retired ids. A complete snapshot is sent at
    /// session start and on explicit request (join/resync); the reliable partial stream
    /// carries every subsequent change, so there is no periodic full scan. Both scans are
    /// time-sliced (round-robin over a small budget per flush), with a fast path for the
    /// actively held/placing box so interaction stays responsive.
    /// </summary>
    public class BoxEngine : CoopModule
    {
        private const int MaxBoxes = 1000;
        private const float PartialPeriod = 0.10f;  // host: how often changed boxes flush
        private const float LeaseTimeout = 3.0f;
        private const float LeaseRenewPeriod = 1.0f;
        private const float ContentReportPeriod = 0.2f;
        private const int ClientScanBudget = 16;    // boxes checked per frame (round-robin)
        private const int HostScanBudget = 24;      // boxes checked per partial flush

        // ---- push-motion thresholds (shared with BoxPushProbe / BoxPlacement) ----
        internal const float PushHysteresis = 0.2f;      // last-contact grace before a push prunes
        internal const float MotionLeaseTimeout = 0.25f; // a push stream this stale has stopped
        internal const float MotionSettleSpeed = 0.05f;  // below this speed a box is at rest
        private const float MotionEpsilon = 0.01f;       // pose delta that counts as a change

        private const int HostConn = 0;
        private const int NoOwner = -1;

        private struct Lease
        {
            public int Owner;                 // HostConn / connId / NoOwner
            public int LastOwner;             // last connId the host accepted as owner; survives release
            public BoxPossession Possession;
            public float LastSeen;
            // Push-motion state: while MotionDriven the pose is owned by the transient motion
            // stream (from Driver), and the snapshot path must not also write it.
            public bool MotionDriven;
            public int Driver;                // connId (HostConn) of the player driving the push
            public float LastMotion;          // _leaseClock of the last accepted motion frame
            public float MotionBlockedUntil;  // _leaseClock until which transient frames absorb
            public Vector3 MotionPos;
            public float MotionYaw;
            public Vector3 MotionVel;
            public Vector3 MotionAngVel;
        }

        private readonly List<IBoxFamily> _families;
        private readonly BoxIdentityMap<InteractablePackagingBox> _hostIdentity
            = new BoxIdentityMap<InteractablePackagingBox>();
        private readonly Dictionary<ushort, Lease> _leases = new Dictionary<ushort, Lease>();

        // ---- host snapshot state ----
        private readonly Dictionary<ushort, int> _hostHashes = new Dictionary<ushort, int>();
        private readonly HashSet<ushort> _hostDirty = new HashSet<ushort>();
        private readonly Dictionary<ushort, BoxFamily> _hostPendingRemoved = new Dictionary<ushort, BoxFamily>();
        private readonly List<ushort> _deadHost = new List<ushort>();
        private int _hostFam;
        private int _hostIdx;
        // Reused snapshot buffers. Broadcast serializes synchronously, so a buffer can be
        // refilled on the next flush.
        private readonly List<BoxWire> _hostList = new List<BoxWire>(256);
        private readonly List<BoxWire> _heldList = new List<BoxWire>(1);
        private bool _fullPending; // next flush is a complete snapshot (session start / join / resync)

        // ---- client state ----
        private readonly Dictionary<ushort, InteractablePackagingBox> _clientById
            = new Dictionary<ushort, InteractablePackagingBox>();
        private readonly Dictionary<InteractablePackagingBox, ushort> _clientIdOf
            = new Dictionary<InteractablePackagingBox, ushort>();
        private readonly Dictionary<InteractablePackagingBox, BoxPossession> _reported
            = new Dictionary<InteractablePackagingBox, BoxPossession>();
        private readonly Dictionary<InteractablePackagingBox, int> _reportedContent
            = new Dictionary<InteractablePackagingBox, int>();
        private readonly Dictionary<InteractablePackagingBox, int> _baselineItemCount
            = new Dictionary<InteractablePackagingBox, int>();
        private readonly Dictionary<InteractablePackagingBox, int> _baselineItemType
            = new Dictionary<InteractablePackagingBox, int>();
        // A loose-box item delta this client sent that the host has not yet answered. The host
        // echoes TransferSeq in a BoxTransferResult; the difference between the requested and
        // accepted delta is what the client must roll back in its own hand (last to pull loses).
        private readonly PendingTransferLedger<ushort> _transfers = new PendingTransferLedger<ushort>();
        private readonly HostTransferAcks _hostAcks = new HostTransferAcks();
        private readonly HashSet<ushort> _suppressedSnapshotForBox = new HashSet<ushort>();
        private bool _resyncRequested;
        private float _lastResyncRequestAt = -999f;
        private const float ResyncCooldownSeconds = 2f;
        private readonly Dictionary<InteractablePackagingBox, float> _contentSentAt
            = new Dictionary<InteractablePackagingBox, float>();
        private readonly Dictionary<InteractablePackagingBox, float> _lastSentAt
            = new Dictionary<InteractablePackagingBox, float>();
        private readonly HashSet<InteractablePackagingBox> _active = new HashSet<InteractablePackagingBox>();
        private readonly List<InteractablePackagingBox> _activeScratch = new List<InteractablePackagingBox>();
        private readonly HashSet<InteractablePackagingBox> _clientDirty = new HashSet<InteractablePackagingBox>();
        private readonly HashSet<ushort> _snapshotIds = new HashSet<ushort>();
        private readonly List<ushort> _sweep = new List<ushort>();
        // Client-local retires are guarded only briefly: the host's in-flight snapshot lag is at
        // most a flush or two (~0.1s). Without a TTL a rejected/never-processed local retire would
        // suppress all authoritative snapshots for that id until a full snapshot (there is no
        // periodic full scan).
        private readonly Dictionary<ushort, float> _localRemoved = new Dictionary<ushort, float>();
        private readonly List<ushort> _localRemovedScratch = new List<ushort>();
        // Per-box local overrides on the client, keyed by wire id:
        //  - MotionUntil: a reliable possession edge just landed; ignore transient push frames
        //    until it passes, so they cannot re-claim the pose we just committed.
        //  - OpenUntil: this client just reported an open/close edge; keep our local lid state
        //    over a snapshot already in flight. Must outlast the game's 0.85s toggle animation
        //    (during which SetOpenCloseBox ignores calls) plus the network round trip.
        private sealed class ClientGuard
        {
            public float MotionUntil;
            public float OpenUntil;
        }
        private const float ClientOpenBlockPeriod = 1.5f;
        private readonly Dictionary<ushort, ClientGuard> _clientGuards = new Dictionary<ushort, ClientGuard>();

        private ClientGuard Guard(ushort id)
        {
            if (!_clientGuards.TryGetValue(id, out var guard))
                _clientGuards[id] = guard = new ClientGuard();
            return guard;
        }
        private const float LocalRemovedTtl = 5.0f;
        private int _clientFam;
        private int _clientIdx;

        // ---- local push-motion state (both roles) ----
        // The boxes the local player is driving (previous tick's driven set, used for settle
        // detection) and the per-box send bookkeeping. Only the pushed set (a handful of boxes)
        // is ever touched here - never O(all boxes).
        private readonly HashSet<InteractablePackagingBox> _pushDriven
            = new HashSet<InteractablePackagingBox>();
        private readonly List<InteractablePackagingBox> _pushScratch
            = new List<InteractablePackagingBox>();
        private readonly Dictionary<InteractablePackagingBox, float> _pushSentAt
            = new Dictionary<InteractablePackagingBox, float>();
        private readonly Dictionary<InteractablePackagingBox, Vector3> _pushSentPos
            = new Dictionary<InteractablePackagingBox, Vector3>();
        private readonly Dictionary<InteractablePackagingBox, float> _pushSentYaw
            = new Dictionary<InteractablePackagingBox, float>();
        private static readonly IReadOnlyCollection<InteractablePackagingBox> NoPushed
            = new InteractablePackagingBox[0];

        private float _hostTimer;
        private float _leaseClock;

        /// <summary>Host: broadcast a snapshot (Full flag distinguishes complete vs partial).</summary>
        public Action<BoxSnapshotMessage> SendSnapshot;
        /// <summary>Client: send one possession update to the host.</summary>
        public Action<BoxUpdateMessage> SendUpdate;
        /// <summary>Host: send the outcome of one loose-box item delta back to its sender, so the
        /// requester can roll back a transfer the host could not satisfy.</summary>
        public Action<BoxTransferResultMessage, int> SendTransferResult;
        /// <summary>Client: a host snapshot just spawned this local box mirror. ContainerSync
        /// uses it to claim an acknowledged empty-box take the moment the box appears.</summary>
        public Action<InteractablePackagingBox> OnClientBoxSpawned;
        /// <summary>Wired by CoopCore: the box the LOCAL player is currently holding, if any.
        /// Processed every tick so a host/client pickup or drop emits on the same frame
        /// instead of waiting for the round-robin slice to reach it.</summary>
        public Func<InteractablePackagingBox> LocalHeld;
        /// <summary>Wired by CoopCore: the boxes the LOCAL player is physically pushing (the
        /// BoxPushProbe set). Read every tick so a push starts streaming on the same frame.</summary>
        public Func<IReadOnlyCollection<InteractablePackagingBox>> LocalPushed;
        /// <summary>Client -> host: send one transient push-motion frame for a locally pushed box.</summary>
        public Action<BoxMotionMessage> SendMotion;
        /// <summary>Host -> all except the given conn id (-1 = all): relay/authoritative
        /// push-motion so every non-driver peer smooths the box.</summary>
        public Action<BoxMotionStateMessage, int> RelayMotion;
        /// <summary>Client: request an authoritative snapshot after a reserved local add was
        /// protected from a stale snapshot and its transfer has resolved.</summary>
        public Action RequestBoxResync;

        /// <summary>Receiver-local heal: coalesce into the next ClientTick instead of sending a
        /// resync request immediately, so a persistently-failing snapshot cannot form a
        /// request/response loop at the flush cadence.</summary>
        public void RequestResyncCoalesced()
        {
            if (CoopCore.Role == CoopRole.Client)
                _resyncRequested = true;
        }

        public override string Name => "boxes";

        public override void ForceResend() => RequestFullSnapshot();

        public BoxEngine(IEnumerable<IBoxFamily> families)
        {
            _families = new List<IBoxFamily>(families);
        }

        public bool TryGetHostBox(ushort id, out InteractablePackagingBox box)
        {
            return _hostIdentity.ById.TryGetValue(id, out box);
        }

        public ushort EnsureHostId(InteractablePackagingBox box)
        {
            return _hostIdentity.GetOrAssign(box);
        }

        public bool TryGetHostId(InteractablePackagingBox box, out ushort id)
        {
            return _hostIdentity.TryGetId(box, out id);
        }

        public void ForgetHostBox(ushort id)
        {
            // Emit an explicit Removed so partial-snapshot clients retire it immediately
            // (a full sweep still catches anything missed).
            BoxFamily family = BoxFamily.Item;
            if (_hostIdentity.ById.TryGetValue(id, out var box) && box != null)
                family = FamilyOf(box);
            if (!_hostPendingRemoved.ContainsKey(id))
                _hostPendingRemoved[id] = family;

            // Use the id overload: it removes the box->id side too even when the box is a
            // destroyed Unity object (a fake-null reference is not a C# null).
            _hostIdentity.Remove(id);
            _leases.Remove(id);
            _hostHashes.Remove(id);
            _hostDirty.Remove(id);
            if (!(box is null))
            {
                ClearPushEntries(box);
                BoxPlacement.CancelRemoteMotion(box); // stop any push smoothing follower
            }
            // These maps retain CLR references even after Unity reports fake-null.
            BoxVisuals.Forget(box);
            BoxPlacement.ClearThrow(box);
        }

        /// <summary>Host: drop ids for boxes that were destroyed without a Removed edge
        /// (scene teardown, a destroy path that bypassed the op).</summary>
        private void PruneDeadHostBoxes()
        {
            _deadHost.Clear();
            foreach (var kv in _hostIdentity.ById)
                if (kv.Value == null) // Unity fake-null
                    _deadHost.Add(kv.Key);
            for (int i = 0; i < _deadHost.Count; i++)
                ForgetHostBox(_deadHost[i]);
        }

        public bool TryGetClientBox(ushort id, out InteractablePackagingBox box)
        {
            return _clientById.TryGetValue(id, out box);
        }

        public bool TryGetClientId(InteractablePackagingBox box, out ushort id)
        {
            return _clientIdOf.TryGetValue(box, out id);
        }

        /// <summary>Client: resolve a furniture box id from its boxed object. The object's
        /// packaging-box reference can be stale after a mirror replacement, so fall back to
        /// the currently bound client mirrors.</summary>
        public bool TryResolveClientBoxId(InteractableObject boxedObject, out ushort id)
        {
            id = 0;
            if (boxedObject == null)
                return false;
            var box = boxedObject.GetPackagingBoxShelf();
            if (box != null && TryGetClientId(box, out id))
                return true;
            foreach (var pair in _clientById)
            {
                var boundBox = pair.Value;
                if (boundBox == null || FurnitureBoxFamily.BoxedObject(boundBox) != boxedObject)
                    continue;
                id = pair.Key;
                return true;
            }
            return false;
        }

        /// <summary>Client: this machine retired the box locally (place/collect/sell).
        /// Suppress the host's in-flight snapshot for that id until it agrees.</summary>
        public void ForgetClientBox(ushort id)
        {
            _clientById.TryGetValue(id, out var box);
            ForgetClientMirror(id, box);
            _localRemoved[id] = Time.time + LocalRemovedTtl;
        }

        /// <summary>Drop every per-box client bookkeeping entry keyed on the object. Does not
        /// touch _clientById (callers either reassign it or follow with ForgetClientMirror).</summary>
        private void ClearClientBoxEntries(InteractablePackagingBox box)
        {
            // Reference check, not Unity's overloaded ==: a destroyed box is "fake-null" but
            // still a valid dictionary key, and its stale entries are exactly what this drops.
            if (box is null)
                return;
            _clientIdOf.Remove(box);
            _reported.Remove(box);
            _reportedContent.Remove(box);
            _baselineItemCount.Remove(box);
            _baselineItemType.Remove(box);
            _contentSentAt.Remove(box);
            _lastSentAt.Remove(box);
            _active.Remove(box);
            ClearPushEntries(box);
            BoxVisuals.Forget(box);
            BoxPlacement.ClearThrow(box);
            BoxPlacement.CancelRemoteMotion(box); // stop any push smoothing follower
        }

        /// <summary>Client: unbind a mirror id and drop all of its per-box state.</summary>
        private void ForgetClientMirror(ushort id, InteractablePackagingBox box)
        {
            BoxShared.DebugLog("box-forget",
                $"client forget id={id} name={(box != null ? box.name : "null")}", id, 1f);
            ClearClientBoxEntries(box);
            _transfers.AbandonTarget(id);
            _clientById.Remove(id);
            _clientGuards.Remove(id);
            _suppressedSnapshotForBox.Remove(id);
        }

        public bool HostBoxHeldByOther(ushort id, int connId)
        {
            if (!_leases.TryGetValue(id, out var lease))
                return false;
            return (lease.Possession == BoxPossession.Held || lease.Possession == BoxPossession.Placing)
                && lease.Owner != NoOwner && lease.Owner != HostConn && lease.Owner != connId;
        }

        /// <summary>Client: this machine destroyed a box through local gameplay (trash,
        /// storage). Tell the host to retire the real one and stop tracking the mirror.</summary>
        public void ClientNotifyLocalDestroyed(InteractablePackagingBox box)
        {
            if (box == null || !_clientIdOf.TryGetValue(box, out ushort id))
                return;
            BoxShared.DebugLog("box-destroy", $"client local destroy id={id} name={box.name}", id, 1f);
            SendUpdate?.Invoke(new BoxUpdateMessage
            {
                Box = new BoxWire
                {
                    Id = id,
                    Family = FamilyOf(box),
                    Possession = BoxPossession.Removed,
                },
            });
            ForgetClientBox(id);
        }

        /// <summary>Host: the host player destroyed a box locally - drop its identity so it
        /// is never re-broadcast and the id is not leaked.</summary>
        public void HostNotifyLocalDestroyed(InteractablePackagingBox box)
        {
            if (box == null || !_hostIdentity.TryGetId(box, out ushort id))
                return;
            ForgetHostBox(id);
            ForceNextTick();
        }

        /// <summary>Host: atomically consume a loose item box for an empty-box storage
        /// operation. The caller increments storage only when this succeeds.</summary>
        public bool HostConsumeForEmptyBoxStorage(ushort id, int expectedType, bool expectedBig)
        {
            if (CoopCore.Role != CoopRole.Host)
                return false;
            if (!_hostIdentity.ById.TryGetValue(id, out var baseBox)
                || !(baseBox is InteractablePackagingBox_Item box))
                return false;
            var family = Family(BoxFamily.Item);
            if (family == null
                || !family.TryReadLocal(box, out var poss, out _, out _, out _, out _, out _)
                || poss != BoxPossession.Free)
                return false;
            try
            {
                if (box.m_IsBigBox != expectedBig)
                    return false;
                if ((int)box.m_ItemCompartment.GetItemType() != expectedType
                    || box.m_ItemCompartment.GetItemCount() != 0)
                    return false;

                CoopPlugin.Log.LogInfo($"BoxEngine: host consuming empty box id {id} for storage");
                BoxShared.ApplyingRemote = true;
                try
                {
                    ItemBoxFamily.UnhookIfStored(box);
                    box.OnDestroyed();
                }
                finally { BoxShared.ApplyingRemote = false; }

                ForgetHostBox(id);
                ForceNextTick();
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("BoxEngine storage consume: " + e.Message);
                return false;
            }
        }

        public override void Reset()
        {
            _hostIdentity.Clear();
            _leases.Clear();
            _hostHashes.Clear();
            _hostDirty.Clear();
            _hostPendingRemoved.Clear();
            _hostFam = 0;
            _hostIdx = 0;
            _clientById.Clear();
            _clientIdOf.Clear();
            _localRemoved.Clear();
            _localRemovedScratch.Clear();
            _clientGuards.Clear();
            _reported.Clear();
            _reportedContent.Clear();
            _baselineItemCount.Clear();
            _baselineItemType.Clear();
            _transfers.Clear();
            _hostAcks.Clear();
            CardBoxOps.ClearCollectAcks();
            _suppressedSnapshotForBox.Clear();
            _contentSentAt.Clear();
            _lastSentAt.Clear();
            _active.Clear();
            _clientDirty.Clear();
            _resyncRequested = false;
            _lastResyncRequestAt = -999f;
            _clientFam = 0;
            _clientIdx = 0;
            _pushDriven.Clear();
            _pushScratch.Clear();
            _pushSentAt.Clear();
            _pushSentPos.Clear();
            _pushSentYaw.Clear();
            _hostTimer = 0f;
            _fullPending = true; // send a full snapshot on the first tick of a session
            _leaseClock = 0f;
            BoxPlacement.Reset();
            BoxVisuals.Reset();
        }

        /// <summary>Ask for a prompt host flush (partial).</summary>
        public void ForceNextTick()
        {
            _hostTimer = PartialPeriod;
        }

        /// <summary>Host: mark a specific box to be emitted on the next partial flush
        /// (e.g. an imminent throw that must go out as soon as its velocity is real).</summary>
        public void MarkBoxDirty(InteractablePackagingBox box)
        {
            if (box == null)
                return;
            if (CoopCore.Role == CoopRole.Host)
            {
                if (_hostIdentity.TryGetId(box, out ushort id))
                    _hostDirty.Add(id);
            }
            else if (CoopCore.Role == CoopRole.Client && _clientIdOf.ContainsKey(box))
            {
                _clientDirty.Add(box);
            }
        }

        /// <summary>Client: an open item box's contents live on its child ShelfCompartment, and
        /// vanilla's take/return paths run on the compartment, not the box. Mark the owning
        /// mirror dirty so the content delta is reported this frame (and can be escrowed)
        /// instead of waiting for the round-robin to reach the box.</summary>
        public void MarkClientCompartmentDirty(ShelfCompartment comp)
        {
            if (CoopCore.Role != CoopRole.Client || comp == null)
                return;
            foreach (var kv in _clientById)
            {
                var box = kv.Value as InteractablePackagingBox_Item;
                if (box != null && box.m_ItemCompartment == comp)
                {
                    _clientDirty.Add(box);
                    return;
                }
            }
        }

        /// <summary>Host: force the next flush to be a complete snapshot (join/resync).</summary>
        public void RequestFullSnapshot()
        {
            _fullPending = true;
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool active)
        {
            if (!active)
                return;
            BoxVisuals.TickPending();
            _leaseClock += dt;
            // Forced path: the box the host player is holding must emit on the same frame,
            // not when the round-robin slice happens to reach it.
            ProcessLocalHeldHost();
            _hostTimer += dt;
            bool full = _fullPending;
            if (!full && _hostTimer < PartialPeriod)
                return;
            _hostTimer = 0f;
            if (full)
                _fullPending = false;

            ExpireLeases();
            PruneDeadHostBoxes();

            var list = _hostList;
            list.Clear();
            if (full)
            {
                // Clearing the hashes makes every live box "changed", so the scan emits a
                // complete list; ProcessHostBox rebuilds the per-id hashes as it goes.
                _hostHashes.Clear();
                ScanHostAll(list);
                _hostDirty.Clear();
                _hostPendingRemoved.Clear();
                _hostFam = 0;
                _hostIdx = 0;
                SendSnapshot?.Invoke(new BoxSnapshotMessage { Boxes = list, Full = true });
                return;
            }

            ScanHostDirty(list);
            ScanHostSlice(list);
            if (_hostPendingRemoved.Count > 0)
            {
                foreach (var kv in _hostPendingRemoved)
                    list.Add(new BoxWire { Id = kv.Key, Family = kv.Value, Possession = BoxPossession.Removed });
                _hostPendingRemoved.Clear();
            }
            if (list.Count > 0)
                SendSnapshot?.Invoke(new BoxSnapshotMessage { Boxes = list, Full = false });
        }

        /// <summary>Full scan: emit every live box (session start / explicit resync).</summary>
        private void ScanHostAll(List<BoxWire> list)
        {
            for (int f = 0; f < _families.Count; f++)
            {
                var family = _families[f];
                var boxes = family.LiveBoxes();
                if (boxes == null)
                    continue;
                for (int i = 0; i < boxes.Count && list.Count < MaxBoxes; i++)
                    ProcessHostBox(family, boxes[i], list, fullScan: true);
            }
        }

        private void ScanHostDirty(List<BoxWire> list)
        {
            if (_hostDirty.Count == 0)
                return;
            _deadHost.Clear();
            foreach (var id in _hostDirty)
                _deadHost.Add(id);
            _hostDirty.Clear();
            for (int i = 0; i < _deadHost.Count; i++)
            {
                if (_hostIdentity.ById.TryGetValue(_deadHost[i], out var box) && box != null)
                    ProcessHostBox(Family(FamilyOf(box)), box, list, fullScan: false);
            }
        }

        private void ScanHostSlice(List<BoxWire> list)
        {
            int budget = HostScanBudget;
            int guard = 0;
            while (budget > 0 && _families.Count > 0 && guard++ < 64 && list.Count < MaxBoxes)
            {
                if (_hostFam >= _families.Count)
                {
                    _hostFam = 0;
                    _hostIdx = 0;
                    break;
                }
                var family = _families[_hostFam];
                var boxes = family.LiveBoxes();
                if (boxes == null || boxes.Count == 0)
                {
                    _hostFam++;
                    _hostIdx = 0;
                    continue;
                }
                if (_hostIdx >= boxes.Count)
                {
                    _hostFam++;
                    _hostIdx = 0;
                    continue;
                }
                int take = Mathf.Min(budget, boxes.Count - _hostIdx);
                for (int k = 0; k < take && list.Count < MaxBoxes; k++)
                    ProcessHostBox(family, boxes[_hostIdx + k], list, fullScan: false);
                _hostIdx += take;
                budget -= take;
                if (_hostIdx >= boxes.Count)
                {
                    _hostFam++;
                    _hostIdx = 0;
                }
            }
        }

        private void ProcessLocalHeldHost()
        {
            if (LocalHeld == null)
                return;
            var held = LocalHeld();
            if (held == null)
                return;
            var family = Family(FamilyOf(held));
            if (family == null)
                return;
            var one = _heldList;
            one.Clear();
            ProcessHostBox(family, held, one, fullScan: false);
            if (one.Count > 0)
                SendSnapshot?.Invoke(new BoxSnapshotMessage { Boxes = one, Full = false });
        }

        /// <summary>Ensure a box (the dynamic body) carries a contact receiver. The player's
        /// kinematic body does not receive collision callbacks, so the box reports real
        /// contacts with the local player to <see cref="BoxPushProbe"/>.</summary>
        private static void EnsureContactProbe(InteractablePackagingBox box)
        {
            if (box == null || box.GetComponent<BoxContactProbe>() != null)
                return;
            box.gameObject.AddComponent<BoxContactProbe>().Init(box);
        }

        private void ProcessHostBox(IBoxFamily family, InteractablePackagingBox box, List<BoxWire> list,
            bool fullScan)
        {
            if (family == null || box == null)
                return;
            EnsureContactProbe(box);
            if (!family.TryReadLocal(box, out var localPoss, out var pos, out var yaw,
                    out var vel, out var angVel, out var stored))
                return;
            ushort id = _hostIdentity.GetOrAssign(box);
            if (id == 0)
                return;
            Lease lease = _leases.TryGetValue(id, out var l) ? l : default(Lease);
            // A pushed box's pose is owned by the transient motion stream: skip the snapshot
            // path (FillContent/hash/emit) so the stream is the single pose writer. This is
            // both the authority rule and the perf fix (no per-frame FillContent while pushed).
            // A FULL scan is the exception: it is a membership baseline, and a receiver's full
            // snapshot sweep deletes any tracked box missing from it, so a deferred box must
            // still be listed (with its current state) even while the stream owns its pose.
            if (lease.MotionDriven && !fullScan)
                return;
            if (BoxPlacement.IsThrowPending(box))
            {
                if (localPoss != BoxPossession.Free)
                    BoxPlacement.ClearThrow(box); // picked back up before the throw was reported
                else if (!BoxPlacement.ThrowReady(box, vel))
                {
                    if (!fullScan)
                    {
                        _hostDirty.Add(id); // retry next flush, once the impulse is integrated
                        return;
                    }
                    // full scan: emit the current state for membership; the throw's own Free
                    // report follows once the impulse integrates.
                }
                else
                    BoxPlacement.ClearThrow(box);
            }

            bool clientOwned = lease.Owner != NoOwner && lease.Owner != HostConn;
            BoxPossession poss;
            int owner;
            if (clientOwned)
            {
                poss = lease.Possession;
                owner = lease.Owner;
            }
            else
            {
                poss = localPoss;
                owner = localPoss == BoxPossession.Held || localPoss == BoxPossession.Placing
                    ? HostConn : NoOwner;
                lease.Owner = owner;
                lease.Possession = localPoss;
                lease.LastSeen = _leaseClock;
                _leases[id] = lease;
            }

            var w = new BoxWire
            {
                Id = id,
                Family = family.Family,
                Possession = poss,
                OwnerConn = owner,
            };
            if (poss == BoxPossession.Free)
            {
                w.Pos = pos;
                w.Yaw = yaw;
                w.Velocity = vel;
                w.AngularVelocity = angVel;
            }
            // FillContent also fills the Free+stored rack address and the furniture Unpack flag.
            family.FillContent(box, ref w);

            int hash = HostHash(w);
            if (!_hostHashes.TryGetValue(id, out var old) || old != hash)
            {
                _hostHashes[id] = hash;
                list.Add(w);
            }
        }

        private static int HostHash(in BoxWire w)
        {
            int h = 17;
            h = h * 31 + w.Id;
            h = h * 31 + (int)w.Possession;
            h = h * 31 + w.OwnerConn;
            h = h * 31 + (w.Unpack ? 1 : 0);
            if (w.Possession == BoxPossession.Free)
            {
                h = h * 31 + Mathf.RoundToInt(w.Pos.x * 8f);
                h = h * 31 + Mathf.RoundToInt(w.Pos.y * 8f);
                h = h * 31 + Mathf.RoundToInt(w.Pos.z * 8f);
                h = h * 31 + Mathf.RoundToInt(w.Yaw * 8f);
                h = h * 31 + Mathf.RoundToInt(w.Velocity.x * 16f);
                h = h * 31 + Mathf.RoundToInt(w.Velocity.y * 16f);
                h = h * 31 + Mathf.RoundToInt(w.Velocity.z * 16f);
                h = h * 31 + Mathf.RoundToInt(w.AngularVelocity.x * 16f);
                h = h * 31 + Mathf.RoundToInt(w.AngularVelocity.y * 16f);
                h = h * 31 + Mathf.RoundToInt(w.AngularVelocity.z * 16f);
            }
            h = h * 31 + ContentHash(w);
            return h;
        }

        /// <summary>Content signature folded into the host hash (family content is already in
        /// the wire; this adds the fields that matter for a change and aren't covered above).</summary>
        private static int ContentHash(in BoxWire w)
        {
            int h = 17;
            h = h * 31 + w.ItemType;
            h = h * 31 + w.ItemCount;
            h = h * 31 + (w.Big ? 1 : 0);
            h = h * 31 + (w.Open ? 1 : 0);
            h = h * 31 + w.StoreShelfId;
            h = h * 31 + w.StoreShelf;
            h = h * 31 + w.StoreComp;
            h = h * 31 + (w.Stored ? 1 : 0);
            h = h * 31 + w.ObjType;
            h = h * 31 + w.ObjIndex;
            h = h * 31 + w.NameHash;
            h = h * 31 + w.Kind;
            if (w.Cards != null)
            {
                h = h * 31 + w.Cards.Count;
                for (int i = 0; i < w.Cards.Count; i++)
                {
                    var c = w.Cards[i];
                    if (c == null)
                        continue;
                    h = h * 31 + (int)c.monsterType;
                    h = h * 31 + (int)c.expansionType;
                    h = h * 31 + (c.isFoil ? 1 : 0);
                }
            }
            return h;
        }

        /// <summary>True when a non-owner's Free report may be applied as content-only: the box is
        /// loose on the host (no lease owner, and the host is not carrying or placing it). Keeps
        /// a physical hand or a stale possession claim from being edited under the holder.</summary>
        private bool CanApplyContentOnly(InteractablePackagingBox box, PlayerRef currentOwner)
        {
            if (currentOwner.Kind != PlayerKind.None)
                return false;
            var family = Family(FamilyOf(box));
            if (family == null
                || !family.TryReadLocal(box, out var hostPoss, out _, out _, out _, out _, out var hostStored))
                return false;
            return hostPoss == BoxPossession.Free && !hostStored;
        }

        public void HostApplyUpdate(BoxUpdateMessage msg, int connId)
        {
            if (msg == null)
                return;
            if (msg.TransferSeq != 0 && _hostAcks.TryGet(connId, msg.TransferSeq, out int stored))
            {
                ReplyStoredTransfer(connId, msg, stored);
                return;
            }
            var w = msg.Box;
            BoxPlacement.SanitizeVelocity(ref w.Velocity, ref w.AngularVelocity);
            // The host is the only id authority: a client cannot legitimately reference an id the
            // host has never assigned. Drop it (and any lingering lease) instead of seeding an
            // unowned Free lease that ExpireLeases deliberately never expires.
            if (!_hostIdentity.ById.TryGetValue(w.Id, out var knownBox) || knownBox == null)
            {
                _leases.Remove(w.Id);
                _hostDirty.Remove(w.Id);
                ReplyTransfer(connId, msg, 0); // unknown box: the requester rolls its transfer back
                return;
            }
            if (w.Family != FamilyOf(knownBox))
            {
                BoxShared.DebugLog("box-rx", $"id={w.Id} fam={w.Family} sender={connId} rejected=family-mismatch");
                ReplyTransfer(connId, msg, 0);
                return;
            }
            if (!Enum.IsDefined(typeof(BoxPossession), w.Possession))
            {
                BoxShared.DebugLog("box-rx", $"id={w.Id} fam={w.Family} sender={connId} rejected=invalid-possession");
                ReplyTransfer(connId, msg, 0);
                return;
            }
            var sender = PlayerRegistry.ForConnection(connId);
            // default(Lease) has LastOwner == 0 (HostConn); seed NoOwner so a brand-new lease
            // cannot be released by a phantom conn 0.
            Lease lease = _leases.TryGetValue(w.Id, out var l)
                ? l
                : new Lease { Owner = NoOwner, LastOwner = NoOwner };
            var currentOwner = lease.Owner == NoOwner
                ? PlayerRef.None
                : lease.Owner == HostConn
                    ? new PlayerRef { Kind = PlayerKind.Host, Id = 0 }
                    : PlayerRegistry.ForConnection(lease.Owner);
            // LastOwner is only meaningful for a remote client; host ownership (HostConn) never
            // authorizes a client's release.
            var lastOwner = lease.LastOwner == NoOwner || lease.LastOwner == HostConn
                ? PlayerRef.None
                : PlayerRegistry.ForConnection(lease.LastOwner);

            // A client's possession edge never writes box content; only an explicit item delta
            // (TransferSeq != 0) may, and it is merged against the host's current contents. This
            // covers a loose, unowned box and the current owner editing while held/placing, so a
            // stale mirror (a pickup after the host removed items) can never restore what was taken.
            var itemFamily = w.Family == BoxFamily.Item && w.Possession != BoxPossession.Removed
                ? Family(BoxFamily.Item)
                : null;
            bool senderMayEdit = itemFamily != null
                && (lease.Owner == connId
                    || (currentOwner.Kind == PlayerKind.None && CanApplyContentOnly(knownBox, currentOwner)));
            bool itemDelta = senderMayEdit && msg.TransferSeq != 0;
            bool lidOnly = senderMayEdit && !itemDelta && w.Possession == BoxPossession.Free;
            bool contentMerged = false;
            int acceptedDelta = 0;
            if (itemDelta)
            {
                try
                {
                    contentMerged = itemFamily.ReconcileContent(knownBox, w, msg.ContentBaseItemCount,
                        msg.ContentTransferType, out acceptedDelta);
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogError($"BoxEngine: transfer reconciliation failed seq={msg.TransferSeq}: {ex}");
                    contentMerged = false;
                    acceptedDelta = 0;
                }
            }
            if (lidOnly)
            {
                try
                {
                    itemFamily.ApplyLidOnly(knownBox, w.Open);
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogError($"BoxEngine: lid update failed seq={msg.TransferSeq}: {ex}");
                    lidOnly = false;
                }
            }
            // A refused delta (e.g. a conflicting type) must be corrected: drop the cached hash so
            // the authoritative re-assert is sent even though the host's content did not change.
            if (itemDelta && !contentMerged)
                _hostHashes.Remove(w.Id);
            // Store the ack as soon as the merge outcome is known, BEFORE any fallible apply
            // step below, so a dispatch retry replays this result instead of re-applying the
            // delta. Store keeps the first value, so the later ReplyTransfer is a silent no-op.
            if (msg.TransferSeq != 0)
                _hostAcks.Store(connId, msg.TransferSeq, contentMerged ? acceptedDelta : 0);
            // The result is the single terminal synchronization point. Emit it before any
            // authority or mirror operation that can throw.
            ReplyTransfer(connId, msg, contentMerged ? acceptedDelta : 0);

            if (!BoxAuthority.AcceptBoxUpdate(currentOwner, sender, w.Possession, lastOwner))
            {
                if (itemFamily != null && senderMayEdit)
                {
                    _hostDirty.Add(w.Id);
                    BoxShared.DebugLog("box-rx",
                        $"id={w.Id} fam={w.Family} sender={connId} state={w.Possession} content-preserved applied={contentMerged} base={msg.ContentBaseItemCount} count={w.ItemCount}");
                }
                else
                {
                    BoxShared.DebugLog("box-rx", $"id={w.Id} fam={w.Family} sender={connId} state={w.Possession} accepted=false owner={currentOwner} last={lastOwner}");
                }
                return;
            }

            if (itemFamily != null)
            {
                // The host now holds the merged (or, when no delta was sent/refused, still its own)
                // content. Re-assert it in the wire entry so ApplyState's content write below is a
                // no-op instead of replaying the reporter's stale absolute.
                var merged = new BoxWire();
                itemFamily.FillContent(knownBox, ref merged);
                w.ItemType = merged.ItemType;
                w.ItemCount = merged.ItemCount;
            }

            var next = BoxAuthority.NextBoxOwner(sender, w.Possession);
            BoxShared.DebugLog("box-rx", $"id={w.Id} fam={w.Family} sender={connId} state={w.Possession} open={w.Open} accepted=true owner={lease.Owner}->{next}");
            // The client does not know the host's owner id for itself; stamp the sender so
            // the families resolve the correct avatar for a remote Held/Placing box.
            w.OwnerConn = connId;
            lease.Owner = next.Kind == PlayerKind.None
                ? NoOwner
                : next.Kind == PlayerKind.Host ? HostConn : next.Id;
            if (next.IsOwned)
                lease.LastOwner = lease.Owner; // remembered across the later Free/Removed release
            lease.Possession = w.Possession;
            lease.LastSeen = _leaseClock;
            // A reliable possession edge supersedes any in-flight push stream: stop the
            // kinematic follower and hold off transient frames for a beat so a late motion
            // frame (the transient lane overtakes reliable on the LAN transport) can't
            // re-claim the pose we just committed.
            lease.MotionDriven = false;
            if (w.Possession != BoxPossession.Removed)
                lease.MotionBlockedUntil = _leaseClock + MotionLeaseTimeout;
            _leases[w.Id] = lease;

            // Apply the owner's state to the host's own copy so the host sees it too.
            if (knownBox != null)
            {
                for (int f = 0; f < _families.Count; f++)
                    if (_families[f].Family == w.Family)
                        _families[f].ApplyState(knownBox, w, isOwner: false);
                // The reliable edge ends the push: stop the smoothing follower so physics
                // can resume from the pose ApplyState just committed.
                BoxPlacement.CancelRemoteMotion(knownBox);
            }

            if (w.Possession == BoxPossession.Removed && knownBox != null)
            {
                // The sender retired the real object on their machine (trash/storage) - do
                // the same here, or the next snapshot re-assigns an id and resurrects it.
                BoxShared.ApplyingRemote = true;
                try
                {
                    Family(FamilyOf(knownBox))?.DestroyBox(knownBox);
                }
                finally { BoxShared.ApplyingRemote = false; }
                ForgetHostBox(w.Id);
                return;
            }
            _hostDirty.Add(w.Id); // emit just this box on the next flush
        }

        /// <summary>Host: echo the outcome of one transfer to its sender when the request carried
        /// a sequence. A zero accepted delta tells the requester to roll the whole transfer back.</summary>
        private void ReplyTransfer(int connId, BoxUpdateMessage msg, int acceptedDelta)
        {
            if (msg == null || msg.TransferSeq == 0)
                return;
            _hostAcks.Store(connId, msg.TransferSeq, acceptedDelta);
            ReplyStoredTransfer(connId, msg, acceptedDelta);
        }

        private void ReplyStoredTransfer(int connId, BoxUpdateMessage msg, int acceptedDelta)
        {
            if (msg == null || msg.TransferSeq == 0)
                return;
            SendTransferResult?.Invoke(new BoxTransferResultMessage
            {
                TransferSeq = msg.TransferSeq,
                BoxId = msg.Box.Id,
                AcceptedDelta = acceptedDelta,
            }, connId);
        }

        /// <summary>Client: the host resolved one of our item transfers. Anything the host
        /// could not accept is rolled back out of our hand/inventory here (last to pull loses),
        /// so a box count that the host clamped cannot leave a duplicated item in our hand.</summary>
        public void ClientApplyTransferResult(BoxTransferResultMessage msg)
        {
            if (msg == null || msg.TransferSeq == 0)
                return;
            if (!_transfers.TryGet(msg.TransferSeq, out var pending))
            {
                BoxShared.DebugLog("box-transfer",
                    $"resolve seq={msg.TransferSeq} box={msg.BoxId} no pending transfer (ignored)", msg.BoxId, 2f);
                return;
            }
            int rejected = pending.RequestedDelta - msg.AcceptedDelta;
            bool fault = false;
            bool escrowResolved = true;
            bool reconciliationAttempted = false;
            try
            {
                if (pending.RequestedDelta < 0)
                {
                    reconciliationAttempted = true;
                    escrowResolved = _transfers.ResolveTake(pending, msg.AcceptedDelta);
                    if (rejected < 0)
                        BoxShared.DebugLog("box-transfer", $"box={pending.Target} take rejected={-rejected} type={pending.TransferType}", pending.Target, 2f);
                }
                else if (rejected > 0 && _clientById.TryGetValue(pending.Target, out var box) && box != null)
                {
                    var family = Family(FamilyOf(box));
                    var itemBox = box as InteractablePackagingBox_Item;
                    var comp = itemBox?.m_ItemCompartment;
                    reconciliationAttempted = true;
                    int returned = HandEscrow.EscrowAdded(comp, pending.TransferType, rejected);
                    if (family != null)
                    {
                        _reportedContent[box] = family.ContentSignature(box);
                        _baselineItemCount[box] = family.ReadItemCount(box);
                        _baselineItemType[box] = family.ReadItemType(box);
                    }
                    BoxShared.DebugLog("box-transfer",
                        $"box={pending.Target} add rejected={rejected} returnedToHand={returned} type={pending.TransferType}",
                        pending.Target, 2f);
                }
            }
            catch (Exception ex)
            {
                fault = true;
                CoopPlugin.Log.LogError($"BoxEngine: transfer result handler failed seq={msg.TransferSeq}: {ex}");
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
                            CoopPlugin.Log.LogError($"BoxEngine: take fault reconciliation failed seq={msg.TransferSeq}: {reconcileEx}");
                            HandEscrow.DeferRejectedTake(
                                pending.EscrowToken, Mathf.Max(0, -msg.AcceptedDelta));
                        }
                    }
                    else if (pending.RequestedDelta < 0)
                        HandEscrow.DeferRejectedTake(pending.EscrowToken, Mathf.Max(0, -msg.AcceptedDelta));
                    else if (pending.RequestedDelta > 0 && rejected > 0 && !reconciliationAttempted
                        && _clientById.TryGetValue(pending.Target, out var fallbackBox) && fallbackBox != null)
                    {
                        var fallbackComp = (fallbackBox as InteractablePackagingBox_Item)?.m_ItemCompartment;
                        reconciliationAttempted = true;
                        HandEscrow.EscrowAdded(fallbackComp, pending.TransferType, Mathf.Max(0, rejected));
                    }
                }
                catch (Exception reconcileEx)
                {
                    CoopPlugin.Log.LogError($"BoxEngine: add fault reconciliation failed seq={msg.TransferSeq}: {reconcileEx}");
                }
            }
            bool suppressed = _suppressedSnapshotForBox.Remove(pending.Target);
            if (fault)
            {
                _transfers.TryResolve(msg.TransferSeq, out _);
                RequestResyncCoalesced();
            }
            else if (!escrowResolved)
            {
                _transfers.TryResolve(msg.TransferSeq, out _);
                CoopPlugin.Log.LogError($"BoxEngine: transfer result seq={msg.TransferSeq} could not remove rejected hand items; released container guard for deferred local cleanup");
                RequestResyncCoalesced();
            }
            else
                _transfers.TryResolve(msg.TransferSeq, out _);
            if (suppressed || rejected != 0 || fault)
                RequestBoxResync?.Invoke();
        }

        public void HostReleaseConn(int connId)
        {
            _hostAcks.ReleaseConn(connId);
            var release = new List<ushort>();
            foreach (var kv in _leases)
                // A box is released if the peer owned it OR was driving its push (a pushed
                // Free box has Owner == NoOwner, so the Driver check is what catches it).
                if (kv.Value.Owner == connId
                    || (kv.Value.MotionDriven && kv.Value.Driver == connId))
                    release.Add(kv.Key);
            for (int i = 0; i < release.Count; i++)
            {
                _leases.Remove(release[i]);
                RestoreHostBoxToFree(release[i]);
                // The reliable restore is the final pose: stop any push-smoothing follower
                // so it cannot keep writing a stale extrapolated pose over the restored one.
                if (_hostIdentity.ById.TryGetValue(release[i], out var released))
                    BoxPlacement.CancelRemoteMotion(released);
            }
            ForceNextTick();
        }

        /// <summary>Host: restore a box to an authoritative Free state on the host's own object.
        /// A Held update hid this box (SetVisible(false)); when the lease ends without an explicit
        /// Free report (disconnect or lease timeout), the host must unhide/resume it from its REAL
        /// local content. The partial wire that callers would otherwise build has ItemType/ItemCount
        /// at their defaults and apply them, wiping the box's contents.</summary>
        private void RestoreHostBoxToFree(ushort id)
        {
            if (!_hostIdentity.ById.TryGetValue(id, out var box) || box == null)
                return;
            var family = Family(FamilyOf(box));
            if (family == null)
                return;
            if (!family.TryReadLocal(box, out _, out var pos, out var yaw,
                    out var vel, out var angVel, out _))
                return;
            var w = new BoxWire
            {
                Id = id,
                Family = family.Family,
                Possession = BoxPossession.Free,
                Pos = pos,
                Yaw = yaw,
                Velocity = vel,
                AngularVelocity = angVel,
            };
            family.FillContent(box, ref w);
            // isOwner:false is required: each family's ApplyState early-returns its Free branch for
            // the owning machine. This call IS the host applying the restored state, so it must run
            // the same branch a receiver runs.
            family.ApplyState(box, w, isOwner: false);
            _hostDirty.Add(id);
        }

        private void ExpireLeases()
        {
            var stale = new List<ushort>();
            foreach (var kv in _leases)
                // The host's own possession is always current; only remote leases time out.
                if (kv.Value.Owner != NoOwner && kv.Value.Owner != HostConn
                    && kv.Value.Possession != BoxPossession.Free
                    && _leaseClock - kv.Value.LastSeen > LeaseTimeout)
                    stale.Add(kv.Key);
            for (int i = 0; i < stale.Count; i++)
            {
                var lease = _leases[stale[i]];
                lease.Owner = NoOwner;
                lease.Possession = BoxPossession.Free;
                _leases[stale[i]] = lease;
                RestoreHostBoxToFree(stale[i]);
            }
            // A pushed box whose motion stream went silent (the driver left, dropped the box,
            // or the connection hiccuped) settles from the last pose the host applied, and the
            // real physics resumes on the next flush. Crash-safe: it never depends on the
            // driver's settle update arriving.
            var settled = new List<ushort>();
            foreach (var kv in _leases)
                if (kv.Value.MotionDriven && _leaseClock - kv.Value.LastMotion > MotionLeaseTimeout)
                    settled.Add(kv.Key);
            for (int i = 0; i < settled.Count; i++)
            {
                var lease = _leases[settled[i]];
                var settleVel = lease.MotionVel;
                var settleAngVel = lease.MotionAngVel;
                lease.MotionDriven = false;
                lease.Driver = NoOwner;
                _leases[settled[i]] = lease;
                var box = _hostIdentity.ById.TryGetValue(settled[i], out var b) ? b : null;
                BoxPlacement.CancelRemoteMotion(box);
                // Cancelling the glider kills its own physics-resume path: restore the real
                // simulation from the last streamed velocity here, or the host's copy would
                // freeze kinematic until the next possession edge.
                BoxPlacement.ResumePushPhysics(box, settleVel, settleAngVel);
                _hostDirty.Add(settled[i]);
            }
        }

        private static BoxFamily FamilyOf(InteractablePackagingBox box)
        {
            if (box is InteractablePackagingBox_Card)
                return BoxFamily.Card;
            if (box is InteractablePackagingBox_Shelf)
                return BoxFamily.Furniture;
            return BoxFamily.Item;
        }

        // ---------------- client ----------------

        private bool IsLocalRemoved(ushort id)
        {
            if (!_localRemoved.TryGetValue(id, out var expiry))
                return false;
            if (Time.time < expiry)
                return true;
            _localRemoved.Remove(id);
            return false;
        }

        private void PruneExpiredLocalRemoved()
        {
            if (_localRemoved.Count == 0)
                return;
            _localRemovedScratch.Clear();
            foreach (var kv in _localRemoved)
                if (Time.time >= kv.Value)
                    _localRemovedScratch.Add(kv.Key);
            for (int i = 0; i < _localRemovedScratch.Count; i++)
                _localRemoved.Remove(_localRemovedScratch[i]);
        }

        public void ClientApplySnapshot(BoxSnapshotMessage msg)
        {
            if (msg == null || msg.Boxes == null)
                return;
            PruneExpiredLocalRemoved();
            bool full = msg.Full;
            bool truncated = full && msg.Boxes.Count >= MaxBoxes;
            if (full)
            {
                _snapshotIds.Clear();
                for (int i = 0; i < msg.Boxes.Count; i++)
                    _snapshotIds.Add(msg.Boxes[i].Id);
                _localRemovedScratch.Clear();
                foreach (var kv in _localRemoved)
                    if (!_snapshotIds.Contains(kv.Key))
                        _localRemovedScratch.Add(kv.Key);
                for (int i = 0; i < _localRemovedScratch.Count; i++)
                    _localRemoved.Remove(_localRemovedScratch[i]);
            }
            List<InteractablePackagingBox> spawned = null;
            for (int i = 0; i < msg.Boxes.Count; i++)
            {
                var w = msg.Boxes[i];
                BoxPlacement.SanitizeVelocity(ref w.Velocity, ref w.AngularVelocity);
                var family = Family(w.Family);
                if (family == null)
                    continue;
                if (IsLocalRemoved(w.Id))
                {
                    // The host's explicit Removed is its agreement that the box is gone;
                    // drop the local-retire guard so we don't depend on a full sweep to
                    // prune it (there is no periodic full snapshot).
                    if (w.Possession == BoxPossession.Removed)
                        _localRemoved.Remove(w.Id);
                    continue; // locally retired; wait for the host to agree
                }

                bool hadEntry = _clientById.TryGetValue(w.Id, out var box);
                if (hadEntry && box != null && FamilyOf(box) != w.Family)
                {
                    // The host re-used this id for a different box family (a host-side identity
                    // reset). The old mirror is no longer this id's object: destroy it before
                    // re-adopting, or it is orphaned and a duplicate is spawned. A content
                    // mismatch within the SAME family is NOT an identity change - a pending
                    // local add and ordinary drift keep the mirror and are handled below.
                    BoxShared.DebugLog("box-rx",
                        $"id={w.Id} fam={w.Family} family changed from {FamilyOf(box)}; recreating mirror",
                        w.Id, 2f);
                    DestroyClientBox(box);
                    ForgetClientMirror(w.Id, box);
                    box = null;
                    hadEntry = false;
                }
                if (!hadEntry || box == null)
                {
                    // The key existed but held a destroyed object: drop its stale per-box
                    // entries before this id is bound to the new object.
                    if (hadEntry)
                    {
                        ClearClientBoxEntries(box);
                        _clientGuards.Remove(w.Id);
                    }
                    if (w.Possession == BoxPossession.Removed)
                        continue;
                    // The client joins by loading the host's own save, so it usually already
                    // owns this box. Bind that object instead of spawning a duplicate.
                    box = AdoptExisting(family, w);
                    if (box != null)
                    {
                        _clientById[w.Id] = box;
                        _clientIdOf[box] = w.Id;
                        BoxShared.DebugLog("box-adopt", $"id={w.Id} fam={w.Family} state={w.Possession} open={w.Open} name={box.name} adopted=true");
                    }
                    else
                    {
                        box = family.Spawn(w);
                        if (box == null)
                            continue;
                        _clientById[w.Id] = box;
                        _clientIdOf[box] = w.Id;
                        (spawned ?? (spawned = new List<InteractablePackagingBox>())).Add(box);
                        BoxShared.DebugLog("box-adopt", $"id={w.Id} fam={w.Family} state={w.Possession} name={box.name} adopted=false");
                    }
                }
                // Local possession wins only while the wire still agrees we own the box: a
                // stale Free echo right after a quick re-pickup must not yank the held box
                // out of the hand. But if the host resolved a near-simultaneous pickup against
                // us, the incoming state names another player as owner, and yielding is the
                // only way back into sync - every later Held/drop we send is rejected until we
                // do. Placing is deliberately not yielded: cancelling an in-progress local
                // placement mid-ghost is riskier than the rare placing/pickup race, which
                // converges when the placement completes.
                if (w.Possession != BoxPossession.Removed
                    && family.TryReadLocal(box, out var localPoss, out _, out _, out _, out _, out _)
                    && (localPoss == BoxPossession.Held || localPoss == BoxPossession.Placing))
                {
                    bool lostToAnotherOwner = localPoss == BoxPossession.Held
                        && (w.Possession == BoxPossession.Held || w.Possession == BoxPossession.Placing)
                        && w.OwnerConn != CoopCore.LocalConnectionId;
                    if (!lostToAnotherOwner)
                        continue;
                    BoxShared.DebugLog("box-rx",
                        $"id={w.Id} fam={w.Family} local-held but authoritative owner={w.OwnerConn}; releasing local hold");
                    CoopCore.ForceExitHoldBox(box);
                    // fall through and apply the authoritative state (hides the box)
                }
                // A local take/add on this mirror that has not been reported yet must not be
                // erased by an authoritative snapshot: the baseline still holds the last synced
                // count, so a mismatch is an un-sent local edit. Skip this entry entirely -
                // including the content recreate below, which would otherwise drop a just-added
                // item type - so ClientTick reports the delta and the next snapshot applies the
                // merged result. (A lid-only edit has no count mismatch and stays protected by
                // the open guard in the apply branch.)
                if (w.Possession == BoxPossession.Free
                    && w.Family == BoxFamily.Item
                    && _baselineItemCount.TryGetValue(box, out var pendingBaseline)
                    && family.ReadItemCount(box) != pendingBaseline)
                    continue;
                bool reservedAdd = _transfers.IsAddReserved(w.Id);
                bool reservedTake = _transfers.IsTakeReserved(w.Id);
                if (!reservedAdd && !reservedTake && !family.ContentMatches(box, w))
                {
                    if (!family.RecreateOnContentMismatch)
                    {
                        // Immutable content (graded card boxes): a mismatch is save drift, not
                        // a content change. Recreating the mirror here multiplied boxes while
                        // the two saves' graded albums had drifted; keep the existing mirror
                        // and let the host's Removed retire it.
                        BoxShared.DebugLog("box-content",
                            $"id={w.Id} fam={w.Family} content mismatch on an immutable family; keeping mirror",
                            w.Id, 5f);
                    }
                    else
                    {
                        DestroyClientBox(box);
                        ForgetClientMirror(w.Id, box);
                        if (w.Possession == BoxPossession.Removed)
                            continue;
                        box = family.Spawn(w);
                        if (box == null)
                            continue;
                        _clientById[w.Id] = box;
                        _clientIdOf[box] = w.Id;
                        (spawned ?? (spawned = new List<InteractablePackagingBox>())).Add(box);
                    }
                }
                if (w.Possession == BoxPossession.Removed)
                {
                    DestroyClientBox(box);
                    ForgetClientMirror(w.Id, box);
                    continue;
                }
                // my own Held/Placing box is driven by my local game; never re-render it
                bool mine = (w.Possession == BoxPossession.Held || w.Possession == BoxPossession.Placing)
                    && w.OwnerConn == CoopCore.LocalConnectionId;
                if (!mine)
                {
                    if (w.Possession == BoxPossession.Free
                        && _clientGuards.TryGetValue(w.Id, out var openGuard)
                        && Time.time < openGuard.OpenUntil)
                        w.Open = BoxVisuals.ReadOpen(box); // keep the lid state we just reported
                    if (reservedAdd || reservedTake)
                    {
                        // A pending local edit must not be repainted by host truth: for an add
                        // the local count/type is the optimistic truth; for a take the local
                        // reduced count is, and restoring the host's pre-take content while the
                        // item is still escrowed in the hand would duplicate it.
                        int localCount = family.ReadItemCount(box);
                        int localType = EnumMap.ToWire(EnumKind.ItemType, family.ReadItemType(box));
                        if (localCount != w.ItemCount || localType != w.ItemType)
                        {
                            w.ItemCount = localCount;
                            w.ItemType = localType;
                            _suppressedSnapshotForBox.Add(w.Id);
                            BoxShared.DebugLog("box-transfer",
                                $"id={w.Id} pending {(reservedTake ? "take" : "add")} protected local content count={localCount} type={localType}",
                                w.Id, 1f);
                        }
                    }
                    family.ApplyState(box, w, isOwner: false);
                    // A reliable Free pose is the settle commit: end any in-flight push
                    // smoothing so physics resumes from the authoritative pose, not a
                    // stale extrapolated target.
                    if (w.Possession == BoxPossession.Free)
                    {
                        Guard(w.Id).MotionUntil = Time.time + MotionLeaseTimeout;
                        BoxPlacement.CancelRemoteMotion(box);
                    }
                    if (w.Possession != BoxPossession.Removed)
                    {
                        _reported[box] = w.Possession;
                        _reportedContent[box] = family.ContentSignature(box);
                        _baselineItemCount[box] = family.ReadItemCount(box);
                        _baselineItemType[box] = family.ReadItemType(box);
                    }
                }
            }

            if (spawned != null)
                for (int i = 0; i < spawned.Count; i++)
                    OnClientBoxSpawned?.Invoke(spawned[i]);

            if (full && !truncated)
            {
                _sweep.Clear();
                foreach (var kv in _clientById)
                    if (!_snapshotIds.Contains(kv.Key))
                        _sweep.Add(kv.Key);
                for (int i = 0; i < _sweep.Count; i++)
                {
                    _clientById.TryGetValue(_sweep[i], out var box);
                    if (box != null)
                        DestroyClientBox(box);
                    ForgetClientMirror(_sweep[i], box);
                }
            }
        }

        /// <summary>Destroy a client mirror without triggering the local-destroy op:
        /// this destroy IS the reconciliation, not player gameplay.</summary>
        private void DestroyClientBox(InteractablePackagingBox box)
        {
            _clientIdOf.Remove(box);
            bool prev = BoxShared.ApplyingRemote;
            BoxShared.ApplyingRemote = true;
            try
            {
                Family(FamilyOf(box))?.DestroyBox(box);
            }
            finally { BoxShared.ApplyingRemote = prev; }
        }

        /// <summary>Find a box this client already owns that represents the host's wire
        /// entry, so a join/reload binds the existing object instead of spawning a duplicate.
        /// Stored boxes match on their rack/slot address; loose boxes match on content plus
        /// nearest pose.</summary>
        private InteractablePackagingBox AdoptExisting(IBoxFamily family, in BoxWire w)
        {
            var boxes = family.LiveBoxes();
            if (boxes == null)
                return null;
            InteractablePackagingBox best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < boxes.Count; i++)
            {
                var cand = boxes[i];
                if (cand == null || _clientIdOf.ContainsKey(cand))
                    continue; // null or already bound to another id
                if (!family.ContentMatches(cand, w))
                    continue;
                var have = new BoxWire();
                family.FillContent(cand, ref have);
                if (have.IsStored != w.IsStored)
                    continue;
                if (w.IsStored)
                {
                    bool sameSlot = w.StoreShelfId != 0 && have.StoreShelfId != 0
                        ? have.StoreShelfId == w.StoreShelfId
                        : have.StoreShelf == w.StoreShelf && have.StoreComp == w.StoreComp;
                    if (sameSlot)
                        return cand;
                    continue;
                }
                float d = Vector3.Distance(BoxPlacement.PhysicsPosition(cand), w.Pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = cand;
                }
            }
            return best;
        }

        public void ClientTick(float dt, bool active)
        {
            if (!active)
                return;
            if (_resyncRequested && Time.time - _lastResyncRequestAt >= ResyncCooldownSeconds)
            {
                _resyncRequested = false;
                _lastResyncRequestAt = Time.time;
                RequestBoxResync?.Invoke();
            }
            BoxVisuals.TickPending();

            // Forced path: the box the local player just picked up is processed immediately,
            // not when the round-robin slice reaches it.
            if (LocalHeld != null)
            {
                var held = LocalHeld();
                if (held != null && ProcessClientBox(Family(FamilyOf(held)), held))
                    _active.Add(held);
            }

            // Mutation postfixes mark open/close edges directly. Process those before
            // the round-robin so a lid change is reported without waiting for the box's
            // position in a large warehouse scan.
            if (_clientDirty.Count > 0)
            {
                _activeScratch.Clear();
                foreach (var b in _clientDirty)
                    _activeScratch.Add(b);
                _clientDirty.Clear();
                for (int i = 0; i < _activeScratch.Count; i++)
                {
                    var b = _activeScratch[i];
                    if (b != null && ProcessClientBox(Family(FamilyOf(b)), b, force: true))
                        _active.Add(b);
                }
            }

            // Fast path: boxes currently carried/placed by the local player, checked every
            // frame so interaction is never delayed by the round-robin.
            if (_active.Count > 0)
            {
                _activeScratch.Clear();
                foreach (var b in _active)
                    _activeScratch.Add(b);
                for (int i = 0; i < _activeScratch.Count; i++)
                {
                    var box = _activeScratch[i];
                    if (box == null)
                    {
                        _active.Remove(box);
                        continue;
                    }
                    if (!ProcessClientBox(Family(FamilyOf(box)), box))
                        _active.Remove(box);
                }
            }

            // Round-robin the rest: a small budget per frame, eventually covering all boxes.
            int budget = ClientScanBudget;
            int guard = 0;
            while (budget > 0 && _families.Count > 0 && guard++ < 64)
            {
                if (_clientFam >= _families.Count)
                {
                    _clientFam = 0;
                    _clientIdx = 0;
                    break;
                }
                var family = _families[_clientFam];
                var boxes = family.LiveBoxes();
                if (boxes == null || boxes.Count == 0)
                {
                    _clientFam++;
                    _clientIdx = 0;
                    continue;
                }
                if (_clientIdx >= boxes.Count)
                {
                    _clientFam++;
                    _clientIdx = 0;
                    continue;
                }
                int take = Mathf.Min(budget, boxes.Count - _clientIdx);
                for (int k = 0; k < take; k++)
                {
                    var box = boxes[_clientIdx + k];
                    if (box == null)
                        continue;
                    if (ProcessClientBox(family, box))
                        _active.Add(box);
                }
                _clientIdx += take;
                budget -= take;
                if (_clientIdx >= boxes.Count)
                {
                    _clientFam++;
                    _clientIdx = 0;
                }
            }
        }

        /// <summary>Inspect one client-side box and report a change. Returns true while the
        /// box is carried/placed (so the caller keeps it on the every-frame fast path).
        /// <paramref name="force"/> bypasses the content-report throttle for a box a mutation
        /// postfix explicitly marked dirty (a local take must be reported - and escrowed - now).</summary>
        private bool ProcessClientBox(IBoxFamily family, InteractablePackagingBox box, bool force = false)
        {
            if (family == null || box == null)
                return false;
            EnsureContactProbe(box);
            bool read = family.TryReadLocal(box, out var poss, out var pos, out var yaw,
                out var vel, out var angVel, out var stored);
            if (!_clientIdOf.ContainsKey(box))
            {
                // A box the local player is holding/placing but that has no host id cannot
                // be reported at all - its pickup/throw will never reach the host.
                if (read && (poss == BoxPossession.Held || poss == BoxPossession.Placing))
                    BoxShared.DebugLog("box-orphan",
                        $"fam={family.Family} name={box.name} state={poss} has no host id - local actions will NOT sync",
                        box.GetInstanceID(), 1f);
                return false;
            }
            if (!read)
                return false;
            if (BoxPlacement.IsThrowPending(box))
            {
                if (poss != BoxPossession.Free)
                    BoxPlacement.ClearThrow(box); // picked back up before the throw was reported
                else if (!BoxPlacement.ThrowReady(box, vel))
                    return true; // keep on the fast path; report once the impulse integrates
                else
                    BoxPlacement.ClearThrow(box);
            }
            // First sighting of a box we have never reported: seed the local baseline instead
            // of transmitting it. A joiner mirrors the host's own save, so its initial Free
            // pose/content is not a change - sending it would let a client that never owned
            // the box rewrite the host's authoritative state. A genuine local edge (Held/
            // Placing, or a content/open change after this seed) still reports normally.
            if (poss == BoxPossession.Free && !_reported.ContainsKey(box))
            {
                _reported[box] = poss;
                _reportedContent[box] = family.ContentSignature(box);
                _baselineItemCount[box] = family.ReadItemCount(box);
                _baselineItemType[box] = family.ReadItemType(box);
                return false;
            }
            bool change = !_reported.TryGetValue(box, out var prev) || prev != poss;
            int sig = family.ContentSignature(box);
            bool contentChange = !_reportedContent.TryGetValue(box, out var pc) || pc != sig;
            // Rate-limit content-only reports; a state edge is immediate. A forced call is a
            // mutation postfix's "report now" mark (a take must be escrowed this frame).
            if (contentChange && !change && !force
                && _contentSentAt.TryGetValue(box, out var lastContent)
                && Time.time - lastContent < ContentReportPeriod)
                contentChange = false;
            // A held/placing box sends no edge while it is held, so refresh the host lease
            // periodically or the 3s timeout would release it mid-hold.
            bool renewLease = (poss == BoxPossession.Held || poss == BoxPossession.Placing)
                && (!_lastSentAt.TryGetValue(box, out var lastSent)
                    || Time.time - lastSent >= LeaseRenewPeriod);
            bool isActive = poss == BoxPossession.Held || poss == BoxPossession.Placing;
            if (!change && !contentChange && !renewLease)
                return isActive;
            _contentSentAt[box] = Time.time;
            _lastSentAt[box] = Time.time;
            SendClientBoxReport(family, box, poss, pos, yaw, vel, angVel, sig);
            return isActive;
        }

        private uint SendClientBoxReport(IBoxFamily family, InteractablePackagingBox box,
            BoxPossession poss, Vector3 pos, float yaw, Vector3 vel, Vector3 angVel, int sig)
        {
            var w = new BoxWire
            {
                Id = _clientIdOf[box],
                Family = family.Family,
                Possession = poss,
            };
            if (poss == BoxPossession.Free)
            {
                w.Pos = pos;
                w.Yaw = yaw;
                w.Velocity = vel;
                w.AngularVelocity = angVel;
            }
            family.FillContent(box, ref w);
            int baseItemCount = _baselineItemCount.TryGetValue(box, out var bc) ? bc : w.ItemCount;
            int baseItemType = _baselineItemType.TryGetValue(box, out var bt) ? bt : (int)EItemType.None;
            // An item content delta (loose edit, or the current owner editing while
            // held/placing) is tracked as a transfer so the host can merge exactly that much
            // and tell us what it accepted; the rest is rolled back out of our hand. Invariant:
            // every local content mutation must either refresh _baselineItemCount/_reportedContent
            // or produce a transfer here, or the next possession edge would report a stale delta.
            int requestedDelta = w.ItemCount - baseItemCount;
            int transferType = -1;
            uint transferSeq = 0;
            if (poss != BoxPossession.Removed && family.Family == BoxFamily.Item && requestedDelta != 0)
            {
                // Take: the type we had before (vanilla clears it to None when we empty the
                // box). Add: the type we now hold.
                int localTransfer = requestedDelta < 0 ? baseItemType : family.ReadItemType(box);
                transferType = EnumMap.ToWire(EnumKind.ItemType, localTransfer);
                // A loose-box content decrease is escrowed out of the hand only when the item
                // actually went into the hand. A container move (box <-> shelf) has no recent
                // hand-take note, so escrowing it would reserve an unrelated hand item.
                bool escrowTake = requestedDelta > 0 || HandEscrow.HasRecentTakeOfType(localTransfer);
                transferSeq = _transfers.Begin(w.Id, requestedDelta, localTransfer, out var effectiveTransfer, escrowTake);
                if (transferSeq != 0)
                    transferType = EnumMap.ToWire(EnumKind.ItemType, effectiveTransfer);
                if (transferSeq == 0)
                {
                    CoopPlugin.Log.LogError(
                        $"BoxEngine: transfer reserve failed box={w.Id} delta={requestedDelta}; reverting local mutation and requesting resync");
                    if (requestedDelta < 0 && escrowTake)
                        HandEscrow.RollbackUnreservedTake(localTransfer, -requestedDelta);
                    else
                    {
                        var itemBox = box as InteractablePackagingBox_Item;
                        int returned = itemBox?.m_ItemCompartment != null
                            ? HandEscrow.EscrowAdded(itemBox.m_ItemCompartment, localTransfer, requestedDelta)
                            : 0;
                        if (requestedDelta > 0 && returned != requestedDelta)
                            CoopPlugin.Log.LogError($"BoxEngine: failed to return all {requestedDelta} untracked added items (returned {returned}) box={w.Id}");
                    }
                    transferType = -1;
                    int liveCount = family.ReadItemCount(box);
                    int liveType = family.ReadItemType(box);
                    w.ItemCount = liveCount;
                    w.ItemType = EnumMap.ToWire(EnumKind.ItemType, liveType);
                    baseItemCount = liveCount;
                    _baselineItemCount[box] = liveCount;
                    _baselineItemType[box] = liveType;
                    sig = family.ContentSignature(box);
                    _reportedContent[box] = sig;
                    _resyncRequested = true;
                }
            }
            _reported[box] = poss;
            _reportedContent[box] = sig;
            BoxShared.DebugLog("box-tx", $"id={w.Id} fam={w.Family} state={poss} open={w.Open} sig={sig} name={box.name}",
                box.GetInstanceID(), 0.05f);
            var update = new BoxUpdateMessage
            {
                Box = w,
                ContentBaseItemCount = baseItemCount,
                ContentTransferType = transferType,
                TransferSeq = transferSeq,
            };
            // Advance the baseline BEFORE the single send so a scan cannot create a duplicate
            // obligation for the same local mutation.
            _baselineItemCount[box] = w.ItemCount;
            _baselineItemType[box] = family.ReadItemType(box);
            SendUpdate?.Invoke(update);
            Guard(w.Id).OpenUntil = Time.time + ClientOpenBlockPeriod;
            return transferSeq;
        }

        // ---------------- push motion ----------------
        //
        // A box the LOCAL player is physically pushing is streamed as a transient pose/velocity
        // (BoxMotion client->host, BoxMotionState host->peers) at the network send rate, gated on
        // change. While a box is motion-driven, exactly one writer owns its pose (the stream) and
        // the body is kinematic; on settle the host re-emits it from its real pose and the driver
        // sends one reliable Free (with content) so the commit and broadcast converge and physics
        // resumes with the streamed velocity. Only the pushed set (a handful of boxes) is touched
        // per frame - never O(all boxes), and no FillContent/ContentSignature in the per-frame path.

        /// <summary>True if the local player may physically push this box: it is loose (Free,
        /// not stored) in a family we sync, and (on a client) it has a host id. The BoxPushProbe
        /// uses this to decide which contacts count as a push.</summary>
        public bool CanLocallyPush(InteractablePackagingBox box)
        {
            if (box == null)
                return false;
            var family = Family(FamilyOf(box));
            if (family == null)
            {
                BoxShared.DebugLog("push-can", $"name={box.name} reject=no-family role={CoopCore.Role}", box.GetInstanceID(), 1f);
                return false;
            }
            if (!family.TryReadLocal(box, out var poss, out _, out _, out _, out _, out var stored))
            {
                BoxShared.DebugLog("push-can", $"name={box.name} reject=tryread-false fam={family.Family}", box.GetInstanceID(), 1f);
                return false;
            }
            if (poss != BoxPossession.Free || stored)
            {
                BoxShared.DebugLog("push-can", $"name={box.name} reject=poss={poss} stored={stored} fam={family.Family}", box.GetInstanceID(), 1f);
                return false;
            }
            if (CoopCore.Role == CoopRole.Client && !_clientIdOf.ContainsKey(box))
            {
                BoxShared.DebugLog("push-can", $"name={box.name} reject=no-client-id fam={family.Family}", box.GetInstanceID(), 1f);
                return false;
            }
            return true;
        }

        /// <summary>Drive the boxes the local player is physically pushing. Runs on both roles.
        /// Host: broadcast each pushed box's pose as an authoritative BoxMotionState and mark the
        /// lease motion-driven. Client: send each pushed box's pose to the host as a transient
        /// BoxMotion. A box that drops out of the pushed set settles. Reads only the pushed set.</summary>
        public void PushTick(float dt, bool active)
        {
            if (!active || LocalPushed == null)
                return;
            var pushed = LocalPushed() ?? NoPushed;

            // 1) Settle any box we were driving that is no longer being pushed.
            if (_pushDriven.Count > 0)
            {
                _pushScratch.Clear();
                foreach (var box in _pushDriven)
                    if (!IsPushed(box, pushed))
                        _pushScratch.Add(box);
                for (int i = 0; i < _pushScratch.Count; i++)
                    SettlePushed(_pushScratch[i]);
            }

            // 2) Drive the boxes currently being pushed.
            float interval = 1f / SendRate();
            foreach (var box in pushed)
                DrivePushed(box, interval);
        }

        /// <summary>Reference membership test over the pushed set: O(contacts), no allocation,
        /// no Linq (the project stays allocation-free per frame).</summary>
        private static bool IsPushed(InteractablePackagingBox box, IReadOnlyCollection<InteractablePackagingBox> pushed)
        {
            foreach (var candidate in pushed)
                if (ReferenceEquals(candidate, box))
                    return true;
            return false;
        }

        /// <summary>Read one pushed box's pose and stream it if it moved since the last frame we
        /// sent and the rate window allows. No FillContent / ContentSignature here.</summary>
        private void DrivePushed(InteractablePackagingBox box, float interval)
        {
            if (box == null)
                return;
            var family = Family(FamilyOf(box));
            if (family == null)
                return;
            bool isHost = CoopCore.Role == CoopRole.Host;
            ushort id;
            if (isHost)
            {
                if (!_hostIdentity.TryGetId(box, out id))
                    return; // no id yet; the next ProcessHostBox will assign one
            }
            else
            {
                if (!_clientIdOf.TryGetValue(box, out id))
                    return; // the host has not given this box an id; nothing to stream
            }
            if (!family.TryReadLocal(box, out var poss, out var pos, out var yaw,
                    out var vel, out var angVel, out var stored))
                return;
            if (poss != BoxPossession.Free || stored)
                return; // picked up or racked mid-push; stop driving it this frame

            // Change gate: only send when the pose actually moved since the last frame we SENT.
            if (_pushSentPos.TryGetValue(box, out var lastPos)
                && _pushSentYaw.TryGetValue(box, out var lastYaw)
                && Vector3.SqrMagnitude(pos - lastPos) <= MotionEpsilon * MotionEpsilon
                && Mathf.Abs(Mathf.DeltaAngle(lastYaw, yaw)) <= 1f)
                return;
            // Rate limit: at most SendRateHz frames per second per box.
            if (_pushSentAt.TryGetValue(box, out var lastSent) && Time.time - lastSent < interval)
                return;

            _pushSentAt[box] = Time.time;
            _pushSentPos[box] = pos;
            _pushSentYaw[box] = yaw;
            _pushDriven.Add(box);

            if (isHost)
            {
                Lease lease = _leases.TryGetValue(id, out var l) ? l : default(Lease);
                if (lease.Possession == BoxPossession.Held || lease.Possession == BoxPossession.Placing)
                    return; // the authoritative lease says it is in someone's hand; the local
                            // push of the (hidden) host mirror waits for the Free edge
                lease.MotionDriven = true;
                lease.Driver = HostConn;
                lease.LastMotion = _leaseClock;
                lease.MotionPos = pos;
                lease.MotionYaw = yaw;
                lease.MotionVel = vel;
                lease.MotionAngVel = angVel;
                _leases[id] = lease;
                RelayMotion?.Invoke(new BoxMotionStateMessage
                {
                    Id = id,
                    DriverConn = HostConn,
                    Pos = pos,
                    Yaw = yaw,
                    Velocity = vel,
                    AngularVelocity = angVel,
                }, -1);
            }
            else
            {
                BoxShared.DebugLog("box-motion-tx", $"id={id} name={box.name} pos={pos}", box.GetInstanceID(), 0.5f);
                SendMotion?.Invoke(new BoxMotionMessage
                {
                    Id = id,
                    Pos = pos,
                    Yaw = yaw,
                    Velocity = vel,
                    AngularVelocity = angVel,
                });
            }
        }

        /// <summary>A pushed box dropped out of the local pushed set (the player stopped). Host:
        /// re-emit it from its real pose. Client: send ONE reliable Free with full content so the
        /// pose and content commit converge and physics resumes with the streamed velocity.</summary>
        private void SettlePushed(InteractablePackagingBox box)
        {
            if (box == null)
                return;
            ClearPushEntries(box);
            if (CoopCore.Role == CoopRole.Host)
            {
                if (!_hostIdentity.TryGetId(box, out var hostId))
                    return;
                if (_leases.TryGetValue(hostId, out var lease) && lease.MotionDriven)
                {
                    lease.MotionDriven = false;
                    lease.Driver = NoOwner;
                    _leases[hostId] = lease;
                    // The driver's OWN box never went kinematic (local physics stayed the
                    // single truth while pushed), so no physics resume is needed here - and
                    // re-enabling would be wrong if the box was picked up mid-slide (the
                    // game's hold already switched physics off). Just re-emit the real pose.
                    _hostDirty.Add(hostId);
                }
                return;
            }
            var family = Family(FamilyOf(box));
            if (family == null || !_clientIdOf.ContainsKey(box))
                return;
            if (!family.TryReadLocal(box, out var poss, out var pos, out var yaw,
                    out var vel, out var angVel, out var stored))
                return;
            if (poss != BoxPossession.Free || stored)
                return; // the normal possession path will report the real state
            SendClientBoxReport(family, box, BoxPossession.Free, pos, yaw, vel, angVel,
                family.ContentSignature(box));
        }

        /// <summary>Host: a client is pushing a box. Make the host's real box a kinematic
        /// follower of the streamed pose (the stream is the single pose writer while fresh) and
        /// relay the motion to the other peers. Guard clauses reject frames that would fight a
        /// possession edge or a newer driver (first-claim wins within the motion window).</summary>
        public void HostApplyMotion(BoxMotionMessage msg, int connId)
        {
            if (msg == null)
                return;
            if (!BoxPlacement.IsSanePose(msg.Pos, msg.Yaw))
                return;
            var motionVelocity = msg.Velocity;
            var motionAngularVelocity = msg.AngularVelocity;
            BoxPlacement.SanitizeVelocity(ref motionVelocity, ref motionAngularVelocity);
            if (!_hostIdentity.ById.TryGetValue(msg.Id, out var box) || box == null)
            {
                BoxShared.DebugLog("box-motion-rx", $"id={msg.Id} sender={connId} reject=unknown-box", msg.Id, 0.5f);
                return; // unknown/removed box
            }
            Lease lease = _leases.TryGetValue(msg.Id, out var l) ? l : default(Lease);
            if (lease.Possession == BoxPossession.Held || lease.Possession == BoxPossession.Placing)
            {
                BoxShared.DebugLog("box-motion-rx", $"id={msg.Id} sender={connId} reject=poss={lease.Possession}", msg.Id, 0.5f);
                return; // in someone's hand; a push can't own it
            }
            var family = Family(FamilyOf(box));
            if (family != null
                && family.TryReadLocal(box, out _, out _, out _, out _, out _, out var stored)
                && stored)
            {
                BoxShared.DebugLog("box-motion-rx", $"id={msg.Id} sender={connId} reject=stored", msg.Id, 0.5f);
                return; // a racked (stored) box isn't pushed
            }
            if (_leaseClock < lease.MotionBlockedUntil)
            {
                BoxShared.DebugLog("box-motion-rx", $"id={msg.Id} sender={connId} reject=blocked", msg.Id, 0.5f);
                return; // a reliable edge just landed; absorb late transient frames
            }
            if (lease.MotionDriven && lease.Driver != connId
                && _leaseClock - lease.LastMotion < MotionLeaseTimeout)
            {
                BoxShared.DebugLog("box-motion-rx", $"id={msg.Id} sender={connId} reject=other-driver={lease.Driver}", msg.Id, 0.5f);
                return; // another player already drives it (first-claim wins)
            }
            BoxShared.DebugLog("box-motion-rx", $"id={msg.Id} sender={connId} accept pos={msg.Pos}", msg.Id, 0.5f);

            lease.MotionDriven = true;
            lease.Driver = connId;
            lease.LastMotion = _leaseClock;
            lease.MotionPos = msg.Pos;
            lease.MotionYaw = msg.Yaw;
            lease.MotionVel = motionVelocity;
            lease.MotionAngVel = motionAngularVelocity;
            _leases[msg.Id] = lease;

            // Kinematic follower: the motion stream is the single pose writer while it is fresh.
            BoxLifecycle.ApplyEnabled(box, false);
            BoxPlacement.ScheduleRemoteMotion(box, msg.Pos, msg.Yaw, motionVelocity, motionAngularVelocity);
            RelayMotion?.Invoke(new BoxMotionStateMessage
            {
                Id = msg.Id,
                DriverConn = connId,
                Pos = msg.Pos,
                Yaw = msg.Yaw,
                Velocity = motionVelocity,
                AngularVelocity = motionAngularVelocity,
            }, connId);
        }

        /// <summary>Client: a peer (or the host) is pushing a box. Smooth our mirror of it from the
        /// streamed pose (dead-reckon). The driver ignores its own relay; every other peer
        /// dead-reckons. The body is kept kinematic while the stream is fresh and resumes physics
        /// when it goes stale or a reliable Free snapshot arrives (see BoxPlacement).</summary>
        public void ClientApplyMotion(BoxMotionStateMessage msg)
        {
            if (msg == null)
                return;
            if (!BoxPlacement.IsSanePose(msg.Pos, msg.Yaw))
                return;
            var motionVelocity = msg.Velocity;
            var motionAngularVelocity = msg.AngularVelocity;
            BoxPlacement.SanitizeVelocity(ref motionVelocity, ref motionAngularVelocity);
            if (msg.DriverConn == CoopCore.LocalConnectionId)
                return; // my own push; I'm the driver (my box is the real local one)
            if (_clientGuards.TryGetValue(msg.Id, out var guard))
            {
                if (Time.time < guard.MotionUntil)
                    return;
                guard.MotionUntil = 0f;
            }
            if (!_clientById.TryGetValue(msg.Id, out var box) || box == null)
                return; // no local mirror for this id
            BoxPlacement.ScheduleRemoteMotion(box, msg.Pos, msg.Yaw, motionVelocity, motionAngularVelocity);
        }

        /// <summary>Drop the local push bookkeeping for one box (it is being forgotten/destroyed
        /// or has settled).</summary>
        private void ClearPushEntries(InteractablePackagingBox box)
        {
            if (box is null)
                return;
            _pushDriven.Remove(box);
            _pushSentAt.Remove(box);
            _pushSentPos.Remove(box);
            _pushSentYaw.Remove(box);
        }

        /// <summary>The configured network send rate (Hz), clamped to a sane floor. Shared by the
        /// player-state and box-push streams so a config change moves both.</summary>
        private static float SendRate()
        {
            float hz = CoopPlugin.SendRateHz != null ? CoopPlugin.SendRateHz.Value : 15f;
            return hz > 1f ? hz : 1f;
        }

        private IBoxFamily Family(BoxFamily f)
        {
            for (int i = 0; i < _families.Count; i++)
                if (_families[i].Family == f)
                    return _families[i];
            return null;
        }
    }
}
