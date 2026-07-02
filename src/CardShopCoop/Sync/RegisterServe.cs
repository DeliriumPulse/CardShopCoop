using System.Collections.Generic;
using System.IO;
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
        private static readonly MethodInfo MiCheckChangeReady = AccessTools.Method(typeof(InteractableCashierCounter), "CheckChangeReady");
        private static readonly MethodInfo MiSpaceBar = AccessTools.Method(typeof(InteractableCashierCounter), "OnPressSpaceBar");
        private static readonly FieldInfo FiIsChangeReady = AccessTools.Field(typeof(InteractableCashierCounter), "m_IsChangeReady");
        private static readonly FieldInfo FiScannedCount = AccessTools.Field(typeof(Customer), "m_ItemScannedCount");

        /// <summary>Client: nearest cashier counter index within reach, or -1.</summary>
        public static int FindNearestCounter(Vector3 playerPos, float maxDist = 7f, bool quiet = false)
        {
            var sm = Object.FindObjectOfType<ShelfManager>();
            if (sm == null) { if (!quiet) CoopPlugin.Log.LogInfo("serve: no ShelfManager"); return -1; }
            int best = -1;
            float bestSq = maxDist * maxDist;
            for (int i = 0; i < sm.m_CashierCounterList.Count; i++)
            {
                var counter = sm.m_CashierCounterList[i];
                if (counter == null) continue;
                float sq = (counter.transform.position - playerPos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            if (!quiet)
                CoopPlugin.Log.LogInfo($"serve: {sm.m_CashierCounterList.Count} counters, nearest={best} ({Mathf.Sqrt(bestSq):F1}m)");
            return best;
        }

        /// <summary>Host: execute one register step. Returns feedback text; when a scan
        /// landed, scanEcho carries (counterIdx, kind, price, identity) so the client can
        /// fill its vanilla checkout UI.</summary>
        public static string Serve(int counterIndex, string serverName, out byte[] scanEcho)
        {
            scanEcho = null;
            string result = ServeInner(counterIndex, serverName, ref scanEcho);
            CoopPlugin.Log.LogInfo($"serve: {serverName} @ counter {counterIndex} -> {result}");
            return result;
        }

        private static string ServeInner(int counterIndex, string serverName, ref byte[] scanEcho)
        {
            var sm = Object.FindObjectOfType<ShelfManager>();
            if (sm == null || counterIndex < 0 || counterIndex >= sm.m_CashierCounterList.Count)
                return "no register here";
            var counter = sm.m_CashierCounterList[counterIndex];
            if (counter == null) return "no register here";
            CoopPlugin.Log.LogInfo($"serve: counter {counterIndex} state={counter.m_CashierCounterState} customer={(counter.m_CurrentCustomer != null ? counter.m_CurrentCustomer.name : "none")} byPlayer={FiMannedByPlayer?.GetValue(counter)} byNPC={FiMannedByNpc?.GetValue(counter)}");

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
                    double before = FiTotalScanned?.GetValue(counter) is double b ? b : 0.0;
                    bool scanned = false;
                    EItemType scannedType = default;
                    CardData scannedCard = null;
                    var items = customer.GetItemInBagList();
                    for (int i = 0; i < items.Count && !scanned; i++)
                    {
                        var scan = items[i] != null ? items[i].m_InteractableScanItem : null;
                        if (scan != null && scan.IsNotScanned())
                        {
                            scannedType = items[i].GetItemType();
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
                                try { scannedCard = cards[i].m_Card3dUI.m_CardUI.GetCardData(); } catch { }
                                cards[i].OnMouseButtonUp();
                                scanned = true;
                            }
                        }
                    }
                    if (!scanned) return "nothing left to scan - wait a moment";

                    // echo the landed scan (price = observed total delta, exact by construction)
                    // and the authoritative per-customer running total, so the client's
                    // checkout display is SET to the truth instead of accumulating forever
                    double after = FiTotalScanned?.GetValue(counter) is double a ? a : before;
                    using (var ms = new MemoryStream())
                    using (var bw = new BinaryWriter(ms))
                    {
                        bw.Write((byte)counterIndex);
                        bw.Write(scannedCard != null);
                        bw.Write(after - before);
                        bw.Write(after);
                        if (scannedCard != null) Net.Msg.WriteCard(bw, scannedCard);
                        else bw.Write((int)scannedType);
                        bw.Flush();
                        scanEcho = ms.ToArray();
                    }

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
                        // card: EvaluateCreditCard completes the transaction in one step
                        double totalCost = FiTotalScanned?.GetValue(counter) is double d ? d : 0.0;
                        MiCreditCard?.Invoke(counter, new object[] { totalCost });
                        CoopPlugin.Log.LogInfo($"{serverName} completed a card sale at register {counterIndex}");
                        return "sale complete!";
                    }
                    // cash is two-phase, exactly like the worker automation:
                    // 1) count exact change  2) hand it over (the SPACE action)
                    bool changeReady = FiIsChangeReady?.GetValue(counter) is bool r && r;
                    if (!changeReady)
                    {
                        MiNpcChange?.Invoke(counter, null);
                        MiCheckChangeReady?.Invoke(counter, null);
                        return "change counted - click again to hand it over";
                    }
                    MiSpaceBar?.Invoke(counter, null);
                    CoopPlugin.Log.LogInfo($"{serverName} completed a cash sale at register {counterIndex}");
                    return "sale complete!";
                }
                default:
                    return "no customer ready at this register";
            }
        }

        /// <summary>Client: apply a scan echo. The counter's running total is SET to the
        /// host's authoritative value (minus this scan, which AddScanned* re-adds), so the
        /// checkout display can never drift or accumulate across customers.</summary>
        public static void ApplyScanEcho(InteractableCashierCounter counter, bool isCard,
            double price, double hostTotal, EItemType itemType, CardData card)
        {
            FiTotalScanned?.SetValue(counter, hostTotal - price);
            if (isCard) counter.AddScannedCardCostTotal(price, card);
            else counter.AddScannedItemCostTotal(price, itemType);
        }

        /// <summary>Client: zero every counter's running total (sale completed).</summary>
        public static void ClientResetTotals()
        {
            var sm = Object.FindObjectOfType<ShelfManager>();
            if (sm == null) return;
            for (int i = 0; i < sm.m_CashierCounterList.Count; i++)
                if (sm.m_CashierCounterList[i] != null)
                    FiTotalScanned?.SetValue(sm.m_CashierCounterList[i], 0.0);
        }

        // ================= register state mirroring (host -> client) =================

        public struct CounterInfo
        {
            public byte Index;
            public byte State;     // ECashierCounterState
            public byte Scanned;
            public byte Total;
            public List<int> ItemTypes;      // unscanned cart items (for the visual mirror)
            public List<Vector3> ItemLocal;  // their REAL positions, local to the counter
        }

        /// <summary>Host: 2 Hz snapshot of every counter that has a current customer.</summary>
        public static byte[] CollectStates()
        {
            var sm = Object.FindObjectOfType<ShelfManager>();
            if (sm == null) return null;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                int count = 0;
                bw.Write((byte)0);
                for (int i = 0; i < sm.m_CashierCounterList.Count && i < 250; i++)
                {
                    var counter = sm.m_CashierCounterList[i];
                    if (counter == null) continue;
                    var cust = counter.m_CurrentCustomer;
                    if (cust == null || !cust.m_IsActive) continue;

                    var items = cust.GetItemInBagList();
                    var cards = cust.GetCardInBagList();
                    int total = items.Count + cards.Count;
                    int scanned = FiScannedCount?.GetValue(cust) is int c ? c : 0;

                    bw.Write((byte)i);
                    bw.Write((byte)counter.m_CashierCounterState);
                    bw.Write((byte)Mathf.Clamp(scanned, 0, 255));
                    bw.Write((byte)Mathf.Clamp(total, 0, 255));
                    int n = Mathf.Min(items.Count, 12);
                    int written = 0;
                    var typeBuf = new List<int>(n);
                    var posBuf = new List<Vector3>(n);
                    var ct = counter.transform;
                    for (int k = 0; k < items.Count && written < n; k++)
                    {
                        var scan = items[k] != null ? items[k].m_InteractableScanItem : null;
                        if (scan != null && scan.IsNotScanned())
                        {
                            typeBuf.Add((int)items[k].GetItemType());
                            posBuf.Add(ct.InverseTransformPoint(items[k].transform.position));
                            written++;
                        }
                    }
                    bw.Write((byte)typeBuf.Count);
                    for (int k = 0; k < typeBuf.Count; k++)
                    {
                        bw.Write(typeBuf[k]);
                        bw.Write(posBuf[k].x); bw.Write(posBuf[k].y); bw.Write(posBuf[k].z);
                    }
                    count++;
                }
                if (count == 0) return null;
                bw.Flush();
                ms.Position = 0;
                ms.WriteByte((byte)count);
                return ms.ToArray();
            }
        }

        public static List<CounterInfo> ReadStates(BinaryReader br)
        {
            int count = br.ReadByte();
            var list = new List<CounterInfo>(count);
            for (int i = 0; i < count; i++)
            {
                var ci = new CounterInfo
                {
                    Index = br.ReadByte(),
                    State = br.ReadByte(),
                    Scanned = br.ReadByte(),
                    Total = br.ReadByte(),
                };
                int n = br.ReadByte();
                ci.ItemTypes = new List<int>(n);
                ci.ItemLocal = new List<Vector3>(n);
                for (int k = 0; k < n; k++)
                {
                    ci.ItemTypes.Add(br.ReadInt32());
                    ci.ItemLocal.Add(new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
                }
                list.Add(ci);
            }
            return list;
        }
    }

    /// <summary>
    /// Client-side visual mirror of the register: shows the current customer's unscanned
    /// cart items on the counter (pooled Item meshes) and provides the data for the
    /// "press V to serve" prompt.
    /// </summary>
    public class RegisterMirror
    {
        private readonly Dictionary<int, RegisterServe.CounterInfo> _states = new Dictionary<int, RegisterServe.CounterInfo>();
        private readonly Dictionary<int, List<Item>> _props = new Dictionary<int, List<Item>>();
        private readonly Dictionary<int, string> _propSig = new Dictionary<int, string>();
        private readonly Dictionary<Collider, int> _propColliders = new Dictionary<Collider, int>();
        private ShelfManager _sm;
        private float _staleTimer;

        /// <summary>Was this collider one of our mirrored cart items? (click-to-scan)</summary>
        public bool TryGetPropCounter(Collider c, out int counterIdx)
        {
            return _propColliders.TryGetValue(c, out counterIdx);
        }

        /// <summary>True while the nearest counter waits for a payment/change click.</summary>
        public bool IsPaymentPhase(int counterIdx)
        {
            return _states.TryGetValue(counterIdx, out var ci)
                && ((ECashierCounterState)ci.State == ECashierCounterState.TakingCash
                 || (ECashierCounterState)ci.State == ECashierCounterState.GivingChange);
        }

        public void Reset()
        {
            foreach (var list in _props.Values)
                foreach (var item in list)
                    if (item != null) ItemSpawnManager.DisableItem(item);
            _props.Clear();
            _propSig.Clear();
            _propColliders.Clear();
            _states.Clear();
            _sm = null;
            _staleTimer = 0f;
        }

        public void Apply(List<RegisterServe.CounterInfo> infos)
        {
            _states.Clear();
            foreach (var ci in infos) _states[ci.Index] = ci;
            _staleTimer = 0f;
            RefreshProps();
        }

        public void Tick(float dt)
        {
            _staleTimer += dt;
            if (_staleTimer > 3f && _states.Count > 0)
            {
                _states.Clear();
                RefreshProps();
            }
        }

        /// <summary>Prompt for the nearest counter with a customer, or null.</summary>
        public string PromptFor(int nearestCounter)
        {
            if (nearestCounter < 0 || !_states.TryGetValue(nearestCounter, out var ci)) return null;
            var state = (ECashierCounterState)ci.State;
            switch (state)
            {
                case ECashierCounterState.ScanningItem:
                    return $"press {CoopPlugin.ServeKey.Value} - scan items ({ci.Scanned}/{ci.Total})";
                case ECashierCounterState.TakingCash:
                    return $"press {CoopPlugin.ServeKey.Value} - take payment";
                case ECashierCounterState.GivingChange:
                    return $"press {CoopPlugin.ServeKey.Value} - give change";
                default:
                    return null;
            }
        }

        private void ReleaseProps(int idx)
        {
            foreach (var item in _props[idx])
            {
                if (item == null) continue;
                if (item.m_Collider != null) _propColliders.Remove(item.m_Collider);
                ItemSpawnManager.DisableItem(item);
            }
        }

        private void RefreshProps()
        {
            if (_sm == null) _sm = Object.FindObjectOfType<ShelfManager>();
            if (_sm == null) return;

            // release props for counters that no longer have a customer
            List<int> drop = null;
            foreach (var kv in _props)
                if (!_states.ContainsKey(kv.Key)) (drop = drop ?? new List<int>()).Add(kv.Key);
            if (drop != null)
                foreach (int idx in drop)
                {
                    ReleaseProps(idx);
                    _props.Remove(idx);
                    _propSig.Remove(idx);
                }

            foreach (var kv in _states)
            {
                int idx = kv.Key;
                var ci = kv.Value;
                if (idx >= _sm.m_CashierCounterList.Count) continue;
                var counter = _sm.m_CashierCounterList[idx];
                if (counter == null) continue;

                string sig = string.Join(",", ci.ItemTypes);
                if (_propSig.TryGetValue(idx, out var oldSig) && oldSig == sig) continue;
                _propSig[idx] = sig;

                if (_props.ContainsKey(idx)) ReleaseProps(idx);
                var list = new List<Item>();
                _props[idx] = list;

                var t = counter.transform;
                for (int k = 0; k < ci.ItemTypes.Count; k++)
                {
                    try
                    {
                        var meshData = InventoryBase.GetItemMeshData((EItemType)ci.ItemTypes[k]);
                        if (meshData == null) continue;
                        var item = ItemSpawnManager.GetItem(t);
                        item.SetMesh(meshData.mesh, meshData.material, (EItemType)ci.ItemTypes[k],
                            meshData.meshSecondary, meshData.materialSecondary, meshData.materialList);
                        // exactly where the item really sits on the host's counter
                        var local = k < ci.ItemLocal.Count
                            ? ci.ItemLocal[k]
                            : new Vector3(-0.45f + (k % 4) * 0.3f, 1.02f, 0.05f + (k / 4) * 0.3f);
                        item.transform.position = t.TransformPoint(local);
                        item.transform.rotation = t.rotation;
                        item.gameObject.SetActive(true);
                        if (item.m_Rigidbody != null) item.m_Rigidbody.isKinematic = true;
                        if (item.m_Collider != null)
                        {
                            item.m_Collider.enabled = true; // clickable: click a cart item to scan it
                            _propColliders[item.m_Collider] = idx;
                        }
                        list.Add(item);
                    }
                    catch { }
                }
            }
        }
    }
}
