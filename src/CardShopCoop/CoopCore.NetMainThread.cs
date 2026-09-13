using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        private void QueueMainThread(string stage, Action action, bool retryable)
        {
            if (action == null)
                throw new ArgumentNullException("action");
            var work = new MainThreadWork(stage, action, retryable);
            _mainThread.Enqueue(() => RunMainThread(work));
        }

        private void RunMainThread(MainThreadWork work)
        {
            if (Time.frameCount < work.NotBeforeFrame)
            {
                _mainThread.Enqueue(() => RunMainThread(work));
                return;
            }
            try
            {
                work.Action();
            }
            catch (Exception e)
            {
                work.Attempts++;
                CoopPlugin.Log.LogError($"main-thread action '{work.Stage}' failed (attempt {work.Attempts}): {e}");
                if (work.Retryable && work.Attempts <= MaxDispatchRetries)
                {
                    work.NotBeforeFrame = Time.frameCount + 1 + work.Attempts;
                    _mainThread.Enqueue(() => RunMainThread(work));
                }
                else
                    CoopPlugin.Log.LogError($"main-thread action '{work.Stage}' abandoned after {work.Attempts} attempt(s)");
            }
        }

        /// <summary>What one queued message costs against DispatchBudget. Everything is 1 unit
        /// except a CardDeltaBatch, which carries up to CardDeltaBatchMax card applies behind a
        /// single message - charging it 1 made the budget bound message COUNT, not work. Read
        /// off the decoded DTO so nothing is deserialized twice; anything malformed falls back
        /// to 1 and the handler's own bogus-count guard drops it.</summary>
        private static int DispatchCost(InMsg m)
        {
            if (m.Type != MsgType.CardDeltaBatch)
                return 1;
            int n = m.Message is CardDeltaBatchMessage batch ? batch.Deltas?.Count ?? 0 : 0;
            if (n < 1)
                return 1;
            return n > CardDeltaBatchMax ? CardDeltaBatchMax : n;
        }

        internal static void EnqueueMainThread(Action action)
        {
            if (action == null)
                return;
            if (!TryEnqueueMainThread(action))
                throw new InvalidOperationException("CoopCore is not running");
        }

        internal static bool TryEnqueueMainThread(Action action)
        {
            if (action == null)
                return false;
            var core = Instance;
            if (core == null)
                return false;
            core.QueueMainThread("external-main-thread", action, false);
            return true;
        }

        /// <summary>Host: relay a customer speech bubble after vanilla has actually
        /// displayed it. Speech is a one-shot cosmetic event, so it uses the reliable lane.</summary>
        public static bool TryGetLocalPlayerPosition(out Vector3 position)
        {
            position = default(Vector3);
            var core = Instance;
            var player = core != null ? core.ResolvePlayer() : null;
            if (player == null)
                return false;
            position = player.position;
            return true;
        }

        // what the local player is carrying (private fields; the game has no public API)
        internal static bool NativeTextInputFocused()
        {
            var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (sel == null)
                return false;
            var tmp = sel.GetComponent<TMPro.TMP_InputField>();
            return tmp != null && tmp.isFocused;
        }

    }
}
