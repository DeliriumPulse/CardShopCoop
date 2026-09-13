using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// v0.3 phase B: the host streams every active customer and worker (identity, position,
    /// yaw, speed, action flags) at 8 Hz; the client renders normal NPCs as stripped-clone
    /// puppets and temporarily uses real pooled customers for register interaction.
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
    public class NpcSync : CoopModule
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
        private static readonly System.Reflection.MethodInfo MiEvaluateWorkerAttribute =
            HarmonyLib.AccessTools.Method(typeof(Worker), "EvaluateWorkerAttribute");
        private static readonly System.Reflection.MethodInfo MiEvaluateSkillLevel =
            HarmonyLib.AccessTools.Method(typeof(Worker), "EvaluateSkillLevel");
        private static readonly System.Reflection.FieldInfo FiCurrentHoldItemBox =
            HarmonyLib.AccessTools.Field(typeof(Worker), "m_CurrentHoldItemBox");

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
        private int _chunkCount;
        private NpcStateMessage _currentChunk;
        private int _chunkJsonLength;
        private readonly Dictionary<int, string> _sentNames = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _sentIdentities = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _customerGenerations = new Dictionary<int, int>();
        private readonly Dictionary<int, bool> _customerActive = new Dictionary<int, bool>();
        private readonly Dictionary<int, ECustomerState> _customerStates = new Dictionary<int, ECustomerState>();
        private readonly Dictionary<int, int> _customerGrabSequences = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _workerActionSequences = new Dictionary<int, int>();
        private readonly Dictionary<int, byte> _workerActionKinds = new Dictionary<int, byte>();
        private readonly Dictionary<int, int> _workerGenerations = new Dictionary<int, int>();
        private readonly Dictionary<int, bool> _workerActive = new Dictionary<int, bool>();
        private readonly Dictionary<int, ExistingCustomer> _existing = new Dictionary<int, ExistingCustomer>();
        private static NpcSync _live;
        private bool _missingHoldFieldLogged;

        public override string Name => "npcs";

        public override void Start()
        {
            ActivateLive(this);
        }

        public override void ForceResend()
        {
            _sendTimer = SendInterval;
            _sentNames.Clear();
            _sentIdentities.Clear();
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(_live, this))
                ClearLive();
        }

        /// <summary>Disable Harmony callbacks before a session's module state is torn down.</summary>
        public static void ClearLive()
        {
            _live = null;
        }

        public static void ActivateLive(NpcSync instance)
        {
            _live = instance;
        }

        private sealed class ExistingCustomer
        {
            public Customer Customer;
            public int Generation;
            public float LastSeen;
            public readonly Snap[] Buf = new Snap[4];
            public int BufHead;
            public int BufCount;
            public NpcFlags Flags;
            public int AppliedFlags = -1;
            public Vector3 PrevRenderedPos;
            public float RenderYaw;
            public float AnimSpeed;
            public float AppliedAnimSpeed = float.NaN;
            public int GrabSequence;
            public bool KeepPuppetVisible;
        }

        public override void Reset()
        {
            // Shutdown clears the pointer before resetting instance state. Do not resurrect it
            // while late Harmony callbacks can still arrive during teardown.
            if (CoopCore.Role != CoopRole.None && !CoopCore.IsTearingDown)
                _live = this;
            _cm = null;
            _missingHoldFieldLogged = false;
            _sendTimer = 0f;
            _nameRefreshIn = 0f;
            _sentNames.Clear();
            _sentIdentities.Clear();
            _customerGenerations.Clear();
            _customerActive.Clear();
            _customerStates.Clear();
            _customerGrabSequences.Clear();
            _workerActionSequences.Clear();
            _workerActionKinds.Clear();
            _workerGenerations.Clear();
            _workerActive.Clear();
            foreach (var mirror in _existing.Values)
                if (mirror.Customer != null)
                    mirror.Customer.gameObject.SetActive(false);
            _existing.Clear();
            ClearPuppets();
        }

        /// <summary>Host only. Serializes active NPCs into one or more NpcState payloads,
        /// each under the Steam unreliable packet limit (null when not due / nothing).</summary>
        public List<NpcStateMessage> HostCollect(float dt)
        {
            _live = this;
            _sendTimer += dt;
            if (_sendTimer < SendInterval)
                return null;
            _sendTimer -= SendInterval; // preserve cadence across frame boundaries
            if (_sendTimer > SendInterval)
                _sendTimer = SendInterval; // clamp debt after a hitch

            if (_cm == null)
                _cm = Object.FindObjectOfType<CustomerManager>();
            if (_cm == null)
                return null;

            _nameRefreshIn -= SendInterval;
            if (_nameRefreshIn <= 0f)
            {
                _sentNames.Clear();
                _nameRefreshIn = NameRefreshInterval;
            }

            var chunks = new List<NpcStateMessage>(1);
            float hostTime = Time.unscaledTime;
            BeginChunk(hostTime);

            var customers = _cm.GetCustomerList();
            long tCustomers = Util.PerfProbe.Start();
            for (int i = 0; i < customers.Count; i++)
            {
                var c = customers[i];
                bool active = c != null && c.m_IsActive && c.gameObject.activeSelf;
                bool wasActive = _customerActive.TryGetValue(i, out var oldActive) && oldActive;
                if (active && !wasActive)
                {
                    _customerGenerations.TryGetValue(i, out int generation);
                    _customerGenerations[i] = generation + 1;
                }
                _customerActive[i] = active;
                if (!active)
                    continue;
                // m_CharacterCustom is momentarily null during pooled activation; skipping
                // one tick is harmless (client despawn timeout is 1.5s) whereas an empty
                // name would churn the puppet through a bogus re-dress
                var cc = c.m_CharacterCustom;
                if (cc == null || string.IsNullOrEmpty(cc.CharacterName))
                    continue;
                var flags = CollectFlags(c.m_Anim);
                int grabSequence = GetGrabSequence(i, c.m_CurrentState);
                byte actionKind = c.m_CurrentState == ECustomerState.TournamentTakePrize ? (byte)2 : (byte)1;
                // smelly is sim state, not an animator bool - without it the joiner
                // can't see the stink cloud the host (and the cleansers) react to
                try
                {
                    if (c.IsSmelly())
                        flags |= NpcFlags.Smelly;
                }
                catch (System.Exception e) { Swallow.Log(e); }
                // the red "!" trade/sell-in prompt is a plain mesh toggle, not an animator
                // bool - mirror it so the guest can see which customer wants to be served
                try
                {
                    if (c.m_ExclaimationMesh != null && c.m_ExclaimationMesh.activeSelf)
                        flags |= NpcFlags.Exclaim;
                }
                catch (System.Exception e) { Swallow.Log(e); }
                WriteEntry(chunks, hostTime, KindCustomer, (ushort)i, cc.CharacterName,
                    c.transform, c.m_CurrentMoveSpeed, flags, _customerGenerations[i], grabSequence, actionKind);
            }
            Util.PerfProbe.End("npc-customers", tCustomers);

            var workers = WorkerManager.GetWorkerList();
            long tWorkers = Util.PerfProbe.Start();
            if (workers != null)
            {
                for (int i = 0; i < workers.Count; i++)
                {
                    var w = workers[i];
                    bool workerActive = w != null && w.m_IsActive && w.gameObject.activeSelf;
                    if (!workerActive)
                    {
                        _workerActive[i] = false;
                        continue;
                    }
                    bool workerWasActive = _workerActive.TryGetValue(i, out var oldWorkerActive) && oldWorkerActive;
                    if (!workerWasActive)
                    {
                        _workerGenerations.TryGetValue(i, out int generation);
                        _workerGenerations[i] = generation + 1;
                    }
                    _workerActive[i] = true;
                    var cc = w.m_CharacterCustom;
                    if (cc == null || string.IsNullOrEmpty(cc.CharacterName))
                        continue;
                    // worker names aren't prefixed "Female", so gender must ride a flag or
                    // female workers spawn from the male customer prefab on the guest
                    var wflags = CollectFlags(w.m_Anim);
                    if (FiCurrentHoldItemBox == null && !_missingHoldFieldLogged)
                    {
                        _missingHoldFieldLogged = true;
                        CoopPlugin.Log.LogError("NpcSync: Worker hold-item field is missing; holding-box visuals disabled");
                    }
                    var holdBox = FiCurrentHoldItemBox == null ? null : FiCurrentHoldItemBox.GetValue(w) as InteractablePackagingBox_Item;
                    bool holdBig = false;
                    int holdItemType = 0;
                    if (holdBox != null)
                    {
                        wflags |= NpcFlags.IsHoldingBox;
                        holdBig = holdBox.m_IsBigBox;
                        holdItemType = (int)holdBox.GetItemType();
                    }
                    if (w.m_IsFemale)
                        wflags |= NpcFlags.Female;
                    _workerActionSequences.TryGetValue(i, out int workerAction);
                    _workerActionKinds.TryGetValue(i, out byte workerActionKind);
                    WriteEntry(chunks, hostTime, KindWorker, (ushort)i, cc.CharacterName,
                        w.transform, 0f, wflags, speedFromAnim: w.m_Anim, identity: _workerGenerations[i],
                        actionSequence: workerAction, actionKind: workerActionKind,
                        holdBig: holdBig, holdItemType: holdItemType);
                }
            }
            Util.PerfProbe.End("npc-workers", tWorkers);

            long tFlush = Util.PerfProbe.Start();
            FlushChunk(chunks);
            Util.PerfProbe.End("npc-flush", tFlush);
            return chunks.Count > 0 ? chunks : null;
        }

        private void BeginChunk(float hostTime)
        {
            _chunkCount = 0;
            _currentChunk = new NpcStateMessage { HostTime = hostTime };
            _chunkJsonLength = WireCodec.SerializeUtf8Length(_currentChunk);
        }

        private void FlushChunk(List<NpcStateMessage> chunks)
        {
            if (_chunkCount == 0)
                return;
            chunks.Add(_currentChunk);
            _chunkCount = 0;
        }

        private void WriteEntry(List<NpcStateMessage> chunks, float hostTime, byte kind, ushort index,
            string charName, Transform t, float moveSpeed, NpcFlags flags,
            int identity = 0, int actionSequence = 0, byte actionKind = 0, Animator speedFromAnim = null,
            bool holdBig = false, int holdItemType = 0)
        {
            if (_chunkCount == byte.MaxValue)
            {
                FlushChunk(chunks);
                BeginChunk(hostTime);
            }
            if (speedFromAnim != null)
            {
                try
                {
                    moveSpeed = speedFromAnim.GetFloat(HashMoveSpeed);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            int key = (kind << 16) | index;
            bool sendName = !_sentNames.TryGetValue(key, out var prev) || prev != charName
                || !_sentIdentities.TryGetValue(key, out var prevIdentity) || prevIdentity != identity;
            if (sendName)
                _sentNames[key] = charName;
            _sentIdentities[key] = identity;

            var p = t.position;

            // Build the strongly-typed DTO entry for the chunk this becomes.
            var entry = new NpcEntry
            {
                Kind = kind,
                Index = index,
                Identity = identity,
                HasName = sendName,
                CharName = sendName ? charName : null,
                Position = p,
                Yaw = t.eulerAngles.y,
                Speed = moveSpeed,
                Flags = (byte)flags,
                ActionSequence = actionSequence,
                ActionKind = actionKind,
                HoldBig = holdBig,
                HoldItemType = holdItemType,
            };
            int entryLength = WireCodec.SerializeUtf8Length(entry);
            _currentChunk.Entries.Add(entry);
            _chunkCount++;
            int addedLength = entryLength + (_chunkCount > 1 ? 1 : 0);
            _chunkJsonLength += addedLength;

            // JSON is the wire payload now, so measure the actual DTO instead of maintaining
            // a second binary size meter. If this entry pushed a non-empty chunk over the soft
            // limit, move it to a fresh chunk; a single oversized entry is still sent intact.
            if (_chunkCount > 1 && _chunkJsonLength > ChunkSoftLimit)
            {
                var last = _currentChunk.Entries[_currentChunk.Entries.Count - 1];
                _currentChunk.Entries.RemoveAt(_currentChunk.Entries.Count - 1);
                _chunkJsonLength -= addedLength;
                _chunkCount--;
                FlushChunk(chunks);
                BeginChunk(hostTime);
                _currentChunk.Entries.Add(last);
                _chunkCount = 1;
                _chunkJsonLength += entryLength;
            }
        }

        private int GetGrabSequence(int index, ECustomerState state)
        {
            _customerStates.TryGetValue(index, out var previous);
            if (state == ECustomerState.TakingItemFromShelf
                || state == ECustomerState.TakingItemFromCardShelf
                || state == ECustomerState.TakingItemFromBulkDonationBox
                || state == ECustomerState.TournamentTakePrize)
            {
                if (previous != state)
                {
                    _customerGrabSequences.TryGetValue(index, out int sequence);
                    _customerGrabSequences[index] = sequence + 1;
                }
            }
            _customerStates[index] = state;
            return _customerGrabSequences.TryGetValue(index, out int current) ? current : 0;
        }

        public static void RecordWorkerAction(Worker worker)
        {
            if (_live == null || worker == null || CoopCore.Role != CoopRole.Host)
                return;
            var workers = WorkerManager.GetWorkerList();
            int index = workers != null ? workers.IndexOf(worker) : -1;
            if (index < 0)
                return;
            _live._workerActionSequences.TryGetValue(index, out int sequence);
            _live._workerActionSequences[index] = sequence + 1;
            _live._workerActionKinds[index] = 3; // ScanItem
        }

        public static Transform GetWorkerHoldAnchor(int index)
        {
            if (_live == null)
                return null;
            int key = (KindWorker << 16) | index;
            return _live._puppets.TryGetValue(key, out var p) ? p.HoldBox : null;
        }

        public static Worker GetWorkerPuppet(int index)
        {
            if (_live == null)
                return null;
            int key = (KindWorker << 16) | index;
            return _live._puppets.TryGetValue(key, out var p) && p.Go != null
                ? p.Go.GetComponent<Worker>() : null;
        }

        /// <summary>Show a stripped cosmetic box on a worker puppet. The synchronized
        /// gameplay object is intentionally never attached to the puppet.</summary>
        public static void SetWorkerBoxVisual(int index, bool visible, bool isBig, int itemType)
        {
            if (_live == null)
                return;
            int key = (KindWorker << 16) | index;
            if (!_live._puppets.TryGetValue(key, out var p) || p.Go == null)
                return;
            if (!visible)
            {
                _live.ReleaseWorkerBoxProp(p);
                return;
            }
            if (p.BoxProp != null && p.BoxPropBig == isBig && p.BoxPropType == itemType)
            {
                p.BoxProp.SetActive(true);
                return;
            }
            _live.ReleaseWorkerBoxProp(p);
            try
            {
                var rm = Object.FindObjectOfType<RestockManager>();
                var prefab = isBig ? rm?.m_PackageBoxPrefab : rm?.m_PackageBoxSmallPrefab;
                if (prefab == null || p.HoldBox == null)
                    return;
                var holder = new GameObject("CoopWorkerBoxProp_tmp");
                holder.SetActive(false);
                var clone = Object.Instantiate(prefab.gameObject, holder.transform);
                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null)
                        Object.DestroyImmediate(mb);
                foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                    if (rb != null)
                        Object.DestroyImmediate(rb);
                foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                    if (col != null)
                        Object.DestroyImmediate(col);
                clone.transform.SetParent(p.HoldBox, false);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.name = "CoopWorkerBoxProp";
                clone.SetActive(true);
                Object.Destroy(holder);
                p.BoxProp = clone;
                p.BoxPropBig = isBig;
                p.BoxPropType = itemType;
            }
            catch (System.Exception e) { CoopPlugin.Log.LogInfo("worker box prop unavailable: " + e.Message); }
        }

        /// <summary>Copies authoritative staff settings onto the inert worker component
        /// inside the local visual puppet so the original WorkerInteractUIScreen can use
        /// its normal data and labels.</summary>
        public static void RefreshWorkerUi(int index, WorkerSaveData data)
        {
            if (_live == null || data == null)
                return;
            int key = (KindWorker << 16) | index;
            if (!_live._puppets.TryGetValue(key, out var p) || p.Go == null)
                return;
            var worker = p.Go.GetComponent<Worker>();
            if (worker == null)
                return;
            try
            {
                worker.m_PrimaryTask = data.primaryTask;
                worker.m_SecondaryTask = data.secondaryTask;
                worker.m_WorkerTask = data.workerTask;
                worker.m_CurrentState = data.currentState;
                worker.m_IsBonusBoosted = data.isBonusBoosted;
                worker.m_BonusBoostedCount = data.bonusBoostedCount;
                worker.SetRestockShelfWithNoLabel(data.isFillShelfWithoutLabel);
                worker.UpdateSetPriceOption(data.isRoundUpPrice, data.isAvoidSetCardPrice, data.setPriceMultiplier);
                worker.UpdateSetCardPriceOption(data.isRoundUpCardPrice, data.isAvoidSetCardPriceWhileRestock, data.setCardPriceMultiplier);
                if (data.cardPackItemTypeEnabledList != null)
                    for (int i = 0; i < data.cardPackItemTypeEnabledList.Count; i++)
                        if (i < worker.GetCardPackItemTypeEnabledList().Count)
                            worker.SetCardPackItemTypeEnabled(i, data.cardPackItemTypeEnabledList[i]);
                if (data.expList != null)
                {
                    worker.m_ExpList.Clear();
                    worker.m_ExpList.AddRange(data.expList);
                }
                MiEvaluateSkillLevel?.Invoke(worker, null);
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        private static NpcFlags CollectFlags(Animator anim)
        {
            var f = NpcFlags.None;
            if (anim == null)
                return f;
            try
            {
                if (anim.GetBool(HashHoldingBag))
                    f |= NpcFlags.HoldingBag;
                if (anim.GetBool(HashHandingOverCash))
                    f |= NpcFlags.HandingOverCash;
                if (anim.GetBool(HashIsSitting))
                    f |= NpcFlags.IsSitting;
                if (anim.GetBool(HashIsPlaying))
                    f |= NpcFlags.IsPlaying;
                if (anim.GetBool(HashIsHoldingBox))
                    f |= NpcFlags.IsHoldingBox;
            }
            catch (System.Exception e) { Swallow.Log(e); }
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
            public byte Kind;
            public int Identity;
            public bool HasIdentity;
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
            public float AppliedAnimSpeed = float.NaN;
            public int GrabSequence;
            public Transform HoldBox;
            public GameObject BoxProp;
            public bool BoxPropBig;
            public int BoxPropType;
            public bool HoldBig;
            public int HoldItemType;
        }

        private void ReleaseWorkerBoxProp(Puppet p)
        {
            if (p == null || p.BoxProp == null)
                return;
            try
            {
                Object.Destroy(p.BoxProp);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            p.BoxProp = null;
            p.BoxPropType = 0;
        }

        private readonly Dictionary<int, Puppet> _puppets = new Dictionary<int, Puppet>();
        private CustomerManager _cmClient;
        private WorkerManager _wmClient;
        private float _now;
        private float _clockOffset;
        private bool _clockInit;

        /// <summary>Client: customer list indices whose puppet clone must NOT render, because
        /// the register's carrier (a real, active pool customer) IS that served customer and
        /// shows its own real interactable cash. Populated by RegisterSync; cleared on reset.</summary>
        public static readonly HashSet<int> SuppressedCustomer = new HashSet<int>();

        public int PuppetCount => _puppets.Count;

        private static CustomerManager s_diagCm;

        /// <summary>Diagnostic: how many REAL (non-puppet) NPCs are currently active in
        /// this instance's own managers. On the host that's the true crowd; on the client
        /// it should be zero (anything else is escaping suppression).</summary>
        public static int CountLocalActiveNpcs()
        {
            // cached across calls; Unity's overloaded == re-resolves after scene changes
            if (s_diagCm == null)
                s_diagCm = Object.FindObjectOfType<CustomerManager>();
            int n = 0;
            if (s_diagCm != null)
            {
                var list = s_diagCm.GetCustomerList();
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].gameObject.activeSelf)
                        n++;
            }
            var workers = WorkerManager.GetWorkerList();
            if (workers != null)
                for (int i = 0; i < workers.Count; i++)
                    if (workers[i] != null && workers[i].gameObject.activeSelf)
                        n++;
            return n;
        }

        /// <summary>Client diagnostic: active local NPCs which are not intentional register
        /// carriers or NpcSync customer mirrors.</summary>
        public static int CountUnexpectedActiveNpcs()
        {
            if (s_diagCm == null)
                s_diagCm = Object.FindObjectOfType<CustomerManager>();
            int n = 0;
            if (s_diagCm != null)
            {
                var list = s_diagCm.GetCustomerList();
                for (int i = 0; i < list.Count; i++)
                {
                    var customer = list[i];
                    if (customer != null && customer.gameObject.activeSelf
                        && !RegisterSync.IsCarrier(customer) && !IsExistingCustomer(customer))
                        n++;
                }
            }
            var workers = WorkerManager.GetWorkerList();
            if (workers != null)
                for (int i = 0; i < workers.Count; i++)
                    if (workers[i] != null && workers[i].gameObject.activeSelf)
                        n++;
            return n;
        }

        public static int ExistingMirrorCount => _live == null ? 0 : _live._existing.Count;

        public void ClearPuppets()
        {
            foreach (var p in _puppets.Values)
            {
                ReleaseWorkerBoxProp(p);
                if (p.Go != null)
                    Object.Destroy(p.Go);
            }
            _puppets.Clear();
            SuppressedCustomer.Clear();
            _cmClient = null;
            _clockInit = false;
        }

        public static int GetCustomerGeneration(Customer customer)
        {
            if (_live == null || customer == null)
                return 0;
            if (_live._cm == null)
                _live._cm = Object.FindObjectOfType<CustomerManager>();
            var list = _live._cm != null ? _live._cm.GetCustomerList() : null;
            if (list == null)
                return 0;
            int index = list.IndexOf(customer);
            if (index < 0)
                return 0;
            if (!_live._customerGenerations.TryGetValue(index, out int generation) || generation == 0)
            {
                generation = 1;
                _live._customerGenerations[index] = generation;
            }
            bool active = customer.m_IsActive && customer.gameObject.activeSelf;
            bool wasActive = _live._customerActive.TryGetValue(index, out var oldActive) && oldActive;
            if (active && !wasActive)
                generation++;
            _live._customerGenerations[index] = generation;
            _live._customerActive[index] = active;
            return generation;
        }

        /// <summary>Maps a host customer transform to its list index plus generation
        /// identity.</summary>
        public static bool TryGetCustomerIdentity(Transform transform, out ushort index, out int identity)
        {
            index = 0;
            identity = 0;
            if (_live == null || transform == null)
                return false;
            if (_live._cm == null)
                _live._cm = Object.FindObjectOfType<CustomerManager>();
            var list = _live._cm != null ? _live._cm.GetCustomerList() : null;
            if (list == null)
                return false;
            for (int i = 0; i < list.Count; i++)
            {
                var customer = list[i];
                if (customer == null || customer.transform != transform)
                    continue;
                index = (ushort)i;
                identity = GetCustomerGeneration(customer);
                return true;
            }
            return false;
        }

        private Transform ResolveCustomerAnchor(ushort index, int identity)
        {
            int key = (KindCustomer << 16) | index;
            if (_existing.TryGetValue(index, out var existing)
                && existing.Generation == identity && existing.Customer != null)
                return existing.Customer.transform;
            if (_puppets.TryGetValue(key, out var puppet)
                && puppet.HasIdentity && puppet.Identity == identity && puppet.Go != null)
                return puppet.Go.transform;
            return null;
        }

        /// <summary>Client-only: show a host-selected customer speech bubble over the
        /// corresponding visible representation. Missing puppets are intentionally ignored;
        /// speech is cosmetic and should not keep stale references alive.</summary>
        public void ShowSpeech(NpcSpeechMessage message, bool inGame)
        {
            if (!inGame || message == null || string.IsNullOrEmpty(message.Text))
                return;
            if (message.Kind != KindCustomer)
                return;
            Transform anchor = ResolveCustomerAnchor(message.Index, message.Identity);
            if (anchor == null)
                return;
            var spawner = CSingleton<PricePopupSpawner>.Instance;
            if (spawner == null)
                return;
            spawner.ShowTextPopup(message.Text, message.OffsetUp, anchor);
        }

        /// <summary>Mirrors the host's green add-money popup on the matching puppet or
        /// mirror.</summary>
        public void ShowMoneyPopup(NpcMoneyPopupMessage message, bool inGame)
        {
            if (!inGame || message == null || message.Amount <= 0f)
                return;
            Transform anchor = ResolveCustomerAnchor(message.Index, message.Identity);
            if (anchor == null)
                return;
            var spawner = CSingleton<PricePopupSpawner>.Instance;
            if (spawner == null)
                return;
            spawner.ShowPricePopup(message.Amount, message.OffsetUp, anchor);
        }

        public static void DetachExistingCustomer(int index, Customer customer)
        {
            if (_live == null)
                return;
            int key = (KindCustomer << 16) | index;
            // The list index can be reused by a newer pooled customer. Only tear down the mirror
            // registry entries that still belong to the customer being detached, so an old
            // teardown can never hide or de-suppress a newer customer that took the same slot.
            bool ownsMirror = _live._existing.TryGetValue(index, out var existing)
                && (customer == null || ReferenceEquals(existing.Customer, customer));
            if (ownsMirror)
            {
                _live._existing.Remove(index);
                SuppressedCustomer.Remove(index);
                if (_live._puppets.TryGetValue(key, out var puppet) && puppet.Go != null && puppet.BufCount > 0)
                    puppet.Go.SetActive(true);
            }
            // The caller owns the real carrier it passes in: always take it off the scene.
            // A ready puppet was revealed above; if none exists yet, the next snapshot spawns it.
            if (customer != null)
                customer.gameObject.SetActive(false);
        }

        public static void AttachExistingCustomer(int index, int generation, Customer customer, bool keepPuppetVisible = false)
        {
            if (_live == null || customer == null)
                return;
            if (!_live._existing.TryGetValue(index, out var existing) || existing.Generation != generation)
            {
                existing = new ExistingCustomer
                {
                    Customer = customer,
                    Generation = generation,
                    LastSeen = _live._now,
                    PrevRenderedPos = customer.transform.position,
                    RenderYaw = customer.transform.eulerAngles.y,
                    KeepPuppetVisible = keepPuppetVisible,
                };
                _live._existing[index] = existing;
            }
            else
            {
                existing.Customer = customer;
                existing.KeepPuppetVisible = keepPuppetVisible;
            }
            int key = (KindCustomer << 16) | index;
            if (_live._puppets.TryGetValue(key, out var puppet))
            {
                if (puppet.Go != null)
                    puppet.Go.SetActive(!keepPuppetVisible);
            }
        }

        public static bool IsExistingCustomer(Customer customer)
        {
            if (_live == null || customer == null)
                return false;
            foreach (var mirror in _live._existing.Values)
                if (ReferenceEquals(mirror.Customer, customer))
                    return true;
            return false;
        }

        /// <summary>Client only. Apply one received NpcState batch.</summary>
        public void ApplyBatch(NpcStateMessage message, bool inGame)
        {
            float hostTime = message.HostTime;
            var entries = message.Entries;
            int count = entries.Count;

            // map host time onto the local timeline; low-pass the offset so per-packet
            // network jitter cannot corrupt snapshot spacing (snap on init / big jumps)
            float rawOffset = _now - hostTime;
            if (!_clockInit || Mathf.Abs(rawOffset - _clockOffset) > 1f)
            {
                // re-basing invalidates buffered snapshot times; drop them so the
                // stale-packet guard cannot reject fresh snapshots against old ones
                if (_clockInit)
                    foreach (var pup in _puppets.Values)
                        pup.BufCount = 0;
                _clockOffset = rawOffset;
                _clockInit = true;
            }
            else
                _clockOffset += 0.1f * (rawOffset - _clockOffset);
            float snapTime = hostTime + _clockOffset;

            for (int n = 0; n < count; n++)
            {
                var ent = entries[n];
                byte kind = ent.Kind;
                ushort index = ent.Index;
                int identity = ent.Identity;
                bool hasName = ent.HasName;
                string charName = ent.CharName;
                var pos = ent.Position;
                float yaw = ent.Yaw;
                float speed = ent.Speed;
                var flags = (NpcFlags)ent.Flags;
                int actionSequence = ent.ActionSequence;
                byte actionKind = ent.ActionKind;
                if (!inGame)
                    continue; // consume the payload, render nothing yet

                int key = (kind << 16) | index;
                if (kind == KindCustomer && _existing.TryGetValue(index, out var existingMirror)
                    && existingMirror.Generation == identity)
                {
                    var existing = existingMirror;
                    if (existing != null)
                    {
                        existing.LastSeen = _now;
                        if (existing.BufCount == 0 || snapTime > existing.Buf[existing.BufHead].Time + 0.0005f)
                        {
                            existing.BufHead = (existing.BufHead + 1) & 3;
                            existing.Buf[existing.BufHead] = new Snap
                            {
                                Pos = pos,
                                Yaw = yaw,
                                Speed = speed,
                                Flags = flags,
                                Time = snapTime
                            };
                            if (existing.BufCount < 4)
                                existing.BufCount++;
                        }
                        existing.Flags = flags;
                        if (actionSequence != existing.GrabSequence && existing.Customer != null
                            && existing.Customer.m_Anim != null)
                        {
                            existing.Customer.m_Anim.SetTrigger(actionKind == 2 ? "GrabItemHigh" : "GrabItem");
                            existing.GrabSequence = actionSequence;
                        }

                        // A pooled customer is the interaction carrier. Keep its visual puppet
                        // alive and fed with the same snapshot even while the carrier is on
                        // screen, so handing the customer back (register sale finish, trade end)
                        // reveals a ready puppet instead of a hole that NpcSync has to rebuild
                        // from scratch - the puppet must not age out while it is hidden.
                        int visualKey = (KindCustomer << 16) | index;
                        if (_puppets.TryGetValue(visualKey, out var visual) && visual != null)
                        {
                            visual.LastSeen = _now;
                            visual.Flags = flags;
                            if (visual.BufCount == 0 || snapTime > visual.Buf[visual.BufHead].Time + 0.0005f)
                            {
                                visual.BufHead = (visual.BufHead + 1) & 3;
                                visual.Buf[visual.BufHead] = new Snap { Pos = pos, Yaw = yaw, Speed = speed, Flags = flags, Time = snapTime };
                                if (visual.BufCount < 4)
                                    visual.BufCount++;
                            }
                            if (actionSequence != visual.GrabSequence && visual.Anim != null)
                            {
                                try
                                {
                                    visual.Anim.SetTrigger(actionKind == 2 ? "GrabItemHigh" : "GrabItem");
                                }
                                catch (System.Exception e) { Swallow.Log(e); }
                                visual.GrabSequence = actionSequence;
                            }
                            // Register carriers render the real customer, so their puppet stays
                            // hidden (still alive and buffered) until DetachExistingCustomer shows
                            // it; trade carriers keep the puppet visible.
                            if (visual.Go != null)
                                visual.Go.SetActive(existing.KeepPuppetVisible);
                        }
                    }
                    continue;
                }
                // the register carrier renders this customer for real (with clickable cash);
                // do not also paint an inert clone over it
                if (!_puppets.TryGetValue(key, out var p))
                {
                    // no cached wardrobe yet: skip this tick; the periodic name refresh
                    // (or the next change) delivers it well inside the despawn timeout
                    if (!hasName)
                        continue;
                    p = new Puppet();
                    _puppets[key] = p;
                }

                bool identityChanged = p.HasIdentity && p.Identity != identity;
                if (identityChanged)
                {
                    if (p.Go != null)
                        Object.Destroy(p.Go);
                    p.Go = null;
                    p.CharName = "";
                    p.BufCount = 0;
                    p.GrabSequence = actionSequence;
                }
                p.Kind = kind;
                p.Identity = identity;
                p.HasIdentity = true;
                if (kind == KindWorker)
                {
                    p.HoldBig = ent.HoldBig;
                    p.HoldItemType = ent.HoldItemType;
                }

                bool female = (flags & NpcFlags.Female) != 0;
                if (hasName && p.CharName != charName)
                    ReDress(p, charName, pos, female, kind, index);
                else if (p.Go == null && p.CharName.Length > 0)
                    Spawn(p, p.CharName, pos, female, kind, index); // retry a spawn that failed (e.g. manager not ready)

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
                    if (p.BufCount < 4)
                        p.BufCount++;
                }
                p.Flags = flags;
                if (actionSequence != p.GrabSequence && p.Anim != null)
                {
                    try
                    {
                        string trigger = kind == KindWorker ? "ScanItem"
                            : actionKind == 2 ? "GrabItemHigh" : "GrabItem";
                        p.Anim.SetTrigger(trigger);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    p.GrabSequence = actionSequence;
                }
                p.LastSeen = _now;
                if (kind == KindCustomer && SuppressedCustomer.Contains(index) && p.Go != null)
                    p.Go.SetActive(false);
            }
        }

        private static void ApplyExistingFlags(Customer customer, NpcFlags flags, float speed)
        {
            if (customer.m_Anim == null)
                return;
            customer.m_Anim.SetFloat(HashMoveSpeed, speed);
            customer.m_Anim.SetBool(HashHoldingBag, (flags & NpcFlags.HoldingBag) != 0);
            customer.m_Anim.SetBool(HashHandingOverCash, (flags & NpcFlags.HandingOverCash) != 0);
            customer.m_Anim.SetBool(HashIsSitting, (flags & NpcFlags.IsSitting) != 0);
            customer.m_Anim.SetBool(HashIsPlaying, (flags & NpcFlags.IsPlaying) != 0);
            customer.m_Anim.SetBool(HashIsHoldingBox, (flags & NpcFlags.IsHoldingBox) != 0);
            if (customer.m_ShoppingBagTransform != null)
                customer.m_ShoppingBagTransform.gameObject.SetActive((flags & NpcFlags.HoldingBag) != 0);
            if (customer.m_CustomerCash != null)
                customer.m_CustomerCash.gameObject.SetActive((flags & NpcFlags.HandingOverCash) != 0);
            if (customer.m_GameCardFanOut != null)
                customer.m_GameCardFanOut.SetActive((flags & NpcFlags.IsPlaying) != 0);
            if (customer.m_GameCardSingle != null)
                customer.m_GameCardSingle.SetActive((flags & NpcFlags.IsPlaying) != 0);
            if (customer.m_SmellyFX != null)
                customer.m_SmellyFX.SetActive((flags & NpcFlags.Smelly) != 0);
            if (customer.m_ExclaimationMesh != null)
                customer.m_ExclaimationMesh.SetActive((flags & NpcFlags.Exclaim) != 0);
        }

        /// <summary>On a wardrobe change, re-dress the existing clone in place via the
        /// game's own Initialize() (m_HasInit routes to LoadFromJSON, which re-applies
        /// hair/apparel for the new name). Full respawn only when there is no clone yet
        /// or the male/female prefab no longer matches.</summary>
        private void ReDress(Puppet p, string charName, Vector3 pos, bool femaleHint, byte kind, ushort index)
        {
            // gender from the transmitted flag (workers) OR the "Female..." name prefix
            // (customers); compared to the prefab we actually spawned from (p.Female)
            bool female = femaleHint || (charName != null && charName.StartsWith("Female"));
            bool genderChanged = p.Go != null && p.Female != female;
            if (p.Go == null || p.Custom == null || genderChanged)
            {
                if (p.Go != null)
                    Object.Destroy(p.Go);
                p.Go = null;
                Spawn(p, charName, pos, femaleHint, kind, index);
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
            if (!inGame || dt <= 0f)
                return;
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
                if (kv.Key < 65536 && SuppressedCustomer.Contains(kv.Key)
                    && (!_existing.TryGetValue(kv.Key, out var existingVisual) || !existingVisual.KeepPuppetVisible)
                    && p.Go != null)
                    p.Go.SetActive(false);
                // generous: NPC state rides the UNRELIABLE lane, and flaky NATs starve
                // it in bursts - a 1.5s timeout made whole crowds blink out and back
                // for players on rough connections (first field report)
                if (_now - p.LastSeen > 6f)
                {
                    if (p.Go != null)
                        Object.Destroy(p.Go);
                    (dead = dead ?? new List<int>()).Add(kv.Key);
                    continue;
                }
                if (p.Go == null || p.BufCount == 0)
                    continue;

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
                if (p.AnimSpeed < 0.05f)
                    p.AnimSpeed = 0f;

                if (p.Anim != null)
                {
                    if (float.IsNaN(p.AppliedAnimSpeed)
                        || Mathf.Abs(p.AppliedAnimSpeed - p.AnimSpeed) > 0.01f)
                    {
                        try
                        {
                            p.Anim.SetFloat(HashMoveSpeed, p.AnimSpeed);
                            p.AppliedAnimSpeed = p.AnimSpeed;
                        }
                        catch (System.Exception e) { Swallow.Log(e); }
                    }
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
                        catch (System.Exception e) { Swallow.Log(e); }
                    }
                    Toggle(p.Bag, (p.Flags & NpcFlags.HoldingBag) != 0);
                    Toggle(p.Cash, (p.Flags & NpcFlags.HandingOverCash) != 0);
                    Toggle(p.CardFan, (p.Flags & NpcFlags.IsPlaying) != 0);
                    Toggle(p.CardSingle, (p.Flags & NpcFlags.IsPlaying) != 0);
                    Toggle(p.Smelly, (p.Flags & NpcFlags.Smelly) != 0);
                    Toggle(p.Exclaim, (p.Flags & NpcFlags.Exclaim) != 0);
                    p.AppliedFlags = (int)p.Flags;
                }
                if (p.Kind == KindWorker)
                {
                    if ((p.Flags & NpcFlags.IsHoldingBox) != 0)
                        SetWorkerBoxVisual((int)(kv.Key & 0xffff), true, p.HoldBig, p.HoldItemType);
                    else
                        ReleaseWorkerBoxProp(p);
                }
            }
            if (dead != null)
                foreach (int k in dead)
                    _puppets.Remove(k);

            List<int> existingDead = null;
            foreach (var kv in _existing)
            {
                var mirror = kv.Value;
                if (_now - mirror.LastSeen > 6f)
                {
                    if (mirror.Customer != null)
                        mirror.Customer.gameObject.SetActive(false);
                    (existingDead = existingDead ?? new List<int>()).Add(kv.Key);
                    continue;
                }
                if (mirror.Customer == null || mirror.BufCount == 0)
                    continue;
                SampleExisting(mirror, renderTime, out var target, out float targetYaw);
                float existingPosBlend = 1f - Mathf.Exp(-18f * dt);
                float existingYawBlend = 1f - Mathf.Exp(-14f * dt);
                float existingSpeedBlend = 1f - Mathf.Exp(-8f * dt);
                var transform = mirror.Customer.transform;
                bool snap = (transform.position - target).sqrMagnitude > 25f;
                var newPos = snap ? target : Vector3.Lerp(transform.position, target, existingPosBlend);
                transform.position = newPos;
                mirror.RenderYaw = snap ? targetYaw : Mathf.LerpAngle(mirror.RenderYaw, targetYaw, existingYawBlend);
                transform.rotation = Quaternion.Euler(0f, mirror.RenderYaw, 0f);
                float rendered = snap ? 0f : Mathf.Min((newPos - mirror.PrevRenderedPos).magnitude / dt, 10f);
                mirror.PrevRenderedPos = newPos;
                mirror.AnimSpeed = Mathf.Lerp(mirror.AnimSpeed, rendered, existingSpeedBlend);
                if (mirror.AnimSpeed < 0.05f)
                    mirror.AnimSpeed = 0f;
                if (mirror.Customer.m_Anim != null
                    && (float.IsNaN(mirror.AppliedAnimSpeed)
                        || Mathf.Abs(mirror.AppliedAnimSpeed - mirror.AnimSpeed) > 0.01f))
                {
                    mirror.Customer.m_Anim.SetFloat(HashMoveSpeed, mirror.AnimSpeed);
                    mirror.AppliedAnimSpeed = mirror.AnimSpeed;
                }
                if ((int)mirror.Flags != mirror.AppliedFlags)
                {
                    ApplyExistingFlags(mirror.Customer, mirror.Flags, mirror.AnimSpeed);
                    mirror.AppliedAnimSpeed = mirror.AnimSpeed;
                    mirror.AppliedFlags = (int)mirror.Flags;
                }
            }
            if (existingDead != null)
                foreach (int key in existingDead)
                {
                    // The mirror timed out and was hidden: release the suppression too, or the
                    // next puppet that reuses this list slot would stay hidden until Reset.
                    _existing.Remove(key);
                    SuppressedCustomer.Remove(key);
                }
        }

        private static void SampleExisting(ExistingCustomer mirror, float renderTime,
            out Vector3 target, out float targetYaw)
        {
            var newest = mirror.Buf[mirror.BufHead];
            if (newest.Time <= renderTime)
            {
                var velocity = Vector3.zero;
                if (mirror.BufCount >= 2)
                {
                    var previous = mirror.Buf[(mirror.BufHead + 3) & 3];
                    float span = newest.Time - previous.Time;
                    if (span > 0.001f)
                    {
                        velocity = (newest.Pos - previous.Pos) / span;
                        velocity.y = 0f;
                        velocity = Vector3.ClampMagnitude(velocity, 5f);
                    }
                }
                float extrapolation = Mathf.Min(renderTime - newest.Time, 0.25f);
                velocity *= Mathf.Exp(-3f * extrapolation);
                target = newest.Pos + velocity * extrapolation;
                targetYaw = newest.Yaw;
                return;
            }
            var newer = newest;
            for (int i = 1; i < mirror.BufCount; i++)
            {
                var older = mirror.Buf[(mirror.BufHead - i + 4) & 3];
                if (older.Time <= renderTime)
                {
                    float span = newer.Time - older.Time;
                    float blend = span > 0.0001f ? (renderTime - older.Time) / span : 1f;
                    target = Vector3.Lerp(older.Pos, newer.Pos, blend);
                    targetYaw = Mathf.LerpAngle(older.Yaw, newer.Yaw, blend);
                    return;
                }
                newer = older;
            }
            target = newer.Pos;
            targetYaw = newer.Yaw;
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
            if (go != null && go.activeSelf != on)
                go.SetActive(on);
        }

        private void Spawn(Puppet p, string charName, Vector3 pos, bool femaleHint, byte kind, ushort index)
        {
            // workers carry gender in the flag (their names aren't "Female"-prefixed);
            // customers still carry it in the name
            bool female = femaleHint || charName.StartsWith("Female");
            p.Female = female;
            p.Kind = kind;
            GameObject prefabObject;
            bool clonedLiveWorker = false;
            if (kind == KindWorker)
            {
                if (_wmClient == null)
                    _wmClient = Object.FindObjectOfType<WorkerManager>();
                if (_wmClient == null)
                    return;
                var workers = WorkerManager.GetWorkerList();
                Worker source = workers != null && index < workers.Count ? workers[index] : null;
                if (source != null)
                {
                    // Appearance mods commonly replace the visual hierarchy on the live
                    // worker after WorkerManager.Start. Clone that post-mod instance so
                    // custom animators, skeletons, sockets, and visual scripts survive.
                    prefabObject = source.gameObject;
                    clonedLiveWorker = true;
                }
                else
                {
                    var prefab = female ? _wmClient.m_WorkerFemalePrefab : _wmClient.m_WorkerPrefab;
                    if (prefab == null)
                        return;
                    prefabObject = prefab.gameObject;
                }
            }
            else
            {
                if (_cmClient == null)
                    _cmClient = Object.FindObjectOfType<CustomerManager>();
                if (_cmClient == null)
                    return;
                var prefab = female ? _cmClient.m_CustomerFemalePrefab : _cmClient.m_CustomerPrefab;
                if (prefab == null)
                    return;
                prefabObject = prefab.gameObject;
            }

            var holder = new GameObject("CoopNpcHolder_tmp");
            holder.SetActive(false);
            var clone = Object.Instantiate(prefabObject, holder.transform);
            clone.transform.SetParent(null, worldPositionStays: false);
            clone.transform.position = pos;
            clone.SetActive(true);
            Object.Destroy(holder);

            var cust = clone.GetComponent<Customer>();
            var worker = clone.GetComponent<Worker>();
            if (worker != null)
            {
                // WorkerManager.ActivateWorker is deliberately not used on clients: it
                // starts the real AI loop and changes worker counts. These are the two
                // vanilla initialization routines OpenScreen's labels depend on.
                try
                {
                    worker.m_WorkerIndex = index;
                    worker.InitializeCharacter();
                }
                catch (System.Exception e) { Swallow.Log(e); }
                try
                {
                    MiEvaluateWorkerAttribute?.Invoke(worker, null);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                try
                {
                    MiEvaluateSkillLevel?.Invoke(worker, null);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            p.Custom = cust != null ? cust.m_CharacterCustom
                : worker != null ? worker.m_CharacterCustom : null;
            try
            {
                if (p.Custom != null && charName.Length > 0 && !clonedLiveWorker)
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
                    Toggle(p.Bag, false);
                    Toggle(p.Cash, false);
                    Toggle(p.CardFan, false);
                    Toggle(p.CardSingle, false);
                    if (cust.m_CleanFX != null)
                        cust.m_CleanFX.SetActive(false);
                    if (cust.m_ExclaimationMesh != null)
                        cust.m_ExclaimationMesh.SetActive(false);
                    if (cust.m_InteractCollider != null)
                        cust.m_InteractCollider.SetActive(false);
                    if (cust.m_SmellyFX != null)
                        cust.m_SmellyFX.SetActive(false);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }

            else if (worker != null)
            {
                // Worker appearance mods replace the visual hierarchy and animator on
                // the live Worker component; never leave its interaction marker visible.
                p.Exclaim = worker.m_ExclaimationMesh;
                p.HoldBox = worker.m_HoldBoxLoc;
                if (p.Exclaim != null)
                    p.Exclaim.SetActive(false);
            }

            // CharacterCustomization must survive the strip so wardrobe changes can
            // re-dress in place instead of Destroy+Instantiate churn
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null)
                    continue;
                string tn = mb.GetType().Name;
                if (tn == "Worker" || tn == "Customer" || tn == "WorkerCollider"
                    || tn == "NavMeshAgent" || tn == "NavMeshObstacle" || tn == "Seeker"
                    || tn == "FunnelModifier" || tn == "InteractableObject")
                {
                    var behaviour = mb as Behaviour;
                    if (behaviour != null)
                        behaviour.enabled = false;
                }
            }
            foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }

            // Keep exactly the vanilla worker interaction surface on the puppet. The
            // Worker component remains behaviour-disabled, but WorkerCollider.OnMousePress
            // can still open the original WorkerInteractUIScreen without enabling AI.
            if (worker != null && worker.m_WorkerCollider != null)
            {
                worker.m_WorkerCollider.enabled = true;
                foreach (var col in worker.m_WorkerCollider.GetComponents<Collider>())
                    col.enabled = true;
            }

            // A staff snapshot can arrive before this puppet is spawned. Seed the
            // vanilla UI model from the already-downloaded save immediately; later
            // StaffState packets continue to refresh it authoritatively.
            try
            {
                var saved = CPlayerData.m_WorkerSaveDataList;
                if (worker != null && saved != null && index < saved.Count)
                    RefreshWorkerUi(index, saved[index]);
            }
            catch (System.Exception e) { Swallow.Log(e); }

            clone.name = "CoopNpc_" + charName;
            p.Go = clone;
            // Prefer the Animator reference owned by the cloned Worker. Mods may leave
            // the original Animator disabled beside a replacement Animator in the same
            // hierarchy; GetComponentInChildren alone can select the wrong one.
            p.Anim = worker != null && worker.m_Anim != null
                ? worker.m_Anim : clone.GetComponentInChildren<Animator>(true);
            p.CharName = charName;
            p.PrevRenderedPos = pos;
            p.RenderYaw = 0f;
            p.AnimSpeed = 0f;
            p.AppliedFlags = -1;
        }
    }
}
