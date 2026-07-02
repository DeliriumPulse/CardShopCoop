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

        /// <summary>Host: execute one register step on the given counter. Returns feedback text.</summary>
        public static string Serve(int counterIndex, string serverName)
        {
            string result = ServeInner(counterIndex, serverName);
            CoopPlugin.Log.LogInfo($"serve: {serverName} @ counter {counterIndex} -> {result}");
            return result;
        }

        private static string ServeInner(int counterIndex, string serverName)
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

        // ================= register state mirroring (host -> client) =================

        public struct CounterInfo
        {
            public byte Index;
            public byte State;     // ECashierCounterState
            public byte Scanned;
            public byte Total;
            public List<int> ItemTypes; // unscanned cart items (for the visual mirror)
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
                    for (int k = 0; k < items.Count && written < n; k++)
                    {
                        var scan = items[k] != null ? items[k].m_InteractableScanItem : null;
                        if (scan != null && scan.IsNotScanned())
                        {
                            typeBuf.Add((int)items[k].GetItemType());
                            written++;
                        }
                    }
                    bw.Write((byte)typeBuf.Count);
                    foreach (int t in typeBuf) bw.Write(t);
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
                for (int k = 0; k < n; k++) ci.ItemTypes.Add(br.ReadInt32());
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
        private ShelfManager _sm;
        private float _staleTimer;

        public void Reset()
        {
            foreach (var list in _props.Values)
                foreach (var item in list)
                    if (item != null) ItemSpawnManager.DisableItem(item);
            _props.Clear();
            _propSig.Clear();
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
                    foreach (var item in _props[idx]) if (item != null) ItemSpawnManager.DisableItem(item);
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

                if (_props.TryGetValue(idx, out var old))
                    foreach (var item in old) if (item != null) ItemSpawnManager.DisableItem(item);
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
                        // small grid on the counter top
                        var local = new Vector3(-0.45f + (k % 4) * 0.3f, 1.02f, 0.05f + (k / 4) * 0.3f);
                        item.transform.position = t.TransformPoint(local);
                        item.transform.rotation = t.rotation;
                        item.gameObject.SetActive(true);
                        if (item.m_Rigidbody != null) item.m_Rigidbody.isKinematic = true;
                        if (item.m_Collider != null) item.m_Collider.enabled = false;
                        list.Add(item);
                    }
                    catch { }
                }
            }
        }
    }
}
