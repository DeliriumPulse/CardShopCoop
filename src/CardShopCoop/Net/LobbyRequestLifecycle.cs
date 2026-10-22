using System;
using System.Collections.Generic;

namespace CardShopCoop.Net
{
    // Steam may finish a create/join after the player has cancelled it. Keep those
    // callbacks alive so their membership can be released, without publishing an old
    // result or leaving a lobby that a newer request still needs.
    internal sealed class LobbyRequestLifecycle
    {
        private readonly LobbyJoinAttempt _attempt = new LobbyJoinAttempt();
        private readonly HashSet<ulong> _memberships = new HashSet<ulong>();
        private readonly Action<ulong> _leave;
        private ulong _requestedLobby;

        internal ulong CurrentLobby
        {
            get; private set;
        }

        internal LobbyRequestLifecycle(Action<ulong> leave)
        {
            _leave = leave;
        }

        internal long Begin(ulong requestedLobby = 0)
        {
            CurrentLobby = 0;
            _requestedLobby = requestedLobby;
            long generation = _attempt.Begin();
            ReleaseUnusedMemberships();
            return generation;
        }

        internal bool Complete(long generation, ulong lobby, bool success)
        {
            if (success && lobby != 0)
                _memberships.Add(lobby);
            bool current = _attempt.TryComplete(generation);
            if (current)
            {
                _requestedLobby = 0;
                CurrentLobby = success ? lobby : 0;
            }
            ReleaseUnusedMemberships();
            return current;
        }

        internal void Cancel()
        {
            _attempt.Cancel();
            _requestedLobby = 0;
            CurrentLobby = 0;
            ReleaseUnusedMemberships();
        }

        private void ReleaseUnusedMemberships()
        {
            var unused = new List<ulong>();
            foreach (ulong lobby in _memberships)
                if (lobby != CurrentLobby && lobby != _requestedLobby)
                    unused.Add(lobby);
            foreach (ulong lobby in unused)
            {
                _leave(lobby);
                _memberships.Remove(lobby);
            }
        }
    }
}
