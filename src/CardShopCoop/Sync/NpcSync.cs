using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// v0.3 phase B: the host streams every active customer and worker (identity, position,
    /// yaw, speed, action flags) at 8 Hz; the client renders them as stripped-clone puppets.
    /// Identity is the NPC's index in its manager list, which the game keeps stable for a
    /// whole session (pooled, append-only). Appearance is CharacterCustomization.CharacterName,
    /// sent only when it changes (plus a periodic refresh for late joiners / lost packets);
    /// the client dresses clones via the game's own wardrobe Initialize().
    ///
    /// Motion model: snapshot interpolation. Each batch carries the host's clock; the client
    /// maps host time onto its own timeline (low-passed offset, so network jitter cannot
    /// corrupt snapshot spacing) and renders 150 ms in the past between the two bracketing
    /// snapshots of a small per-puppet ring buffer. Extrapolation only happens when the
    /// buffer runs dry, capped and velocity-decayed. This keeps puppet velocity continuous
    /// instead of the surge-correct-surge of chasing the newest packet.
    ///
    /// Wire format is internal to this class (CoopCore just relays payload bytes). Batches
    /// are chunked below the 1200-byte Steam unreliable packet limit so a crowded shop can
    /// never silently drop the whole tick.
    /// </summary>
    public class NpcSync
    {
        private const byte KindCustomer = 0;
        private const byte KindWorker = 1;

        private const float SendInterval = 0.125f;
        /// <summary>Render this far in the past (~1.2x send interval) so two bracketing
        /// snapshots almost always exist.</summary>
        private const float InterpDelay = 0.15f;
        /// <summary>Flush a chunk before it can cross 1200 bytes with one more entry
        /// (Steam unreliable limit incl. our 5-byte framing).</summary>
        private const int ChunkSoftLimit = 1100;
        /// <summary>Names normally go out only on change; a periodic full refresh covers
        /// late joiners and name packets lost on the unreliable channel.</summary>
        private const float NameRefreshInterval = 5f;

        // string-keyed animator calls hash the name on every call; cache the ids once
        private static readonly int HashMoveSpeed = Animator.StringToHash("MoveSpeed");
        private static readonly int HashHoldingBag = Animator.StringToHash("HoldingBag");
        private static readonly int HashHandingOverCash = Animator.StringToHash("HandingOverCash");
        private static readonly int HashIsSitting = Animator.StringToHash("IsSitting");
        private static readonly int HashIsPlaying = Animator.StringToHash("IsPlaying");
        private static readonly int HashIsHoldingBox = Animator.StringToHash("IsHoldingBox");

        [System.Flags]
        private enum NpcFlags : byte
        {
            None = 0,
            HoldingBag = 1,
            HandingOverCash = 2,
            IsSitting = 4,
            IsPlaying = 8,
            IsHoldingBox = 16,
            Smelly = 32,
            Exclaim = 64,   // the red "!" trade-prompt mesh is showing
            Female = 128,   // puppet should spawn from the female prefab (workers esp.)
        }

        // ---------------- host: collect & serialize ----------------

        private CustomerManager _cm;
        private float _sendTimer;
        private float _nameRefreshIn;
        private MemoryStream _sendMs;
        private BinaryWriter _sendBw;
        private int _chunkCount;
        private readonly Dictionary<int, string> _sentNames = new Dictionary<int, string>();

        public void Reset()
        {
            _cm = null;
            _sendTimer = 0f;
            _nameRefreshIn = 0f;
            _sentNames.Clear();
            ClearPuppets();
        }

        /// <summary>Host only. Serializes active NPCs into one or more NpcState payloads,
        /// each under the Steam unreliable packet limit (null when not due / nothing).</summary>
        public List<byte[]> HostCollect(float dt)
        {
            _sendTimer += dt;
            if (_sendTimer < SendInterval) return null;
            _sendTimer -= SendInterval; // preserve cadence across frame boundaries
            if (_sendTimer > SendInterval) _sendTimer = SendInterval; // clamp debt after a hitch

            if (_cm == null) _cm = Object.FindObjectOfType<CustomerManager>();
            if (_cm == null) return null;

            _nameRefreshIn -= SendInterval;
            if (_nameRefreshIn <= 0f)
            {
                _sentNames.Clear();
                _nameRefreshIn = NameRefreshInterval;
            }

            if (_sendMs == null)
            {
                _sendMs = new MemoryStream(1280);
                _sendBw = new BinaryWriter(_sendMs);
            }

            var chunks = new List<byte[]>(1);
            float hostTime = Time.unscaledTime;
            BeginChunk(hostTime);

            var customers = _cm.GetCustomerList();
            for (int i = 0; i < customers.Count; i++)
            {
                var c = customers[i];
                if (c == null || !c.m_IsActive || !c.gameObject.activeSelf) continue;
                // m_CharacterCustom is momentarily null during pooled activation; skipping
                // one tick is harmless (client despawn timeout is 1.5s) whereas an empty
                // name would churn the puppet through a bogus re-dress
                var cc = c.m_CharacterCustom;
                if (cc == null || string.IsNullOrEmpty(cc.CharacterName)) continue;
                var flags = CollectFlags(c.m_Anim);
                // smelly is sim state, not an animator bool - without it the joiner
                // can't see the stink cloud the host (and the cleansers) react to
                try { if (c.IsSmelly()) flags |= NpcFlags.Smelly; } catch { }
                // the red "!" trade/sell-in prompt is a plain mesh toggle, not an animator
                // bool - mirror it so the guest can see which customer wants to be served
                try { if (c.m_ExclaimationMesh != null && c.m_ExclaimationMesh.activeSelf) flags |= NpcFlags.Exclaim; } catch { }
                WriteEntry(chunks, hostTime, KindCustomer, (ushort)i, cc.CharacterName,
                    c.transform, c.m_CurrentMoveSpeed, flags);
            }

            var workers = WorkerManager.GetWorkerList();
            if (workers != null)
            {
                for (int i = 0; i < workers.Count; i++)
                {
                    var w = workers[i];
                    if (w == null || !w.m_IsActive || !w.gameObject.activeSelf) continue;
                    var cc = w.m_CharacterCustom;
                    if (cc == null || string.IsNullOrEmpty(cc.CharacterName)) continue;
                    // worker names aren't prefixed "Female", so gender must ride a flag or
                    // female workers spawn from the male customer prefab on the guest
                    var wflags = CollectFlags(w.m_Anim);
                    if (w.m_IsFemale) wflags |= NpcFlags.Female;
                    WriteEntry(chunks, hostTime, KindWorker, (ushort)i, cc.CharacterName,
                        w.transform, 0f, wflags, speedFromAnim: w.m_Anim);
                }
            }

            FlushChunk(chunks);
            return chunks.Count > 0 ? chunks : null;
        }

        private void BeginChunk(float hostTime)
        {
            _sendMs.SetLength(0); // also rewinds Position; one stream reused across all chunks
            _sendBw.Write(hostTime);
            _sendBw.Write((byte)0); // count placeholder at offset 4
            _chunkCount = 0;
        }

        private void FlushChunk(List<byte[]> chunks)
        {
            if (_chunkCount == 0) return;
            _sendBw.Flush();
            _sendMs.Position = 4;
            _sendMs.WriteByte((byte)_chunkCount);
            chunks.Add(_sendMs.ToArray());
            _chunkCount = 0;
        }

        private void WriteEntry(List<byte[]> chunks, float hostTime, byte kind, ushort index,
            string charName, Transform t, float moveSpeed, NpcFlags flags, Animator speedFromAnim = null)
        {
            if (_sendMs.Position >= ChunkSoftLimit || _chunkCount == byte.MaxValue)
            {
                FlushChunk(chunks);
                BeginChunk(hostTime);
            }
            if (speedFromAnim != null)
            {
                try { moveSpeed = speedFromAnim.GetFloat(HashMoveSpeed); } catch { }
            }
            int key = (kind << 16) | index;
            bool sendName = !_sentNames.TryGetValue(key, out var prev) || prev != charName;
            if (sendName) _sentNames[key] = charName;

            _sendBw.Write(kind);
            _sendBw.Write(index);
            _sendBw.Write((byte)(sendName ? 1 : 0));
            if (sendName) _sendBw.Write(charName);
            var p = t.position;
            _sendBw.Write(p.x); _sendBw.Write(p.y); _sendBw.Write(p.z);
            _sendBw.Write(t.eulerAngles.y);
            _sendBw.Write(moveSpeed);
            _sendBw.Write((byte)flags);
            _chunkCount++;
        }

        private static NpcFlags CollectFlags(Animator anim)
        {
            var f = NpcFlags.None;
            if (anim == null) return f;
            try
            {
                if (anim.GetBool(HashHoldingBag)) f |= NpcFlags.HoldingBag;
                if (anim.GetBool(HashHandingOverCash)) f |= NpcFlags.HandingOverCash;
                if (anim.GetBool(HashIsSitting)) f |= NpcFlags.IsSitting;
                if (anim.GetBool(HashIsPlaying)) f |= NpcFlags.IsPlaying;
                if (anim.GetBool(HashIsHoldingBox)) f |= NpcFlags.IsHoldingBox;
            }
            catch { }
            return f;
        }

        // ---------------- client: puppets ----------------

        private struct Snap
        {
            public Vector3 Pos;
            public float Yaw;
            public float Speed;
            public NpcFlags Flags;
            public float Time; // host time mapped onto the local _now timeline
        }

        private class Puppet
        {
            public GameObject Go;
            public Animator Anim;
            public CC.CharacterCustomization Custom;
            public GameObject Bag;
            public GameObject Cash;
            public GameObject CardFan;
            public GameObject CardSingle;
            public GameObject Smelly;
            public GameObject Exclaim;   // the red "!" trade prompt mesh
            public bool Female;          // which prefab this puppet was spawned from
            public string CharName = "";
            public readonly Snap[] Buf = new Snap[4]; // ring buffer, newest at BufHead
            public int BufHead;
            public int BufCount;
            public NpcFlags Flags;
            public int AppliedFlags = -1; // -1 forces the first animator/prop push
            public float LastSeen;
            public float RenderYaw;
            public Vector3 PrevRenderedPos;
            public float AnimSpeed;
        }

        private readonly Dictionary<int, Puppet> _puppets = new Dictionary<int, Puppet>();
        private CustomerManager _cmClient;
        private float _now;
        private float _clockOffset;
        private bool _clockInit;

        public int PuppetCount => _puppets.Count;

        private static CustomerManager s_diagCm;

        /// <summary>Diagnostic: how many REAL (non-puppet) NPCs are currently active in
        /// this instance's own managers. On the host that's the true crowd; on the client
        /// it should be zero (anything else is escaping suppression).</summary>
        public static int CountLocalActiveNpcs()
        {
            // cached across calls; Unity's overloaded == re-resolves after scene changes
            if (s_diagCm == null) s_diagCm = Object.FindObjectOfType<CustomerManager>();
            int n = 0;
            if (s_diagCm != null)
            {
                var list = s_diagCm.GetCustomerList();
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
            _clockInit = false;
        }

        /// <summary>Client only. Apply one received NpcState batch.</summary>
        public void ApplyBatch(BinaryReader br, bool inGame)
        {
            float hostTime = br.ReadSingle();
            int count = br.ReadByte();

            // map host time onto the local timeline; low-pass the offset so per-packet
            // network jitter cannot corrupt snapshot spacing (snap on init / big jumps)
            float rawOffset = _now - hostTime;
            if (!_clockInit || Mathf.Abs(rawOffset - _clockOffset) > 1f)
            {
                // re-basing invalidates buffered snapshot times; drop them so the
                // stale-packet guard cannot reject fresh snapshots against old ones
                if (_clockInit)
                    foreach (var pup in _puppets.Values) pup.BufCount = 0;
                _clockOffset = rawOffset;
                _clockInit = true;
            }
            else _clockOffset += 0.1f * (rawOffset - _clockOffset);
            float snapTime = hostTime + _clockOffset;

            for (int n = 0; n < count; n++)
            {
                byte kind = br.ReadByte();
                ushort index = br.ReadUInt16();
                bool hasName = (br.ReadByte() & 1) != 0;
                string charName = hasName ? br.ReadString() : null;
                var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                float yaw = br.ReadSingle();
                float speed = br.ReadSingle();
                var flags = (NpcFlags)br.ReadByte();
                if (!inGame) continue; // consume the payload, render nothing yet

                int key = (kind << 16) | index;
                if (!_puppets.TryGetValue(key, out var p))
                {
                    // no cached wardrobe yet: skip this tick; the periodic name refresh
                    // (or the next change) delivers it well inside the despawn timeout
                    if (!hasName) continue;
                    p = new Puppet();
                    _puppets[key] = p;
                }

                bool female = (flags & NpcFlags.Female) != 0;
                if (hasName && p.CharName != charName)
                    ReDress(p, charName, pos, female);
                else if (p.Go == null && p.CharName.Length > 0)
                    Spawn(p, p.CharName, pos, female); // retry a spawn that failed (e.g. manager not ready)

                // reject stale/duplicate packets (unreliable channel can reorder)
                if (p.BufCount == 0 || snapTime > p.Buf[p.BufHead].Time + 0.0005f)
                {
                    p.BufHead = (p.BufHead + 1) & 3;
                    p.Buf[p.BufHead] = new Snap
                    {
                        Pos = pos,
                        Yaw = yaw,
                        Speed = speed,
                        Flags = flags,
                        Time = snapTime,
                    };
                    if (p.BufCount < 4) p.BufCount++;
                }
                p.Flags = flags;
                p.LastSeen = _now;
            }
        }

        /// <summary>On a wardrobe change, re-dress the existing clone in place via the
        /// game's own Initialize() (m_HasInit routes to LoadFromJSON, which re-applies
        /// hair/apparel for the new name). Full respawn only when there is no clone yet
        /// or the male/female prefab no longer matches.</summary>
        private void ReDress(Puppet p, string charName, Vector3 pos, bool femaleHint)
        {
            // gender from the transmitted flag (workers) OR the "Female..." name prefix
            // (customers); compared to the prefab we actually spawned from (p.Female)
            bool female = femaleHint || (charName != null && charName.StartsWith("Female"));
            bool genderChanged = p.Go != null && p.Female != female;
            if (p.Go == null || p.Custom == null || genderChanged)
            {
                if (p.Go != null) Object.Destroy(p.Go);
                p.Go = null;
                Spawn(p, charName, pos, femaleHint);
                return;
            }
            p.CharName = charName;
            p.Go.name = "CoopNpc_" + charName;
            try
            {
                p.Custom.CharacterName = charName;
                p.Custom.Initialize();
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning($"NPC re-dressing '{charName}': {e.Message}");
            }
        }

        /// <summary>Client only. Interpolate puppets; despawn ones the host stopped sending.</summary>
        public void TickPuppets(float dt, bool inGame)
        {
            // Freeze the local clock while out of game: ApplyBatch also skips LastSeen
            // updates while !inGame, so advancing _now during a loading flicker would make
            // (_now - LastSeen) blow past the 6s despawn timeout and blink the WHOLE crowd
            // out on resume. Keeping _now anchored keeps both on the same timeline; the
            // clock-offset re-base on the first post-gap batch re-syncs interpolation.
            if (!inGame || dt <= 0f) return;
            _now += dt;

            float renderTime = _now - InterpDelay;
            // frame-rate-independent blend factors (never dt*k, which overshoots at low fps)
            float posBlend = 1f - Mathf.Exp(-18f * dt);
            float yawBlend = 1f - Mathf.Exp(-14f * dt);
            float speedBlend = 1f - Mathf.Exp(-8f * dt);

            List<int> dead = null;
            foreach (var kv in _puppets)
            {
                var p = kv.Value;
                // generous: NPC state rides the UNRELIABLE lane, and flaky NATs starve
                // it in bursts - a 1.5s timeout made whole crowds blink out and back
                // for players on rough connections (first field report)
                if (_now - p.LastSeen > 6f)
                {
                    if (p.Go != null) Object.Destroy(p.Go);
                    (dead = dead ?? new List<int>()).Add(kv.Key);
                    continue;
                }
                if (p.Go == null || p.BufCount == 0) continue;

                Sample(p, renderTime, out var target, out float targetYaw);

                var t = p.Go.transform;
                bool snap = (t.position - target).sqrMagnitude > 25f; // teleports (spawn, seat snap)
                var newPos = snap ? target : Vector3.Lerp(t.position, target, posBlend);
                t.position = newPos;
                p.RenderYaw = snap ? targetYaw : Mathf.LerpAngle(p.RenderYaw, targetYaw, yawBlend);
                t.rotation = Quaternion.Euler(0f, p.RenderYaw, 0f);

                // drive the walk cycle from what the puppet actually did this frame, not
                // the host's speed - that is what keeps feet and translation in sync
                float rendered = snap ? 0f : Mathf.Min((newPos - p.PrevRenderedPos).magnitude / dt, 10f);
                p.PrevRenderedPos = newPos;
                p.AnimSpeed = Mathf.Lerp(p.AnimSpeed, rendered, speedBlend);
                if (p.AnimSpeed < 0.05f) p.AnimSpeed = 0f;

                if (p.Anim != null)
                {
                    try { p.Anim.SetFloat(HashMoveSpeed, p.AnimSpeed); } catch { }
                }
                if ((int)p.Flags != p.AppliedFlags)
                {
                    if (p.Anim != null)
                    {
                        try
                        {
                            p.Anim.SetBool(HashHoldingBag, (p.Flags & NpcFlags.HoldingBag) != 0);
                            p.Anim.SetBool(HashHandingOverCash, (p.Flags & NpcFlags.HandingOverCash) != 0);
                            p.Anim.SetBool(HashIsSitting, (p.Flags & NpcFlags.IsSitting) != 0);
                            p.Anim.SetBool(HashIsPlaying, (p.Flags & NpcFlags.IsPlaying) != 0);
                            p.Anim.SetBool(HashIsHoldingBox, (p.Flags & NpcFlags.IsHoldingBox) != 0);
                        }
                        catch { }
                    }
                    Toggle(p.Bag, (p.Flags & NpcFlags.HoldingBag) != 0);
                    Toggle(p.Cash, (p.Flags & NpcFlags.HandingOverCash) != 0);
                    Toggle(p.CardFan, (p.Flags & NpcFlags.IsPlaying) != 0);
                    Toggle(p.CardSingle, (p.Flags & NpcFlags.IsPlaying) != 0);
                    Toggle(p.Smelly, (p.Flags & NpcFlags.Smelly) != 0);
                    Toggle(p.Exclaim, (p.Flags & NpcFlags.Exclaim) != 0);
                    p.AppliedFlags = (int)p.Flags;
                }
            }
            if (dead != null) foreach (int k in dead) _puppets.Remove(k);
        }

        /// <summary>Interpolate between the two snapshots bracketing renderTime. If the
        /// buffer is dry (newest snapshot older than renderTime) extrapolate from the
        /// newest, capped at 250 ms with decaying velocity so a stopped stream eases to a
        /// halt instead of gliding off.</summary>
        private static void Sample(Puppet p, float renderTime, out Vector3 target, out float targetYaw)
        {
            var newest = p.Buf[p.BufHead];
            if (newest.Time <= renderTime)
            {
                var v = Vector3.zero;
                if (p.BufCount >= 2)
                {
                    var prev = p.Buf[(p.BufHead + 3) & 3];
                    float span = newest.Time - prev.Time;
                    if (span > 0.001f)
                    {
                        v = (newest.Pos - prev.Pos) / span;
                        v.y = 0f;
                        v = Vector3.ClampMagnitude(v, 5f);
                    }
                }
                float ex = Mathf.Min(renderTime - newest.Time, 0.25f);
                v *= Mathf.Exp(-3f * ex);
                target = newest.Pos + v * ex;
                targetYaw = newest.Yaw;
                return;
            }

            // scan newest -> oldest for the first snapshot at or before renderTime
            var newer = newest;
            for (int k = 1; k < p.BufCount; k++)
            {
                var older = p.Buf[(p.BufHead - k + 4) & 3];
                if (older.Time <= renderTime)
                {
                    float span = newer.Time - older.Time;
                    float u = span > 0.0001f ? (renderTime - older.Time) / span : 1f;
                    target = Vector3.Lerp(older.Pos, newer.Pos, u);
                    targetYaw = Mathf.LerpAngle(older.Yaw, newer.Yaw, u);
                    return;
                }
                newer = older;
            }
            // renderTime predates the whole buffer (fresh puppet): hold the oldest snapshot
            target = newer.Pos;
            targetYaw = newer.Yaw;
        }

        private static void Toggle(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        private void Spawn(Puppet p, string charName, Vector3 pos, bool femaleHint)
        {
            if (_cmClient == null) _cmClient = Object.FindObjectOfType<CustomerManager>();
            if (_cmClient == null) return;

            // workers carry gender in the flag (their names aren't "Female"-prefixed);
            // customers still carry it in the name
            bool female = femaleHint || charName.StartsWith("Female");
            p.Female = female;
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
            p.Custom = cust != null ? cust.m_CharacterCustom : null;
            try
            {
                if (p.Custom != null && charName.Length > 0)
                {
                    p.Custom.CharacterName = charName;
                    p.Custom.Initialize(); // deterministic wardrobe by name
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
                p.Smelly = cust.m_SmellyFX; // plain child FX object, survives the strip
                p.Exclaim = cust.m_ExclaimationMesh; // the "!" trade prompt, driven by flags below
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

            // CharacterCustomization must survive the strip so wardrobe changes can
            // re-dress in place instead of Destroy+Instantiate churn
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string tn = mb.GetType().Name;
                if (tn == "CopyPose" || tn == "BlendshapeManager" || tn == "ScaleCharacter"
                    || tn == "TransformBone" || tn == "MipBiasAdjust" || tn == "CharacterCustomization")
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
            p.PrevRenderedPos = pos;
            p.RenderYaw = 0f;
            p.AnimSpeed = 0f;
            p.AppliedFlags = -1;
        }
    }
}
