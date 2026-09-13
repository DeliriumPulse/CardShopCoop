using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace CardShopCoop.Net
{
    /// <summary>
    /// TESTING DECORATOR around a real transport. Delays every message this process
    /// RECEIVES by CoopPlugin.ArtificialLagMs, plus a per-message random +/- of
    /// CoopPlugin.ArtificialJitterMs. It wraps both the LAN TCP and Steam transports, so a
    /// lag test behaves the same either way, and it reads the config live so the SETTINGS
    /// sliders take effect without restarting.
    ///
    /// Inbound-only by design: with the same value on both peers each hop adds one delay,
    /// so the perceived round trip is roughly twice the configured value. Frames are
    /// released in arrival order (FIFO), matching ordered/TCP delivery: a frame whose
    /// release time is still ahead holds the ones behind it, exactly like a slow serial
    /// link. With both settings at 0 the decorator is a transparent pass-through with one
    /// queue hop per frame. Config values are clamped to 0-60s and the delay buffer has a
    /// hard frame cap, so a hand-edited config cannot throw or grow memory without bound.
    /// SecondsSinceLastRecv/TimeoutSeconds deliberately pass through to the inner transport:
    /// artificial lag must not trip the peer-silence timeout, so those timers follow real
    /// wire receipt rather than delayed delivery.
    /// </summary>
    public sealed class LagTransport : ICoopTransport
    {
        private struct Pending
        {
            public long ReleaseAt; // Stopwatch timestamp
            public InMsg Message;
        }

        // Hand-edited configs can exceed the slider bounds; clamp to a sane ceiling and cap
        // the buffer so a pathological value can never grow memory without bound.
        private const int MaxDelayMs = 60000;
        private const int MaxBufferedFrames = 20000;
        private const long MaxBufferedBytes = 64L * 1024 * 1024;
        private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        private readonly ICoopTransport _inner;
        private readonly ConcurrentQueue<InMsg> _incoming = new ConcurrentQueue<InMsg>();
        private readonly Queue<Pending> _delay = new Queue<Pending>();
        private readonly Random _random = new Random();
        private bool _overflowWarned;
        private long _delayBytes;

        private LagTransport(ICoopTransport inner)
        {
            if (inner == null)
                throw new ArgumentNullException("inner");
            _inner = inner;
        }

        /// <summary>Wrap a freshly created transport. Every CoopCore._net assignment goes
        /// through here so lag applies to LAN and Steam alike.</summary>
        public static ICoopTransport Wrap(ICoopTransport inner)
        {
            return inner == null ? null : new LagTransport(inner);
        }

        public ConcurrentQueue<InMsg> Incoming
        {
            get
            {
                return _incoming;
            }
        }

        public ConcurrentQueue<int> Disconnects
        {
            get
            {
                return _inner.Disconnects;
            }
        }

        public ConcurrentQueue<int> Connects
        {
            get
            {
                return _inner.Connects;
            }
        }

        public int ConnectionCount
        {
            get
            {
                return _inner.ConnectionCount;
            }
        }

        public double TimeoutSeconds
        {
            get
            {
                return _inner.TimeoutSeconds;
            }
        }

        public void Send(int connId, INetMessage message)
        {
            _inner.Send(connId, message);
        }

        public void Broadcast(INetMessage message)
        {
            _inner.Broadcast(message);
        }

        public void SendTransient(int connId, INetMessage message)
        {
            _inner.SendTransient(connId, message);
        }

        public void BroadcastTransient(INetMessage message)
        {
            _inner.BroadcastTransient(message);
        }

        public double SecondsSinceLastRecv(int connId)
        {
            return _inner.SecondsSinceLastRecv(connId);
        }

        public List<int> ConnIds()
        {
            return _inner.ConnIds();
        }

        public void Kick(int connId)
        {
            _inner.Kick(connId);
        }

        public void Stop()
        {
            _inner.Stop();
            while (_incoming.TryDequeue(out _))
            {
            }
            _delay.Clear();
            _delayBytes = 0;
            _overflowWarned = false;
        }

        public void PumpMainThread()
        {
            // Steam does all of its sending and receiving here; TCP fills its queue from
            // background threads. Either way the inner pump must run first so this frame's
            // arrivals are considered for (possibly zero) delay before anything is released.
            _inner.PumpMainThread();

            int lag = ClampMs(CoopPlugin.ArtificialLagMs != null ? CoopPlugin.ArtificialLagMs.Value : 0);
            int jitter = ClampMs(CoopPlugin.ArtificialJitterMs != null ? CoopPlugin.ArtificialJitterMs.Value : 0);

            if (lag == 0 && jitter == 0)
            {
                // A live config change can leave frames in the delayed path. They are due
                // immediately now, and must be delivered before newly arrived frames.
                while (_delay.Count > 0)
                    _incoming.Enqueue(_delay.Dequeue().Message);
                _delayBytes = 0;
                _overflowWarned = false;

                while (_inner.Incoming.TryDequeue(out InMsg msg))
                    _incoming.Enqueue(msg);
                return;
            }

            long now = Stopwatch.GetTimestamp();

            while (_inner.Incoming.TryDequeue(out InMsg msg))
            {
                if (_delay.Count >= MaxBufferedFrames || _delayBytes >= MaxBufferedBytes)
                {
                    // Safety valve: a huge delay must never let the buffer grow without
                    // bound. Release the oldest frame undelayed and warn once; newer frames
                    // keep their delay so the test stays as close to the setting as it can.
                    if (!_overflowWarned)
                    {
                        _overflowWarned = true;
                        CoopPlugin.Log?.LogWarning("LagTransport: delay buffer reached "
                            + MaxBufferedFrames + " frames - releasing the oldest frames without delay");
                    }
                    var released = _delay.Dequeue().Message;
                    _incoming.Enqueue(released);
                    _delayBytes = Math.Max(0, _delayBytes - EstimateBytes(released));
                }
                int extra = jitter > 0 ? _random.Next(-jitter, jitter + 1) : 0;
                int delayMs = lag + extra;
                if (delayMs < 0)
                    delayMs = 0;
                _delay.Enqueue(new Pending
                {
                    ReleaseAt = now + (long)(delayMs * TicksPerMs),
                    Message = msg,
                });
                _delayBytes += EstimateBytes(msg);
            }

            while (_delay.Count > 0 && _delay.Peek().ReleaseAt <= now)
            {
                var released = _delay.Dequeue().Message;
                _incoming.Enqueue(released);
                _delayBytes = Math.Max(0, _delayBytes - EstimateBytes(released));
            }
            if (_delay.Count < MaxBufferedFrames / 2 && _delayBytes < MaxBufferedBytes / 2)
                _overflowWarned = false;
        }

        private static int ClampMs(int value)
        {
            if (value < 0)
                return 0;
            return value > MaxDelayMs ? MaxDelayMs : value;
        }

        private static int EstimateBytes(InMsg message)
        {
            return message.Message == null ? Msg.MinimumFrameSize : NetMessageCodec.Encode(message.Message).Length;
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }
}
