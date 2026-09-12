using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;

namespace CardShopCoop
{
    /// <summary>
    /// Central logger for exceptions that are deliberately contained (Unity object teardown,
    /// optional-mod reflection, best-effort cleanup). The project's policy is fail-fast, so an
    /// exception must never be swallowed silently; but several of these boundaries live in
    /// per-frame or per-object paths where a raw warning would flood the log. This helper
    /// records one line per call site per <see cref="CooldownMs"/> and captures the caller
    /// member/file automatically so every empty catch needs only <c>Swallow.Log(e);</c>.
    /// </summary>
    internal static class Swallow
    {
        private const int CooldownMs = 2000;

        private static readonly ConcurrentDictionary<string, int> LastLogged
            = new ConcurrentDictionary<string, int>();

        /// <summary>Logs a contained exception with caller context, rate-limited per call site.
        /// Safe to call from the net thread and before <see cref="CoopPlugin.Log"/> exists.</summary>
        public static void Log(Exception e,
            [CallerMemberName] string member = "",
            [CallerFilePath] string file = "")
        {
            if (e == null)
                return;

            string filePart;
            try
            {
                filePart = string.IsNullOrEmpty(file) ? "?" : Path.GetFileNameWithoutExtension(file);
            }
            catch
            {
                filePart = "?";
            }
            string site = filePart + "." + (member ?? "?");

            int now = Environment.TickCount;
            if (LastLogged.TryGetValue(site, out int last) && unchecked(now - last) < CooldownMs)
                return;
            LastLogged[site] = now;

            // Log may be null very early in plugin startup; never let diagnostics throw.
            try
            {
                CoopPlugin.Log?.LogWarning("[caught] " + site + ": " + e);
            }
            catch
            {
            }
        }
    }
}
