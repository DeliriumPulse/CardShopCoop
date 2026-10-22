using System;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop.Sync
{
    internal static class NpcAnimationPlayback
    {
        // Parameters alone cannot recover a controller reset while hidden, or
        // transitions driven by triggers in the host's disabled-on-guests AI.
        internal static NpcAnimationLayer[] Capture(Animator animator)
        {
            if (animator == null || !animator.isInitialized)
                return null;
            int count = Math.Min(animator.layerCount, 8);
            var layers = new NpcAnimationLayer[count];
            for (int i = 0; i < count; i++)
            {
                var state = animator.IsInTransition(i)
                    ? animator.GetNextAnimatorStateInfo(i) : animator.GetCurrentAnimatorStateInfo(i);
                layers[i] = new NpcAnimationLayer
                {
                    State = state.fullPathHash,
                    Time = state.normalizedTime,
                    Weight = animator.GetLayerWeight(i)
                };
            }
            return layers;
        }

        internal static void Apply(Animator animator, NpcAnimationLayer[] layers)
        {
            if (animator == null || !animator.isActiveAndEnabled || !animator.isInitialized || layers == null)
                return;
            int count = Math.Min(Math.Min(animator.layerCount, layers.Length), 8);
            for (int i = 0; i < count; i++)
            {
                var layer = layers[i];
                if (layer == null || layer.State == 0 || float.IsNaN(layer.Time) || float.IsInfinity(layer.Time)
                    || float.IsNaN(layer.Weight) || float.IsInfinity(layer.Weight))
                    continue;
                animator.SetLayerWeight(i, Mathf.Clamp01(layer.Weight));
                if (!animator.HasState(i, layer.State))
                    continue; // a replacement model can have a different controller
                var current = animator.GetCurrentAnimatorStateInfo(i);
                if (current.fullPathHash == layer.State
                    || (animator.IsInTransition(i) && animator.GetNextAnimatorStateInfo(i).fullPathHash == layer.State))
                    continue; // keep playback continuous, rather than restarting every snapshot
                animator.CrossFade(layer.State, 0.1f, i, Mathf.Max(0f, layer.Time));
            }
        }
    }
}
