using System;

namespace CardShopCoop.Sync
{
    internal sealed class AppearanceDelivery
    {
        private string _pending;
        private double _retryAt;

        internal void Sent(string signature, double now)
        {
            _pending = signature;
            _retryAt = now + 3;
        }

        internal void Acknowledge(string signature)
        {
            if (string.Equals(_pending, signature, StringComparison.Ordinal))
                _pending = null;
        }

        internal bool NeedsRetry(double now) => _pending != null && now >= _retryAt;
        internal void Reset() => _pending = null;
    }
}
