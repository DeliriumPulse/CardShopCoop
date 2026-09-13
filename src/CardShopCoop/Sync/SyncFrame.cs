namespace CardShopCoop.Sync
{
    /// <summary>Immutable per-frame lifecycle inputs shared by all session modules.</summary>
    public readonly struct SyncFrame
    {
        public readonly float Dt;
        public readonly bool InGame;
        public readonly bool PreloadHold;

        public SyncFrame(float dt, bool inGame, bool preloadHold)
        {
            Dt = dt;
            InGame = inGame;
            PreloadHold = preloadHold;
        }
    }

    /// <summary>One entry in CoopCore's explicit per-role tick pipeline. The probe name is
    /// precomputed so the hot per-frame loop allocates nothing (not even a string).</summary>
    public readonly struct TickEntry
    {
        public readonly ITickableCoopModule Module;
        public readonly string Probe;

        public TickEntry(ITickableCoopModule module, string probe)
        {
            Module = module;
            Probe = probe;
        }
    }
}
