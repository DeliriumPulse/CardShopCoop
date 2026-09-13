using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>One client-originated item transfer awaiting an authoritative result.
    /// TKey is the target identity (a box id or a shelf-compartment key).</summary>
    internal struct PendingTransfer<TKey>
    {
        public uint Seq;
        public TKey Target;
        public int RequestedDelta;
        public int TransferType; // local EItemType id
        public int EscrowToken;  // > 0 for a take, 0 for an add
    }

    /// <summary>
    /// Shared ledger for client-originated item transfers. Owns wire-sequence allocation,
    /// single-attempt wire sequence allocation, the take escrow token, and the pending-add
    /// reservation set, so a box and a shelf cannot drift apart in how they track the protocol.
    /// An entry leaves only on its one result, an explicit handler-fault abandonment, or teardown.
    /// </summary>
    internal sealed class PendingTransferLedger<TKey>
    {
        public const int MaxOutstanding = 256;
        private readonly Dictionary<uint, PendingTransfer<TKey>> _entries
            = new Dictionary<uint, PendingTransfer<TKey>>();
        private readonly HashSet<TKey> _pendingAdds = new HashSet<TKey>();
        private readonly HashSet<TKey> _pendingTakes = new HashSet<TKey>();
        private uint _seq;

        public int Count => _entries.Count;

        /// <summary>Record a new transfer. A negative delta escrows the just-taken items out
        /// of the hand; a positive delta reserves the target against authoritative content
        /// overwrites until the result arrives. Returns the wire sequence to send.</summary>
        public uint Begin(TKey target, int requestedDelta, int transferType, out int effectiveTransferType)
            => Begin(target, requestedDelta, transferType, out effectiveTransferType, true);

        public uint Begin(TKey target, int requestedDelta, int transferType, out int effectiveTransferType, bool escrowTake)
        {
            effectiveTransferType = transferType;
            if (_entries.Count >= MaxOutstanding)
            {
                CoopPlugin.Log.LogError($"PendingTransferLedger.Begin: outstanding limit {MaxOutstanding} reached; refusing transfer");
                return 0;
            }
            uint seq = ++_seq;
            if (seq == 0)
                seq = ++_seq; // 0 means "no transfer" on the wire
            int token = requestedDelta < 0 && escrowTake
                ? HandEscrow.ReserveTake(transferType, -requestedDelta, out effectiveTransferType)
                : 0;
            if (requestedDelta < 0 && escrowTake && token == 0)
            {
                CoopPlugin.Log.LogWarning($"PendingTransferLedger.Begin: take of {-requestedDelta} type {transferType} could not be reserved; refusing to track it");
                return 0;
            }
            if (requestedDelta > 0)
                _pendingAdds.Add(target);
            else if (requestedDelta < 0)
            {
                _pendingTakes.Add(target);
            }
            _entries[seq] = new PendingTransfer<TKey>
            {
                Seq = seq,
                Target = target,
                RequestedDelta = requestedDelta,
                TransferType = effectiveTransferType,
                EscrowToken = token,
            };
            return seq;
        }

        /// <summary>Remove and return the entry for a result. False when unknown. Releases
        /// the add reservation for the target when no other add still targets it.</summary>
        public bool TryResolve(uint seq, out PendingTransfer<TKey> entry)
        {
            if (!_entries.TryGetValue(seq, out entry))
                return false;
            _entries.Remove(seq);
            if (entry.RequestedDelta < 0)
            {
                ReleaseTake(entry.Target);
            }
            ReleaseAdd(entry.Target);
            return true;
        }

        /// <summary>True while an add for this target is unresolved; authoritative content
        /// must not overwrite it.</summary>
        public bool IsAddReserved(TKey target)
        {
            return _pendingAdds.Contains(target);
        }

        /// <summary>True while a take for this target is unresolved. Authoritative content
        /// must not repaint the container to its pre-take count while the taken item is still
        /// escrowed in the hand, or the item exists in both places.</summary>
        public bool IsTakeReserved(TKey target)
        {
            return _pendingTakes.Contains(target);
        }

        public bool TryGet(uint seq, out PendingTransfer<TKey> entry) => _entries.TryGetValue(seq, out entry);

        /// <summary>Resolve the escrow of a take from the host's accepted delta.</summary>
        public bool ResolveTake(in PendingTransfer<TKey> entry, int acceptedDelta)
        {
            return HandEscrow.ResolveTake(entry.EscrowToken, Mathf.Max(0, -acceptedDelta));
        }

        /// <summary>Drop the add reservation for a target once no live add targets it.</summary>
        public void ReleaseAdd(TKey target)
        {
            foreach (var entry in _entries.Values)
                if (EqualityComparer<TKey>.Default.Equals(entry.Target, target)
                    && entry.RequestedDelta > 0)
                    return;
            _pendingAdds.Remove(target);
        }

        /// <summary>Drop the take reservation for a target once no live take targets it.</summary>
        public void ReleaseTake(TKey target)
        {
            foreach (var entry in _entries.Values)
                if (EqualityComparer<TKey>.Default.Equals(entry.Target, target)
                    && entry.RequestedDelta < 0)
                    return;
            _pendingTakes.Remove(target);
        }

        /// <summary>Abandon one outstanding transfer after a client-side handler fault.
        /// This only releases reservations and escrow; it never destroys an item.</summary>
        public bool Abandon(uint seq)
        {
            if (!_entries.TryGetValue(seq, out var entry))
                return false;
            _entries.Remove(seq);
            if (entry.RequestedDelta < 0)
                HandEscrow.ExpireTake(entry.EscrowToken);
            ReleaseTake(entry.Target);
            ReleaseAdd(entry.Target);
            return true;
        }

        public void AbandonTarget(TKey target)
        {
            var sequences = new List<uint>();
            foreach (var entry in _entries)
                if (EqualityComparer<TKey>.Default.Equals(entry.Value.Target, target))
                    sequences.Add(entry.Key);
            for (int i = 0; i < sequences.Count; i++)
                Abandon(sequences[i]);
        }

        /// <summary>Session/scene teardown: optimistically release takes and drop all
        /// bookkeeping. Does not discard a live item (HandEscrow owns that decision).</summary>
        public void Clear()
        {
            foreach (var entry in _entries.Values)
                if (entry.RequestedDelta < 0)
                    HandEscrow.ExpireTake(entry.EscrowToken);
            _entries.Clear();
            _pendingAdds.Clear();
            _pendingTakes.Clear();
            // Deliberately do NOT reset _seq: the host keeps its (connId, seq) ack map across a
            // client scene reload, so reusing sequence numbers could collide with a stale ack
            // and silently drop a later transfer.
        }
    }
}
