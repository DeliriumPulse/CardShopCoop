namespace CardShopCoop.Sync
{
    internal enum SharedDayAction
    {
        Ignore, ConfirmQueue, OpenReport, ResendReport, Advance
    }

    internal static class SharedDayRules
    {
        public static SharedDayAction Decide(int requestedDay, int currentDay, bool dayEnded,
            bool loadingNextDay, bool reportOpen, bool advance, bool queueWaiting, bool confirmedQueue)
        {
            if (requestedDay != currentDay || !dayEnded || loadingNextDay)
                return SharedDayAction.Ignore;
            if (advance)
                return reportOpen ? SharedDayAction.Advance : SharedDayAction.Ignore;
            if (reportOpen)
                return SharedDayAction.ResendReport;
            if (queueWaiting && !confirmedQueue)
                return SharedDayAction.ConfirmQueue;
            return SharedDayAction.OpenReport;
        }
    }
}
