namespace CardShopCoop.Net
{
    internal static class MessageRetryRules
    {
        // These commands can charge money or deliver inventory before a later step
        // throws, and carry no operation ID with a retained outcome. A failed dispatch
        // must refresh state instead of executing the command again.
        internal static bool IsNonIdempotent(MsgType type)
        {
            switch (type)
            {
                case MsgType.PurchaseRequest:
                case MsgType.StaffOp:
                case MsgType.ShopOp:
                case MsgType.SettingsOp:
                case MsgType.GradingOp:
                case MsgType.TradeOp:
                case MsgType.EconContrib:
                    return true;
                default:
                    return false;
            }
        }
    }
}
