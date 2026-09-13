using System;
using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Owns the ordered lifecycle of session subsystems. The registry deliberately keeps
    /// module ordering explicit: several synchronizers depend on population and box cleanup
    /// happening before their own state is read, and disposal runs in reverse so static
    /// Harmony entry points detach before the state they reference is cleared.
    ///
    /// Per-frame ticks are NOT driven from here: host and client need different orders, so
    /// CoopCore owns the pipeline and calls <see cref="ITickableCoopModule.Tick"/> directly.
    /// </summary>
    public sealed class CoopModuleRegistry : IDisposable
    {
        private readonly List<ICoopModule> _modules;
        private bool _started;
        private bool _disposed;

        public CoopModuleRegistry(IEnumerable<ICoopModule> modules)
        {
            if (modules == null)
                throw new ArgumentNullException(nameof(modules));
            _modules = new List<ICoopModule>(modules);
            for (int i = 0; i < _modules.Count; i++)
                if (_modules[i] == null)
                    throw new ArgumentException("Co-op module list contains null.", nameof(modules));
        }

        public void Start()
        {
            ThrowIfDisposed();
            if (_started)
                throw new InvalidOperationException("Co-op module registry was started twice.");

            for (int i = 0; i < _modules.Count; i++)
                _modules[i].Start();
            _started = true;
        }

        public void ResetState()
        {
            ThrowIfDisposed();
            for (int i = 0; i < _modules.Count; i++)
            {
                try
                {
                    _modules[i].ResetState();
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("co-op module reset failed (" + _modules[i].Name + "): " + e);
                }
            }
        }

        public void ForceResend()
        {
            ThrowIfDisposed();
            for (int i = 0; i < _modules.Count; i++)
            {
                try
                {
                    _modules[i].ForceResend();
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("co-op module resend failed (" + _modules[i].Name + "): " + e);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // Dispose in reverse dependency order. This also ensures static Harmony
            // entry points are detached before the state they reference is cleared.
            for (int i = _modules.Count - 1; i >= 0; i--)
            {
                try
                {
                    _modules[i].Dispose();
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("co-op module dispose failed (" + _modules[i].Name + "): " + e);
                }
            }
            _started = false;
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CoopModuleRegistry));
        }
    }

    /// <summary>Lifecycle adapter for subsystems that do not need a per-frame tick of their own.</summary>
    public sealed class DelegateCoopModule : ICoopModule
    {
        private readonly Action _start;
        private readonly Action _reset;
        private readonly Action _resend;
        private readonly Action _dispose;

        public string Name
        {
            get;
        }

        public DelegateCoopModule(string name, Action start, Action reset, Action resend, Action dispose = null)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Module name is required.", nameof(name));
            Name = name;
            _start = start ?? (() => { });
            _reset = reset ?? (() => { });
            _resend = resend ?? (() => { });
            _dispose = dispose ?? (() => { });
        }

        public void Start() => _start();
        public void ResetState() => _reset();
        public void ForceResend() => _resend();
        public void Dispose() => _dispose();
    }
}
