using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// v0.3 phase A: the host streams every active customer and worker (identity, wardrobe
    /// name, position, yaw, speed, action flags) at 8 Hz; the client renders them as
    /// stripped-clone puppets - the same technique as player avatars. Identity is the
    /// NPC's index in its manager list, which the game keeps stable for a whole session
    /// (pooled, append-only). Appearance is CharacterCustomization.CharacterName, which
    /// deterministically dresses a fresh clone via the game's own wardrobe Initialize().
    /// </summary>
    public class NpcSync
    {
        private const byte KindCustomer = 0;
        private const byte KindWorker = 1;

        [System.Flags]
        private enum NpcFlags : byte
        {
            None = 0,
            HoldingBag = 1,
            HandingOverCash = 2,
            IsSitting = 4,
            IsPlaying = 8,
            IsHoldingBox = 16,
        }

        // ---------------- host: collect & serialize ----------------

        private CustomerManager _cm;
        private float _sendTimer;

        public void Reset()
        {
            _cm = null;
            _sendTimer = 0f;
            ClearPuppets();
        }

        /// <summary>Host only. Serializes active NPCs into a NpcState payload (or null).</summary>
        public byte[] HostCollect(float dt)
        {
            _sendTimer += dt;
            if (_sendTimer < 0.125f) return null;
            _sendTimer = 0f;

            if (_cm == null) _cm = Object.FindObjectOfType<CustomerManager>();
            if (_cm == null) return null;

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                int countPos = 0;
                bw.Write((byte)0); // count placeholder
                int count = 0;

                var customers = _cm.GetCustomerList();
                for (int i = 0; i < customers.Count && count < 250; i++)
                {
                    var c = customers[i];
                    if (c == null || !c.m_IsActive || !c.gameObject.activeSelf) continue;
                    WriteNpc(bw, KindCustomer, (ushort)i,
                        c.m_CharacterCustom != null ? c.m_CharacterCustom.CharacterName ?? "" : "",
                        c.transform, c.m_CurrentMoveSpeed, CollectFlags(c.m_Anim));
                    count++;
                }

                var workers = WorkerManager.GetWorkerList();
                if (workers != null)
                {
                    for (int i = 0; i < workers.Count && count < 250; i++)
                    {
                        var w = workers[i];
                        if (w == null || !w.m_IsActive || !w.gameObject.activeSelf) continue;
                        WriteNpc(bw, KindWorker, (ushort)i,
                            w.m_CharacterCustom != null ? w.m_CharacterCustom.CharacterName ?? "" : "",
                            w.transform, 0f, CollectFlags(w.m_Anim), speedFromAnim: w.m_Anim);
                        count++;
                    }
                }

                bw.Flush();
                ms.Position = countPos;
                ms.WriteByte((byte)count);
                return count > 0 ? ms.ToArray() : null;
            }
        }

        private static void WriteNpc(BinaryWriter bw, byte kind, ushort index, string charName,
            Transform t, float moveSpeed, NpcFlags flags, Animator speedFromAnim = null)
        {
            if (speedFromAnim != null)
            {
                try { moveSpeed = speedFromAnim.GetFloat("MoveSpeed"); } catch { }
            }
            bw.Write(kind);
            bw.Write(index);
            bw.Write(charName);
            var p = t.position;
            bw.Write(p.x); bw.Write(p.y); bw.Write(p.z);
            bw.Write(t.eulerAngles.y);
            bw.Write(moveSpeed);
            bw.Write((byte)flags);
        }

        private static NpcFlags CollectFlags(Animator anim)
        {
            var f = NpcFlags.None;
            if (anim == null) return f;
            try
            {
                if (anim.GetBool("HoldingBag")) f |= NpcFlags.HoldingBag;
                if (anim.GetBool("HandingOverCash")) f |= NpcFlags.HandingOverCash;
                if (anim.GetBool("IsSitting")) f |= NpcFlags.IsSitting;
                if (anim.GetBool("IsPlaying")) f |= NpcFlags.IsPlaying;
                if (anim.GetBool("IsHoldingBox")) f |= NpcFlags.IsHoldingBox;
            }
            catch { }
            return f;
        }

        // ---------------- client: puppets ----------------

        private class Puppet
        {
            public GameObject Go;
            public Animator Anim;
            public GameObject Bag;
            public GameObject Cash;
            public GameObject CardFan;
            public GameObject CardSingle;
            public string CharName = "";
            public Vector3 TargetPos;
            public float TargetYaw;
            public float Speed;
            public NpcFlags Flags;
            public float LastSeen;
        }

        private readonly Dictionary<int, Puppet> _puppets = new Dictionary<int, Puppet>();
        private CustomerManager _cmClient;
        private float _now;

        public int PuppetCount => _puppets.Count;

        /// <summary>Diagnostic: how many REAL (non-puppet) NPCs are currently active in
        /// this instance's own managers. On the host that's the true crowd; on the client
        /// it should be zero (anything else is escaping suppression).</summary>
        public static int CountLocalActiveNpcs()
        {
            int n = 0;
            var cm = Object.FindObjectOfType<CustomerManager>();
            if (cm != null)
            {
                var list = cm.GetCustomerList();
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].gameObject.activeSelf) n++;
            }
            var workers = WorkerManager.GetWorkerList();
            if (workers != null)
                for (int i = 0; i < workers.Count; i++)
                    if (workers[i] != null && workers[i].gameObject.activeSelf) n++;
            return n;
        }

        public void ClearPuppets()
        {
            foreach (var p in _puppets.Values)
                if (p.Go != null) Object.Destroy(p.Go);
            _puppets.Clear();
            _cmClient = null;
        }

        /// <summary>Client only. Apply one received NpcState batch.</summary>
        public void ApplyBatch(BinaryReader br, bool inGame)
        {
            int count = br.ReadByte();
            for (int n = 0; n < count; n++)
            {
                byte kind = br.ReadByte();
                ushort index = br.ReadUInt16();
                string charName = br.ReadString();
                var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                float yaw = br.ReadSingle();
                float speed = br.ReadSingle();
                var flags = (NpcFlags)br.ReadByte();
                if (!inGame) continue; // consume the payload, render nothing yet

                int key = (kind << 16) | index;
                if (!_puppets.TryGetValue(key, out var p))
                {
                    p = new Puppet();
                    _puppets[key] = p;
                }
                p.TargetPos = pos;
                p.TargetYaw = yaw;
                p.Speed = speed;
                p.Flags = flags;
                p.LastSeen = _now;
                if (p.Go == null || p.CharName != charName)
                {
                    if (p.Go != null) Object.Destroy(p.Go);
                    Spawn(p, charName, pos);
                }
            }
        }

        /// <summary>Client only. Interpolate puppets; despawn ones the host stopped sending.</summary>
        public void TickPuppets(float dt, bool inGame)
        {
            _now += dt;
            if (!inGame) return;
            List<int> dead = null;
            foreach (var kv in _puppets)
            {
                var p = kv.Value;
                if (_now - p.LastSeen > 1.5f)
                {
                    if (p.Go != null) Object.Destroy(p.Go);
                    (dead = dead ?? new List<int>()).Add(kv.Key);
                    continue;
                }
                if (p.Go == null) continue;
                var t = p.Go.transform;
                bool snap = (t.position - p.TargetPos).sqrMagnitude > 25f; // teleports (spawn, seat snap)
                t.position = snap ? p.TargetPos : Vector3.Lerp(t.position, p.TargetPos, dt * 10f);
                t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, p.TargetYaw, 0f), dt * 10f);

                if (p.Anim != null)
                {
                    try
                    {
                        p.Anim.SetFloat("MoveSpeed", p.Speed);
                        p.Anim.SetBool("HoldingBag", (p.Flags & NpcFlags.HoldingBag) != 0);
                        p.Anim.SetBool("HandingOverCash", (p.Flags & NpcFlags.HandingOverCash) != 0);
                        p.Anim.SetBool("IsSitting", (p.Flags & NpcFlags.IsSitting) != 0);
                        p.Anim.SetBool("IsPlaying", (p.Flags & NpcFlags.IsPlaying) != 0);
                        p.Anim.SetBool("IsHoldingBox", (p.Flags & NpcFlags.IsHoldingBox) != 0);
                    }
                    catch { }
                }
                Toggle(p.Bag, (p.Flags & NpcFlags.HoldingBag) != 0);
                Toggle(p.Cash, (p.Flags & NpcFlags.HandingOverCash) != 0);
                Toggle(p.CardFan, (p.Flags & NpcFlags.IsPlaying) != 0);
                Toggle(p.CardSingle, (p.Flags & NpcFlags.IsPlaying) != 0);
            }
            if (dead != null) foreach (int k in dead) _puppets.Remove(k);
        }

        private static void Toggle(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        private void Spawn(Puppet p, string charName, Vector3 pos)
        {
            if (_cmClient == null) _cmClient = Object.FindObjectOfType<CustomerManager>();
            if (_cmClient == null) return;

            bool female = charName.StartsWith("Female");
            var prefab = female ? _cmClient.m_CustomerFemalePrefab : _cmClient.m_CustomerPrefab;
            if (prefab == null) return;

            var holder = new GameObject("CoopNpcHolder_tmp");
            holder.SetActive(false);
            var clone = Object.Instantiate(prefab.gameObject, holder.transform);
            clone.transform.SetParent(null, worldPositionStays: false);
            clone.transform.position = pos;
            clone.SetActive(true);
            Object.Destroy(holder);

            var cust = clone.GetComponent<Customer>();
            try
            {
                if (cust != null && cust.m_CharacterCustom != null && charName.Length > 0)
                {
                    cust.m_CharacterCustom.CharacterName = charName;
                    cust.m_CharacterCustom.Initialize(); // deterministic wardrobe by name
                }
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning($"NPC dressing '{charName}': {e.Message}");
            }

            // capture prop children BEFORE stripping the Customer script
            if (cust != null)
            {
                p.Bag = cust.m_ShoppingBagTransform != null ? cust.m_ShoppingBagTransform.gameObject : null;
                p.Cash = cust.m_CustomerCash != null ? cust.m_CustomerCash.gameObject : null;
                p.CardFan = cust.m_GameCardFanOut;
                p.CardSingle = cust.m_GameCardSingle;
                try
                {
                    Toggle(p.Bag, false); Toggle(p.Cash, false);
                    Toggle(p.CardFan, false); Toggle(p.CardSingle, false);
                    if (cust.m_CleanFX != null) cust.m_CleanFX.SetActive(false);
                    if (cust.m_ExclaimationMesh != null) cust.m_ExclaimationMesh.SetActive(false);
                    if (cust.m_InteractCollider != null) cust.m_InteractCollider.SetActive(false);
                    if (cust.m_SmellyFX != null) cust.m_SmellyFX.SetActive(false);
                }
                catch { }
            }

            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string tn = mb.GetType().Name;
                if (tn == "CopyPose" || tn == "BlendshapeManager" || tn == "ScaleCharacter"
                    || tn == "TransformBone" || tn == "MipBiasAdjust")
                    continue;
                Object.DestroyImmediate(mb);
            }
            foreach (var comp in clone.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                string n = comp.GetType().Name;
                if (n == "NavMeshAgent" || n == "NavMeshObstacle" || n == "Seeker" || n == "FunnelModifier")
                    Object.DestroyImmediate(comp);
            }
            foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);
            foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(rb);

            clone.name = "CoopNpc_" + charName;
            p.Go = clone;
            p.Anim = clone.GetComponentInChildren<Animator>(true);
            p.CharName = charName;
        }
    }
}
