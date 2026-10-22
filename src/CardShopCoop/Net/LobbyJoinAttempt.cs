namespace CardShopCoop.Net
{
    internal sealed class LobbyJoinAttempt
    {
        private long _generation;
        private bool _pending;

        internal long Begin()
        {
            _pending = true;
            return ++_generation;
        }

        internal bool TryComplete(long generation)
        {
            if (!_pending || generation != _generation)
                return false;
            _pending = false;
            return true;
        }

        internal void Cancel()
        {
            _pending = false;
            ++_generation;
        }
    }
}
