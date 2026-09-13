namespace CardShopCoop.Net.Messages
{
    // One reliable request moves one item on the host. The guest never mutates either mirror.
    [NetworkMessage(MsgType.ShelfBoxPull, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class ShelfBoxPullMessage : INetMessage
    {
        public int ShelfKey;
        public ushort BoxId;
        public EItemType ItemType;
        public uint Sequence;
        public MsgType Type => MsgType.ShelfBoxPull;
    }
}
