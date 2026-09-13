using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// One reusable clipboard button. It draws the supplied label, and for a couple of seconds
    /// after a click draws "Copied!" instead before reverting on its own - no per-button flag or
    /// timer bookkeeping at the call site. If the value it would copy changes, the confirmation
    /// is cleared immediately, so a stale "Copied!" can never describe different content.
    /// </summary>
    public sealed class CopyButton
    {
        private const float RevertSeconds = 2.5f;

        private string _seen;
        private float _revertAt;

        /// <summary>Draws the button; returns true on the frame it is clicked. The copied value
        /// is written to the system clipboard when that happens.</summary>
        public bool Draw(string label, string value, GUIStyle style, params GUILayoutOption[] options)
        {
            if (!string.Equals(value, _seen))
            {
                _seen = value;
                _revertAt = 0f;
            }

            bool copied = Time.realtimeSinceStartup < _revertAt;
            if (GUILayout.Button(copied ? "Copied!" : label, style, options))
            {
                GUIUtility.systemCopyBuffer = value ?? string.Empty;
                _revertAt = Time.realtimeSinceStartup + RevertSeconds;
                return true;
            }

            return false;
        }
    }
}
