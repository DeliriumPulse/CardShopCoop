using System;

namespace CardShopCoop.Sync
{
    internal static class BillPayment
    {
        // Bill kinds match EBillType: 0 = all, 1 = rent, 2 = electricity, 3 = salary.
        public static bool TryPay(byte kind, float rent, float electric, float salary,
            Func<double, bool> reserve, Action<byte> pay)
        {
            float amount;
            switch (kind)
            {
                case 0:
                    amount = Math.Max(0f, rent) + Math.Max(0f, electric) + Math.Max(0f, salary);
                    break;
                case 1:
                    amount = rent;
                    break;
                case 2:
                    amount = electric;
                    break;
                case 3:
                    amount = salary;
                    break;
                default:
                    return false;
            }
            if (amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount)
                || reserve == null || pay == null || !reserve(amount))
                return false;
            pay(kind);
            return true;
        }
    }
}
