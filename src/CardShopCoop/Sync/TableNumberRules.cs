using System;
using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    internal enum TableNumberAction
    {
        Set, Auto, Clear
    }

    /// <summary>Compute one whole edit from the host's current layout, including swaps and compaction.</summary>
    internal static class TableNumberRules
    {
        internal static int[] Plan(IReadOnlyList<int> numbers, int target, TableNumberAction action,
            int requested, bool tournament)
        {
            if (numbers == null || target < 0 || target >= numbers.Count)
                return null;
            int old = numbers[target], highest = 0;
            for (int i = 0; i < numbers.Count; i++)
                highest = Math.Max(highest, numbers[i]);
            if (action == TableNumberAction.Auto && old > 0)
                return null;
            int number = action == TableNumberAction.Clear ? 0
                : action == TableNumberAction.Auto ? highest + 1 : requested;
            if (number < 0 || number > 99 || number > highest + (old == 0 ? 1 : 0)
                || (tournament && old > 0 && old != number))
                return null;
            var result = new int[numbers.Count];
            for (int i = 0; i < numbers.Count; i++)
                result[i] = numbers[i];
            if (action == TableNumberAction.Clear)
            {
                if (old > 0)
                    for (int i = 0; i < result.Length; i++)
                        if (result[i] > old)
                            result[i]--;
            }
            else if (number > 0 && number != old)
            {
                for (int i = 0; i < result.Length; i++)
                    if (i != target && result[i] == number)
                    {
                        // Numbered tournament tables cannot be displaced indirectly either.
                        if (tournament)
                            return null;
                        result[i] = old;
                        break;
                    }
            }
            result[target] = number;
            return result;
        }
    }
}
