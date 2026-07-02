using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using CardShopCoop.Net;
using CardShopCoop.Sync;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop
{
    public enum CoopRole { None, Host, Client }

    public class CoopCore : MonoBehaviour
    {
        public static CoopCore Instance { get; private set; }
        public static CoopRole Role { get; private set; } = CoopRole.None;

        public string StatusLine = "Not connected";
        public string ErrorLine = "";
        public string HostTimeLine = "";
        public string RegisterLine = "";
        public float RegisterLineTimer;
        private float _serveThrottle;
        public readonly Dictionary<int, string> PeerNames = new Dictionary<int, string>();

        private ICoopTransport _net;
        private readonly SteamLobby _steamLobby = new SteamLobby();
        private ulong _autoJoinSteamLobby; // from +connect_lobby (game launched via invite)
        public bool IsSteamSession { get; private set; }
        private readonly AvatarManager _avatars = new AvatarManager();
        private readonly WorldSync _world = new WorldSync();
        private readonly NpcSync _npcs = new NpcSync();
        private readonly CardShelfSync _cardShelves = new CardShelfSync();
        private readonly Sync.RegisterMirror _registerMirror = new Sync.RegisterMirror();
        private float _npcSweepTimer;
        private float _regStateTimer;
        public string PromptLine = "";
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private UI.CoopUI _ui;

        // client-side save + mod-sidecar download
        private MemoryStream _saveBuf;
        private int _saveExpected = -1;
        private byte[] _pendingSave;
        private MemoryStream _bundleBuf;
        private int _bundleExpected = -1;
        private int _hostSlot;
        private bool _worldRequested;

        // host price sync
        private float _priceTimer;
        private int _lastPriceHash;

        // timers
        private float _stateTimer;
        private float _pingTimer;
        private float _econTimer;
        private float _dayTimer;

        // local movement measurement
        private Vector3 _lastPos;
        private bool _hasLastPos;

        // host economy/progression change detection
        private double _lastCoinSent = double.MinValue;
        private long _lastProgressSent = long.MinValue;

        // one-time link confirmation logging
        private readonly HashSet<int> _gotStateFrom = new HashSet<int>();
        private bool _loggedEconLink;
        private bool _loggedTimeLink;

        // pipeline diagnostics: prove where sync stalls instead of guessing
        private long _diagSent;
        private long _diagRecvStates;
        private float _diagTimer;
        private float _errLogCooldown;

        private void Guarded(string stage, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                if (_errLogCooldown <= 0f)
                {
                    _errLogCooldown = 5f;
                    CoopPlugin.Log.LogError($"[{stage}] {e}");
                }
            }
        }

        // LightManager reflection (time of day)
        private static readonly FieldInfo FiTimeHour = typeof(LightManager).GetField("m_TimeHour", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FiTimeMin = typeof(LightManager).GetField("m_TimeMin", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FiTimeMinFloat = typeof(LightManager).GetField("m_TimeMinFloat", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FiHasDayEnded = typeof(LightManager).GetField("m_HasDayEnded", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly System.Reflection.MethodInfo MiDayReset = typeof(LightManager).GetMethod("DelayUpdateEnv", BindingFlags.NonPublic | BindingFlags.Instance);
        private LightManager _lightManager;

        // headless auto-test / shortcut args: -coopautohost=SLOT  -coopautojoin=IP
        private int _autoHostSlot = -1;
        private string _autoJoinIp;
        private int _autoPhase;
        private float _autoTimer;

        private void Awake()
        {
            Instance = this;
            _ui = new UI.CoopUI();
            _world.OnLocalChanges = OnLocalWorldChanges;
            _cardShelves.OnLocalChanges = changes =>
            {
                if (Role == CoopRole.Host)
                    Broadcast(MsgType.CardShelfDelta, bw => CardShelfSync.WriteEntries(bw, changes));
                else if (Role == CoopRole.Client)
                    Send(1, MsgType.CardShelfRequest, bw => CardShelfSync.WriteEntries(bw, changes));
            };
            SceneManager.sceneLoaded += OnSceneLoaded;

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("-coopautohost=") && int.TryParse(arg.Substring(14), out int slot))
                    _autoHostSlot = slot;
                else if (arg.StartsWith("-coopautojoin="))
                    _autoJoinIp = arg.Substring(14);
                else if (arg == "+connect_lobby" && i + 1 < args.Length && ulong.TryParse(args[i + 1], out ulong lob))
                    _autoJoinSteamLobby = lob; // game was launched by accepting a Steam invite
            }
            if (_autoHostSlot >= 0) CoopPlugin.Log.LogInfo($"AUTO: will load slot {_autoHostSlot} and host");
            if (_autoJoinIp != null) CoopPlugin.Log.LogInfo($"AUTO: will join {_autoJoinIp}");
            if (_autoJoinSteamLobby != 0) CoopPlugin.Log.LogInfo($"AUTO: will join Steam lobby {_autoJoinSteamLobby}");

            _steamLobby.Init();
            _steamLobby.OnError = err => { ErrorLine = err; CoopPlugin.Log.LogWarning(err); };
            _steamLobby.OnLobbyCreated = lobby =>
            {
                if (_net is SteamTransport st) st.LobbyId = lobby;
                StatusLine = "Hosting via Steam - click 'Invite friend'";
                CoopPlugin.Log.LogInfo("steam: lobby live " + lobby);
            };
            _steamLobby.OnEnteredLobby = owner =>
            {
                if (Role != CoopRole.Client || !(_net is SteamTransport st)) return;
                st.LobbyId = _steamLobby.LobbyId;
                st.ConnectToHost(owner);
                StatusLine = "Connected via Steam - requesting world...";
                SendHello();
            };
            _steamLobby.OnInviteAccepted = lobby =>
            {
                CoopPlugin.Log.LogInfo("steam: invite accepted -> lobby " + lobby);
                JoinSteam(lobby);
            };

            CEventManager.AddListener<CEventPlayer_OnOpenCardPack>(OnLocalPackOpened);
        }

        public string HostPassword = "";        // required from joiners when non-empty
        private string _joinPassword = "";       // sent in our Hello when joining
        public CSteamID LastFailedLobby = CSteamID.Nil; // for the wrong-password retry flow
        public SteamLobby Lobby => _steamLobby;

        /// <summary>Join a host through Steam (invite accept, browser, +connect_lobby).</summary>
        public void JoinSteam(CSteamID lobby, string password = "")
        {
            ErrorLine = "";
            if (Role != CoopRole.None) { ErrorLine = "Already in a session."; return; }
            if (InGameLevel()) { ErrorLine = "Go to the main menu first, then accept the invite again."; return; }
            if (!_steamLobby.SteamAvailable()) { ErrorLine = "Steam isn't running."; return; }
            Role = CoopRole.Client;
            IsSteamSession = true;
            _joinPassword = password ?? "";
            LastFailedLobby = lobby;
            _net = new SteamTransport(isHost: false) { KeepaliveFrame = Msg.Build(MsgType.Ping) };
            StatusLine = "Joining Steam lobby...";
            _steamLobby.Join(lobby);
        }

        /// <summary>Host through Steam: friends-only (invite) or public (lobby browser).</summary>
        public void StartHostingSteam(bool isPublic, string lobbyName, string password)
        {
            ErrorLine = "";
            if (Role != CoopRole.None) { ErrorLine = "Already in a session."; return; }
            if (!InGameLevel()) { ErrorLine = "Load your shop first, then host."; return; }
            if (!_steamLobby.SteamAvailable()) { ErrorLine = "Steam isn't running - use LAN instead."; return; }
            Role = CoopRole.Host;
            IsSteamSession = true;
            HostPassword = password ?? "";
            _net = new SteamTransport(isHost: true) { KeepaliveFrame = Msg.Build(MsgType.Ping) };
            StatusLine = "Creating Steam lobby...";
            _steamLobby.Host(isPublic, lobbyName, HostPassword.Length > 0);
        }

        public void OpenSteamInvite() { _steamLobby.OpenInviteDialog(); }

        private void SendHello()
        {
            Send(1, MsgType.Hello, bw =>
            {
                bw.Write(CoopPlugin.Version);
                bw.Write(CoopPlugin.PlayerName.Value);
                bw.Write(_joinPassword ?? "");
                bw.Write(Util.ModParity.PluginHash());
                bw.Write(Util.ModParity.EnumHash());
            });
        }

        // Bye must actually reach the peer before the connection dies; on Steam, sends
        // drain on later frames, so the kick is deferred a moment.
        private readonly List<KeyValuePair<int, float>> _pendingKicks = new List<KeyValuePair<int, float>>();

        private void RejectConn(int connId, string reason)
        {
            CoopPlugin.Log.LogWarning($"rejected connection {connId}: {reason}");
            Send(connId, MsgType.Bye, bw => bw.Write(reason));
            _pendingKicks.Add(new KeyValuePair<int, float>(connId, 1.5f));
        }

        private static void ReadHoldPayload(System.IO.BinaryReader br, byte hold,
            out List<int> types, out List<CardData> cards)
        {
            types = null; cards = null;
            int n = br.ReadByte();
            if (n == 0) return;
            if (hold == 3)
            {
                cards = new List<CardData>(n);
                for (int i = 0; i < n; i++) cards.Add(Msg.ReadCard(br));
            }
            else
            {
                types = new List<int>(n);
                for (int i = 0; i < n; i++) types.Add(br.ReadInt32());
            }
        }

        private static void WriteHoldPayload(System.IO.BinaryWriter bw, byte hold,
            List<int> types, List<CardData> cards)
        {
            if (hold == 3)
            {
                bw.Write((byte)(cards?.Count ?? 0));
                if (cards != null) foreach (var c in cards) Msg.WriteCard(bw, c);
            }
            else
            {
                bw.Write((byte)(types?.Count ?? 0));
                if (types != null) foreach (int t in types) bw.Write(t);
            }
        }

        private void RelayTagToOthers(int senderConn, byte kind, int extra = -1)
        {
            if (Role != CoopRole.Host || _net == null || _net.ConnectionCount <= 1) return;
            var relay = Msg.Build(MsgType.RelayTag, bw => { bw.Write((byte)senderConn); bw.Write(kind); bw.Write(extra); });
            foreach (int cid in _net.ConnIds())
                if (cid != senderConn) _net.Send(cid, relay);
        }

        private void BroadcastRoster()
        {
            if (Role != CoopRole.Host) return;
            var entries = new List<KeyValuePair<int, string>>(PeerNames);
            Broadcast(MsgType.Roster, bw =>
            {
                bw.Write((byte)entries.Count);
                foreach (var e in entries) { bw.Write((byte)e.Key); bw.Write(e.Value); }
            });
        }

        private int _selfId = -1; // our connId on the host, from Welcome
        private readonly HashSet<int> _relayIds = new HashSet<int>(); // other clients we render

        private void OnLocalPackOpened(CEventPlayer_OnOpenCardPack evt)
        {
            if (Role != CoopRole.None && _net != null && _net.ConnectionCount > 0)
                Broadcast(MsgType.Activity, bw => { bw.Write((byte)1); bw.Write(evt.m_PackIndex); });
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            CEventManager.RemoveListener<CEventPlayer_OnOpenCardPack>(OnLocalPackOpened);
            Shutdown("plugin unloaded");
        }

        private void OnApplicationQuit()
        {
            Shutdown("game closed");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _avatars.Clear();
            _world.Reset();
            _npcs.Reset();
            _cardShelves.Reset();
            _registerMirror.Reset();
            PromptLine = "";
            _lightManager = null;
            _playerTf = null;
            _playerCamTf = null;
            _playerIpc = null;
            if (scene.name == "Title" && Role == CoopRole.Client && _net != null)
            {
                // client backed out to the main menu -> leave the session
                Shutdown("left the session");
            }
        }

        private bool InGameLevel()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel;
        }

        /// <summary>The game assigns neither CGameManager.Player nor
        /// InteractionPlayerController.m_Instance (both are dead statics), so find the
        /// player controller in the scene once and cache its transform. FindObjectOfType
        /// never auto-creates, unlike CSingleton&lt;T&gt;.Instance.</summary>
        private Transform _playerTf;   // the MOVING body: IPC.m_WalkerCtrl (CMF walker)
        private Transform _playerCamTf; // player camera, for look yaw
        private InteractionPlayerController _playerIpc;

        /// <summary>InteractionPlayerController itself sits on a stationary manager object -
        /// its transform never moves (that was the frozen-avatar bug). The walking body is
        /// its public m_WalkerCtrl (the game's own teleport code moves the player by setting
        /// m_WalkerCtrl.transform.position), and look direction lives on m_Cam.</summary>
        private Transform ResolvePlayer()
        {
            if (_playerTf != null) return _playerTf;
            var ipc = InteractionPlayerController.m_Instance;
            if (ipc == null) ipc = FindObjectOfType<InteractionPlayerController>();
            if (ipc != null)
            {
                _playerIpc = ipc;
                _playerTf = ipc.m_WalkerCtrl != null ? ipc.m_WalkerCtrl.transform : ipc.transform;
                _playerCamTf = ipc.m_Cam != null ? ipc.m_Cam.transform : null;
                CoopPlugin.Log.LogInfo($"Player body resolved: {_playerTf.name} at {_playerTf.position}, cam={(_playerCamTf != null ? _playerCamTf.name : "none")}");
            }
            return _playerTf;
        }

        // what the local player is carrying (private fields; the game has no public API)
        private static readonly FieldInfo FiHoldBox = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBox");
        private static readonly FieldInfo FiHoldItemBox = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingItemBox");
        private static readonly FieldInfo FiHoldBoxShelf = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBoxShelf");
        private static readonly FieldInfo FiHoldBoxCard = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBoxCard");
        private static readonly FieldInfo FiHoldItemList = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_HoldItemList");

        private readonly List<int> _holdTypesBuf = new List<int>(6);
        private readonly List<CardData> _holdCardsBuf = new List<CardData>(4);
        private static readonly FieldInfo FiHoldCard3dList = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingCard3dList");
        private static readonly FieldInfo FiViewAlbum = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_IsViewCardAlbumMode");

        /// <summary>What the local player carries: 0 none / 1 box / 2 items / 3 cards / 4 binder.
        /// Items fill the type buffer, cards the CardData buffer - both render as the REAL
        /// things on the other side (modded ids resolve identically via registry parity).</summary>
        private byte ComputeHoldState()
        {
            _holdTypesBuf.Clear();
            _holdCardsBuf.Clear();
            if (_playerIpc == null) return 0;
            try
            {
                if (IsAlive(FiHoldBox) || IsAlive(FiHoldItemBox) || IsAlive(FiHoldBoxShelf) || IsAlive(FiHoldBoxCard))
                    return 1; // carrying a box
                if (FiHoldItemList?.GetValue(_playerIpc) is List<Item> items && items.Count > 0)
                {
                    for (int i = 0; i < items.Count && _holdTypesBuf.Count < 6; i++)
                        if (items[i] != null) _holdTypesBuf.Add((int)items[i].GetItemType());
                    return 2; // items in hand
                }
                if (FiHoldCard3dList?.GetValue(_playerIpc) is List<InteractableCard3d> cards && cards.Count > 0)
                {
                    for (int i = 0; i < cards.Count && _holdCardsBuf.Count < 4; i++)
                    {
                        var c = cards[i];
                        if (c != null && c.m_Card3dUI != null && c.m_Card3dUI.m_CardUI != null)
                            _holdCardsBuf.Add(c.m_Card3dUI.m_CardUI.GetCardData());
                    }
                    if (_holdCardsBuf.Count > 0) return 3; // loose cards fanned in hand
                }
                if (FiViewAlbum?.GetValue(_playerIpc) is bool album && album)
                    return 4; // reading the collection binder
            }
            catch { }
            return 0;
        }

        private bool IsAlive(FieldInfo fi)
        {
            var obj = fi?.GetValue(_playerIpc) as UnityEngine.Object;
            return obj != null;
        }

        // ------------------------------------------------ public entry points (UI)

        public void StartHosting()
        {
            ErrorLine = "";
            if (Role != CoopRole.None) { ErrorLine = "Already in a session."; return; }
            if (!InGameLevel()) { ErrorLine = "Load your shop first, then host."; return; }
            try
            {
                var tcp = new Transport { KeepaliveFrame = Msg.Build(MsgType.Ping) };
                tcp.StartHost(CoopPlugin.Port.Value);
                _net = tcp;
                Role = CoopRole.Host;
                StatusLine = "Hosting - waiting for a player...";
                CoopPlugin.Log.LogInfo($"Hosting on port {CoopPlugin.Port.Value}");
            }
            catch (Exception e)
            {
                ErrorLine = "Could not host: " + e.Message;
                _net?.Stop(); _net = null;
                Role = CoopRole.None;
            }
        }

        public void Join(string ip)
        {
            ErrorLine = "";
            if (Role != CoopRole.None) { ErrorLine = "Already in a session."; return; }
            if (InGameLevel()) { ErrorLine = "Join from the main menu (Title screen)."; return; }
            ip = (ip ?? "").Trim();
            if (ip.Length == 0) { ErrorLine = "Enter the host's IP address."; return; }

            CoopPlugin.LastJoinIP.Value = ip;
            Role = CoopRole.Client;
            StatusLine = "Connecting to " + ip + "...";
            var net = new Transport { KeepaliveFrame = Msg.Build(MsgType.Ping) };
            _net = net;
            int port = CoopPlugin.Port.Value;
            new Thread(() =>
            {
                try
                {
                    net.StartClient(ip, port);
                    _mainThread.Enqueue(() =>
                    {
                        StatusLine = "Connected - requesting world...";
                        SendHello();
                    });
                }
                catch (Exception e)
                {
                    _mainThread.Enqueue(() =>
                    {
                        ErrorLine = "Could not connect: " + e.Message;
                        Shutdown(null);
                    });
                }
            }) { IsBackground = true, Name = "CoopConnect" }.Start();
        }

        public void Disconnect()
        {
            Shutdown("disconnected");
        }

        public void SendEmote()
        {
            if (_net != null && Role != CoopRole.None)
                Broadcast(MsgType.Emote, bw => bw.Write((byte)1));
        }

        /// <summary>Client: route a locally-earned gain/spend to the host's real economy.
        /// kinds: 1 AddCoin, 2 ReduceCoin, 3 AddShopExp, 4 AddFame.</summary>
        public void ForwardContribution(byte kind, float value)
        {
            if (Role != CoopRole.Client || _net == null) return;
            Send(1, MsgType.EconContrib, bw => { bw.Write(kind); bw.Write(value); });
        }

        /// <summary>Both roles: mirror a collection change (pack pull, trash, sale) to the
        /// other side so there is one shared binder.</summary>
        public void ForwardCardDelta(CardData card, int amount, bool isAdd)
        {
            if (Role == CoopRole.None || _net == null || card == null || amount <= 0) return;
            Broadcast(MsgType.CardDelta, bw =>
            {
                bw.Write(isAdd);
                bw.Write(amount);
                Msg.WriteCard(bw, card);
            });
        }

        /// <summary>Both roles: mirror a marked-card-price change.</summary>
        public void ForwardCardPrice(CardData card, float price)
        {
            if (Role == CoopRole.None || _net == null || card == null) return;
            Broadcast(MsgType.CardPriceSet, bw =>
            {
                Msg.WriteCard(bw, card);
                bw.Write(price);
            });
        }

        private void Shutdown(string reason)
        {
            if (_net != null)
            {
                try { Broadcast(MsgType.Bye, null); } catch { }
                _net.Stop();
                _net = null;
            }
            _avatars.Clear();
            PeerNames.Clear();
            _saveBuf = null;
            _saveExpected = -1;
            _pendingSave = null;
            _bundleBuf = null;
            _bundleExpected = -1;
            _worldRequested = false;
            _hasLastPos = false;
            _lastCoinSent = double.MinValue;
            _lastPriceHash = 0;
            _lastProgressSent = long.MinValue;
            _world.Reset();
            _npcs.Reset();
            _cardShelves.Reset();
            _registerMirror.Reset();
            PromptLine = "";
            _steamLobby.Leave();
            IsSteamSession = false;
            HostPassword = "";
            _joinPassword = "";
            _selfId = -1;
            _relayIds.Clear();
            _pendingKicks.Clear();
            Application.runInBackground = false; // back to the game's normal behavior
            Role = CoopRole.None;
            if (reason != null)
            {
                StatusLine = "Not connected (" + reason + ")";
                CoopPlugin.Log.LogInfo("Session ended: " + reason);
            }
        }

        // ------------------------------------------------ send helpers

        private void Send(int connId, MsgType type, Action<BinaryWriter> write)
        {
            _net?.Send(connId, Msg.Build(type, write));
        }

        private void Broadcast(MsgType type, Action<BinaryWriter> write)
        {
            _net?.Broadcast(Msg.Build(type, write));
        }

        // ------------------------------------------------ per-frame

        private void Update()
        {
            while (_mainThread.TryDequeue(out var act))
            {
                try { act(); } catch (Exception e) { CoopPlugin.Log.LogError(e); }
            }

            AutoTick(Time.deltaTime);

            if (Input.GetKeyDown(CoopPlugin.UiToggleKey.Value))
                _ui.Visible = !_ui.Visible;
            if (Role != CoopRole.None && Input.GetKeyDown(CoopPlugin.EmoteKey.Value) && !UI.CoopUI.TextFieldFocused)
                SendEmote();

            if (_serveThrottle > 0f) _serveThrottle -= Time.deltaTime;
            if (RegisterLineTimer > 0f)
            {
                RegisterLineTimer -= Time.deltaTime;
                if (RegisterLineTimer <= 0f) RegisterLine = "";
            }
            // tap V = one register action; HOLD V = auto-serve (~4 actions/sec)
            bool serveTap = Input.GetKeyDown(CoopPlugin.ServeKey.Value);
            if (Role == CoopRole.Client && _serveThrottle <= 0f && InGameLevel()
                && (serveTap || Input.GetKey(CoopPlugin.ServeKey.Value)) && !UI.CoopUI.TextFieldFocused)
            {
                _serveThrottle = 0.25f;
                Guarded("serve", () =>
                {
                    var tf = ResolvePlayer();
                    int idx = tf != null ? Sync.RegisterServe.FindNearestCounter(tf.position, 7f, quiet: !serveTap) : -1;
                    if (idx < 0)
                    {
                        if (serveTap) // don't nag every repeat while held
                        {
                            RegisterLine = "walk up to the register first";
                            RegisterLineTimer = 2f;
                        }
                    }
                    else
                    {
                        Send(1, MsgType.ServeRequest, bw => bw.Write(idx));
                    }
                });
            }

            // natural register: clicking a mirrored cart item scans it; clicking during the
            // payment/change phases advances the sale - works like the normal till.
            if (Role == CoopRole.Client && _serveThrottle <= 0f && InGameLevel()
                && Input.GetMouseButtonDown(0) && !UI.CoopUI.TextFieldFocused)
            {
                Guarded("serve-click", () =>
                {
                    var cam = Camera.main;
                    if (cam == null) return;
                    if (Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out var hit, 6f)
                        && _registerMirror.TryGetPropCounter(hit.collider, out int propIdx))
                    {
                        _serveThrottle = 0.25f;
                        Send(1, MsgType.ServeRequest, bw => bw.Write(propIdx));
                        return;
                    }
                    var tf = ResolvePlayer();
                    int near = tf != null ? Sync.RegisterServe.FindNearestCounter(tf.position, 7f, quiet: true) : -1;
                    if (near >= 0 && _registerMirror.IsPaymentPhase(near))
                    {
                        _serveThrottle = 0.3f;
                        Send(1, MsgType.ServeRequest, bw => bw.Write(near));
                    }
                });
            }

            if (_net == null) return;

            Guarded("net-pump", () => _net.PumpMainThread());

            // The game forces Application.runInBackground=false (changeFramerate coroutine),
            // which freezes the whole simulation when the window loses focus - fatal for
            // co-op (and for two-instance testing). Re-assert while in a session.
            if (!Application.runInBackground)
            {
                Application.runInBackground = true;
                CoopPlugin.Log.LogInfo("Forced runInBackground=true for the co-op session");
            }

            while (_net.Connects.TryDequeue(out int joined))
            {
                CoopPlugin.Log.LogInfo("Connection " + joined + " opened");
            }
            while (_net.Disconnects.TryDequeue(out int left))
            {
                string name = PeerNames.TryGetValue(left, out var n) ? n : ("player " + left);
                PeerNames.Remove(left);
                _avatars.Remove(left);
                if (Role == CoopRole.Host)
                {
                    BroadcastRoster();
                    StatusLine = _net.ConnectionCount == 0
                        ? "Hosting - waiting for a player..."
                        : $"Hosting - {_net.ConnectionCount} player(s)";
                    CoopPlugin.Log.LogInfo(name + " left");
                }
                else if (Role == CoopRole.Client)
                {
                    ErrorLine = "Lost connection to the host. You can keep walking around; nothing here touches your own saves.";
                    Shutdown("host connection lost");
                    return;
                }
            }

            while (_net != null && _net.Incoming.TryDequeue(out var msg))
            {
                try { Dispatch(msg); }
                catch (Exception e) { CoopPlugin.Log.LogError($"Dispatch {msg.Type}: {e}"); }
            }
            if (_net == null) return; // a Bye may have shut us down mid-drain

            float dt = Time.deltaTime;
            if (_errLogCooldown > 0f) _errLogCooldown -= dt;

            // Every stage is individually armored: one failing subsystem must degrade
            // that feature only, never kill position sync for the whole session.
            Guarded("avatars", () => _avatars.Tick(dt));
            bool syncActive = Role != CoopRole.None && _net.ConnectionCount > 0 && InGameLevel();
            Guarded("world", () => _world.Tick(dt, syncActive));
            Guarded("cardshelves", () => _cardShelves.Tick(dt, syncActive));

            if (Role == CoopRole.Client)
            {
                Guarded("npc-puppets", () => _npcs.TickPuppets(dt, InGameLevel()));
                Guarded("register-mirror", () =>
                {
                    _registerMirror.Tick(dt);
                    _regStateTimer += dt;
                    if (_regStateTimer >= 0.5f && InGameLevel())
                    {
                        _regStateTimer = 0f;
                        var tf = ResolvePlayer();
                        int near = tf != null ? Sync.RegisterServe.FindNearestCounter(tf.position, 7f, quiet: true) : -1;
                        PromptLine = _registerMirror.PromptFor(near) ?? "";
                    }
                });

                // The save-load path can leave inert vanilla customers standing around on
                // the client even though their AI is suppressed; sweep them off so only
                // the host's mirrored puppets are visible.
                _npcSweepTimer += dt;
                if (_npcSweepTimer >= 2f && InGameLevel())
                {
                    _npcSweepTimer = 0f;
                    Guarded("npc-sweep", () =>
                    {
                        var cm = FindObjectOfType<CustomerManager>();
                        if (cm != null)
                        {
                            var list = cm.GetCustomerList();
                            for (int i = 0; i < list.Count; i++)
                                if (list[i] != null && list[i].gameObject.activeSelf)
                                    list[i].gameObject.SetActive(false);
                        }
                        var workers = WorkerManager.GetWorkerList();
                        if (workers != null)
                            for (int i = 0; i < workers.Count; i++)
                                if (workers[i] != null && workers[i].gameObject.activeSelf)
                                    workers[i].gameObject.SetActive(false);
                    });
                }
            }

            // position updates
            _stateTimer += dt;
            float interval = 1f / Mathf.Clamp(CoopPlugin.SendRateHz.Value, 4f, 30f);
            Guarded("state-send", () =>
            {
                Transform playerTf = InGameLevel() ? ResolvePlayer() : null;
                if (_stateTimer >= interval && playerTf != null)
                {
                    Vector3 pos = playerTf.position;
                    float speed = 0f;
                    if (_hasLastPos)
                    {
                        Vector3 delta = pos - _lastPos;
                        delta.y = 0f;
                        speed = Mathf.Clamp(delta.magnitude / _stateTimer, 0f, 6f);
                    }
                    _lastPos = pos; _hasLastPos = true;
                    float yaw = _playerCamTf != null ? _playerCamTf.eulerAngles.y
                        : (Camera.main != null ? Camera.main.transform.eulerAngles.y : playerTf.eulerAngles.y);
                    byte hold = ComputeHoldState();
                    Broadcast(MsgType.PlayerState, bw =>
                    {
                        bw.Write(pos.x); bw.Write(pos.y); bw.Write(pos.z);
                        bw.Write(yaw); bw.Write(speed); bw.Write(hold);
                        if (hold == 3)
                        {
                            bw.Write((byte)_holdCardsBuf.Count);
                            foreach (var c in _holdCardsBuf) Msg.WriteCard(bw, c);
                        }
                        else
                        {
                            bw.Write((byte)_holdTypesBuf.Count);
                            foreach (int t in _holdTypesBuf) bw.Write(t);
                        }
                    });
                    _diagSent++;
                    _stateTimer = 0f;
                }
            });

            _diagTimer += dt;
            if (_diagTimer >= 15f)
            {
                _diagTimer = 0f;
                var diagTf = InGameLevel() ? ResolvePlayer() : null;
                string posStr = diagTf != null ? $"({diagTf.position.x:F1},{diagTf.position.y:F1},{diagTf.position.z:F1})" : "n/a";
                string npcStr = "";
                if (InGameLevel())
                {
                    try
                    {
                        int local = NpcSync.CountLocalActiveNpcs();
                        npcStr = Role == CoopRole.Client
                            ? $" puppets={_npcs.PuppetCount} localNpcs={local}(should be 0)"
                            : $" liveNpcs={local}";
                    }
                    catch { }
                }
                CoopPlugin.Log.LogInfo($"diag: role={Role} conns={_net.ConnectionCount} sentStates={_diagSent} recvStates={_diagRecvStates} inGame={InGameLevel()} pos={posStr}{npcStr}");
            }

            // deferred kicks (give a rejection Bye time to reach the peer first)
            for (int i = _pendingKicks.Count - 1; i >= 0; i--)
            {
                float left = _pendingKicks[i].Value - dt;
                if (left <= 0f)
                {
                    int cid = _pendingKicks[i].Key;
                    _pendingKicks.RemoveAt(i);
                    _net.Kick(cid);
                }
                else _pendingKicks[i] = new KeyValuePair<int, float>(_pendingKicks[i].Key, left);
            }

            // heartbeat + timeout
            _pingTimer += dt;
            if (_pingTimer >= 2f)
            {
                _pingTimer = 0f;
                Broadcast(MsgType.Ping, null);
                foreach (int id in _net.ConnIds())
                {
                    if (_net.SecondsSinceLastRecv(id) > _net.TimeoutSeconds)
                    {
                        CoopPlugin.Log.LogWarning("Connection " + id + " timed out");
                        _net.Kick(id);
                    }
                }
            }

            if (Role == CoopRole.Host) HostTick(dt);
        }

        /// <summary>Drives the -coopautohost / -coopautojoin command-line flows.</summary>
        private void AutoTick(float dt)
        {
            if (_autoHostSlot < 0 && _autoJoinIp == null) return;
            if (_autoPhase >= 99) return;
            _autoTimer += dt;

            if (_autoHostSlot >= 0)
            {
                if (_autoPhase == 0 && _autoTimer > 6f && !InGameLevel()
                    && CSingleton<CGameManager>.Instance != null)
                {
                    CoopPlugin.Log.LogInfo($"AUTO: loading slot {_autoHostSlot}...");
                    Sync.SaveTransfer.ForceLoadSlot(_autoHostSlot);
                    _autoPhase = 1;
                    _autoTimer = 0f;
                }
                else if (_autoPhase == 1 && InGameLevel() && GameInstance.m_FinishedSavefileLoading)
                {
                    _autoPhase = 2;
                    _autoTimer = 0f;
                }
                else if (_autoPhase == 2 && _autoTimer > 3f)
                {
                    CoopPlugin.Log.LogInfo("AUTO: hosting now");
                    StartHosting();
                    _autoPhase = 99;
                }
            }
            else if (_autoJoinIp != null)
            {
                if (_autoPhase == 0 && _autoTimer > 10f && !InGameLevel()
                    && CSingleton<CGameManager>.Instance != null)
                {
                    CoopPlugin.Log.LogInfo($"AUTO: joining {_autoJoinIp}...");
                    Join(_autoJoinIp);
                    _autoPhase = 99;
                }
            }
            else if (_autoJoinSteamLobby != 0)
            {
                if (_autoPhase == 0 && _autoTimer > 10f && !InGameLevel()
                    && CSingleton<CGameManager>.Instance != null)
                {
                    CoopPlugin.Log.LogInfo($"AUTO: joining Steam lobby {_autoJoinSteamLobby}...");
                    JoinSteam(new CSteamID(_autoJoinSteamLobby));
                    _autoPhase = 99;
                }
            }
        }

        /// <summary>Local snapshot diffs: host broadcasts authoritative state, client requests.</summary>
        private void OnLocalWorldChanges(System.Collections.Generic.List<WorldSync.Entry> changes)
        {
            if (Role == CoopRole.Host)
                Broadcast(MsgType.ShelfDelta, bw => WorldSync.WriteEntries(bw, changes));
            else if (Role == CoopRole.Client)
                Send(1, MsgType.ShelfRequest, bw => WorldSync.WriteEntries(bw, changes));
        }

        private void HostTick(float dt)
        {
            if (_net.ConnectionCount == 0) return;

            if (InGameLevel())
            {
                Guarded("npc-collect", () =>
                {
                    var batch = _npcs.HostCollect(dt);
                    if (batch != null)
                        Broadcast(MsgType.NpcState, bw => bw.Write(batch));
                });
                Guarded("register-collect", () =>
                {
                    _regStateTimer += dt;
                    if (_regStateTimer >= 0.5f)
                    {
                        _regStateTimer = 0f;
                        var batch = Sync.RegisterServe.CollectStates();
                        if (batch != null)
                            Broadcast(MsgType.RegisterState, bw => bw.Write(batch));
                    }
                });
            }

            _priceTimer += dt;
            if (_priceTimer >= 3f)
            {
                _priceTimer = 0f;
                try
                {
                    var prices = CPlayerData.m_SetItemPriceList;
                    int hash = 17;
                    for (int i = 0; i < prices.Count; i++)
                        hash = hash * 31 + prices[i].GetHashCode();
                    if (hash != _lastPriceHash)
                    {
                        _lastPriceHash = hash;
                        Broadcast(MsgType.PriceList, bw =>
                        {
                            bw.Write((ushort)prices.Count);
                            for (int i = 0; i < prices.Count; i++) bw.Write(prices[i]);
                        });
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("price sync: " + e.Message); }
            }

            _econTimer += dt;
            if (_econTimer >= 0.5f)
            {
                _econTimer = 0f;
                double coin = CPlayerData.m_CoinAmountDouble;
                if (Math.Abs(coin - _lastCoinSent) > 0.0001)
                {
                    _lastCoinSent = coin;
                    float coinF = CPlayerData.m_CoinAmount;
                    Broadcast(MsgType.CoinSet, bw => { bw.Write(coin); bw.Write(coinF); });
                }

                int exp = CPlayerData.m_ShopExpPoint;
                int level = CPlayerData.m_ShopLevel;
                int fame = CPlayerData.m_FamePoint;
                long progress = ((long)level << 40) ^ ((long)fame << 20) ^ (uint)exp;
                if (progress != _lastProgressSent)
                {
                    _lastProgressSent = progress;
                    Broadcast(MsgType.ProgressSet, bw => { bw.Write(exp); bw.Write(level); bw.Write(fame); });
                }
            }

            _dayTimer += dt;
            if (_dayTimer >= 2f)
            {
                _dayTimer = 0f;
                int hour = 8, min = 0;
                try
                {
                    if (_lightManager == null) _lightManager = FindObjectOfType<LightManager>();
                    if (_lightManager != null)
                    {
                        if (FiTimeHour != null) hour = (int)FiTimeHour.GetValue(_lightManager);
                        if (FiTimeMin != null) min = (int)FiTimeMin.GetValue(_lightManager);
                    }
                }
                catch { }
                int day = CPlayerData.m_CurrentDay;
                Broadcast(MsgType.DayTime, bw => { bw.Write(day); bw.Write(hour); bw.Write(min); });
            }
        }

        // ------------------------------------------------ message handling

        private void Dispatch(InMsg msg)
        {
            switch (msg.Type)
            {
                case MsgType.Hello:
                {
                    if (Role != CoopRole.Host) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        string version = br.ReadString();
                        string name = br.ReadString();
                        string password = br.ReadString();
                        string pluginHash = br.ReadString();
                        string enumHash = br.ReadString();

                        if (version != CoopPlugin.Version)
                        {
                            RejectConn(msg.ConnId, $"version mismatch - host runs CardShopCoop {CoopPlugin.Version}, you have {version}");
                            break;
                        }
                        if (HostPassword.Length > 0 && password != HostPassword)
                        {
                            RejectConn(msg.ConnId, "wrong password");
                            break;
                        }
                        if (pluginHash != Util.ModParity.PluginHash())
                        {
                            RejectConn(msg.ConnId, "your mod set differs from the host's - both players need identical mods (same versions)");
                            break;
                        }
                        string hostEnum = Util.ModParity.EnumHash();
                        if (enumHash != "none" && hostEnum != "none" && enumHash != hostEnum)
                        {
                            RejectConn(msg.ConnId, "custom-card registry differs (EPL enum_values.json) - see the mod page's compatibility notes");
                            break;
                        }

                        PeerNames[msg.ConnId] = name;
                        _avatars.SetName(msg.ConnId, name);
                        StatusLine = $"Hosting - {name} joined!";
                        CoopPlugin.Log.LogInfo(name + " joined, sending world...");
                        SendWorldTo(msg.ConnId);
                        BroadcastRoster();
                    }
                    break;
                }
                case MsgType.Welcome:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        br.ReadString(); // host plugin version (already matched by host)
                        string hostName = br.ReadString();
                        _saveExpected = br.ReadInt32();
                        _hostSlot = br.ReadInt32();
                        _bundleExpected = br.ReadInt32();
                        _selfId = br.ReadByte();
                        PeerNames[msg.ConnId] = hostName;
                        _avatars.SetName(msg.ConnId, hostName);
                        _saveBuf = new MemoryStream(_saveExpected > 0 ? _saveExpected : 1024);
                        _bundleBuf = new MemoryStream(_bundleExpected > 0 ? _bundleExpected : 16);
                        StatusLine = $"Downloading {hostName}'s shop ({(_saveExpected + _bundleExpected) / 1024} KB)...";
                    }
                    break;
                }
                case MsgType.SaveChunk:
                {
                    if (Role != CoopRole.Client || _saveBuf == null) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        br.ReadInt32(); // offset (TCP keeps order; kept for sanity/debug)
                        int len = br.ReadInt32();
                        var bytes = br.ReadBytes(len);
                        _saveBuf.Write(bytes, 0, bytes.Length);
                    }
                    break;
                }
                case MsgType.SaveDone:
                {
                    if (Role != CoopRole.Client || _saveBuf == null || _worldRequested) break;
                    var data = _saveBuf.ToArray();
                    _saveBuf = null;
                    if ((_saveExpected >= 0 && data.Length != _saveExpected)
                        || data.Length < 1024 || data[0] != (byte)'{')
                    {
                        ErrorLine = $"World download looked corrupted ({data.Length}/{_saveExpected} bytes) - try again.";
                        Shutdown("bad download");
                        break;
                    }
                    _pendingSave = data;
                    StatusLine = "Shop received - waiting for mod data...";
                    break;
                }
                case MsgType.BundleChunk:
                {
                    if (Role != CoopRole.Client || _bundleBuf == null) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        br.ReadInt32(); // offset
                        int len = br.ReadInt32();
                        var bytes = br.ReadBytes(len);
                        _bundleBuf.Write(bytes, 0, bytes.Length);
                    }
                    break;
                }
                case MsgType.BundleDone:
                {
                    if (Role != CoopRole.Client || _worldRequested || _pendingSave == null) break;
                    var bundle = _bundleBuf != null ? _bundleBuf.ToArray() : new byte[0];
                    _bundleBuf = null;
                    _worldRequested = true;
                    StatusLine = "World received - loading...";
                    try
                    {
                        SidecarTransfer.ApplyBundle(bundle, _hostSlot, SaveTransfer.CoopSlot);
                    }
                    catch (Exception e)
                    {
                        CoopPlugin.Log.LogWarning("Sidecar apply failed (continuing): " + e.Message);
                    }
                    SaveTransfer.ApplyAndLoad(_pendingSave);
                    _pendingSave = null;
                    break;
                }
                case MsgType.ShelfDelta:
                {
                    // Dropping deltas while not in the game scene is safe (the world just
                    // loaded from the host's save; the host keeps re-diffing changes) and
                    // avoids touching scene managers that don't exist yet.
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _world.ApplyRemote(WorldSync.ReadEntries(br));
                    break;
                }
                case MsgType.ShelfRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _world.ApplyRemote(WorldSync.ReadEntries(br));
                    break;
                }
                case MsgType.PriceList:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int n = br.ReadUInt16();
                        var prices = CPlayerData.m_SetItemPriceList;
                        for (int i = 0; i < n && i < prices.Count; i++)
                        {
                            float v = br.ReadSingle();
                            if (Math.Abs(prices[i] - v) > 0.0001f)
                            {
                                prices[i] = v;
                                CEventManager.QueueEvent(new CEventPlayer_ItemPriceChanged((EItemType)i, v));
                            }
                        }
                    }
                    break;
                }
                case MsgType.PlayerState:
                {
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        float yaw = br.ReadSingle();
                        float speed = br.ReadSingle();
                        byte hold = br.ReadByte();
                        ReadHoldPayload(br, hold, out var holdTypes, out var holdCards);
                        _diagRecvStates++;
                        _avatars.UpdateState(msg.ConnId, pos, yaw, speed, hold, holdTypes, holdCards);
                        if (PeerNames.TryGetValue(msg.ConnId, out var peerName))
                            _avatars.SetName(msg.ConnId, peerName); // re-seed after scene loads clear avatars
                        if (Role == CoopRole.Host && _net.ConnectionCount > 1)
                        {
                            // other clients should see this player too
                            var relay = Msg.Build(MsgType.RelayState, bw =>
                            {
                                bw.Write((byte)msg.ConnId);
                                bw.Write(pos.x); bw.Write(pos.y); bw.Write(pos.z);
                                bw.Write(yaw); bw.Write(speed); bw.Write(hold);
                                WriteHoldPayload(bw, hold, holdTypes, holdCards);
                            });
                            foreach (int cid in _net.ConnIds())
                                if (cid != msg.ConnId) _net.Send(cid, relay);
                        }
                        if (_gotStateFrom.Add(msg.ConnId))
                        {
                            string who = PeerNames.TryGetValue(msg.ConnId, out var n) ? n : ("player " + msg.ConnId);
                            CoopPlugin.Log.LogInfo($"Position link active with {who}");
                            if (Role == CoopRole.Host) StatusLine = $"Hosting - {who} is in your shop!";
                        }
                    }
                    break;
                }
                case MsgType.CoinSet:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        double coin = br.ReadDouble();
                        float coinF = br.ReadSingle();
                        if (!_loggedEconLink)
                        {
                            _loggedEconLink = true;
                            CoopPlugin.Log.LogInfo("Economy link active (host wallet mirrored)");
                        }
                        if (Math.Abs(CPlayerData.m_CoinAmountDouble - coin) > 0.0001)
                            CEventManager.QueueEvent(new CEventPlayer_SetCoin(coinF, coin));
                    }
                    break;
                }
                case MsgType.DayTime:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int day = br.ReadInt32();
                        int hour = br.ReadInt32();
                        int min = br.ReadInt32();
                        if (!_loggedTimeLink)
                        {
                            _loggedTimeLink = true;
                            CoopPlugin.Log.LogInfo($"Time link active (Day {day} {hour:00}:{min:00})");
                        }
                        HostTimeLine = $"Day {day + 1}  {hour:00}:{min:00}"; // HUD shows day+1
                        bool dayChanged = day != CPlayerData.m_CurrentDay;
                        CPlayerData.m_CurrentDay = day;
                        // The client clock only advances while the shop-open flag is set and
                        // the day hasn't "ended"; both are cosmetic here, keep them permissive.
                        CPlayerData.m_IsShopOnceOpen = true;
                        try
                        {
                            if (_lightManager == null) _lightManager = FindObjectOfType<LightManager>();
                            if (_lightManager != null)
                            {
                                if (dayChanged && InGameLevel() && MiDayReset != null)
                                {
                                    // Run the game's own new-day environment reset (skybox, GI,
                                    // 08:00 clock, morning music) and let exactly one
                                    // OnDayStarted through so the HUD/day label refresh.
                                    Patches.GamePatches.AllowNextDayStarted = true;
                                    _lightManager.StartCoroutine(
                                        (System.Collections.IEnumerator)MiDayReset.Invoke(_lightManager, null));
                                    CoopPlugin.Log.LogInfo($"Mirroring host day change -> Day {day}");
                                }
                                else
                                {
                                    FiTimeHour?.SetValue(_lightManager, hour);
                                    FiTimeMin?.SetValue(_lightManager, min);
                                    FiTimeMinFloat?.SetValue(_lightManager, (float)min);
                                    FiHasDayEnded?.SetValue(_lightManager, false); // never freeze at closing
                                }
                            }
                        }
                        catch { }
                    }
                    break;
                }
                case MsgType.ProgressSet:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int exp = br.ReadInt32();
                        int level = br.ReadInt32();
                        int fame = br.ReadInt32();
                        int prevLevel = CPlayerData.m_ShopLevel;
                        // Order matters: set the level FIRST, because the SetShopExp handler
                        // levels up while exp >= required-for-current-level. With the host's
                        // consistent (level, exp) pair, exp < required and no spurious level-up.
                        CPlayerData.m_ShopLevel = level;
                        CEventManager.QueueEvent(new CEventPlayer_SetShopExp(exp));
                        CEventManager.QueueEvent(new CEventPlayer_SetFame(fame));
                        if (level > prevLevel)
                            CEventManager.QueueEvent(new CEventPlayer_ShopLeveledUp(level));
                    }
                    break;
                }
                case MsgType.Emote:
                {
                    _avatars.ShowEmote(msg.ConnId);
                    RelayTagToOthers(msg.ConnId, 0);
                    break;
                }
                case MsgType.Activity:
                {
                    int packIdx = -1;
                    try { using (var br = Msg.Reader(msg.Payload)) { br.ReadByte(); packIdx = br.ReadInt32(); } }
                    catch { }
                    _avatars.ShowTag(msg.ConnId, "opening a pack!", 3f);
                    _avatars.ShowPackOpen(msg.ConnId, packIdx);
                    RelayTagToOthers(msg.ConnId, 1, packIdx);
                    break;
                }
                case MsgType.Roster:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int n = br.ReadByte();
                        var seen = new HashSet<int>();
                        for (int i = 0; i < n; i++)
                        {
                            int id = br.ReadByte();
                            string name = br.ReadString();
                            if (id == _selfId) continue;
                            seen.Add(id);
                            if (_relayIds.Add(id)) CoopPlugin.Log.LogInfo($"peer in shop: {name}");
                            _avatars.SetName(1000 + id, name);
                        }
                        _relayIds.RemoveWhere(id =>
                        {
                            if (seen.Contains(id)) return false;
                            _avatars.Remove(1000 + id);
                            return true;
                        });
                    }
                    break;
                }
                case MsgType.RelayState:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int senderId = br.ReadByte();
                        var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        float yaw = br.ReadSingle();
                        float speed = br.ReadSingle();
                        byte hold = br.ReadByte();
                        ReadHoldPayload(br, hold, out var holdTypes, out var holdCards);
                        if (senderId != _selfId)
                            _avatars.UpdateState(1000 + senderId, pos, yaw, speed, hold, holdTypes, holdCards);
                    }
                    break;
                }
                case MsgType.RelayTag:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int senderId = br.ReadByte();
                        byte kind = br.ReadByte();
                        int extra = -1;
                        try { extra = br.ReadInt32(); } catch { }
                        if (senderId == _selfId) break;
                        if (kind == 0) _avatars.ShowEmote(1000 + senderId);
                        else
                        {
                            _avatars.ShowTag(1000 + senderId, "opening a pack!", 3f);
                            _avatars.ShowPackOpen(1000 + senderId, extra);
                        }
                    }
                    break;
                }
                case MsgType.CardDelta:
                {
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        bool isAdd = br.ReadBoolean();
                        int amount = br.ReadInt32();
                        var card = new CardData
                        {
                            expansionType = (ECardExpansionType)br.ReadInt32(),
                            monsterType = (EMonsterType)br.ReadInt32(),
                            borderType = (ECardBorderType)br.ReadInt32(),
                            isFoil = br.ReadBoolean(),
                            isDestiny = br.ReadBoolean(),
                            isChampionCard = br.ReadBoolean(),
                            isNew = br.ReadBoolean(),
                            cardGrade = br.ReadInt32(),
                            gradedCardIndex = br.ReadInt32(),
                        };
                        Patches.GamePatches.ApplyingRemoteCards = true;
                        try
                        {
                            if (isAdd) CPlayerData.AddCard(card, amount);
                            else CPlayerData.ReduceCard(card, amount);
                        }
                        finally { Patches.GamePatches.ApplyingRemoteCards = false; }
                    }
                    break;
                }
                case MsgType.NpcState:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _npcs.ApplyBatch(br, InGameLevel());
                    break;
                }
                case MsgType.CardShelfDelta:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _cardShelves.ApplyRemote(CardShelfSync.ReadEntries(br));
                    break;
                }
                case MsgType.CardShelfRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _cardShelves.ApplyRemote(CardShelfSync.ReadEntries(br));
                    break;
                }
                case MsgType.CardPriceSet:
                {
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        var card = Msg.ReadCard(br);
                        float price = br.ReadSingle();
                        Patches.GamePatches.ApplyingRemotePrice = true;
                        try { CPlayerData.SetCardPrice(card, price); }
                        finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                    }
                    break;
                }
                case MsgType.RegisterState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _registerMirror.Apply(Sync.RegisterServe.ReadStates(br));
                    break;
                }
                case MsgType.ServeRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int idx = br.ReadInt32();
                        string who = PeerNames.TryGetValue(msg.ConnId, out var n) ? n : "player";
                        string status = Sync.RegisterServe.Serve(idx, who, out var scanEcho);
                        Send(msg.ConnId, MsgType.ServeStatus, bw => bw.Write(status));
                        if (scanEcho != null)
                            Send(msg.ConnId, MsgType.ScanEcho, bw => bw.Write(scanEcho));
                    }
                    break;
                }
                case MsgType.ServeStatus:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        RegisterLine = br.ReadString();
                        RegisterLineTimer = 3f;
                    }
                    if (RegisterLine == "sale complete!")
                    {
                        // clear the vanilla checkout screen AND the counters' running
                        // totals for the next customer
                        try { CSingleton<UI_CashCounterScreen>.Instance.ResetCounter(); } catch { }
                        Guarded("reset-totals", Sync.RegisterServe.ClientResetTotals);
                    }
                    break;
                }
                case MsgType.ScanEcho:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int counterIdx = br.ReadByte();
                        bool isCard = br.ReadBoolean();
                        double price = br.ReadDouble();
                        double hostTotal = br.ReadDouble();
                        try
                        {
                            var sm = FindObjectOfType<ShelfManager>();
                            if (sm == null || counterIdx >= sm.m_CashierCounterList.Count) break;
                            var counter = sm.m_CashierCounterList[counterIdx];
                            CardData card = isCard ? Msg.ReadCard(br) : null;
                            EItemType itemType = isCard ? default : (EItemType)br.ReadInt32();
                            Sync.RegisterServe.ApplyScanEcho(counter, isCard, price, hostTotal, itemType, card);
                        }
                        catch { } // vanilla UI not open on this side - totals still fine
                    }
                    break;
                }
                case MsgType.EconContrib:
                {
                    if (Role != CoopRole.Host) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        byte kind = br.ReadByte();
                        float v = br.ReadSingle();
                        switch (kind)
                        {
                            case 1: CEventManager.QueueEvent(new CEventPlayer_AddCoin(v)); break;
                            case 2: CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(v)); break;
                            case 3: CEventManager.QueueEvent(new CEventPlayer_AddShopExp((int)v)); break;
                            case 4: CEventManager.QueueEvent(new CEventPlayer_AddFame((int)v)); break;
                        }
                    }
                    break;
                }
                case MsgType.Ping:
                    Send(msg.ConnId, MsgType.Pong, null);
                    break;
                case MsgType.Pong:
                    break;
                case MsgType.Bye:
                {
                    string reason = "the host ended the session";
                    if (msg.Payload.Length > 0)
                    {
                        try { using (var br = Msg.Reader(msg.Payload)) reason = br.ReadString(); }
                        catch { }
                    }
                    if (Role == CoopRole.Client)
                    {
                        ErrorLine = reason;
                        Shutdown("rejected: " + reason);
                    }
                    else
                    {
                        _net.Kick(msg.ConnId);
                    }
                    break;
                }
            }
        }

        private void SendWorldTo(int connId)
        {
            byte[] payload;
            byte[] bundle;
            int hostSlot;
            try
            {
                hostSlot = CSingleton<CGameManager>.Instance.m_CurrentSaveLoadSlotSelectedIndex;
                payload = SaveTransfer.BuildHostPayload();
                try { bundle = SidecarTransfer.BuildBundle(hostSlot); }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning("Sidecar bundle failed (sending base save only): " + e.Message);
                    bundle = new byte[0];
                }
            }
            catch (Exception e)
            {
                ErrorLine = "Could not snapshot the shop: " + e.Message;
                CoopPlugin.Log.LogError(e);
                return;
            }

            Send(connId, MsgType.Welcome, bw =>
            {
                bw.Write(CoopPlugin.Version);
                bw.Write(CoopPlugin.PlayerName.Value);
                bw.Write(payload.Length);
                bw.Write(hostSlot);
                bw.Write(bundle.Length);
                bw.Write((byte)connId); // tells the client its own id (to skip in rosters)
            });

            var net = _net;
            new Thread(() =>
            {
                const int chunk = 128 * 1024;
                try
                {
                    for (int off = 0; off < payload.Length; off += chunk)
                    {
                        int len = Math.Min(chunk, payload.Length - off);
                        int o = off;
                        net.Send(connId, Msg.Build(MsgType.SaveChunk, bw =>
                        {
                            bw.Write(o);
                            bw.Write(len);
                            bw.Write(payload, o, len);
                        }));
                    }
                    net.Send(connId, Msg.Build(MsgType.SaveDone, bw => bw.Write(payload.Length)));

                    for (int off = 0; off < bundle.Length; off += chunk)
                    {
                        int len = Math.Min(chunk, bundle.Length - off);
                        int o = off;
                        net.Send(connId, Msg.Build(MsgType.BundleChunk, bw =>
                        {
                            bw.Write(o);
                            bw.Write(len);
                            bw.Write(bundle, o, len);
                        }));
                    }
                    net.Send(connId, Msg.Build(MsgType.BundleDone, bw => bw.Write(bundle.Length)));
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("World send failed: " + e.Message);
                }
            }) { IsBackground = true, Name = "CoopWorldSend" }.Start();
        }

        private void OnGUI()
        {
            _ui.Draw(this, _net);
        }
    }
}
