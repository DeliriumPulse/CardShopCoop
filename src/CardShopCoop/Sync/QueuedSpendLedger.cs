using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CardShopCoop.Sync
{
    // Native coin events can wait for several frames when CEventManager limits
    // queue processing. Keep their debit reserved until the wallet applies it.
    internal sealed class QueuedSpendLedger
    {
        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private readonly Dictionary<object, Queue<double>> _queued = new Dictionary<object, Queue<double>>(new IdentityComparer());
        internal double Total
        {
            get; private set;
        }
        internal int Count
        {
            get; private set;
        }
        internal double Available(double balance, double unqueuedReservation) => balance - unqueuedReservation - Total;

        internal bool Track(object coinEvent, double amount)
        {
            if (coinEvent == null || amount <= 0 || double.IsNaN(amount) || double.IsInfinity(amount))
                return false;
            if (!_queued.TryGetValue(coinEvent, out var amounts))
                _queued[coinEvent] = amounts = new Queue<double>();
            amounts.Enqueue(amount);
            Count++;
            Total += amount;
            return true;
        }

        internal void Applied(object coinEvent)
        {
            if (coinEvent == null || !_queued.TryGetValue(coinEvent, out var amounts))
                return;
            double amount = amounts.Dequeue();
            Count--;
            if (amounts.Count == 0)
                _queued.Remove(coinEvent);
            Total = Math.Max(0, Total - amount);
        }

        internal void Clear()
        {
            _queued.Clear();
            Total = 0;
            Count = 0;
        }
    }
}
