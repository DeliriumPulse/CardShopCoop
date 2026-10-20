namespace CardShopCoop.Net.Messages
{
    // A quest step that only the client's game counts (box pickup, shelving, pricing, ...).
    // The host adds it to the shared tutorial and the ShopState echo carries the result back.
    [NetworkMessage(MsgType.TutorialProgress, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class TutorialProgressMessage : INetMessage
    {
        public ETutorialTaskCondition Condition;
        public float Value;
        public MsgType Type => MsgType.TutorialProgress;
    }
}
