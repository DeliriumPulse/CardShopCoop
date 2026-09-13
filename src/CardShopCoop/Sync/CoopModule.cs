using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Common lifecycle base for a session-owned co-op subsystem. It removes the repeated
    /// no-op Start, ResetState=&gt;Reset, Dispose=&gt;ResetState boilerplate and gives every
    /// module one contextual error boundary (<see cref="Guarded"/>). Modules keep their own
    /// reset/send logic; only the plumbing is shared.
    /// </summary>
    public abstract class CoopModule : ICoopModule
    {
        public abstract string Name
        {
            get;
        }

        /// <summary>Called when the module becomes part of a live session.</summary>
        public virtual void Start()
        {
        }

        /// <summary>Clears state associated with the current world or save.</summary>
        public virtual void ResetState() => Reset();

        /// <summary>The concrete reset. Public because a few call sites reset one module
        /// directly (e.g. the structure-changed hook), not just through the registry.</summary>
        public virtual void Reset()
        {
        }

        /// <summary>Invalidates change gates so the next host tick sends a baseline.</summary>
        public virtual void ForceResend()
        {
        }

        public virtual void Dispose() => ResetState();

        /// <summary>Runs one module step under the shared error boundary. Exceptions are
        /// logged with module context (rate-limited per tag) and contained, matching the
        /// registry pipeline's fail-soft policy at a per-stage boundary.</summary>
        protected void Guarded(string label, Action action)
        {
            ModuleGuard.Run(Name + ":" + label, action);
        }
    }

    /// <summary>A <see cref="CoopModule"/> that also runs in the per-frame co-op pipeline.
    /// The role dispatch that every module used to copy is centralized here: the host and
    /// client orders come from the module catalog, and each role's work lives in
    /// <see cref="OnHostTick"/> / <see cref="OnClientTick"/>.</summary>
    public abstract class TickableCoopModule : CoopModule, ITickableCoopModule
    {
        public void Tick(in SyncFrame frame)
        {
            if (CoopCore.Role == CoopRole.Host)
                OnHostTick(frame);
            else if (CoopCore.Role == CoopRole.Client)
                OnClientTick(frame);
        }

        protected virtual void OnHostTick(in SyncFrame frame)
        {
        }

        protected virtual void OnClientTick(in SyncFrame frame)
        {
        }
    }

    /// <summary>
    /// Central rate-limited logger for module error boundaries. One line per tag per
    /// <see cref="CooldownSeconds"/>, always with the full exception so a support log has the
    /// stack. Keys are the fixed module/step tags, so the map is bounded.
    /// </summary>
    internal static class ModuleGuard
    {
        private const double CooldownSeconds = 5.0;
        private static readonly Dictionary<string, double> NextAt = new Dictionary<string, double>();

        public static void Run(string tag, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log(tag, e);
            }
        }

        /// <summary>The single rate-limited error logger for the module pipeline and any other
        /// per-frame boundary: one line per tag per <see cref="CooldownSeconds"/>, full exception.</summary>
        public static void Log(string tag, Exception e)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (NextAt.TryGetValue(tag, out double next) && now < next)
                return;
            NextAt[tag] = now + CooldownSeconds;
            CoopPlugin.Log.LogError("[" + tag + "] " + e);
        }
    }

    /// <summary>
    /// Shared snapshot scheduler for the common host pattern: a fixed cadence, a change
    /// hash, and a slow periodic heal that re-sends unchanged state. It intentionally stays
    /// two-phase so the (sometimes expensive) hash is only computed when the cadence fires:
    /// <code>
    /// if (!_gate.Due(dt)) return;
    /// int hash = ComputeHash();
    /// if (!_gate.ShouldSend(hash)) return;
    /// Broadcast(Build());
    /// </code>
    /// Modules whose scheduler does not match this shape (event-dirty, per-record maps,
    /// dual message timers, or a load barrier) keep their own explicit scheduler.
    /// </summary>
    internal sealed class SnapshotGate
    {
        private readonly float _interval;
        private readonly float _healInterval;
        private readonly bool _hasHashInitially;
        private float _timer;
        private float _heal;
        private int _lastHash;
        private bool _hasHash;
        private bool _force;

        public SnapshotGate(float interval, float healInterval, float phase = 0f,
            bool hasHashInitially = true)
        {
            if (interval <= 0f)
                throw new ArgumentOutOfRangeException(nameof(interval));
            if (healInterval < 0f)
                throw new ArgumentOutOfRangeException(nameof(healInterval));
            _interval = interval;
            _healInterval = healInterval;
            _hasHashInitially = hasHashInitially;
            _timer = phase;
            _hasHash = hasHashInitially;
        }

        /// <summary>Reset to the staggered phase and clear the change gate.</summary>
        public void Reset(float phase = 0f)
        {
            _timer = phase;
            _heal = 0f;
            _lastHash = 0;
            _hasHash = _hasHashInitially;
            _force = false;
        }

        /// <summary>Advances the cadence; true when this frame should consider a send.
        /// Advances the heal counter once per elapsed interval, matching the original
        /// per-interval heal accounting.</summary>
        public bool Due(float dt)
        {
            _timer += dt;
            if (_timer < _interval)
                return false;
            _timer -= _interval;
            _heal += _interval;
            return true;
        }

        /// <summary>True when the given hash should be sent: the first send, a changed
        /// hash, a heal that has come due, or a forced send. Records the hash on a send.</summary>
        public bool ShouldSend(int hash)
        {
            if (_force)
            {
                _force = false;
                _hasHash = true;
                _lastHash = hash;
                _heal = 0f;
                return true;
            }
            if (_hasHash && hash == _lastHash && _heal < _healInterval)
                return false;
            _hasHash = true;
            _lastHash = hash;
            _heal = 0f;
            return true;
        }

        /// <summary>Force the next tick to be due and send even if the hash is unchanged.</summary>
        public void Force()
        {
            _timer = _interval;
            _force = true;
            _hasHash = false;
            _lastHash = 0;
        }
    }
}
