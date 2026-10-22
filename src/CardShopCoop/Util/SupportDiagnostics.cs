using System.Collections.Generic;

namespace CardShopCoop.Util
{
    internal sealed class RecoveryStatus
    {
        private double _lastRequest = double.NegativeInfinity;
        internal bool Pending
        {
            get; private set;
        }
        internal string Message { get; private set; } = "";
        internal bool CanRequest(double now) => !Pending && now - _lastRequest >= 10;

        internal bool Begin(double now)
        {
            if (!CanRequest(now))
                return false;
            _lastRequest = now;
            Pending = true;
            Message = "Refresh requested. Waiting for the host's object snapshot...";
            return true;
        }

        internal void ReceivedSnapshot()
        {
            if (!Pending)
                return;
            Pending = false;
            Message = "Host object snapshot received. Close the co-op window and try again. Pending item transfers resolve automatically when both sides are connected.";
        }

        internal bool Tick(double now)
        {
            if (!Pending || now - _lastRequest < 15)
                return false;
            Pending = false;
            Message = "No full object snapshot received within 15 seconds. Copy a connection report; if interactions remain stuck, leave and rejoin.";
            return true;
        }

        internal void Reset()
        {
            Pending = false;
            _lastRequest = double.NegativeInfinity;
            Message = "";
        }
    }

    // Log callbacks may arrive from worker threads. Keep report memory bounded.
    internal sealed class DiagnosticHistory
    {
        private readonly Queue<string> _lines = new Queue<string>();
        private readonly object _lock = new object();
        private string _last;
        internal void Add(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;
            line = line.Replace('\r', ' ').Replace('\n', ' ');
            if (line.Length > 800)
                line = line.Substring(0, 800) + "...";
            lock (_lock)
            {
                if (line == _last)
                    return;
                _last = line;
                if (_lines.Count == 12)
                    _lines.Dequeue();
                _lines.Enqueue(line);
            }
        }
        internal string[] Snapshot()
        {
            lock (_lock)
                return _lines.ToArray();
        }
    }
}
