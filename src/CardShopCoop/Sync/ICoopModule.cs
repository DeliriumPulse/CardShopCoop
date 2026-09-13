using System;

namespace CardShopCoop.Sync
{
    /// <summary>Common lifecycle contract for a session-owned co-op subsystem.
    /// The registry owns these calls; per-frame ordering lives in <see cref="ITickableCoopModule"/>.</summary>
    public interface ICoopModule : IDisposable
    {
        string Name
        {
            get;
        }

        /// <summary>Called when the module becomes part of a live session.</summary>
        void Start();

        /// <summary>Clears state associated with the current world or save.</summary>
        void ResetState();

        /// <summary>Invalidates change gates so the next host tick sends a baseline.</summary>
        void ForceResend();

        /// <summary>Host: a specific connection finished joining. Per-conn resync that cannot be
        /// expressed as a broadcast baseline - e.g. replaying state the other players already
        /// have - belongs here. Runs after <see cref="ForceResend"/>, so a module can rely on
        /// its baseline having been armed first.</summary>
        void OnFullyJoin(int connId);
    }

    /// <summary>A module that participates in the per-frame co-op sync pipeline. CoopCore
    /// declares the host/client order explicitly because the two roles need different
    /// sequences (market flush first on the client, module pipelines on the host).</summary>
    public interface ITickableCoopModule : ICoopModule
    {
        /// <summary>Runs once per co-op frame.</summary>
        void Tick(in SyncFrame frame);
    }
}
