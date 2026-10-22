using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    internal sealed class HostTransferAcks
    {
        private const int Capacity = 8192;
        private readonly Dictionary<long, int> _acks = new Dictionary<long, int>();
        private readonly Queue<long> _fifo = new Queue<long>();
        private readonly Dictionary<int, uint> _expiredThrough = new Dictionary<int, uint>();

        private static long Key(int connId, uint seq) => ((long)connId << 32) | seq;

        public bool TryGet(int connId, uint seq, out int acceptedDelta)
            => _acks.TryGetValue(Key(connId, seq), out acceptedDelta);

        public bool IsExpired(int connId, uint seq)
            => seq != 0 && _expiredThrough.TryGetValue(connId, out uint floor) && seq <= floor
                && !_acks.ContainsKey(Key(connId, seq));

        public void Store(int connId, uint seq, int acceptedDelta)
        {
            long key = Key(connId, seq);
            if (_acks.ContainsKey(key))
                return; // first result wins; a retry must replay the same value, not overwrite it
            _acks.Add(key, acceptedDelta);
            _fifo.Enqueue(key);
            CoopPlugin.Log.LogDebug($"HostTransferAcks.Store: conn={connId} seq={seq} accepted={acceptedDelta}");
            while (_fifo.Count > Capacity)
            {
                long expired = _fifo.Dequeue();
                _acks.Remove(expired);
                int peer = (int)(expired >> 32);
                uint sequence = (uint)expired;
                if (!_expiredThrough.TryGetValue(peer, out uint floor) || sequence > floor)
                    _expiredThrough[peer] = sequence;
            }
        }

        public void ReleaseConn(int connId)
        {
            _expiredThrough.Remove(connId);
            var keep = new Queue<long>();
            while (_fifo.Count > 0)
            {
                long key = _fifo.Dequeue();
                if ((int)(key >> 32) == connId)
                    _acks.Remove(key);
                else
                    keep.Enqueue(key);
            }
            while (keep.Count > 0)
                _fifo.Enqueue(keep.Dequeue());
        }

        public void Clear()
        {
            _acks.Clear();
            _fifo.Clear();
            _expiredThrough.Clear();
        }
    }
}
