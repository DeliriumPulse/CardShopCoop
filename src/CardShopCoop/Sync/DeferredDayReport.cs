namespace CardShopCoop.Sync
{
    internal enum ReportDeliveryAction
    {
        Wait, Open, Archive
    }

    /// <summary>A recap survives a busy menu, but cannot reopen after its day was filed.</summary>
    internal sealed class DeferredDayReport<T>
    {
        internal int Day { get; private set; } = -1;
        internal int LastFiledDay { get; private set; } = -1;
        internal T Value
        {
            get; private set;
        }
        internal bool Opened
        {
            get; private set;
        }

        internal bool Offer(int day, int currentDay, T value)
        {
            if (day < currentDay || day <= LastFiledDay || (Day >= 0 && Day != day))
                return false;
            if (Day < 0)
            {
                Day = day;
                Value = value;
            }
            return true;
        }

        internal ReportDeliveryAction Next(int currentDay, bool busy)
        {
            if (Day < 0)
                return ReportDeliveryAction.Wait;
            if (currentDay > Day)
                return ReportDeliveryAction.Archive;
            return currentDay == Day && !Opened && !busy
                ? ReportDeliveryAction.Open : ReportDeliveryAction.Wait;
        }

        internal void MarkOpened() => Opened = Day >= 0;

        internal void Finish()
        {
            if (Day >= 0)
                LastFiledDay = Day;
            Day = -1;
            Value = default(T);
            Opened = false;
        }

        internal void Reset()
        {
            Finish();
            LastFiledDay = -1;
        }
    }
}
