using System;

namespace CardShopCoop.Sync
{
    internal static class BoxRecoveryRules
    {
        internal static bool CanRefreshHeldContents(bool sameOwner, bool itemBox,
            bool hasBaseline, bool localEdit, bool pendingTransfer)
            => sameOwner && itemBox && hasBaseline && !localEdit && !pendingTransfer;

        // Do not predict through an entire motion lease after a dropped packet.
        internal static float PredictionSeconds(float elapsed)
            => float.IsNaN(elapsed) ? 0f : Math.Max(0f, Math.Min(0.1f, elapsed));
    }
}
