using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    // Reliable operations are ordered per connection. Record before executing them:
    // a partial vanilla failure must not replay a money mutation a second time.
    internal sealed class RegisterOperationLedger
    {
        private readonly Dictionary<int, long> _last = new Dictionary<int, long>();

        internal bool Accept(int connection, long sequence)
        {
            if (sequence <= 0 || (_last.TryGetValue(connection, out long last) && sequence <= last))
                return false;
            _last[connection] = sequence;
            return true;
        }

        internal void Clear() => _last.Clear();
    }
}
