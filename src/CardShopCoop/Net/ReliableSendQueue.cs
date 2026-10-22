using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CardShopCoop.Net
{
    public sealed class PeerQueueStatus
    {
        public int ConnectionId;
        public int Frames;
        public long Bytes;
        public double OldestSeconds;
        public double StalledSeconds;
        public bool Overflowed;
    }

    public interface ITransportQueueDiagnostics
    {
        List<PeerQueueStatus> GetSendQueueStatus();
    }

    // Preserve FIFO within each peer. A refused Steam send stops only that peer;
    // it cannot hold another player's checkout or transfer reply behind it.
    internal sealed class ReliableSendQueue
    {
        private const int MaxFramesPerPeer = 8192;
        private const long MaxBytesPerPeer = 256L * 1024 * 1024;
        private sealed class Frame
        {
            internal byte[] Data;
            internal double At;
        }
        private sealed class Peer
        {
            internal readonly Queue<Frame> Frames = new Queue<Frame>();
            internal long Bytes;
            internal double? StalledAt;
            internal bool Overflowed;
        }
        private readonly object _sync = new object();
        private readonly Dictionary<int, Peer> _peers = new Dictionary<int, Peer>();
        private readonly Func<double> _now;
        private int _cursor;

        internal ReliableSendQueue(Func<double> now = null)
        {
            _now = now ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        }

        internal void Enqueue(int connection, byte[] data)
        {
            if (data == null || data.Length == 0)
                return;
            lock (_sync)
            {
                if (!_peers.TryGetValue(connection, out var peer))
                    _peers[connection] = peer = new Peer();
                if (peer.Overflowed || peer.Frames.Count >= MaxFramesPerPeer
                    || peer.Bytes + data.Length > MaxBytesPerPeer)
                {
                    peer.Overflowed = true;
                    return; // Pump explicitly disconnects before sending more of this session.
                }
                peer.Frames.Enqueue(new Frame { Data = data, At = _now() });
                peer.Bytes += data.Length;
            }
        }

        internal int Pump(int byteBudget, int perPeerBudget, Func<int, byte[], bool> send,
            Action<int, string> failed)
        {
            if (byteBudget <= 0 || perPeerBudget <= 0 || send == null)
                return 0;
            List<int> connections;
            lock (_sync)
            {
                connections = new List<int>(_peers.Keys);
            }
            if (connections.Count == 0)
                return 0;
            connections.Sort();
            int start = _cursor % connections.Count;
            _cursor = (start + 1) % connections.Count;
            int sentBytes = 0;
            for (int i = 0; i < connections.Count && byteBudget > 0; i++)
            {
                int connection = connections[(start + i) % connections.Count];
                int allowance = Math.Min(perPeerBudget, byteBudget);
                while (allowance > 0 && byteBudget > 0)
                {
                    Peer peer;
                    Frame frame;
                    lock (_sync)
                    {
                        if (!_peers.TryGetValue(connection, out peer))
                            break;
                        frame = peer.Frames.Count == 0 ? null : peer.Frames.Peek();
                    }
                    if (peer.Overflowed)
                    {
                        RemovePeer(connection);
                        failed?.Invoke(connection, "reliable queue exceeded its per-peer limit");
                        break;
                    }
                    if (frame == null)
                        break;
                    bool sent;
                    try
                    {
                        sent = send(connection, frame.Data);
                    }
                    catch (Exception e)
                    {
                        RemovePeer(connection);
                        failed?.Invoke(connection, "reliable send threw " + e.GetType().Name);
                        break;
                    }
                    bool timedOut = false;
                    lock (_sync)
                    {
                        // Kick/Stop may have removed this peer during the send callback.
                        if (!_peers.TryGetValue(connection, out var current) || !ReferenceEquals(current, peer))
                            break;
                        if (sent)
                        {
                            peer.Frames.Dequeue();
                            peer.Bytes -= frame.Data.Length;
                            peer.StalledAt = null;
                            if (peer.Frames.Count == 0 && !peer.Overflowed)
                                _peers.Remove(connection);
                        }
                        else
                        {
                            if (!peer.StalledAt.HasValue)
                                peer.StalledAt = _now();
                            timedOut = _now() - peer.StalledAt.Value >= 15;
                        }
                    }
                    if (!sent)
                    {
                        if (timedOut)
                        {
                            RemovePeer(connection);
                            failed?.Invoke(connection, "reliable send stalled for 15s");
                        }
                        break;
                    }
                    sentBytes += frame.Data.Length;
                    byteBudget -= frame.Data.Length;
                    allowance -= frame.Data.Length;
                }
            }
            return sentBytes;
        }

        internal List<PeerQueueStatus> Snapshot()
        {
            double now = _now();
            var result = new List<PeerQueueStatus>();
            lock (_sync)
                foreach (var pair in _peers)
                {
                    var peer = pair.Value;
                    result.Add(new PeerQueueStatus
                    {
                        ConnectionId = pair.Key,
                        Frames = peer.Frames.Count,
                        Bytes = peer.Bytes,
                        OldestSeconds = peer.Frames.Count == 0 ? 0 : Math.Max(0, now - peer.Frames.Peek().At),
                        StalledSeconds = peer.StalledAt.HasValue ? Math.Max(0, now - peer.StalledAt.Value) : 0,
                        Overflowed = peer.Overflowed
                    });
                }
            return result;
        }

        internal void RemovePeer(int connection)
        {
            lock (_sync)
            {
                _peers.Remove(connection);
            }
        }

        internal void Clear()
        {
            lock (_sync)
            {
                _peers.Clear();
                _cursor = 0;
            }
        }
    }
}
