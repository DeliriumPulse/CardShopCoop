namespace CardShopCoop.Net
{
    // Only world-transfer progress resets the deadline. Position packets and pings
    // cannot keep a broken handshake or stalled download alive forever.
    internal sealed class JoinWatchdog
    {
        internal enum Stage
        {
            Idle, Lobby, Welcome, Download, Loading
        }
        internal Stage Current
        {
            get; private set;
        }
        private double _progressAt;
        private double _helloAt;
        internal double StageAgeSeconds(double now) => Current == Stage.Idle ? 0 : System.Math.Max(0, now - _progressAt);
        internal double HelloAgeSeconds(double now) => Current == Stage.Welcome ? System.Math.Max(0, now - _helloAt) : 0;

        internal void StartLobby(double now) => Progress(Stage.Lobby, now);

        internal void Start(double now)
        {
            Current = Stage.Welcome;
            _progressAt = _helloAt = now;
        }

        internal void Progress(Stage stage, double now)
        {
            Current = stage;
            _progressAt = now;
        }

        internal bool RetryHello(double now)
        {
            if (Current != Stage.Welcome || now - _helloAt < 3 || TimedOut(now))
                return false;
            _helloAt = now;
            return true;
        }

        internal bool TimedOut(double now)
        {
            double limit = Current == Stage.Lobby ? 30
                : Current == Stage.Welcome ? 90 : Current == Stage.Download ? 120 : 180;
            return Current != Stage.Idle && now - _progressAt >= limit;
        }

        internal void Reset() => Current = Stage.Idle;
    }
}
