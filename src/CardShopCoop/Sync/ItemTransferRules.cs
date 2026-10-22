using System;

namespace CardShopCoop.Sync
{
    internal static class ItemTransferRules
    {
        internal static bool TryDelta(int before, int after, out int delta)
        {
            delta = 0;
            if (before < 0 || after < 0)
                return false;
            delta = after - before;
            return delta != 0;
        }

        internal static bool ValidResult<TKey>(TKey expected, TKey actual, int requested, int accepted)
        {
            if (!System.Collections.Generic.EqualityComparer<TKey>.Default.Equals(expected, actual))
                return false;
            return requested < 0 ? accepted >= requested && accepted <= 0
                : requested > 0 && accepted >= 0 && accepted <= requested;
        }

        // Counts in a request describe one operation, not an instruction to replace
        // current stock. Concurrent customer/worker/player changes must compose.
        internal static bool TryPlan(int currentCount, int currentType, int moveType,
            int requested, int capacity, out int accepted, out int targetType)
        {
            accepted = 0;
            targetType = currentType;
            if (currentCount < 0 || moveType < 0 || requested == 0)
                return false;
            if (requested < 0)
            {
                if (currentCount > 0 && currentType != moveType)
                    return false;
                accepted = -(int)Math.Min(-(long)requested, currentCount);
                return true;
            }
            if (currentCount > 0 && currentType != moveType)
                return false;
            long room = capacity > 0 ? Math.Max(0L, (long)capacity - currentCount)
                : int.MaxValue - (long)currentCount;
            accepted = (int)Math.Min(requested, room);
            if (currentCount == 0)
                targetType = moveType;
            return true;
        }
    }
}
