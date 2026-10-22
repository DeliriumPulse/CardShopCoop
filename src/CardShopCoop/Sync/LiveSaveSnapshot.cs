using System;
using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    internal static class LiveSaveSnapshot
    {
        // Build separately: an exception must not erase the previously valid save.
        // The live object's position in a registry is not an index into an older save.
        internal static List<TSave> Capture<TLive, TSave>(IEnumerable<TLive> live,
            Func<TLive, bool> exists, Func<TLive, TSave> capture)
        {
            var result = new List<TSave>();
            foreach (var item in live)
                if (exists(item))
                    result.Add(capture(item));
            return result;
        }
    }
}
