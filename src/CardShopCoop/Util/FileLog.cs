using System;
using System.Diagnostics;
using System.IO;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Per-process log file (BepInEx/CardShopCoop_{pid}.log). BepInEx's LogOutput.log is
    /// exclusive to the first game instance, so when two instances run on one PC (loopback
    /// testing, or two family members on one machine some day) this keeps each side readable.
    /// </summary>
    public static class FileLog
    {
        private static StreamWriter _writer;
        private static readonly object Lock = new object();
        private static int _lastFlushTick;

        public static string Path { get; private set; }

        public static void Init(string gameRoot)
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                Path = System.IO.Path.Combine(gameRoot, "BepInEx", $"CardShopCoop_{pid}.log");
                // no AutoFlush: an OS flush per line stalls the main thread under disk or
                // antivirus pressure, so lines are batched and flushed at most once/second
                _writer = new StreamWriter(Path, append: false);
                AppDomain.CurrentDomain.ProcessExit += (_, __) => Flush();
                Write("log started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Flush();
            }
            catch
            {
                _writer = null; // never let logging take the game down
            }
        }

        public static void Write(string line)
        {
            if (_writer == null) return;
            lock (Lock)
            {
                try
                {
                    _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
                    // unchecked int math survives TickCount wraparound (~25 days)
                    int now = Environment.TickCount;
                    if (now - _lastFlushTick > 1000)
                    {
                        _writer.Flush();
                        _lastFlushTick = now;
                    }
                }
                catch { }
            }
        }

        private static void Flush()
        {
            lock (Lock)
            {
                try { _writer?.Flush(); } catch { }
            }
        }
    }
}
