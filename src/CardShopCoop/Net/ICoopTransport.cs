using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Transport contract shared by the LAN TCP transport and the Steam P2P transport.
    /// Connection ids are small ints; 1 is always "the host" from a client's view.
    /// </summary>
    public interface ICoopTransport : IDisposable
    {
        ConcurrentQueue<InMsg> Incoming { get; }
        ConcurrentQueue<int> Disconnects { get; }
        ConcurrentQueue<int> Connects { get; }

        void Send(int connId, byte[] frame);
        void Broadcast(byte[] frame);
        int ConnectionCount { get; }
        double SecondsSinceLastRecv(int connId);
        List<int> ConnIds();
        void Kick(int connId);
        void Stop();

        /// <summary>Called every frame from the Unity main thread. TCP ignores it;
        /// Steam does all its sends/receives here (Steamworks is main-thread only).</summary>
        void PumpMainThread();

        /// <summary>Peer-silence tolerance. Steam needs a longer window because its
        /// keepalives also run on the (freezable) main thread.</summary>
        double TimeoutSeconds { get; }
    }
}
