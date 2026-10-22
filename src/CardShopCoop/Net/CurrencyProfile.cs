using System;

namespace CardShopCoop.Net
{
    // Currency is a local game setting, not part of the borrowed shop save.
    // Compare both identity and conversion: several currencies share the same rate.
    public sealed class CurrencyProfile
    {
        public int CurrencyId = -1;
        public double ConversionRate;

        private static bool Valid(CurrencyProfile value)
        {
            return value != null && value.CurrencyId >= 0 && value.ConversionRate > 0
                && !double.IsNaN(value.ConversionRate) && !double.IsInfinity(value.ConversionRate);
        }

        public static bool Matches(CurrencyProfile left, CurrencyProfile right)
        {
            return Valid(left) && Valid(right) && left.CurrencyId == right.CurrencyId
                && left.ConversionRate == right.ConversionRate;
        }

        public static bool MatchesDenomination(double received, double actual)
        {
            return received > 0 && actual > 0 && !double.IsInfinity(received)
                && !double.IsInfinity(actual) && Math.Abs(received - actual) <= 0.0000001;
        }
    }
}
