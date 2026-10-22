namespace CardShopCoop.Net.Messages
{
    [NetworkMessage(MsgType.ShelfBoxCue, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class ShelfBoxCueMessage : INetMessage
    {
        public ushort BoxId;
        public uint Sequence;
        public EItemType ItemType;
        public UnityEngine.Vector3 From, Scale, DestinationLocal;
        public UnityEngine.Quaternion Rotation;
        public MsgType Type => MsgType.ShelfBoxCue;
    }

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
