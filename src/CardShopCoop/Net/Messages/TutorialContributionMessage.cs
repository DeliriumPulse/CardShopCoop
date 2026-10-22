namespace CardShopCoop.Net.Messages
{
    [NetworkMessage(MsgType.TutorialContribution, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class TutorialContributionMessage : INetMessage
    {
        public long Sequence;
        public int Condition;
        public float Delta;
        public MsgType Type => MsgType.TutorialContribution;
    }
}
