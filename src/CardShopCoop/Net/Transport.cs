using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Minimal TCP transport. Host accepts any number of clients (dad + son = 2 players,
    /// but nothing hardcodes that). All socket work happens on background threads;
    /// received messages land in a ConcurrentQueue that the Unity main thread drains.
    /// </summary>
    public class Transport : ICoopTransport
    {
        private const int MaxFrame = 64 * 1024 * 1024; // save files are ~4 MB; hard cap for sanity

        public ConcurrentQueue<InMsg> Incoming { get; } = new ConcurrentQueue<InMsg>();
        public ConcurrentQueue<int> Disconnects { get; } = new ConcurrentQueue<int>();
        public ConcurrentQueue<int> Connects { get; } = new ConcurrentQueue<int>();

        public void PumpMainThread() { } // all socket work lives on background threads

        public double TimeoutSeconds => 60.0;

        // TCP is already low-latency and ordered; the fast lane is just the normal lane
        public void SendTransient(int connId, byte[] frame) { Send(connId, frame); }
        public void BroadcastTransient(byte[] frame) { Broadcast(frame); }

        /// <summary>Frame sent by a transport-owned thread every 2s per connection.
        /// Keeps the link alive even while Unity's main thread is frozen in a scene load.</summary>
        public byte[] KeepaliveFrame;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        private readonly Dictionary<int, Conn> _conns = new Dictionary<int, Conn>();
        private readonly object _connsLock = new object();
        private int _nextConnId = 1;

        public bool IsListening { get; private set; }

        private class Conn
        {
            public int Id;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public Thread ReadThread;
            public readonly object WriteLock = new object();
            public volatile bool Alive = true;
            public long LastRecvTicksUtc = DateTime.UtcNow.Ticks;
        }

        // ---------------- host ----------------

        public void StartHost(int port)
        {
            Stop();
            _running = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsListening = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "CoopAccept" };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { break; } // listener stopped
                tcp.NoDelay = true;
                var conn = new Conn { Tcp = tcp, Stream = tcp.GetStream() };
                lock (_connsLock)
                {
                    conn.Id = _nextConnId++;
                    _conns[conn.Id] = conn;
                }
                conn.ReadThread = new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "CoopRead" + conn.Id };
                conn.ReadThread.Start();
                StartKeepalive(conn);
                Connects.Enqueue(conn.Id);
            }
        }

        // ---------------- client ----------------

        /// <summary>Connect to a host. Returns the connId (always 1 for a client) or throws.</summary>
        public int StartClient(string ip, int port, int timeoutMs = 6000)
        {
            Stop();
            _running = true;
            var tcp = new TcpClient();
            var ar = tcp.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                tcp.Close();
                throw new TimeoutException($"No answer from {ip}:{port} after {timeoutMs / 1000}s");
            }
            tcp.EndConnect(ar);
            tcp.NoDelay = true;
            var conn = new Conn { Id = 1, Tcp = tcp, Stream = tcp.GetStream() };
            lock (_connsLock) { _conns[1] = conn; }
            conn.ReadThread = new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "CoopRead1" };
            conn.ReadThread.Start();
            StartKeepalive(conn);
            return conn.Id;
        }

        // ---------------- shared ----------------

        private void StartKeepalive(Conn conn)
        {
            new Thread(() =>
            {
                while (_running && conn.Alive)
                {
                    Thread.Sleep(2000);
                    var frame = KeepaliveFrame;
                    if (frame == null || !conn.Alive) continue;
                    try
                    {
                        lock (conn.WriteLock) { conn.Stream.Write(frame, 0, frame.Length); }
                    }
                    catch { DropConn(conn.Id); break; }
                }
            }) { IsBackground = true, Name = "CoopKeepalive" + conn.Id }.Start();
        }

        private void ReadLoop(Conn conn)
        {
            var lenBuf = new byte[4];
            try
            {
                while (_running && conn.Alive)
                {
                    ReadExact(conn.Stream, lenBuf, 4);
                    int frameLen = BitConverter.ToInt32(lenBuf, 0);
                    if (frameLen < 1 || frameLen > MaxFrame)
                        throw new IOException("Bad frame length " + frameLen);
                    var frame = new byte[frameLen];
                    ReadExact(conn.Stream, frame, frameLen);
                    conn.LastRecvTicksUtc = DateTime.UtcNow.Ticks;
                    var payload = new byte[frameLen - 1];
                    Buffer.BlockCopy(frame, 1, payload, 0, frameLen - 1);
                    Incoming.Enqueue(new InMsg { ConnId = conn.Id, Type = (MsgType)frame[0], Payload = payload });
                }
            }
            catch
            {
                // fallthrough to disconnect
            }
            DropConn(conn.Id);
        }

        private static void ReadExact(NetworkStream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new IOException("Connection closed");
                off += n;
            }
        }

        public void Send(int connId, byte[] frame)
        {
            Conn conn;
            lock (_connsLock) { if (!_conns.TryGetValue(connId, out conn)) return; }
            if (!conn.Alive) return;
            try
            {
                lock (conn.WriteLock) { conn.Stream.Write(frame, 0, frame.Length); }
            }
            catch
            {
                DropConn(connId);
            }
        }

        public void Broadcast(byte[] frame)
        {
            List<int> ids;
            lock (_connsLock) { ids = new List<int>(_conns.Keys); }
            foreach (int id in ids) Send(id, frame);
        }

        public int ConnectionCount
        {
            get { lock (_connsLock) { return _conns.Count; } }
        }

        public double SecondsSinceLastRecv(int connId)
        {
            Conn conn;
            lock (_connsLock) { if (!_conns.TryGetValue(connId, out conn)) return double.MaxValue; }
            return TimeSpan.FromTicks(DateTime.UtcNow.Ticks - conn.LastRecvTicksUtc).TotalSeconds;
        }

        public List<int> ConnIds()
        {
            lock (_connsLock) { return new List<int>(_conns.Keys); }
        }

        /// <summary>Forcibly drop one connection (timeout, version mismatch...).</summary>
        public void Kick(int connId)
        {
            DropConn(connId);
        }

        private void DropConn(int connId)
        {
            Conn conn;
            lock (_connsLock)
            {
                if (!_conns.TryGetValue(connId, out conn)) return;
                _conns.Remove(connId);
            }
            if (!conn.Alive) return;
            conn.Alive = false;
            try { conn.Stream?.Close(); } catch { }
            try { conn.Tcp?.Close(); } catch { }
            Disconnects.Enqueue(connId);
        }

        public void Stop()
        {
            _running = false;
            IsListening = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            List<int> ids;
            lock (_connsLock) { ids = new List<int>(_conns.Keys); }
            foreach (int id in ids) DropConn(id);
        }

        public void Dispose() { Stop(); }
    }
}
