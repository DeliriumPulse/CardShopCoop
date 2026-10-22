namespace CardShopCoop.Net.Messages
{
    [NetworkMessage(MsgType.ShopPurchaseCue, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class ShopPurchaseCueMessage : INetMessage
    {
        public MsgType Type => MsgType.ShopPurchaseCue;
    }
}
