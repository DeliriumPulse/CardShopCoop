using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Lightweight per-frame stage timing. When CoopPlugin.PerfDebug is on, Measure() times
    /// one sync stage and logs a single line when it exceeds ThresholdMs (rate-limited per
    /// stage) so a real session produces a short list of the actual lag spikes instead of a
    /// flood of numbers.
    /// </summary>
    internal static class PerfProbe
    {
        private const double ThresholdMs = 5.0;
        private const double RepeatSeconds = 2.0;

        private static readonly Dictionary<string, double> _lastLog = new Dictionary<string, double>();

        public static bool Enabled
        {
            get
            {
                try
                {
                    return CoopPlugin.PerfDebug != null && CoopPlugin.PerfDebug.Value;
                }
                catch (System.Exception e) { Swallow.Log(e); return false; }
            }
        }

        /// <summary>Run <paramref name="action"/> and, when probing, log it if it runs long.
        /// Exceptions propagate to the caller (CoopCore.Guarded handles them).</summary>
        public static void Measure(string stage, Action action)
        {
            if (!Enabled)
            {
                action();
                return;
            }
            long start = Stopwatch.GetTimestamp();
            try
            {
                action();
            }
            finally
            {
                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (ms >= ThresholdMs)
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (!_lastLog.TryGetValue(stage, out var last) || now - last >= RepeatSeconds)
                    {
                        _lastLog[stage] = now;
                        CoopPlugin.Log.LogWarning($"[perf] {stage} took {ms:F1} ms");
                    }
                }
            }
        }

        public static void Reset()
        {
            _lastLog.Clear();
        }

        /// <summary>Allocation-free variant for call sites that can't pass a delegate:
        /// Start() returns a token (0 when probing is off) and End() logs the span.</summary>
        public static long Start()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void End(string stage, long start)
        {
            if (start == 0L)
                return;
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms < ThresholdMs)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (_lastLog.TryGetValue(stage, out var last) && now - last < RepeatSeconds)
                return;
            _lastLog[stage] = now;
            CoopPlugin.Log.LogWarning($"[perf] {stage} took {ms:F1} ms");
        }
    }
}
