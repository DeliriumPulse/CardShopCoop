using System;
using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>Retains the original request until its result arrives. A retry must carry
    /// the same sequence and payload so the host's transfer-ack ledger can replay the
    /// first result instead of applying an item delta twice.</summary>
    internal sealed class TransferRetryQueue<TMessage>
    {
        private const double RetryAfterSeconds = 3.0;

        private sealed class Pending
        {
            public TMessage Message;
            public double StartedAt;
            public double LastSentAt;
        }

        private readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();

        public int Count => _pending.Count;
        public bool Contains(uint sequence) => _pending.ContainsKey(sequence);
        public int TotalRetries
        {
            get;
            private set;
        }

        public void Register(uint sequence, TMessage message, double now)
        {
            if (sequence != 0)
                _pending[sequence] = new Pending { Message = message, StartedAt = now, LastSentAt = now };
        }

        public double OldestAge(double now)
        {
            double oldest = now;
            foreach (var pending in _pending.Values)
                oldest = Math.Min(oldest, pending.StartedAt);
            return Math.Max(0, now - oldest);
        }

        public void Remove(uint sequence)
        {
            _pending.Remove(sequence);
        }

        public void RemoveWhere(Predicate<TMessage> predicate)
        {
            if (predicate == null)
                return;
            var remove = new List<uint>();
            foreach (var pair in _pending)
                if (predicate(pair.Value.Message))
                    remove.Add(pair.Key);
            for (int i = 0; i < remove.Count; i++)
                _pending.Remove(remove[i]);
        }

        public int ResendDue(double now, int budget, Action<uint, TMessage> send)
        {
            if (budget <= 0 || send == null)
                return 0;
            var due = new List<uint>(Math.Min(budget, _pending.Count));
            foreach (var pair in _pending)
            {
                if (now - pair.Value.LastSentAt < RetryAfterSeconds)
                    continue;
                due.Add(pair.Key);
                if (due.Count >= budget)
                    break;
            }
            for (int i = 0; i < due.Count; i++)
            {
                if (!_pending.TryGetValue(due[i], out var pending))
                    continue;
                pending.LastSentAt = now;
                send(due[i], pending.Message);
                TotalRetries++;
            }
            return due.Count;
        }

        public void Clear()
        {
            _pending.Clear();
            TotalRetries = 0;
        }
    }
}
