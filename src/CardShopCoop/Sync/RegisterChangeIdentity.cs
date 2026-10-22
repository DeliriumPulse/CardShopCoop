using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    internal static class RegisterChangeIdentity
    {
        // Bills and coins can share a display index. The wire identifies the actual
        // control in the full prefab list, never its visual index or rounded value.
        public static int Slot<T>(IList<T> drawer, T clicked) where T : class
        {
            if (drawer == null || clicked == null)
                return -1;
            for (int i = 0; i < drawer.Count; i++)
                if (ReferenceEquals(drawer[i], clicked))
                    return i;
            return -1;
        }

        public static bool Changed(int before, int after, bool takingBack)
            => before >= 0 && after >= 0 && after <= 100
                && after == before + (takingBack ? -1 : 1);
    }
}
