namespace CardShopCoop.Net
{
    /// <summary>Only a live Steam session with fresh incoming packets can overrule a
    /// timeout callback. Steam can deliver a failure from an older session after the
    /// same friend has already reconnected under a new local connection id.</summary>
    internal static class SteamTimeoutDecision
    {
        internal static bool KeepCurrentPeer(bool isTimeout, bool stateAvailable,
            bool sessionActive, byte sessionError, double secondsSinceLastReceive)
        {
            return isTimeout && stateAvailable && sessionActive && sessionError == 0
                && secondsSinceLastReceive >= 0 && secondsSinceLastReceive <= 10.0;
        }
    }
}
