using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CardShopCoop.Net;

namespace CardShopCoop.Util
{
    /// <summary>Bounded, payload-free protocol activity history for support reports.</summary>
    internal static class MessageTrafficDiagnostics
    {
        private const int Capacity = 2048;
        private const double WindowSeconds = 60.0;
        private static readonly object Gate = new object();
        private static readonly Sample[] Samples = new Sample[Capacity];
        private static int _next;
        private static int _count;

        private struct Sample
        {
            internal long Timestamp;
            internal MsgType Type;
            internal int Bytes;
            internal bool Sent;
        }

        internal static void Record(bool sent, MsgType type, int bytes)
        {
            lock (Gate)
            {
                Samples[_next] = new Sample
                {
                    Timestamp = Stopwatch.GetTimestamp(),
                    Type = type,
                    Bytes = Math.Max(0, bytes),
                    Sent = sent
                };
                _next = (_next + 1) % Capacity;
                if (_count < Capacity)
                    _count++;
            }
        }

        internal static void AppendTo(StringBuilder text)
        {
            long now = Stopwatch.GetTimestamp();
            long window = (long)(WindowSeconds * Stopwatch.Frequency);
            var sentCount = new int[256];
            var receivedCount = new int[256];
            var sentBytes = new long[256];
            var receivedBytes = new long[256];
            int considered = 0;
            double oldest = 0;

            lock (Gate)
            {
                for (int i = 0; i < _count; i++)
                {
                    int index = (_next - 1 - i + Capacity) % Capacity;
                    Sample sample = Samples[index];
                    long age = now - sample.Timestamp;
                    if (age > window)
                        break;
                    int type = (byte)sample.Type;
                    if (sample.Sent)
                    {
                        sentCount[type]++;
                        sentBytes[type] += sample.Bytes;
                    }
                    else
                    {
                        receivedCount[type]++;
                        receivedBytes[type] += sample.Bytes;
                    }
                    considered++;
                    oldest = Math.Max(oldest, (double)age / Stopwatch.Frequency);
                }
            }

            text.AppendLine("Protocol traffic (last 60s; decoded/encoded messages only, no payloads): ");
            text.AppendLine("  Samples in window: " + considered + (considered == Capacity ? " (ring full; older traffic may be omitted)" : ""));
            text.AppendLine("  Oldest included sample: " + oldest.ToString("F1", CultureInfo.InvariantCulture) + "s");
            bool any = false;
            for (int i = 0; i < 256; i++)
            {
                if (sentCount[i] == 0 && receivedCount[i] == 0)
                    continue;
                any = true;
                MsgType type = (MsgType)(byte)i;
                text.Append("  ").Append(type).Append(" (type ").Append(i).Append("): tx=")
                    .Append(sentCount[i]).Append('/').Append(sentBytes[i]).Append("B rx=")
                    .Append(receivedCount[i]).Append('/').Append(receivedBytes[i]).AppendLine("B");
            }
            if (!any)
                text.AppendLine("  No protocol messages recorded in this process window.");
        }
    }
}
