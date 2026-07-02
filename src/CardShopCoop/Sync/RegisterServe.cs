using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Lets the joiner work the register. Each ServeRequest performs on the HOST exactly
    /// what a cashier worker's automation does at the counter (the NPC branch of
    /// InteractableCashierCounter.Update): scan the next unscanned item/card, take the
    /// customer's payment, then auto-evaluate change (cash) or card. After change is
    /// evaluated the customer's own simulation collects it and walks away; sale money and
    /// XP flow through the already-shared economy.
    /// </summary>
    public static class RegisterServe
    {
        private static readonly FieldInfo FiMannedByPlayer = AccessTools.Field(typeof(InteractableCashierCounter), "m_IsMannedByPlayer");
        private static readonly FieldInfo FiMannedByNpc = AccessTools.Field(typeof(InteractableCashierCounter), "m_IsMannedByNPC");
        private static readonly FieldInfo FiIsUsingCard = AccessTools.Field(typeof(InteractableCashierCounter), "m_IsUsingCard");
        private static readonly FieldInfo FiTotalScanned = AccessTools.Field(typeof(InteractableCashierCounter), "m_TotalScannedItemCost");
        private static readonly MethodInfo MiNpcChange = AccessTools.Method(typeof(InteractableCashierCounter), "NPCEvaluateMoneyChange");
        private static readonly MethodInfo MiCreditCard = AccessTools.Method(typeof(InteractableCashierCounter), "EvaluateCreditCard");
        private static readonly FieldInfo FiScannedCount = AccessTools.Field(typeof(Customer), "m_ItemScannedCount");

        /// <summary>Client: nearest cashier counter index within reach, or -1.</summary>
        public static int FindNearestCounter(Vector3 playerPos, float maxDist = 4f)
        {
            var sm = Object.FindObjectOfType<ShelfManager>();
            if (sm == null) return -1;
            int best = -1;
            float bestSq = maxDist * maxDist;
            for (int i = 0; i < sm.m_CashierCounterList.Count; i++)
            {
                var counter = sm.m_CashierCounterList[i];
                if (counter == null) continue;
                float sq = (counter.transform.position - playerPos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            return best;
        }

        /// <summary>Host: execute one register step on the given counter. Returns feedback text.</summary>
        public static string Serve(int counterIndex, string serverName)
        {
            var sm = Object.FindObjectOfType<ShelfManager>();
            if (sm == null || counterIndex < 0 || counterIndex >= sm.m_CashierCounterList.Count)
                return "no register here";
            var counter = sm.m_CashierCounterList[counterIndex];
            if (counter == null) return "no register here";

            if (FiMannedByPlayer?.GetValue(counter) is bool byPlayer && byPlayer)
                return "the host is already at this register";
            if (FiMannedByNpc?.GetValue(counter) is bool byNpc && byNpc)
                return "a worker is already on this register";

            var customer = counter.m_CurrentCustomer;
            if (customer == null || !customer.m_IsActive)
                return "no customer at the counter";

            switch (counter.m_CashierCounterState)
            {
                case ECashierCounterState.ScanningItem:
                {
                    bool scanned = false;
                    var items = customer.GetItemInBagList();
                    for (int i = 0; i < items.Count && !scanned; i++)
                    {
                        var scan = items[i] != null ? items[i].m_InteractableScanItem : null;
                        if (scan != null && scan.IsNotScanned())
                        {
                            scan.OnMouseButtonUp();
                            scanned = true;
                        }
                    }
                    if (!scanned)
                    {
                        var cards = customer.GetCardInBagList();
                        for (int i = 0; i < cards.Count && !scanned; i++)
                        {
                            if (cards[i] != null && cards[i].IsNotScanned())
                            {
                                cards[i].OnMouseButtonUp();
                                scanned = true;
                            }
                        }
                    }
                    if (!scanned) return "nothing left to scan - wait a moment";

                    int total = customer.GetItemInBagList().Count + customer.GetCardInBagList().Count;
                    int done = FiScannedCount?.GetValue(customer) is int c ? c : 0;
                    return done >= total ? $"all {total} scanned - press again to take payment"
                                         : $"scanned {done}/{total}";
                }
                case ECashierCounterState.TakingCash:
                {
                    if (customer.m_CustomerCash != null && customer.m_CustomerCash.gameObject.activeSelf)
                    {
                        customer.m_CustomerCash.OnMouseButtonUp();
                        return "payment taken - press again to give change";
                    }
                    return "waiting for the customer to pay...";
                }
                case ECashierCounterState.GivingChange:
                {
                    bool isCard = FiIsUsingCard?.GetValue(counter) is bool card && card;
                    if (isCard)
                    {
                        double totalCost = FiTotalScanned?.GetValue(counter) is double d ? d : 0.0;
                        MiCreditCard?.Invoke(counter, new object[] { totalCost });
                    }
                    else
                    {
                        MiNpcChange?.Invoke(counter, null);
                    }
                    CoopPlugin.Log.LogInfo($"{serverName} completed a sale at register {counterIndex}");
                    return "sale complete!";
                }
                default:
                    return "no customer ready at this register";
            }
        }
    }
}
