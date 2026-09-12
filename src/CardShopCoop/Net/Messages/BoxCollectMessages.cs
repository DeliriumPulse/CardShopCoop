namespace CardShopCoop.Net.Messages
{
    public enum BoxCollectStatus : byte
    {
        Accepted = 0,
        UnknownBox = 1,
        HeldByOther = 2,
        HashMismatch = 3,
        NullCard = 4,
        MissingContent = 5,
        ApplyFailed = 6,
        HostCarried = 7,
    }

    /// <summary>Client -> host: the joiner opened a graded-returns card box. The host runs
    /// the collect (AddCard per card + achievements) and despawns the box; CardDelta
    /// mirrors the cards back, and the next BoxSnapshot retires the mirror.</summary>
    [NetworkMessage(MsgType.BoxCollect, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class BoxCollectMessage : INetMessage
    {
        public ushort Id;
        public byte CardCount;
        public int CardsHash;

        public MsgType Type => MsgType.BoxCollect;
    }

    /// <summary>Host -> client: the collect was not accepted (unknown/mismatched box);
    /// the client restores the still-live mirror. Empty BoxId == accepted (informational).</summary>
    [NetworkMessage(MsgType.BoxCollectResult, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class BoxCollectResultMessage : INetMessage
    {
        public ushort Id;
        public bool Accepted;
        public byte CardCount;
        public int CardsHash;
        public byte Reason;

        public MsgType Type => MsgType.BoxCollectResult;
    }
}
