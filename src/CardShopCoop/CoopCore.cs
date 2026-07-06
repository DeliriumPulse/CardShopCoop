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
        private readonly ObjMoveSync _objMoves = new ObjMoveSync();
        private readonly BoxSync _boxes = new BoxSync();
        private readonly PopulationSync _population = new PopulationSync();

        // domain sync modules (v0.15): each owns one game system end-to-end and talks
        // through the standard SendOp/BroadcastState/HostApplyOp/ClientApplyState contract
        private readonly GradingSync _grading = new GradingSync();
        private readonly TradeServe _trades = new TradeServe();
        private readonly PlayTableSync _tables = new PlayTableSync();
        private readonly StaffSync _staff = new StaffSync();
        private readonly ShopStateSync _shopState = new ShopStateSync();
        private readonly SettingsSync _settings = new SettingsSync();
        private readonly MarketSync _market = new MarketSync();
        private readonly ReportSync _report = new ReportSync();
        private readonly ContainerSync _containers = new ContainerSync();
        private readonly TournamentSync _tournament = new TournamentSync();
        private readonly CardBoxSync _cardBoxes = new CardBoxSync();
        private readonly FurnBoxSync _furnBoxes = new FurnBoxSync();
        private string _lastShopNameSent;
        private float _shopNameTimer = -1.0f; // staggered phase (see _lightSyncTimer note)
        private readonly Sync.RegisterMirror _registerMirror = new Sync.RegisterMirror();
        private float _npcSweepTimer = -1.3f;
        private float _regStateTimer = -0.17f;
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
        private float _priceTimer = -0.45f;
        private int _lastPriceHash;
        private float _priceHeal;
        private readonly List<KeyValuePair<int, float>> _priceBuf = new List<KeyValuePair<int, float>>();
        private readonly HashSet<int> _priceSeenTypes = new HashSet<int>();

        // timers
        private float _stateTimer;
        private float _pingTimer;
        private float _econTimer = -0.11f;
        private float _dayTimer = -0.9f;

        // local movement measurement
        private Vector3 _lastPos;
        private bool _hasLastPos;

        // host economy/progression change detection
        private double _lastCoinSent = double.MinValue;
        private long _lastProgressSent = long.MinValue;
        private float _coinHeal;      // re-send the wallet every 15s even if unchanged
        private float _progressHeal;  // ...and shop exp/level/fame, so a dropped packet self-heals
        // guest spends applied so far THIS frame; lets the host reject a spend the guest
        // passed against its stale (0.5s-lagged) wallet mirror before the shared balance
        // goes negative. Reset once per frame before the message drain.
        private double _pendingReduceThisFrame = 0.0;

        // one-time link confirmation logging
        private readonly HashSet<int> _gotStateFrom = new HashSet<int>();
        private bool _loggedEconLink;
        private bool _loggedTimeLink;

        // pipeline diagnostics: prove where sync stalls instead of guessing
        private long _diagSent;
        private long _diagRecvStates;
        private float _diagTimer = -7.3f;
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
        private static readonly FieldInfo FiTimeOfDayIdx = typeof(LightManager).GetField("m_TImeOfDayIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FiFinishLoading = typeof(LightManager).GetField("m_FinishLoading", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly System.Reflection.MethodInfo MiLightInit = typeof(LightManager).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly System.Reflection.MethodInfo MiUpdateLightData = typeof(LightManager).GetMethod("UpdateLightTimeData", BindingFlags.NonPublic | BindingFlags.Instance);
        private float _lightSyncTimer = -2.3f;   // timers carry staggered phases so the
        private LightManager _lightManager;      // periodic broadcasts never bunch into
        private float _cardResyncTimer = -5.2f;  // one frame (the rhythmic-hitch bug)
        private int _lastCardResyncHash;         // change-gate for the 12s full card repaint
        private float _cardResyncHeal;           // forces a repaint every 30s regardless
        private float _licenseSyncTimer = -3.7f;
        private double _lastLicenseBuyTime = -999.0;
        private string _lastLightJson;
        private float _lightHeal;
        private double _lastDayMirrorAt = -999.0;
        private int _lastLicenseHash;
        private float _licenseHeal;

        // per-frame stages run through cached delegates: a fresh closure per stage per
        // frame was ~600 allocations/second of GC pressure that only existed in-session
        private float _dt;
        private bool _syncActive;
        private Action _actNetPump, _actAvatars, _actWorld, _actCardShelves, _actObjMoves,
            _actBoxes, _actPopulation, _actNpcPuppets, _actRegisterMirror, _actNpcSweep,
            _actStateSend, _actNpcCollect, _actRegisterCollect, _actModules;
        private CustomerManager _cmSweep;
        private bool _renamerHandled;
        private int _heldBoxFrame = -1;
        private object _heldBoxA, _heldBoxB, _heldBoxC;

        /// <summary>True while a received world is loading (plus a settle grace):
        /// the game's own load-cleanup destroys objects and NOTHING destroyed in
        /// that window is a player action to forward.</summary>
        public static bool ClientReloading;
        private float _reloadGrace;
        /// <summary>The pre-scene-load slice of a reload: the OLD world is still live,
        /// so client box reports would describe a world about to be torn down. Stale
        /// reports can shrink host box contents - hold them until the new scene lands
        /// (once the grace is armed, fresh reports flow again immediately).</summary>
        private bool ClientPreloadHold => ClientReloading && _reloadGrace <= 0f;
        private readonly System.Collections.Generic.List<InMsg> _dispatchBuf
            = new System.Collections.Generic.List<InMsg>(64);
        private readonly System.Collections.Generic.HashSet<long> _dispatchSeen
            = new System.Collections.Generic.HashSet<long>();

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
            _objMoves.OnLocalChanges = changes =>
            {
                if (Role == CoopRole.Host)
                    Broadcast(MsgType.ObjMoveDelta, bw => ObjMoveSync.WriteEntries(bw, changes));
                else if (Role == CoopRole.Client)
                    Send(1, MsgType.ObjMoveRequest, bw => ObjMoveSync.WriteEntries(bw, changes));
            };
            _population.OnHostSnapshot = all =>
                Broadcast(MsgType.PopState, bw => PopulationSync.Write(bw, all));
            _boxes.OnHostSnapshot = list =>
                Broadcast(MsgType.BoxState, bw => BoxSync.WriteEntries(bw, list));
            _boxes.OnClientChanges = list =>
                Send(1, MsgType.BoxRequest, bw => BoxSync.WriteEntries(bw, list));
            BoxSync.IsLocallyCarried = box =>
            {
                if (_playerIpc == null || box == null) return false;
                try
                {
                    // resolve the held refs once per frame, not per box: this delegate
                    // runs for every box in every apply/tick pass
                    if (_heldBoxFrame != Time.frameCount)
                    {
                        _heldBoxFrame = Time.frameCount;
                        _heldBoxA = FiHoldItemBox?.GetValue(_playerIpc);
                        _heldBoxB = FiHoldBox?.GetValue(_playerIpc);
                        _heldBoxC = FiHoldBoxCard?.GetValue(_playerIpc);
                    }
                    return ReferenceEquals(_heldBoxA, box) || ReferenceEquals(_heldBoxB, box);
                }
                catch { return false; }
            };
            CardBoxSync.IsLocallyCarried = box =>
            {
                if (_playerIpc == null || box == null) return false;
                try
                {
                    // card boxes land in BOTH the generic hold field and the card-box
                    // field (OnEnterHoldBoxMode); reuse the per-frame cached reads
                    if (_heldBoxFrame != Time.frameCount)
                    {
                        _heldBoxFrame = Time.frameCount;
                        _heldBoxA = FiHoldItemBox?.GetValue(_playerIpc);
                        _heldBoxB = FiHoldBox?.GetValue(_playerIpc);
                        _heldBoxC = FiHoldBoxCard?.GetValue(_playerIpc);
                    }
                    return ReferenceEquals(_heldBoxC, box) || ReferenceEquals(_heldBoxB, box);
                }
                catch { return false; }
            };
            BoxSync.LocalBoxDestroyed = box =>
            {
                if (!InGameLevel() || ClientReloading) return;
                if (Role == CoopRole.Client) _boxes.NotifyLocalDestroyed(box);
                else if (Role == CoopRole.Host) _boxes.HostNotifyLocalDestroyed();
            };
            _boxes.OnLocalRemoved = (idx, type) =>
                Send(1, MsgType.BoxRemoved, bw => { bw.Write(idx); bw.Write(type); });
            PopulationSync.OnClientStructureChanged = kind =>
            {
                // a repaired/respawned card display starts empty locally; that emptiness
                // is repair fallout, not a player action - never report it to the host
                if (Role == CoopRole.Client && (kind == 2 || kind == 3))
                    _cardShelves.InvalidateBaseline();
            };
            _actNetPump = () => _net.PumpMainThread();
            _actAvatars = () =>
            {
                AvatarManager.ViewCamera = _playerCamTf; // the camera the player SEES through
                _avatars.Tick(_dt);
            };
            _actWorld = () => _world.Tick(_dt, _syncActive);
            _actCardShelves = () =>
            {
                _cardShelves.IsClientRole = Role == CoopRole.Client;
                _cardShelves.Tick(_dt, _syncActive);
            };
            _actObjMoves = () => _objMoves.Tick(_dt, _syncActive);
            _actBoxes = () =>
            {
                if (Role == CoopRole.Host) _boxes.HostTick(_dt, _syncActive);
                else if (Role == CoopRole.Client) _boxes.ClientTick(_dt, _syncActive && !ClientPreloadHold);
            };
            _actPopulation = () => { if (Role == CoopRole.Host) _population.HostTick(_dt, _syncActive); };
            _actNpcPuppets = () => _npcs.TickPuppets(_dt, InGameLevel());
            _actRegisterMirror = RegisterMirrorTick;
            _actNpcSweep = NpcSweepTick;
            _actStateSend = StateSendTick;
            _actNpcCollect = NpcCollectTick;
            _actRegisterCollect = RegisterCollectTick;

            _grading.SendOp = w => Send(1, MsgType.GradingOp, w);
            _grading.BroadcastState = w => Broadcast(MsgType.GradingState, w);
            _trades.SendOp = w => Send(1, MsgType.TradeOp, w);
            _trades.BroadcastState = w => Broadcast(MsgType.TradeState, w);
            _tables.BroadcastState = w => Broadcast(MsgType.TableState, w);
            _staff.SendOp = w => Send(1, MsgType.StaffOp, w);
            _staff.BroadcastState = w => Broadcast(MsgType.StaffState, w);
            _shopState.SendOp = w => Send(1, MsgType.ShopOp, w);
            _shopState.BroadcastState = w => Broadcast(MsgType.ShopState, w);
            _settings.SendOp = w => Send(1, MsgType.SettingsOp, w);
            _settings.BroadcastState = w => Broadcast(MsgType.SettingsState, w);
            _market.BroadcastState = w => Broadcast(MsgType.MarketState, w);
            _report.BroadcastState = w => Broadcast(MsgType.ReportState, w);
            _containers.SendOp = w => Send(1, MsgType.ContainerOp, w);
            _containers.BroadcastState = w => Broadcast(MsgType.ContainerState, w);
            _containers.RequestBoxResync = () => _boxes.ForceBroadcastNextTick();
            _tournament.BroadcastState = w => Broadcast(MsgType.TournamentState, w);
            _cardBoxes.SendOp = w => Send(1, MsgType.CardBoxOp, w);
            _cardBoxes.BroadcastState = w => Broadcast(MsgType.CardBoxState, w);
            _furnBoxes.SendOp = w => Send(1, MsgType.FurnBoxOp, w);
            _furnBoxes.BroadcastState = w => Broadcast(MsgType.FurnBoxState, w);
            FurnBoxSync.IsLocallyCarried = box =>
            {
                if (_playerIpc == null || box == null) return false;
                try
                {
                    // ONLY the generic hold field: the game never nulls
                    // m_CurrentHoldingBoxShelf on set-down, so it lies forever
                    if (_heldBoxFrame != Time.frameCount)
                    {
                        _heldBoxFrame = Time.frameCount;
                        _heldBoxA = FiHoldItemBox?.GetValue(_playerIpc);
                        _heldBoxB = FiHoldBox?.GetValue(_playerIpc);
                        _heldBoxC = FiHoldBoxCard?.GetValue(_playerIpc);
                    }
                    return ReferenceEquals(_heldBoxB, box);
                }
                catch { return false; }
            };
            _actModules = ModulesTick;
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
                bw.Write(Util.ModParity.CardsHash());
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

        // card/price mirrors that arrived during a scene load, flushed once in-game
        private struct PendingCard { public bool IsAdd; public int Amount; public CardData Card; }
        private readonly List<PendingCard> _pendingCardDeltas = new List<PendingCard>();
        private readonly List<KeyValuePair<CardData, float>> _pendingCardPrices = new List<KeyValuePair<CardData, float>>();

        private static InteractionPlayerController _deltaIpc; // NEVER CSingleton<>.Instance (fake-manager landmine)

        private static void ApplyCardDelta(bool isAdd, int amount, CardData card)
        {
            // grades are only ever 1-10; anything else is corruption (the trade-card
            // aliasing bug produced values like 1.3 billion). AddCard routes cardGrade>0
            // into the graded inventory keyed BY the grade, so a garbage grade makes a
            // permanent "fake" card that never matches or displays - refuse it.
            if (card.cardGrade != 0 && (card.cardGrade < 1 || card.cardGrade > 10))
            {
                CoopPlugin.Log.LogWarning($"card delta: dropping corrupt graded card {card.monsterType} (grade {card.cardGrade}) - not applied");
                return;
            }
            Patches.GamePatches.ApplyingRemoteCards = true;
            try
            {
                if (isAdd) CPlayerData.AddCard(card, amount);
                else if (card.cardGrade > 0)
                {
                    // graded cards live in m_GradedCardInventoryList; ReduceCard would miss
                    // them and wrongly decrement the ungraded array. Route through
                    // RemoveGradedCard (the graded-remove mirror normally arrives as
                    // MsgType.GradedRemove; this defends the CardDelta path too).
                    for (int i = 0; i < amount && CPlayerData.HasGradedCardInAlbum(card); i++)
                        CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
                }
                else CPlayerData.ReduceCard(card, amount);
            }
            finally { Patches.GamePatches.ApplyingRemoteCards = false; }
            // "cards didn't show up in the binder" reports were undiagnosable from the
            // receiving side - applies were completely silent
            CoopPlugin.Log.LogInfo($"card delta applied: {(isAdd ? "+" : "-")}{amount} {card.monsterType}{(card.cardGrade > 0 ? $" (grade {card.cardGrade})" : card.isFoil ? " (foil)" : "")}");
            RefreshOpenBinder();
        }

        // NEVER CSingleton<>.Instance (fake-manager landmine); cached, Unity re-resolves.
        private static readonly System.Reflection.MethodInfo MiBinderResort =
            HarmonyLib.AccessTools.Method(typeof(CollectionBinderFlipAnimCtrl), "OnSortingMethodUpdated");
        private static readonly System.Reflection.FieldInfo FiBinderIsBookOpen =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_IsBookOpen");

        /// <summary>Make an ALREADY-OPEN collection binder re-lay-out after a card change.
        /// SetCanUpdateSort alone only ARMS a gate the vanilla per-frame Update never
        /// consumes, so a traded/pulled card stayed invisible until the player flipped a
        /// page or reopened the binder. When the book is open we also invoke the game's own
        /// OnSortingMethodUpdated (backToFirstPage:false, keeps the current page) which
        /// rebuilds the sorted list + relays out all page groups, so the card appears now.</summary>
        private static void RefreshOpenBinder()
        {
            try
            {
                if (_deltaIpc == null) _deltaIpc = FindObjectOfType<InteractionPlayerController>();
                var ctrl = _deltaIpc != null ? _deltaIpc.m_CollectionBinderFlipAnimCtrl : null;
                if (ctrl == null) return;
                ctrl.SetCanUpdateSort(canSort: true);
                bool isOpen = FiBinderIsBookOpen != null && (bool)FiBinderIsBookOpen.GetValue(ctrl);
                if (isOpen && MiBinderResort != null)
                    MiBinderResort.Invoke(ctrl, new object[] { false }); // backToFirstPage:false
            }
            catch (System.Exception e) { CoopPlugin.Log.LogWarning($"binder relayout after card change failed: {e.Message}"); }
        }

        private void FlushPendingCardWork()
        {
            if (!InGameLevel() || (_pendingCardDeltas.Count == 0 && _pendingCardPrices.Count == 0)) return;
            Guarded("pending-cards", () =>
            {
                foreach (var p in _pendingCardDeltas) ApplyCardDelta(p.IsAdd, p.Amount, p.Card);
                if (_pendingCardDeltas.Count > 0)
                    CoopPlugin.Log.LogInfo($"applied {_pendingCardDeltas.Count} card change(s) held during loading");
                _pendingCardDeltas.Clear();
                Patches.GamePatches.ApplyingRemotePrice = true;
                try { foreach (var p in _pendingCardPrices) CPlayerData.SetCardPrice(p.Key, p.Value); }
                finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                _pendingCardPrices.Clear();
            });
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
            _objMoves.Reset();
            _boxes.Reset();
            _population.Reset();
            _registerMirror.Reset();
            ModulesReset();
            PromptLine = "";
            _lightManager = null;
            _cmSweep = null;
            _inventory = null;
            _renamerHandled = false;
            _catalogSent = false;
            if (ClientReloading) _reloadGrace = 10f; // countdown starts once in-game
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

        // NEVER CSingleton<>.Instance for scene-lifetime managers (CGameManager above
        // is a REAL persistent singleton and stays on the getter): touched while no
        // real manager exists (client reload loading screen - InGameLevel() stays true
        // there - or host mid-session save load) the getter fabricates a fake empty
        // DontDestroyOnLoad manager that shadows the real one for the rest of the run
        // (see WorldSync.ResolveShelfManager). Cached; the Unity fake-null re-resolves
        // after scene loads, and OnSceneLoaded clears them besides.
        private static InventoryBase _inventory;

        private static InventoryBase Inv()
        {
            if (_inventory == null) _inventory = FindObjectOfType<InventoryBase>();
            return _inventory;
        }

        private void ModulesTick()
        {
            bool inGame = InGameLevel();
            if (Role == CoopRole.Host)
            {
                _grading.HostTick(_dt, inGame);
                _trades.HostTick(_dt, inGame);
                _tables.HostTick(_dt, inGame);
                _staff.HostTick(_dt, inGame);
                _shopState.HostTick(_dt, inGame);
                _settings.HostTick(_dt, inGame);
                _market.HostTick(_dt, inGame);
                _report.HostTick(_dt, inGame); // per-frame: its report-open flag fires outside the timer
                _containers.HostTick(_dt, inGame);
                _tournament.HostTick(_dt, inGame);
                _cardBoxes.HostTick(_dt, inGame);
                _furnBoxes.HostTick(_dt, inGame);
            }
            else if (Role == CoopRole.Client)
            {
                _trades.ClientTick(_dt, inGame); // offer countdown + accept/decline keys
                _cardBoxes.ClientTick(_dt, inGame && !ClientPreloadHold); // carried transitions + box moves
                _furnBoxes.ClientTick(_dt, inGame && !ClientPreloadHold);
                // content mods register their products SECONDS after the scene loads
                // (and per-save: a host mid-tutorial has none yet) - keep re-digesting
                // as our catalog changes so the comparison never goes stale
                _catalogTimer += _dt;
                if (inGame && (_catalogTimer >= 45f || !_catalogSent))
                {
                    _catalogTimer = 0f;
                    _catalogSent = true;
                    int h = LocalCatalogHash();
                    if (h != _lastCatalogSentHash)
                    {
                        _lastCatalogSentHash = h;
                        SendCatalogDigest();
                    }
                }
            }
        }

        private static int LocalCatalogHash()
        {
            try
            {
                int total = CatalogCount(); // vanilla + EPL virtual entries
                int h = 17;
                for (int i = 0; i < total; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null) h = h * 31 + (((int)rd.itemType << 1) | (rd.isBigBox ? 1 : 0));
                }
                return h;
            }
            catch { return 0; }
        }

        private void ModulesReset()
        {
            _grading.Reset();
            _trades.Reset();
            _tables.Reset();
            _staff.Reset();
            _shopState.Reset();
            _settings.Reset();
            _market.Reset();
            _report.Reset();
            _containers.Reset();
            _tournament.Reset();
            _cardBoxes.Reset();
            _furnBoxes.Reset();
        }

        private void ModulesForceResend()
        {
            _grading.ForceResend();
            _trades.ForceResend();
            _tables.ForceResend();
            _staff.ForceResend();
            _shopState.ForceResend();
            _settings.ForceResend();
            _market.ForceResend();
            _report.ForceResend();
            _containers.ForceResend();
            _tournament.ForceResend();
            _cardBoxes.ForceResend();
            _furnBoxes.ForceResend();
        }

        private void RegisterMirrorTick()
        {
            _registerMirror.Tick(_dt);
            _regStateTimer += _dt;
            if (_regStateTimer >= 0.5f && InGameLevel())
            {
                _regStateTimer -= 0.5f;
                var tf = ResolvePlayer();
                int near = tf != null ? Sync.RegisterServe.FindNearestCounter(tf.position, CoopPlugin.ServeReach.Value, quiet: true) : -1;
                PromptLine = _registerMirror.PromptFor(near) ?? _trades.PromptFor(near) ?? "";
            }
        }

        private void NpcSweepTick()
        {
            // the shop-naming world trigger (and its "!" marker) is host-only; find it
            // ONCE - once disabled, FindObjectOfType can never see it again and each
            // retry was a full-scene scan for nothing
            if (!_renamerHandled)
            {
                _renamerHandled = true;
                var renamer = FindObjectOfType<ShopRenamer>();
                if (renamer != null && renamer.gameObject.activeSelf)
                {
                    renamer.gameObject.SetActive(false);
                    CoopPlugin.Log.LogInfo("disabled shop-renamer trigger (host names the shop)");
                }
            }
            if (_cmSweep == null) _cmSweep = FindObjectOfType<CustomerManager>();
            if (_cmSweep != null)
            {
                var list = _cmSweep.GetCustomerList();
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].gameObject.activeSelf)
                        list[i].gameObject.SetActive(false);
            }
            var workers = WorkerManager.GetWorkerList();
            if (workers != null)
                for (int i = 0; i < workers.Count; i++)
                    if (workers[i] != null && workers[i].gameObject.activeSelf)
                        workers[i].gameObject.SetActive(false);
        }

        private void StateSendTick()
        {
            float interval = 1f / Mathf.Clamp(CoopPlugin.SendRateHz.Value, 4f, 30f);
            Transform playerTf = InGameLevel() ? ResolvePlayer() : null;
            if (_stateTimer < interval || playerTf == null) return;
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
            BroadcastTransient(MsgType.PlayerState, bw =>
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
            _stateTimer = 0f; // full reset: the speed estimate divides by this elapsed time
        }

        private void NpcCollectTick()
        {
            var chunks = _npcs.HostCollect(_dt);
            if (chunks == null) return;
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                BroadcastTransient(MsgType.NpcState, bw => bw.Write(c));
            }
        }

        private void RegisterCollectTick()
        {
            _regStateTimer += _dt;
            if (_regStateTimer >= 0.5f)
            {
                _regStateTimer -= 0.5f;
                var batch = Sync.RegisterServe.CollectStates();
                if (batch != null)
                    BroadcastTransient(MsgType.RegisterState, bw => bw.Write(batch));
            }
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
                {
                    // describe the box (size + contents) so the avatar shows the real thing
                    if (FiHoldItemBox?.GetValue(_playerIpc) is InteractablePackagingBox_Item ib && ib != null)
                    {
                        _holdTypesBuf.Add(ib.m_IsBigBox ? 1 : 0);
                        try { _holdTypesBuf.Add((int)ib.m_ItemCompartment.GetItemType()); }
                        catch { _holdTypesBuf.Add(0); }
                    }
                    return 1; // carrying a box
                }
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

        /// <summary>Both roles: mirror a GRADED-card removal (trade-in, donation, re-grade).
        /// Graded cards live in a separate album ReduceCard/CardDelta never touch, so this
        /// is its own message; the receiver applies RemoveGradedCard by identity.</summary>
        public void ForwardGradedRemoval(CardData card)
        {
            if (Role == CoopRole.None || _net == null || card == null || card.cardGrade <= 0) return;
            Broadcast(MsgType.GradedRemove, bw => Msg.WriteCard(bw, card));
        }

        /// <summary>Client: the joiner bought restock - spawn the delivery on the host.</summary>
        public void ForwardOrder(int restockIndex, int count)
        {
            if (Role != CoopRole.Client || _net == null) return;
            // identity, never the raw index: modded restock lists (EPL packs) can be
            // ordered differently per machine - a raw index once turned a hololive
            // pack order into a $43 vanilla pack on the host
            RestockData rd = null;
            try { rd = InventoryBase.GetRestockData(restockIndex); } catch { }
            if (rd == null)
            {
                CoopPlugin.Log.LogWarning($"order: bad restock index {restockIndex}");
                return;
            }
            // the vanilla line cost rides along so a failed delivery can be REFUNDED -
            // the wallet charge already went through before the spawn call we intercept
            float lineCost = 0f;
            try
            {
                lineCost = CPlayerData.GetItemCost(rd.itemType)
                    * RestockManager.GetMaxItemCountInBox(rd.itemType, rd.isBigBox) * count;
            }
            catch { }
            Send(1, MsgType.OrderRequest, bw =>
            {
                bw.Write((int)rd.itemType);
                bw.Write(rd.isBigBox);
                bw.Write(rd.name ?? "");
                bw.Write(count);
                bw.Write(lineCost);
            });
        }

        /// <summary>Either side bought a product license: share it by identity.</summary>
        public void ForwardLicense(int restockIndex)
        {
            if (Role == CoopRole.None || _net == null) return;
            RestockData rd = null;
            try { rd = InventoryBase.GetRestockData(restockIndex); } catch { }
            if (rd == null) return;
            _lastLicenseBuyTime = UnityEngine.Time.realtimeSinceStartupAsDouble;
            int itemType = (int)rd.itemType;
            bool isBig = rd.isBigBox;
            string rdName = rd.name ?? "";
            if (Role == CoopRole.Host)
                Broadcast(MsgType.LicenseUnlock, bw => { bw.Write(itemType); bw.Write(isBig); bw.Write(rdName); });
            else
                Send(1, MsgType.LicenseUnlock, bw => { bw.Write(itemType); bw.Write(isBig); bw.Write(rdName); });
        }

        // ---- EPL virtual catalog bridge ----
        // EPL never ADDS modded products to m_RestockDataList: it INTERCEPTS the
        // game's list accesses (count/indexing) and serves the extra entries from
        // its own ItemLibrary. Direct list reads from THIS assembly see only the
        // ~135 vanilla rows - which is why hosts "didn't have" products sitting on
        // their own shelves, catalogs compared "identical (135)", and modded
        // license heals missed. Every catalog walk must span rawCount + EPL's
        // entries and read rows through the game's INTERCEPTED GetRestockData
        // (calling a game method executes its rewritten body - field-proven by
        // ForwardOrder reading modded identities on the client).
        private static bool _eplProbed;
        private static System.Reflection.PropertyInfo _eplAssetsProp, _eplItemLibProp, _eplRestockProp;

        private static int EplExtraCount()
        {
            try
            {
                if (!_eplProbed)
                {
                    _eplProbed = true;
                    var t = HarmonyLib.AccessTools.TypeByName("EnhancedPrefabLoader.Core.EplRuntimeData");
                    const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    _eplAssetsProp = t?.GetProperty("Assets", F);
                    var assets = _eplAssetsProp?.GetValue(null);
                    _eplItemLibProp = assets?.GetType().GetProperty("ItemLibrary", F);
                    var lib = assets == null ? null : _eplItemLibProp?.GetValue(assets);
                    _eplRestockProp = lib?.GetType().GetProperty("RestockEntries", F);
                    CoopPlugin.Log.LogInfo(_eplRestockProp != null
                        ? "EPL catalog bridge active (virtual restock entries visible)"
                        : "EPL catalog bridge inactive (EPL absent or its internals changed) - vanilla catalog only");
                }
                var a = _eplAssetsProp?.GetValue(null);
                var l = a == null ? null : _eplItemLibProp?.GetValue(a);
                return (l == null ? null : _eplRestockProp?.GetValue(l) as System.Collections.ICollection)?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>Full catalog size as the GAME sees it: raw vanilla rows plus EPL's
        /// intercepted virtual entries.</summary>
        private static int CatalogCount()
        {
            int raw = 0;
            try { raw = Inv().m_StockItemData_SO.m_RestockDataList.Count; } catch { }
            return raw + EplExtraCount();
        }

        /// <summary>Catalog row through the game's intercepted accessor (valid for
        /// vanilla AND virtual indexes); null when out of range or unresolvable.</summary>
        private static RestockData CatalogAt(int i)
        {
            try { return InventoryBase.GetRestockData(i); }
            catch { return null; }
        }

        /// <summary>Find OUR restock entry for a partner's (itemType, boxSize) identity.
        /// Tiered: exact -> name+size -> same product any size -> name any size. Content
        /// DATA packs are invisible to the plugin-parity hash, so catalogs CAN differ
        /// between machines - a near-match beats a silently lost order.</summary>
        private static int ResolveRestockIndex(int itemType, bool isBig, string name, out bool sizeDiffers)
        {
            sizeDiffers = false;
            try
            {
                int n = CatalogCount();
                for (int i = 0; i < n; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null && (int)rd.itemType == itemType && rd.isBigBox == isBig)
                        return i;
                }
                if (!string.IsNullOrEmpty(name))
                    for (int i = 0; i < n; i++)
                    {
                        var rd = CatalogAt(i);
                        if (rd != null && rd.name == name && rd.isBigBox == isBig)
                            return i;
                    }
                sizeDiffers = true;
                for (int i = 0; i < n; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null && (int)rd.itemType == itemType)
                        return i;
                }
                if (!string.IsNullOrEmpty(name))
                    for (int i = 0; i < n; i++)
                    {
                        var rd = CatalogAt(i);
                        if (rd != null && rd.name == name)
                            return i;
                    }
            }
            catch { }
            return -1;
        }

        private bool ApplyLicenseUnlock(int itemType, bool isBig, string name)
        {
            int idx = ResolveRestockIndex(itemType, isBig, name, out _);
            if (idx < 0)
            {
                CoopPlugin.Log.LogWarning($"license unlock: no local product for type {itemType} big={isBig} '{name}'");
                return false;
            }
            if (CPlayerData.GetIsItemLicenseUnlocked(idx)) return true; // already ours
            Patches.GamePatches.ApplyingRemoteLicense = true;
            try
            {
                CPlayerData.SetUnlockItemLicense(idx);
                // the vanilla purchase's non-UI side effects: achievements, the global
                // flag, and the TUTORIAL TASK credit - without the last one the host's
                // "Unlock Basic Card Box" task never cleared when the joiner bought it
                try { AchievementManager.OnItemLicenseUnlocked((EItemType)itemType); } catch { }
                try { GameInstance.m_IsItemLicenseUnlocked = true; } catch { }
                try
                {
                    if ((EItemType)itemType == EItemType.BasicCardBox)
                        TutorialManager.AddTaskValue(ETutorialTaskCondition.UnlockBasicCardBox, 1f);
                }
                catch { }
            }
            finally { Patches.GamePatches.ApplyingRemoteLicense = false; }
            RefreshLicensePanels();
            CoopPlugin.Log.LogInfo($"license unlocked by partner: {(EItemType)itemType} big={isBig}");
            return true;
        }

        private static readonly FieldInfo FiPanelIndex = HarmonyLib.AccessTools.Field(typeof(RestockItemPanelUI), "m_Index");
        private static readonly FieldInfo FiPanelLicGrp = HarmonyLib.AccessTools.Field(typeof(RestockItemPanelUI), "m_LicenseUIGrp");
        private static readonly FieldInfo FiPanelUIGrp = HarmonyLib.AccessTools.Field(typeof(RestockItemPanelUI), "m_UIGrp");

        /// <summary>A phone shop that's OPEN while a partner's license lands keeps showing
        /// the locked panel until reopened - flip freshly-unlocked panels the way the
        /// vanilla purchase button does. Runs only on license events (rare).</summary>
        private static void RefreshLicensePanels()
        {
            try
            {
                var panels = FindObjectsOfType<RestockItemPanelUI>(); // active = phone open
                foreach (var p in panels)
                {
                    if (!(FiPanelIndex?.GetValue(p) is int idx) || idx < 0) continue;
                    // no raw-list bounds check: modded panels carry VIRTUAL indexes
                    // beyond the raw flag list; the game's accessor handles them
                    bool on = false;
                    try { on = CPlayerData.GetIsItemLicenseUnlocked(idx); } catch { }
                    if (!on) continue;
                    (FiPanelLicGrp?.GetValue(p) as GameObject)?.SetActive(false);
                    (FiPanelUIGrp?.GetValue(p) as GameObject)?.SetActive(true);
                }
            }
            catch { }
        }

        // ---- product catalog diagnosis: content DATA packs (PTCGO expansions) are
        // invisible to the plugin-parity hash, so both sides can pass the handshake
        // while selling different product lists - orders for the missing ones fail.
        // The joiner sends its catalog once; the host reports any difference loudly. ----

        private bool _catalogSent;
        private float _catalogTimer;
        private int _lastCatalogSentHash;
        private readonly HashSet<int> _catalogWarnedConns = new HashSet<int>();
        private readonly Dictionary<int, string> _rosterNames = new Dictionary<int, string>();
        private HashSet<int> _clientPriced = new HashSet<int>();   // itemTypes the host has priced
        private HashSet<int> _incomingPriced = new HashSet<int>(); // scratch, swapped per apply

        private void SendCatalogDigest()
        {
            try
            {
                int total = CatalogCount(); // vanilla + EPL virtual entries
                var entries = new List<RestockData>(total);
                // blank-name entries are mod UI placeholders (Collection Tracker's
                // restock-shop replacement adds two) - not orderable, not comparable
                for (int i = 0; i < total; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null && !string.IsNullOrEmpty(rd.name)) entries.Add(rd);
                }
                Send(1, MsgType.CatalogDigest, bw =>
                {
                    int cnt = Mathf.Min(entries.Count, ushort.MaxValue);
                    bw.Write((ushort)cnt);
                    for (int i = 0; i < cnt; i++)
                    {
                        bw.Write((int)entries[i].itemType);
                        bw.Write(entries[i].isBigBox);
                        bw.Write(Fnv(entries[i].name ?? ""));
                    }
                });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("catalog digest: " + e.Message); }
        }

        private void CompareCatalogs(System.IO.BinaryReader br, int connId)
        {
            int n = br.ReadUInt16();
            var joiner = new HashSet<long>();
            for (int i = 0; i < n; i++)
            {
                int t = br.ReadInt32();
                bool big = br.ReadBoolean();
                int nameHash = br.ReadInt32();
                joiner.Add(CatalogKey(t, big, nameHash));
            }
            if (Inv() == null) return; // no live world to compare against; the next digest retries
            int total = CatalogCount(); // vanilla + EPL virtual entries
            int hostOnly = 0, shared = 0;
            var examples = new List<string>();
            for (int i = 0; i < total; i++)
            {
                var rd = CatalogAt(i);
                if (rd == null || string.IsNullOrEmpty(rd.name)) continue; // placeholder rows, see SendCatalogDigest
                if (joiner.Contains(CatalogKey((int)rd.itemType, rd.isBigBox, Fnv(rd.name))))
                {
                    shared++;
                    continue;
                }
                hostOnly++;
                if (examples.Count < 6) examples.Add(rd.name);
            }
            int joinerOnly = joiner.Count - shared;
            if (hostOnly == 0 && joinerOnly == 0)
            {
                CoopPlugin.Log.LogInfo($"catalog check: identical ({shared} products)");
                // content mods register products seconds-to-minutes after load, so an
                // early check can cry wolf; the recheck should also retract the cry
                if (_catalogWarnedConns.Remove(connId))
                {
                    const string clear = "catalogs match now - the earlier warning was mod startup timing, all good";
                    RegisterLine = clear;
                    RegisterLineTimer = 8f;
                    Send(connId, MsgType.Toast, bw => bw.Write(clear));
                }
                return;
            }
            string who = PeerNames.TryGetValue(connId, out var nm) ? nm : "joiner";
            string summary = $"heads-up: product catalogs differ ({hostOnly} only on host, {joinerOnly} only on {who}) - mismatched items can't be ordered; match your content packs";
            CoopPlugin.Log.LogWarning("catalog check: " + summary
                + (examples.Count > 0 ? " | host-only e.g.: " + string.Join(" / ", examples.ToArray()) : ""));
            _catalogWarnedConns.Add(connId);
            RegisterLine = summary;
            RegisterLineTimer = 10f;
            Send(connId, MsgType.Toast, bw => bw.Write(summary));
        }

        private void LogCatalogCandidates(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return;
                string probe = name.Split(' ')[0];
                int total = CatalogCount(); // vanilla + EPL virtual entries
                var found = new List<string>();
                for (int i = 0; i < total && found.Count < 8; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd?.name != null && rd.name.IndexOf(probe, StringComparison.OrdinalIgnoreCase) >= 0)
                        found.Add($"{rd.name} (type {(int)rd.itemType}, big={rd.isBigBox})");
                }
                CoopPlugin.Log.LogInfo(found.Count > 0
                    ? "similar host entries: " + string.Join(" | ", found.ToArray())
                    : $"no host entries resembling '{probe}'");
            }
            catch { }
        }

        private static long CatalogKey(int type, bool big, int nameHash)
        {
            return ((long)type << 33) ^ ((long)(uint)nameHash << 1) ^ (big ? 1L : 0L);
        }

        /// <summary>Deterministic across machines (string.GetHashCode is not).</summary>
        private static int Fnv(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
                return (int)h;
            }
        }

        /// <summary>Client: the joiner bought furniture - deliver it on the host.</summary>
        public void ForwardFurniture(int objType, Vector3 pos, Quaternion rot)
        {
            if (Role != CoopRole.Client || _net == null) return;
            Send(1, MsgType.FurnitureOrder, bw =>
            {
                bw.Write(objType);
                bw.Write(pos.x); bw.Write(pos.y); bw.Write(pos.z);
                bw.Write(rot.x); bw.Write(rot.y); bw.Write(rot.z); bw.Write(rot.w);
            });
        }

        /// <summary>Client: the joiner set an item price - the host's table is authoritative.</summary>
        public void ForwardItemPrice(EItemType itemType, float price)
        {
            if (Role != CoopRole.Client || _net == null) return;
            Send(1, MsgType.ItemPriceContrib, bw => { bw.Write((int)itemType); bw.Write(price); });
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
            _objMoves.Reset();
            _boxes.Reset();
            _population.Reset();
            _registerMirror.Reset();
            ModulesReset();
            PromptLine = "";
            _lastShopNameSent = null;
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

        /// <summary>Fast lane: transient state that must never wait behind bulk transfers
        /// (unreliable-no-delay on Steam; a lost packet is replaced by the next tick).</summary>
        private void BroadcastTransient(MsgType type, Action<BinaryWriter> write)
        {
            _net?.BroadcastTransient(Msg.Build(type, write));
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
                    int idx = tf != null ? Sync.RegisterServe.FindNearestCounter(tf.position, CoopPlugin.ServeReach.Value, quiet: !serveTap) : -1;
                    // a live trade/sell-in offer owns this counter: TradeServe's own key
                    // handling sends the TradeOp; a ServeRequest here would answer
                    // "no customer" and stomp the trade feedback line
                    if (idx >= 0 && _trades.HasOffer(idx)) return;
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
                    int near = tf != null ? Sync.RegisterServe.FindNearestCounter(tf.position, CoopPlugin.ServeReach.Value, quiet: true) : -1;
                    if (near >= 0 && _registerMirror.IsPaymentPhase(near))
                    {
                        _serveThrottle = 0.3f;
                        Send(1, MsgType.ServeRequest, bw => bw.Write(near));
                    }
                });
            }

            if (_net == null) return;

            Guarded("net-pump", _actNetPump);

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
                // fresh joiner: defeat every module's unchanged-hash gate so full
                // authoritative state goes out on the next tick, not the next heal
                if (Role == CoopRole.Host)
                {
                    ModulesForceResend();
                    _lastCoinSent = double.MinValue;   // guarantee the wallet + progress
                    _lastProgressSent = long.MinValue; // snapshot the very next tick
                }
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

            // Drain with coalescing: after any hitch the backlog holds dozens of stale
            // full-state packets; applying each in one frame turns one slow frame into
            // a cascade. For snapshot types only the NEWEST per (type, sender) matters.
            // (NpcState is chunked - every chunk carries different NPCs - and RelayState
            // multiplexes senders inside the payload, so neither may be coalesced.)
            _pendingReduceThisFrame = 0.0; // reset the per-frame guest-spend accumulator
            _dispatchBuf.Clear();
            while (_net != null && _net.Incoming.TryDequeue(out var msg))
                _dispatchBuf.Add(msg);
            if (_dispatchBuf.Count > 8)
            {
                _dispatchSeen.Clear();
                for (int i = _dispatchBuf.Count - 1; i >= 0; i--)
                {
                    var t = _dispatchBuf[i].Type;
                    if (t != MsgType.PlayerState && t != MsgType.RegisterState
                        && t != MsgType.BoxState && t != MsgType.PopState) continue;
                    long key = ((long)t << 32) | (uint)_dispatchBuf[i].ConnId;
                    if (!_dispatchSeen.Add(key)) _dispatchBuf[i] = default; // superseded
                }
            }
            for (int i = 0; i < _dispatchBuf.Count; i++)
            {
                if (_dispatchBuf[i].Type == 0) continue;
                try { Dispatch(_dispatchBuf[i]); }
                catch (Exception e) { CoopPlugin.Log.LogError($"Dispatch {_dispatchBuf[i].Type}: {e}"); }
                if (_net == null) break; // a Bye may have shut us down mid-drain
            }
            if (_net == null) return;

            float dt = Time.deltaTime;
            if (_errLogCooldown > 0f) _errLogCooldown -= dt;

            // Every stage is individually armored: one failing subsystem must degrade
            // that feature only, never kill position sync for the whole session.
            FlushPendingCardWork();

            _dt = dt;
            if (ClientReloading && _reloadGrace > 0f && InGameLevel())
            {
                _reloadGrace -= dt;
                if (_reloadGrace <= 0f) ClientReloading = false;
            }
            Guarded("avatars", _actAvatars);
            _syncActive = Role != CoopRole.None && _net.ConnectionCount > 0 && InGameLevel();
            Guarded("world", _actWorld);
            Guarded("cardshelves", _actCardShelves);
            Guarded("objmoves", _actObjMoves);
            Guarded("boxes", _actBoxes);
            Guarded("population", _actPopulation);
            Guarded("modules", _actModules);

            if (Role == CoopRole.Client)
            {
                Guarded("npc-puppets", _actNpcPuppets);
                Guarded("register-mirror", _actRegisterMirror);

                // The save-load path can leave inert vanilla customers standing around on
                // the client even though their AI is suppressed; sweep them off so only
                // the host's mirrored puppets are visible.
                _npcSweepTimer += dt;
                if (_npcSweepTimer >= 2f && InGameLevel())
                {
                    _npcSweepTimer -= 2f;
                    Guarded("npc-sweep", _actNpcSweep);
                }
            }

            // position updates
            _stateTimer += dt;
            Guarded("state-send", _actStateSend);

            _diagTimer += dt;
            if (_diagTimer >= 15f)
            {
                _diagTimer -= 15f;
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
                Guarded("npc-collect", _actNpcCollect);
                Guarded("register-collect", _actRegisterCollect);
            }

            _priceTimer += dt;
            if (_priceTimer >= 3f)
            {
                _priceTimer -= 3f;
                try
                {
                    // the table is indexed by RAW itemType and EPL registers modded items
                    // at huge enum values (~200k entries) - ship only the set prices as
                    // (index, price) pairs; the old whole-table send truncated its count
                    // to 16 bits and modded prices never arrived.
                    // Read through the game's WOVEN GetItemPrice, never the raw list:
                    // EPL routes modded set-prices (index >= vanilla count) to its own
                    // per-item save data, so the raw list simply never contains them -
                    // hosts priced modded packs and joiners' tags stayed at "-" forever
                    // (field screenshots; same interception as the restock catalog)
                    _priceBuf.Clear();
                    var seenTypes = _priceSeenTypes;
                    seenTypes.Clear();
                    int n = CatalogCount();
                    int hash = 17;
                    for (int i = 0; i < n; i++)
                    {
                        var rd = CatalogAt(i);
                        if (rd == null) continue;
                        int t = (int)rd.itemType;
                        if (!seenTypes.Add(t)) continue; // big/small share one price row
                        float v = 0f;
                        try { v = CPlayerData.GetItemPrice(rd.itemType, preventZero: false); } catch { }
                        if (v == 0f) continue;
                        _priceBuf.Add(new KeyValuePair<int, float>(t, v));
                        hash = hash * 31 + t;
                        hash = hash * 31 + v.GetHashCode();
                    }
                    // heal beat: the hash updates BEFORE the send, so a single failed
                    // or lost broadcast used to leave those prices stale FOREVER (tag
                    // stuck at "-" on the joiner until the next unrelated price change).
                    // Every other snapshot engine already has a slow heal; now this does
                    _priceHeal += 3f;
                    if (hash != _lastPriceHash || _priceHeal >= 30f)
                    {
                        _lastPriceHash = hash;
                        _priceHeal = 0f;
                        Broadcast(MsgType.PriceList, bw =>
                        {
                            bw.Write(_priceBuf.Count);
                            for (int i = 0; i < _priceBuf.Count; i++)
                            {
                                bw.Write(_priceBuf[i].Key);
                                bw.Write(_priceBuf[i].Value);
                            }
                        });
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("price sync: " + e.Message); }
            }

            _shopNameTimer += dt;
            if (_shopNameTimer >= 3f)
            {
                _shopNameTimer -= 3f;
                string name = CPlayerData.GetPlayerName();
                if (name != _lastShopNameSent)
                {
                    _lastShopNameSent = name;
                    Broadcast(MsgType.ShopName, bw => bw.Write(name));
                }
            }

            _econTimer += dt;
            if (_econTimer >= 0.5f)
            {
                _econTimer -= 0.5f;
                double coin = CPlayerData.m_CoinAmountDouble;
                // heal beat like PriceList/LightState: change-gated alone stranded the
                // guest's wallet forever on a single dropped/failed CoinSet. Re-send every
                // 15s regardless so a missed economy packet self-corrects.
                _coinHeal += 0.5f;
                if (Math.Abs(coin - _lastCoinSent) > 0.0001 || _coinHeal >= 15f)
                {
                    _lastCoinSent = coin;
                    _coinHeal = 0f;
                    float coinF = CPlayerData.m_CoinAmount;
                    Broadcast(MsgType.CoinSet, bw => { bw.Write(coin); bw.Write(coinF); });
                }

                int exp = CPlayerData.m_ShopExpPoint;
                int level = CPlayerData.m_ShopLevel;
                int fame = CPlayerData.m_FamePoint;
                long progress = ((long)level << 40) ^ ((long)fame << 20) ^ (uint)exp;
                _progressHeal += 0.5f;
                if (progress != _lastProgressSent || _progressHeal >= 15f)
                {
                    _lastProgressSent = progress;
                    _progressHeal = 0f;
                    Broadcast(MsgType.ProgressSet, bw => { bw.Write(exp); bw.Write(level); bw.Write(fame); });
                }
            }

            // full lighting-state sync: the sky phase runs on internal timers the clock
            // sync can't correct (the "night at 11 AM" drift)
            _lightSyncTimer += dt;
            if (_lightSyncTimer >= 5f)
            {
                _lightSyncTimer -= 5f;
                try
                {
                    if (_lightManager == null) _lightManager = FindObjectOfType<LightManager>();
                    if (_lightManager != null && MiUpdateLightData != null && CPlayerData.m_LightTimeData != null)
                    {
                        MiUpdateLightData.Invoke(_lightManager, null); // refresh bundle from live state
                        string lightJson = JsonUtility.ToJson(CPlayerData.m_LightTimeData);
                        // the client CORRECTS ITS DRIFT only when a packet arrives - a pure
                        // changed-only gate silenced the corrector whenever the host's sky
                        // was static (pre-open mornings) and the joiner drifted to sunset
                        _lightHeal += 5f;
                        if (lightJson != _lastLightJson || _lightHeal >= 15f)
                        {
                            _lastLightJson = lightJson;
                            _lightHeal = 0f;
                            Broadcast(MsgType.LightState, bw => bw.Write(lightJson));
                        }
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("light sync: " + e.Message); }
            }

            // slow full-truth repaint of card display slots: heals any client whose local
            // display diverged (population repairs, culled reads, missed deltas) without
            // waiting for the host to touch a slot again
            _cardResyncTimer += dt;
            if (_cardResyncTimer >= 12f && InGameLevel())
            {
                _cardResyncTimer -= 12f;
                try
                {
                    var full = _cardShelves.BuildFullState();
                    if (full.Count > 0)
                    {
                        // change-gate the full repaint like every other heal (PriceList,
                        // Population, Box): a big card wall was emitting a multi-KB reliable
                        // packet every 12s even when nothing moved. Hash full card identity;
                        // resend only on change, plus a 30s forced heal for a dropped delta.
                        int h = 17;
                        foreach (var e in full)
                        {
                            h = h * 31 + e.Key;
                            h = h * 31 + (e.Occupied ? 1 : 0);
                            var c = e.Card;
                            if (e.Occupied && c != null)
                            {
                                h = h * 31 + (int)c.monsterType;
                                h = h * 31 + (int)c.expansionType;
                                h = h * 31 + (int)c.borderType;
                                h = h * 31 + c.cardGrade;
                                h = h * 31 + c.gradedCardIndex;
                                h = h * 31 + (c.isFoil ? 1 : 0);
                                h = h * 31 + (c.isDestiny ? 1 : 0);
                                h = h * 31 + (c.isChampionCard ? 1 : 0);
                            }
                        }
                        _cardResyncHeal += 12f;
                        if (h != _lastCardResyncHash || _cardResyncHeal >= 30f)
                        {
                            _lastCardResyncHash = h;
                            _cardResyncHeal = 0f;
                            Broadcast(MsgType.CardShelfDelta, bw => CardShelfSync.WriteEntries(bw, full));
                        }
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("card resync: " + e.Message); }
            }

            // shared product licenses, identity-keyed: the save-file bool list is indexed
            // by restock position, which modded lists can scramble between machines
            _licenseSyncTimer += dt;
            if (_licenseSyncTimer >= 10f && InGameLevel())
            {
                _licenseSyncTimer -= 10f;
                try
                {
                    // full virtual catalog + the game's INTERCEPTED flag accessor:
                    // raw list/flag reads are vanilla-length only, so modded license
                    // unlocks were invisible to this heal (the Hololive/Fossil
                    // "no local product" reports)
                    int total = CatalogCount();
                    var unlocked = new List<RestockData>();
                    for (int i = 0; i < total; i++)
                    {
                        bool on = false;
                        try { on = CPlayerData.GetIsItemLicenseUnlocked(i); } catch { }
                        if (!on) continue;
                        var rd = CatalogAt(i);
                        if (rd != null) unlocked.Add(rd);
                    }
                    bool scanner = CPlayerData.m_IsScannerRestockUnlocked;
                    // licenses change a few times per session: broadcast on change, plus
                    // a slow heal so a client that missed one still converges
                    int lh = 17;
                    foreach (var rd in unlocked)
                        lh = lh * 31 + (((int)rd.itemType << 1) | (rd.isBigBox ? 1 : 0));
                    lh = lh * 31 + (scanner ? 1 : 0);
                    _licenseHeal += 10f;
                    if (lh != _lastLicenseHash || _licenseHeal >= 60f)
                    {
                        _lastLicenseHash = lh;
                        _licenseHeal = 0f;
                        Broadcast(MsgType.LicenseState, bw =>
                        {
                            bw.Write(scanner);
                            bw.Write((ushort)unlocked.Count);
                            foreach (var rd in unlocked)
                            {
                                bw.Write((int)rd.itemType);
                                bw.Write(rd.isBigBox);
                                // NAME identity: modded enum ints can drift between
                                // machines; the heal must still map the unlock
                                bw.Write(Fnv(rd.name ?? ""));
                            }
                        });
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("license sync: " + e.Message); }
            }

            _dayTimer += dt;
            if (_dayTimer >= 2f)
            {
                _dayTimer -= 2f;
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
                        // Version is checked FIRST so a peer on a different version (which
                        // may not send the newer handshake fields) is rejected before we
                        // try to read them.
                        string version = br.ReadString();
                        if (version != CoopPlugin.Version)
                        {
                            RejectConn(msg.ConnId, $"version mismatch - host runs CardShopCoop {CoopPlugin.Version}, you have {version}");
                            break;
                        }
                        string name = br.ReadString();
                        string password = br.ReadString();
                        string pluginHash = br.ReadString();
                        string enumHash = br.ReadString();
                        string cardsHash = br.ReadString();
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
                            // send our registry along with the rejection: the client
                            // backs theirs up, installs ours, and only has to restart -
                            // no more hand-copying enum_values.json between PCs
                            try
                            {
                                var enumBytes = System.IO.File.ReadAllBytes(Util.ModParity.EnumFilePath());
                                var gz = Msg.Gzip(enumBytes);
                                Send(msg.ConnId, MsgType.EnumSync, bw =>
                                {
                                    bw.Write(gz.Length);
                                    bw.Write(gz);
                                });
                            }
                            catch (Exception e) { CoopPlugin.Log.LogWarning("enum sync send: " + e.Message); }
                            RejectConn(msg.ConnId, "your custom-card database differed - it has been synced from the host; RESTART your game, then join again");
                            break;
                        }
                        // Custom CreateCards/CardForge cards aren't covered by the enum
                        // registry above, so check their ID mapping directly. These are
                        // loose files we can't auto-sync, so it's a clear hard stop.
                        if (cardsHash != Util.ModParity.CardsHash())
                        {
                            RejectConn(msg.ConnId, "your custom cards differ from the host's - both players need the same custom cards installed (identical files + IDs), then restart. Share the exact card package (e.g. from CardForge).");
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
                        if (_saveExpected > 0)
                            StatusLine = $"downloading shop... {Math.Min(100, _saveBuf.Length * 100 / _saveExpected)}%";
                    }
                    break;
                }
                case MsgType.SaveDone:
                {
                    if (Role != CoopRole.Client || _saveBuf == null || _worldRequested) break;
                    var data = _saveBuf.ToArray();
                    _saveBuf = null;
                    if (_saveExpected >= 0 && data.Length != _saveExpected)
                    {
                        ErrorLine = $"World download looked corrupted ({data.Length}/{_saveExpected} bytes) - try again.";
                        Shutdown("bad download");
                        break;
                    }
                    try { data = Msg.Gunzip(data); }
                    catch
                    {
                        ErrorLine = "World download could not be unpacked - try again.";
                        Shutdown("bad download");
                        break;
                    }
                    if (data.Length < 1024 || data[0] != (byte)'{')
                    {
                        ErrorLine = "World download looked corrupted - try again.";
                        Shutdown("bad download");
                        break;
                    }
                    _pendingSave = data;
                    StatusLine = "shop received - downloading mod data...";
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
                        if (_bundleExpected > 0)
                            StatusLine = $"downloading mod data... {Math.Min(100, _bundleBuf.Length * 100 / _bundleExpected)}%";
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
                        if (bundle.Length > 0) bundle = Msg.Gunzip(bundle);
                        SidecarTransfer.ApplyBundle(bundle, _hostSlot, SaveTransfer.CoopSlot);
                    }
                    catch (Exception e)
                    {
                        CoopPlugin.Log.LogWarning("Sidecar apply failed (continuing): " + e.Message);
                    }
                    // the game's world-(re)load teardown (LoadInteractableObjectData ->
                    // RestockManager.DestroyAllObject) destroys every existing box via
                    // OnDestroyed - if a world was live (rejoin, or solo save loaded
                    // while waiting for the invite) a 1.0.7 client forwarded all ~250
                    // as player trash actions, wiping the HOST's boxes (first field
                    // report). Suppress until settled. Grace must reset to 0 here: a
                    // leftover countdown from an aborted join would drain the flag
                    // DURING the ~16s async load and the massacre would slip through
                    ClientReloading = true;
                    _reloadGrace = 0f;
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
                    {
                        var entries = WorldSync.ReadEntries(br);
                        _world.ApplyRemote(entries);
                        // applying updates the host's diff baseline, so its own tick
                        // never re-detects this change - with 3+ players the OTHER
                        // clients must be told explicitly
                        if (_net.ConnectionCount > 1)
                            Broadcast(MsgType.ShelfDelta, bw => WorldSync.WriteEntries(bw, entries));
                    }
                    break;
                }
                case MsgType.PriceList:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int n = br.ReadInt32();
                        Patches.GamePatches.ApplyingRemotePrice = true; // don't echo these back
                        try
                        {
                            _incomingPriced.Clear();
                            int changed = 0;
                            for (int k = 0; k < n; k++)
                            {
                                int i = br.ReadInt32();
                                float v = br.ReadSingle();
                                _incomingPriced.Add(i);
                                if (i < 0 || i > 500000) continue;
                                // write through the game's WOVEN SetItemPrice: raw list
                                // writes for modded types land in a shadow list the game
                                // never reads (EPL routes those rows to its own save
                                // data), which kept joiner tags at "-" while the value
                                // "applied" - and SetItemPrice fires the tag-repaint
                                // event itself
                                float cur = 0f;
                                try { cur = CPlayerData.GetItemPrice((EItemType)i, preventZero: false); } catch { }
                                if (Math.Abs(cur - v) > 0.0001f)
                                {
                                    try { CPlayerData.SetItemPrice((EItemType)i, v); changed++; } catch { }
                                }
                            }
                            // stale-price reports were undiagnosable: applies were silent
                            if (changed > 0)
                                CoopPlugin.Log.LogInfo($"price apply: {changed} price(s) updated from host");
                            // a price the host CLEARED is absent from the sparse set
                            foreach (int i in _clientPriced)
                                if (!_incomingPriced.Contains(i) && i >= 0 && i <= 500000)
                                {
                                    float cur = 0f;
                                    try { cur = CPlayerData.GetItemPrice((EItemType)i, preventZero: false); } catch { }
                                    if (cur != 0f)
                                    {
                                        try { CPlayerData.SetItemPrice((EItemType)i, 0f); } catch { }
                                    }
                                }
                            var tmp = _clientPriced;
                            _clientPriced = _incomingPriced;
                            _incomingPriced = tmp;
                        }
                        finally { Patches.GamePatches.ApplyingRemotePrice = false; }
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
                                if (cid != msg.ConnId) _net.SendTransient(cid, relay);
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
                                    _lastDayMirrorAt = Time.realtimeSinceStartupAsDouble;
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
                            _rosterNames[id] = name; // re-applied on every relay packet
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
                        {
                            _avatars.UpdateState(1000 + senderId, pos, yaw, speed, hold, holdTypes, holdCards);
                            // the avatar may have spawned AFTER the roster named it - a
                            // relayed peer then wore the default "Player" tag forever
                            if (_rosterNames.TryGetValue(senderId, out var rn))
                                _avatars.SetName(1000 + senderId, rn);
                        }
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
                        if (!InGameLevel())
                        {
                            // applying mid-scene-load crashes into uninitialized card data;
                            // hold it and flush once the world is up (nothing is lost)
                            _pendingCardDeltas.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = card });
                            break;
                        }
                        ApplyCardDelta(isAdd, amount, card);
                    }
                    break;
                }
                case MsgType.GradedRemove:
                {
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        var card = Msg.ReadCard(br);
                        if (card == null) break;
                        if (!InGameLevel())
                        {
                            // hold until the world is up, same as CardDelta; the graded
                            // branch of ApplyCardDelta routes it through RemoveGradedCard
                            _pendingCardDeltas.Add(new PendingCard { IsAdd = false, Amount = 1, Card = card });
                            break;
                        }
                        ApplyCardDelta(isAdd: false, amount: 1, card: card);
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
                    {
                        var entries = CardShelfSync.ReadEntries(br);
                        _cardShelves.ApplyRemote(entries);
                        if (_net.ConnectionCount > 1) // see ShelfRequest note
                            Broadcast(MsgType.CardShelfDelta, bw => CardShelfSync.WriteEntries(bw, entries));
                    }
                    break;
                }
                case MsgType.BoxState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _boxes.ClientApply(BoxSync.ReadEntries(br));
                    break;
                }
                case MsgType.PopState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _population.ClientApply(PopulationSync.Read(br));
                    break;
                }
                case MsgType.FurnitureOrder:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int objType = br.ReadInt32();
                        var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        var rot = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        string who = PeerNames.TryGetValue(msg.ConnId, out var n) ? n : "player";
                        var eObj = (EObjectType)objType;

                        // The guest already paid: its CEventPlayer_ReduceCoin was forwarded
                        // and debited the shared wallet BEFORE this spawn. If the prefab
                        // isn't in the host's catalog (different mods / load order), the
                        // vanilla spawn Instantiate(null)s and throws - swallowed by the
                        // per-message try/catch - so the money vanished with no box, no
                        // delivery, no refund (the field report). Match the restock-order
                        // path: pre-check, refund from the host's authoritative price, notify.
                        float refund = 0f;
                        try { var fp = InventoryBase.GetFurniturePurchaseData(eObj); if (fp != null) refund = fp.price; }
                        catch { } // GetFurniturePurchaseData indexes a parallel list; a catalog mismatch can throw

                        if (InventoryBase.GetSpawnInteractableObjectPrefab(eObj) == null)
                        {
                            CoopPlugin.Log.LogWarning($"{who} bought furniture {eObj} not in host catalog - refunding {refund:F0}");
                            if (refund > 0f && refund < 100000f)
                                CEventManager.QueueEvent(new CEventPlayer_AddCoin(refund));
                            Send(msg.ConnId, MsgType.Toast, bw => bw.Write(
                                refund > 0f
                                    ? $"that furniture isn't in the host's catalog - refunded ${refund:F0}"
                                    : "that furniture isn't in the host's catalog - nothing was delivered"));
                            break;
                        }

                        CoopPlugin.Log.LogInfo($"{who} bought furniture: {eObj}");
                        try
                        {
                            ShelfManager.SpawnInteractableObjectInPackageBox(eObj, pos, rot);
                        }
                        catch (Exception e)
                        {
                            CoopPlugin.Log.LogWarning("furniture spawn failed on host: " + e.Message);
                            if (refund > 0f && refund < 100000f)
                                CEventManager.QueueEvent(new CEventPlayer_AddCoin(refund));
                            Send(msg.ConnId, MsgType.Toast, bw => bw.Write(
                                refund > 0f
                                    ? $"furniture failed to deliver on the host - refunded ${refund:F0}"
                                    : "furniture failed to deliver on the host"));
                        }
                    }
                    break;
                }
                case MsgType.BoxRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _boxes.HostApplyRequest(BoxSync.ReadEntries(br));
                    break;
                }
                case MsgType.OrderRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int itemType = br.ReadInt32();
                        bool isBig = br.ReadBoolean();
                        string rdName = br.ReadString();
                        int count = br.ReadInt32();
                        float cost = br.ReadSingle();
                        string who = PeerNames.TryGetValue(msg.ConnId, out var n) ? n : "player";
                        int idx = ResolveRestockIndex(itemType, isBig, rdName, out bool sizeDiffers);
                        if (idx >= 0)
                        {
                            CoopPlugin.Log.LogInfo($"{who} ordered {(EItemType)itemType} big={isBig} x{count} -> restock {idx}{(sizeDiffers ? " (size fallback)" : "")}");
                            RestockManager.SpawnPackageBoxItemMultipleFrame(idx, count);
                            if (sizeDiffers)
                                Send(msg.ConnId, MsgType.Toast, bw => bw.Write(
                                    $"'{rdName}' delivered in the host's box size (catalogs differ slightly)"));
                        }
                        else
                        {
                            // the money is already in the shared wallet (the charge fired
                            // before the spawn call we intercept) - give it back loudly
                            int hostCatalog = 0;
                            try { hostCatalog = CatalogCount(); } catch { }
                            CoopPlugin.Log.LogWarning($"{who} ordered unknown product type {itemType} '{rdName}' - refunding {cost:F0} (host catalog: {hostCatalog} products)");
                            LogCatalogCandidates(rdName);
                            if (cost > 0f && cost < 100000f)
                                CEventManager.QueueEvent(new CEventPlayer_AddCoin(cost));
                            // the resolver now spans the full EPL virtual catalog, so a
                            // miss with a big catalog is a genuine pack difference; a
                            // vanilla-sized catalog means the host's EPL entries aren't
                            // visible (bundles still loading right after boot, or the
                            // EPL bridge is inactive - the host's log says which)
                            string reason = hostCatalog > 140
                                ? "the host's content packs don't include this product - match your pack files"
                                : "the host's modded catalog hasn't finished loading (or EPL is missing on the host) - wait a minute and try again, and check the host's BepInEx log";
                            Send(msg.ConnId, MsgType.Toast, bw => bw.Write(
                                $"'{rdName}' isn't in the host's catalog - refunded ${cost:F0}. Note: {reason}"));
                        }
                    }
                    break;
                }
                case MsgType.Toast:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        RegisterLine = br.ReadString();
                        RegisterLineTimer = 8f;
                        // support gold: the on-screen line vanishes in 8s, the log keeps it
                        CoopPlugin.Log.LogInfo("host says: " + RegisterLine);
                    }
                    break;
                }
                case MsgType.CatalogDigest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        CompareCatalogs(br, msg.ConnId);
                    break;
                }
                case MsgType.BoxRemoved:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int id = br.ReadInt32();
                        int type = br.ReadInt32();
                        string who = PeerNames.TryGetValue(msg.ConnId, out var n) ? n : "player";
                        CoopPlugin.Log.LogInfo($"{who} trashed box id {id} ({(EItemType)type})");
                        _boxes.HostApplyRemoval(id, type, msg.ConnId);
                    }
                    break;
                }
                case MsgType.LicenseUnlock:
                {
                    if (!InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int itemType = br.ReadInt32();
                        bool isBig = br.ReadBoolean();
                        string rdName = br.ReadString();
                        bool ok = ApplyLicenseUnlock(itemType, isBig, rdName);
                        if (Role == CoopRole.Host)
                        {
                            if (ok) // echo to the other clients + confirm to the buyer
                            {
                                Broadcast(MsgType.LicenseUnlock, bw =>
                                { bw.Write(itemType); bw.Write(isBig); bw.Write(rdName); });
                                Send(msg.ConnId, MsgType.Toast, bw =>
                                    bw.Write($"license unlocked for everyone: {rdName}"));
                            }
                            else
                                Send(msg.ConnId, MsgType.Toast, bw =>
                                    bw.Write($"'{rdName}' license couldn't unlock on the host (product missing) - match your content packs"));
                        }
                    }
                    break;
                }
                case MsgType.LicenseState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        bool scanner = br.ReadBoolean();
                        int n = br.ReadUInt16();
                        var wanted = new HashSet<long>();
                        var wantedNames = new HashSet<long>();
                        for (int i = 0; i < n; i++)
                        {
                            int t = br.ReadInt32();
                            bool big = br.ReadBoolean();
                            int nameFnv = br.ReadInt32();
                            wanted.Add(((long)t << 1) | (big ? 1L : 0L));
                            wantedNames.Add(((long)(uint)nameFnv << 1) | (big ? 1L : 0L));
                        }
                        // don't re-lock during the window where our own purchase is
                        // still round-tripping to the host
                        bool allowLock = UnityEngine.Time.realtimeSinceStartupAsDouble
                            - _lastLicenseBuyTime > 12.0;
                        Guarded("license-apply", () =>
                        {
                            CPlayerData.m_IsScannerRestockUnlocked |= scanner;
                            var rl = Inv().m_StockItemData_SO.m_RestockDataList;
                            var flags = CPlayerData.m_IsItemLicenseUnlocked;
                            bool anyUnlocked = false;
                            for (int i = 0; i < rl.Count && i < flags.Count; i++)
                            {
                                if (rl[i] == null) continue;
                                long big = rl[i].isBigBox ? 1L : 0L;
                                bool should = wanted.Contains(((long)(int)rl[i].itemType << 1) | big)
                                    || wantedNames.Contains(((long)(uint)Fnv(rl[i].name ?? "") << 1) | big);
                                if (should && !flags[i])
                                {
                                    Patches.GamePatches.ApplyingRemoteLicense = true;
                                    try { CPlayerData.SetUnlockItemLicense(i); }
                                    finally { Patches.GamePatches.ApplyingRemoteLicense = false; }
                                    anyUnlocked = true;
                                    try
                                    {
                                        if (rl[i].itemType == EItemType.BasicCardBox)
                                            TutorialManager.AddTaskValue(ETutorialTaskCondition.UnlockBasicCardBox, 1f);
                                    }
                                    catch { }
                                }
                                else if (!should && flags[i] && i != 0 && allowLock)
                                {
                                    // scrambled save-transfer flag (index order differs
                                    // between machines): host truth says locked
                                    flags[i] = false;
                                }
                            }
                            if (anyUnlocked)
                            {
                                try { GameInstance.m_IsItemLicenseUnlocked = true; } catch { }
                                RefreshLicensePanels();
                            }
                        });
                    }
                    break;
                }
                case MsgType.StaffOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _staff.HostApplyOp(br);
                    break;
                }
                case MsgType.StaffState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _staff.ClientApplyState(br);
                    break;
                }
                case MsgType.ShopOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _shopState.HostApplyOp(br);
                    break;
                }
                case MsgType.ShopState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _shopState.ClientApplyState(br);
                    break;
                }
                case MsgType.SettingsOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _settings.HostApplyOp(br);
                    break;
                }
                case MsgType.SettingsState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _settings.ClientApplyState(br);
                    break;
                }
                case MsgType.MarketState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _market.ClientApplyState(br);
                    break;
                }
                case MsgType.ReportState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _report.ClientApplyState(br);
                    break;
                }
                case MsgType.ContainerOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _containers.HostApplyOp(br);
                    break;
                }
                case MsgType.ContainerState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _containers.ClientApplyState(br);
                    break;
                }
                case MsgType.TournamentState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _tournament.ClientApplyState(br);
                    break;
                }
                case MsgType.CardBoxOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _cardBoxes.HostApplyOp(br, msg.ConnId);
                    break;
                }
                case MsgType.CardBoxState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _cardBoxes.ClientApplyState(br);
                    break;
                }
                case MsgType.FurnBoxOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _furnBoxes.HostApplyOp(br, msg.ConnId);
                    break;
                }
                case MsgType.FurnBoxState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _furnBoxes.ClientApplyState(br);
                    break;
                }
                case MsgType.EnumSync:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int len = br.ReadInt32();
                        var hostBytes = Msg.Gunzip(br.ReadBytes(len));
                        if (CoopPlugin.AutoSyncCardDatabase.Value)
                        {
                            StatusLine = Util.ModParity.InstallEnumFile(hostBytes);
                            CoopPlugin.Log.LogInfo("enum sync: " + StatusLine);
                        }
                        else
                        {
                            StatusLine = "card databases differ - auto-sync is disabled; copy the host's enum_values.json (PrefabLoader folder) yourself";
                        }
                    }
                    break;
                }
                case MsgType.GradingOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _grading.HostApplyOp(br);
                    break;
                }
                case MsgType.GradingState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _grading.ClientApplyState(br);
                    break;
                }
                case MsgType.TradeOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _trades.HostApplyOp(br);
                    break;
                }
                case MsgType.TradeState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _trades.ClientApplyState(br);
                    break;
                }
                case MsgType.TableState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload)) _tables.ClientApplyState(br);
                    break;
                }
                case MsgType.LightState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        var data = JsonUtility.FromJson<LightTimeData>(br.ReadString());
                        if (data == null) break;
                        try
                        {
                            if (_lightManager == null) _lightManager = FindObjectOfType<LightManager>();
                            if (_lightManager == null) break;
                            int localIdx = FiTimeOfDayIdx?.GetValue(_lightManager) is int idx ? idx : -1;
                            int localHour = FiTimeHour?.GetValue(_lightManager) is int h ? h : -1;
                            int localMin = FiTimeMin?.GetValue(_lightManager) is int m2 ? m2 : 0;
                            int driftMin = Math.Abs((data.m_TimeHour * 60 + data.m_TimeMin) - (localHour * 60 + localMin));
                            // a day ROLLOVER is not drift: the host's clock wrapped to
                            // morning before our day mirror ran. Racing an Init against
                            // the mirror's DelayUpdateEnv coroutine stomped the env
                            // updater and froze the sky in daylight (field screenshot:
                            // "phase 4->0, drift 780min" two seconds before the mirror)
                            if (driftMin > 600) break;
                            if (Time.realtimeSinceStartupAsDouble - _lastDayMirrorAt < 10.0) break;
                            // apply the SHOP-LIGHT bit surgically (cheap: just flips the group
                            // + re-evaluates UI brightness) so a wall-switch toggle propagates
                            // without a full lighting Init and its music/skybox churn. This is
                            // the guest half of the light-switch sync (host runs ToggleShopLight
                            // via the forwarded op; here we mirror the resulting state).
                            try
                            {
                                if (LightManager.IsShopLightOn() != data.m_IsShopLightOn)
                                    _lightManager.ToggleShopLight();
                            }
                            catch (Exception le) { CoopPlugin.Log.LogWarning("shop-light apply: " + le.Message); }
                            // re-run the game's own lighting restore only when the sky
                            // phase actually differs (avoids music/blend churn)
                            if (localIdx != data.m_TImeOfDayIndex || driftMin > 4)
                            {
                                CPlayerData.m_LightTimeData = data;
                                FiFinishLoading?.SetValue(_lightManager, false);
                                MiLightInit?.Invoke(_lightManager, null);
                                CoopPlugin.Log.LogInfo($"lighting re-synced (phase {localIdx}->{data.m_TImeOfDayIndex}, drift {driftMin}min)");
                            }
                        }
                        catch (Exception e) { CoopPlugin.Log.LogWarning("light apply: " + e.Message); }
                    }
                    break;
                }
                case MsgType.ShopName:
                {
                    if (Role != CoopRole.Client) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        string name = br.ReadString();
                        if (name.Length > 0 && CPlayerData.GetPlayerName() != name)
                        {
                            CPlayerData.PlayerName = name;
                            CoopPlugin.Log.LogInfo("shop name synced: " + name);
                        }
                    }
                    break;
                }
                case MsgType.ItemPriceContrib:
                {
                    if (Role != CoopRole.Host) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        int itemType = br.ReadInt32();
                        float price = br.ReadSingle();
                        if (itemType >= 0 && itemType <= 500000)
                        {
                            // WOVEN SetItemPrice, never the raw list: EPL routes modded
                            // rows to its own save data, and a raw write is a shadow
                            // entry the game (and our own woven-read broadcast) never
                            // sees. Fires the tag-repaint event itself.
                            Patches.GamePatches.ApplyingRemotePrice = true;
                            try { CPlayerData.SetItemPrice((EItemType)itemType, price); }
                            catch { }
                            finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                            // the periodic PriceList broadcast echoes this to every client
                        }
                    }
                    break;
                }
                case MsgType.ObjMoveDelta:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                        _objMoves.ApplyRemote(ObjMoveSync.ReadEntries(br));
                    break;
                }
                case MsgType.ObjMoveRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        var entries = ObjMoveSync.ReadEntries(br);
                        _objMoves.ApplyRemote(entries);
                        if (_net.ConnectionCount > 1) // see ShelfRequest note
                            Broadcast(MsgType.ObjMoveDelta, bw => ObjMoveSync.WriteEntries(bw, entries));
                    }
                    break;
                }
                case MsgType.CardPriceSet:
                {
                    using (var br = Msg.Reader(msg.Payload))
                    {
                        var card = Msg.ReadCard(br);
                        float price = br.ReadSingle();
                        if (!InGameLevel())
                        {
                            _pendingCardPrices.Add(new KeyValuePair<CardData, float>(card, price));
                            break;
                        }
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
                        // clear EACH counter's own checkout screen AND the counters' running
                        // totals for the next customer. Resetting one arbitrary screen (the
                        // old behavior) left the scanned-item bar stale on the OTHER counters
                        // of a multi-counter shop.
                        Guarded("reset-screens", Sync.RegisterServe.ClientResetScreens);
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
                            case 2:
                            {
                                // The guest's wallet is a 0.5s-lagged mirror that does NOT
                                // reflect its own in-flight forwarded spends, so it can pass
                                // several affordability checks against the same stale balance.
                                // The host is authoritative: reject a spend the SHARED wallet
                                // (minus what other guest spends already claimed this frame)
                                // can't cover, instead of driving it negative.
                                double bal = CPlayerData.m_CoinAmountDouble - _pendingReduceThisFrame;
                                if ((double)v > bal + 0.0001)
                                {
                                    Send(msg.ConnId, MsgType.Toast, w => w.Write("purchase declined - the shared wallet is short"));
                                    _lastCoinSent = double.MinValue; // force the guest's balance to correct next tick
                                    break;
                                }
                                _pendingReduceThisFrame += (double)v;
                                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(v));
                                break;
                            }
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
            byte[] rawSave;
            byte[] rawBundle;
            int hostSlot;
            try
            {
                // game-state reads must stay on the main thread; the gzip below moves to
                // the worker (compressing a multi-MB save used to freeze the host's frame
                // at every join)
                hostSlot = CSingleton<CGameManager>.Instance.m_CurrentSaveLoadSlotSelectedIndex;
                rawSave = SaveTransfer.BuildHostPayload();
                try { rawBundle = SidecarTransfer.BuildBundle(hostSlot); }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning("Sidecar bundle failed (sending base save only): " + e.Message);
                    rawBundle = new byte[0];
                }
            }
            catch (Exception e)
            {
                ErrorLine = "Could not snapshot the shop: " + e.Message;
                CoopPlugin.Log.LogError(e);
                return;
            }

            var net = _net;
            new Thread(() =>
            {
                const int chunk = 128 * 1024;
                try
                {
                    byte[] payload = Msg.Gzip(rawSave);
                    byte[] bundle = rawBundle.Length > 0 ? Msg.Gzip(rawBundle) : rawBundle;
                    CoopPlugin.Log.LogInfo($"transfer: save {payload.Length / 1024} KB, mod data {bundle.Length / 1024} KB (compressed)");

                    net.Send(connId, Msg.Build(MsgType.Welcome, bw =>
                    {
                        bw.Write(CoopPlugin.Version);
                        bw.Write(CoopPlugin.PlayerName.Value);
                        bw.Write(payload.Length);
                        bw.Write(hostSlot);
                        bw.Write(bundle.Length);
                        bw.Write((byte)connId); // tells the client its own id (to skip in rosters)
                    }));

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
