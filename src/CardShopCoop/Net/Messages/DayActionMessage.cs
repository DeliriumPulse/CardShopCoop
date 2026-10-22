namespace CardShopCoop.Net.Messages
{
    [NetworkMessage(MsgType.DayAction, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class DayActionMessage : INetMessage
    {
        public int Day;
        public bool Advance, ConfirmedQueue;
        public MsgType Type => MsgType.DayAction;
    }

    [NetworkMessage(MsgType.DayActionReply, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class DayActionReplyMessage : INetMessage
    {
        public int Day;
        public bool ConfirmQueue;
        public string Notice;
        public MsgType Type => MsgType.DayActionReply;
    }
}
