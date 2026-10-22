using System;

namespace CardShopCoop.Sync
{
    internal static class RemoteAudioRules
    {
        internal static bool IsFresh(float receivedAt, float now)
        {
            float age = now - receivedAt;
            return age >= 0f && age < 1f;
        }

        internal static float Volume(float configured, bool muted)
            => muted || float.IsNaN(configured) ? 0f : Math.Max(0f, Math.Min(1f, configured));

        internal static bool AllowCheckoutSound(bool restoringCart, string clip)
            => !restoringCart || (clip != "SFX_CheckoutScan" && clip != "SFX_CheckoutRegisterOpen"
                && clip != "SFX_CheckoutDone" && clip != "SFX_WhipSoft");
    }
}
