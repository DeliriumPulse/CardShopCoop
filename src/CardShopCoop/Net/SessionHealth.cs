using System;

namespace CardShopCoop.Net
{
    internal static class SessionHealth
    {
        internal static string Describe(int pendingTransfers, double oldestTransfer,
            double oldestQueuedSend, double stalledSend)
        {
            if (double.IsNaN(oldestTransfer) || double.IsInfinity(oldestTransfer)
                || double.IsNaN(oldestQueuedSend) || double.IsInfinity(oldestQueuedSend)
                || double.IsNaN(stalledSend) || double.IsInfinity(stalledSend))
                return "Connection progress diagnostics unavailable.";
            if (stalledSend >= 3)
                return "Sending is delayed; reliable messages are waiting for the connection.";
            if (pendingTransfers > 0 && oldestTransfer >= 15)
                return "Item sync is stalled; retrying the original requests. Open connection help if it persists.";
            if (oldestQueuedSend >= 3)
                return "Outgoing updates are delayed; the send queue is catching up.";
            if (pendingTransfers > 0)
                return "Items are syncing with the shared shop.";
            return "No stalled operations detected. This does not verify every shared object.";
        }
    }
}
