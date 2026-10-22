using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>Tracks temporary worker interactions separately from native worker state.
    /// Repeated requests from the same player remain granted; teardown returns the guest
    /// workers that must resume, while the host's own native menu may remain open.</summary>
    internal sealed class WorkerInteractionLeases
    {
        private readonly Dictionary<int, int> _owners = new Dictionary<int, int>();

        public bool TryAcquire(int worker, int player, out bool alreadyOwned)
        {
            if (_owners.TryGetValue(worker, out int owner))
            {
                alreadyOwned = owner == player;
                return alreadyOwned;
            }
            _owners[worker] = player;
            alreadyOwned = false;
            return true;
        }

        public bool TryGetOwner(int worker, out int player) => _owners.TryGetValue(worker, out player);

        public bool Release(int worker, int player)
        {
            if (!_owners.TryGetValue(worker, out int owner) || owner != player)
                return false;
            _owners.Remove(worker);
            return true;
        }

        public List<int> ReleaseConnection(int player)
        {
            var workers = new List<int>();
            foreach (var pair in _owners)
                if (pair.Value == player)
                    workers.Add(pair.Key);
            for (int i = 0; i < workers.Count; i++)
                _owners.Remove(workers[i]);
            return workers;
        }

        public List<int> Reset()
        {
            var guests = new List<int>();
            foreach (var pair in _owners)
                if (pair.Value > 0)
                    guests.Add(pair.Key);
            _owners.Clear();
            return guests;
        }
    }
}
