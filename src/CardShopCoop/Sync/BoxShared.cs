using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Cross-module box state that belongs to none of the families: whether sync code is
    /// currently applying a remote change and the local-destroy notification.
    /// </summary>
    public static class BoxShared
    {
        /// <summary>True while sync code itself destroys/spawns boxes, so the OnDestroyed
        /// patch doesn't mistake reconciliation for a player throwing boxes away.</summary>
        public static bool ApplyingRemote;

        /// <summary>Wired by CoopCore to the OnDestroyed patch: a box was destroyed by
        /// LOCAL gameplay (trash bin, storage) - not by sync reconciliation.</summary>
        public static Action<InteractablePackagingBox> LocalBoxDestroyed;

        /// <summary>Diagnostics toggle (CoopPlugin "BoxSyncDebug"). When on, the box engine
        /// logs each client possession report, each host accept/reject, and each adopt/spawn.</summary>
        public static bool Debug => CoopPlugin.BoxSyncDebug != null && CoopPlugin.BoxSyncDebug.Value;

        private static readonly Dictionary<int, double> _debugLast = new Dictionary<int, double>();

        /// <summary>Throttled diagnostic line. key == 0 logs unthrottled; otherwise at most
        /// one line per <paramref name="throttle"/> seconds for that key (avoids per-frame spam).</summary>
        public static void DebugLog(string tag, string message, int key = 0, float throttle = 0.5f)
        {
            if (!Debug)
                return;
            try
            {
                if (key != 0 && throttle > 0f)
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (_debugLast.TryGetValue(key, out var last) && now - last < throttle)
                        return;
                    _debugLast[key] = now;
                }
                CoopPlugin.Log.LogInfo($"[{tag}] {message}");
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        public static void ResetDebugThrottles()
        {
            _debugLast.Clear();
        }

        /// <summary>True while ANY holder has this box in hand - the local player or a worker.
        /// The player-only IsLocallyCarried guard left worker-held boxes read as Free, so a
        /// client's mirror was dragged along the floor following the restocker instead of
        /// staying in the worker's hands. Q-drag placement does not set this flag, so
        /// BoxPossession.Placing is unaffected.</summary>
        public static bool IsHeldByAnyone(InteractablePackagingBox box)
        {
            if (box == null)
                return false;
            try
            {
                return BoxFields.BeingHold?.GetValue(box) is bool held && held;
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

    }
}
