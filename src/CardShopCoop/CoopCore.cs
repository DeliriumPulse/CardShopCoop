using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using CardShopCoop.Sync;
// NO `using Steamworks;` HERE, AND NEVER AGAIN. CoopCore is an always-loaded type: a
// single Steamworks token in one of its fields or method bodies makes the whole class
// (and therefore the whole mod) fail to load on the Game Pass build, which ships no
// com.rlabrecque.steamworks.net.dll. Everything Steam-shaped goes through
// Net.ISteamBridge. The absence of this using is the compiler-enforced proof.
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop
{
    public enum CoopRole
    {
        None, Host, Client
    }

    /// <summary>How far the invite code has got. Off = not LAN-hosting (or a Steam session,
    /// where Steam's own invites do this job). Resolving = the worker is still asking the
    /// router and the STUN server. Ready = the code carries a public address. LanOnly = we
    /// could not establish a public address, so the code carries the LAN one - still perfectly
    /// good for the other PC in the house, useless over the internet.</summary>
    public enum InviteState
    {
        Off, Resolving, Ready, LanOnly
    }

    public partial class CoopCore : MonoBehaviour
    {
        public static CoopCore Instance
        {
            get; private set;
        }

        /// <summary>True while this mod owns the game's modal UI state. Camera input
        /// is patched separately because the CMF camera reads raw mouse axes from its
        /// own component, outside InteractionPlayerController.Update.</summary>
        public static bool WindowBlocksInput
        {
            get
            {
                return Instance != null && Instance._uiModeHeldByWindow;
            }
        }
        public static CoopRole Role { get; private set; } = CoopRole.None;
        private static int _lastImmediateObjectSyncFrame = -1;

        /// <summary>Called by mutation postfixes. The modules still coalesce their own
        /// state into one snapshot; this only removes the normal polling latency.</summary>
        public static void RequestImmediateObjectSync()
        {
            var core = Instance;
            if (core == null || Role == CoopRole.None)
                return;
            // Several vanilla methods can participate in one gameplay action (for example
            // removing an item updates both the compartment and the box). Coalesce those
            // callbacks from one vanilla action into one sync pass. A frame boundary is the
            // right coalescing unit here: it merges the several internal mutations made by
            // one pickup/box-up while never delaying a distinct action for 100 ms.
            if (_lastImmediateObjectSyncFrame == Time.frameCount)
                return;
            _lastImmediateObjectSyncFrame = Time.frameCount;
            try
            {
                core._world.ForceNextTick();
                core._cardShelves.ForceNextTick();
                core._objMoves.ForceNextTick();
                core._boxEngine?.ForceNextTick();
                core._population.ForceNextTick();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("Immediate sync request: " + e.Message); }
        }

        /// <summary>Host only: the host just sold/trashed a placed object, so its
        /// ShelfManager list re-indexed and every client's roster is now misaligned. Force
        /// the population broadcast out NEXT frame - before the ~0.75s content syncs can
        /// ship shifted-index deltas - so clients reconcile their placed-object structure
        /// (position-aware) and only then apply the content that follows. Without this the
        /// client can keep the wrong shelf and every item/card on it repaints onto the wrong
        /// physical shelf.</summary>
        public void NotifyHostStructureChanged()
        {
            if (Role != CoopRole.Host || !InGameLevel())
                return;
            try
            {
                RequestImmediateObjectSync();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("host structure change: " + e.Message); }
        }

        /// <summary>True from the moment the guest joins until it returns to the title
        /// screen. The guest is standing in the HOST'S world; saving would overwrite the
        /// guest's own slot with the host's shop. This stays set through a mid-session
        /// disconnect (when Role goes back to None but the guest is STILL in the borrowed
        /// world - the "keep walking around" state), so a day-end autosave or a quit-save
        /// after the host leaves can't pollute the guest's save. Cleared once the guest is
        /// safely back at the title (no session AND out of any game level) - see Update.</summary>
        public static bool GuestBorrowedWorld;

        /// <summary>True only while session teardown is clearing module state.</summary>
        public static bool IsTearingDown
        {
            get; private set;
        }

        public string StatusLine = "Not connected";
        public string ErrorLine = "";
        public string HostTimeLine = "";
        public string RegisterLine = "";
        public float RegisterLineTimer;
        public int PlayerModelGeneration
        {
            get; private set;
        }
        public readonly Dictionary<int, string> PeerNames = new Dictionary<int, string>();

        /// <summary>The name sent to peers: Steam persona when usable, otherwise the
        /// configured profile name.</summary>
        public string EffectivePlayerName
        {
            get
            {
                string persona = _steam == null ? "" : _steam.LocalPersonaName;
                return string.IsNullOrWhiteSpace(persona) ? CoopPlugin.PlayerName.Value : persona;
            }
        }

        public bool UsingSteamPersona
        {
            get
            {
                string persona = _steam == null ? "" : _steam.LocalPersonaName;
                return !string.IsNullOrWhiteSpace(persona);
            }
        }

        private ICoopTransport _net;
        /// <summary>Null on any build where the Steamworks assembly is absent (Game Pass /
        /// DRM-free). NOT the same as "Steam isn't running" - see ISteamBridge. Must stay an
        /// INTERFACE-typed field: a SteamLobby-typed one would put Steamworks metadata back
        /// on CoopCore.</summary>
        private ISteamBridge _steam;
        private readonly Dictionary<int, string> _peerWireNames = new Dictionary<int, string>();
        private readonly Dictionary<int, ulong> _peerSteamIds = new Dictionary<int, ulong>();
        private ulong _autoJoinSteamLobby; // from +connect_lobby (game launched via invite)
        public bool IsSteamSession
        {
            get; private set;
        }
        private readonly AvatarManager _avatars = new AvatarManager();
        private readonly Dictionary<int, PlayerModelEntry> _playerModels = new Dictionary<int, PlayerModelEntry>();
        private PlayerModelEntry _localPlayerModel;
        private bool _localPlayerModelReady;
        private bool _localModelAuto;
        private float _localModelAutoRetry;
        private Transform _localModelAppliedRoot;
        private bool _characterPreviewActive;
        private PlayerModelEntry _lastCommittedPlayerModel;
        private readonly List<PlayerModelEntry> _playerModelUndo = new List<PlayerModelEntry>();
        private readonly List<PlayerModelEntry> _playerModelRedo = new List<PlayerModelEntry>();
        private const int PlayerModelHistoryLimit = 30;
        private bool _localModelSavePending;
        private float _localModelSaveTimer;
        private readonly WorldSync _world = new WorldSync();
        internal WorldSync World => _world;
        private readonly NpcSync _npcs = new NpcSync();
        private readonly CardShelfSync _cardShelves = new CardShelfSync();
        private readonly ObjMoveSync _objMoves = new ObjMoveSync();
        private readonly MovePreviewSync _movePreview = new MovePreviewSync();
        private readonly PopulationSync _population = new PopulationSync();

        // domain sync modules (v0.15): each owns one game system end-to-end and talks
        // through the standard SendOp/BroadcastState/HostApplyOp/ClientApplyState contract
        private readonly GradingSync _grading = new GradingSync();
        private readonly TradeServe _trades = new TradeServe();
        private readonly PlayTableSync _tables = new PlayTableSync();
        private readonly PlayerIntentBus _intents = new PlayerIntentBus();
        private readonly StaffSync _staff = new StaffSync();
        private readonly ShopStateSync _shopState = new ShopStateSync();
        private readonly SettingsSync _settings = new SettingsSync();
        private readonly MarketSync _market = new MarketSync();
        private readonly ReportSync _report = new ReportSync();
        private readonly ContainerSync _containers = new ContainerSync();
        private readonly TournamentSync _tournament = new TournamentSync();
        private readonly TvSync _tv = new TvSync();
        private readonly ItemBoxFamily _itemBoxFamily = new ItemBoxFamily();
        private readonly CardBoxFamily _cardBoxFamily = new CardBoxFamily();
        private readonly FurnitureBoxFamily _furnBoxFamily = new FurnitureBoxFamily();
        private BoxEngine _boxEngine;
        private readonly Sync.RegisterSync _register = new Sync.RegisterSync();
        private Sync.CoopModuleEntry[] _moduleCatalog;
        private ICoopModule[] _allModules;
        internal static Sync.CoopModulePatch[] PatchCatalog
        {
            get; private set;
        }
        private Sync.CoopModuleRegistry _moduleRegistry;
        private Sync.TickEntry[] _hostTickOrder;
        private Sync.TickEntry[] _clientTickOrder;
        private string _lastShopNameSent;
        private float _shopNameTimer = -1.0f; // staggered phase (see _lightSyncTimer note)
        private float _npcSweepTimer = -1.3f;

        // ---- invite code / UPnP (LAN hosting only) ----
        // COMPUTED BY A WORKER THREAD, WRITTEN ON THE MAIN ONE, READ BY OnGUI. The resolve
        // worker blocks on SSDP, HTTP and STUN for several seconds and the host panel has to
        // keep drawing something honest the entire time - so the worker hands each result back
        // through _mainThread (see Publish), which puts every write in the same single-threaded
        // order as Shutdown's reset and the UI's reads. They stay volatile anyway: that is one
        // cheap line against a future caller that writes one of them from a worker again, and
        // OnGUI itself latches them per Layout pass (CoopUI.DrawInvite) because an IMGUI window
        // whose CONTROL COUNT changes between the Layout and Repaint passes throws.
        public volatile InviteState InviteStatus = InviteState.Off;
        /// <summary>The composed code, or null while it is still unknown (the copy button is
        /// disabled until this exists).</summary>
        public volatile string InviteCodeText;
        /// <summary>Why the code is LAN-only, in words a player can act on. Null otherwise.</summary>
        public volatile string InviteReason;
        /// <summary>0 = nothing to say (pending, or auto-forwarding is off), 1 = the router
        /// accepted the request, 2 = the router declined. Deliberately not a bool: "we haven't
        /// asked yet" and "the router said no" must not draw the same line.</summary>
        public volatile int PortForwardState;
        /// <summary>Bumped by every invite resolve AND by every Shutdown. The worker captures
        /// it and publishes nothing if it has moved on - otherwise a host who stops and
        /// re-hosts inside the resolve window (which is SECONDS long) gets the dead session's
        /// address and port presented as this session's ready code.</summary>
        private int _inviteGen;
        private int _sessionGen;

        internal static readonly object JoinTransferLock = new object();

        internal static int SessionGeneration
        {
            get
            {
                var core = Instance;
                return core == null ? -1 : System.Threading.Volatile.Read(ref core._sessionGen);
            }
        }

        internal static bool IsSessionGeneration(int generation)
        {
            var core = Instance;
            return core != null && generation >= 0
                && System.Threading.Volatile.Read(ref core._sessionGen) == generation;
        }

        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private UI.CoopUI _ui;

        // client-side save + mod-sidecar download
        private MemoryStream _saveBuf;
        // Compressed transfers are framed below 64 MB; keep decompression and buffering well
        // below that so a peer cannot force a hostile allocation while remaining above honest saves.
        private const int MaxWorldTransferBytes = 32 * 1024 * 1024;
        private int _saveExpected = -1;
        private byte[] _pendingSave;
        private MemoryStream _bundleBuf;
        private int _bundleExpected = -1;
        private int _hostSlot;
        private bool _worldRequested;

        // host price sync
        private bool _priceFullPending; // send the whole item-price table (join / session reset)
        private readonly Dictionary<int, float> _pricePending = new Dictionary<int, float>(); // host: per-change partials
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

        // StateSendTick change-gate: send only when the pose/camera/hold actually changed,
        // with a keepalive so a standing-still player still refreshes.
        private Vector3 _lastSentPos;
        private float _lastSentCamYaw;
        private Vector3 _lastSentCamPos;
        private Quaternion _lastSentCamRot;
        private byte _lastSentHold;
        private int _lastSentHoldSig;
        private bool _hasSentState;
        private float _stateKeepalive;

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
        // Per-tag rate-limited error logging lives in one place: Sync.ModuleGuard.

        private void Guarded(string stage, Action action)
        {
            try
            {
                Util.PerfProbe.Measure(stage, action);
            }
            catch (Exception e)
            {
                Sync.ModuleGuard.Log(stage, e);
            }
        }

        // LightManager reflection (time of day)
        private static readonly FieldInfo FiTimeHour = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_TimeHour");
        private static readonly FieldInfo FiTimeMin = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_TimeMin");
        private static readonly FieldInfo FiTimeMinFloat = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_TimeMinFloat");
        private static readonly FieldInfo FiTimerLerpSpeed = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_TimerLerpSpeed");
        private static readonly FieldInfo FiHasDayEnded = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_HasDayEnded");
        private static readonly System.Reflection.MethodInfo MiDayReset = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "DelayUpdateEnv");
        private static readonly FieldInfo FiTimeOfDayIdx = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_TImeOfDayIndex");
        private static readonly FieldInfo FiFinishLoading = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_FinishLoading");
        private static readonly System.Reflection.MethodInfo MiLightInit = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "Init");
        private static readonly System.Reflection.MethodInfo MiUpdateLightData = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "UpdateLightTimeData");
        private static readonly System.Reflection.MethodInfo MiEvaluateTimeClock = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "EvaluateTimeClock");
        private static readonly System.Reflection.MethodInfo MiEvaluateWorldUIBrightness = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "EvaluateWorldUIBrightness");
        private float _lightSyncTimer = -2.3f;   // timers carry staggered phases so the
        private LightManager _lightManager;      // periodic broadcasts never bunch into
        private float _cardResyncTimer = -5.2f;  // one frame (the rhythmic-hitch bug)
        private int _lastCardResyncHash;         // change-gate for the 12s full card repaint
        private float _cardResyncHeal;           // forces a repaint every 30s regardless
        private float _cardPriceHealTimer = -2.1f; // periodic displayed-card price rebroadcast
        private int _lastCardPriceHash;            // change-gate for the card-price heal
        private float _cardPriceHealBeat;          // forces a card-price resend every 30s
        private bool _cardPriceHealDirty;           // marked-price writes need a near-term heal
        private int _lastStockResyncHash;          // change-gate for the 12s item-STOCK full heal
        private float _stockResyncHeal;            // forces a stock resend every 36s regardless
        private readonly List<KeyValuePair<CardData, float>> _cardPriceBuf = new List<KeyValuePair<CardData, float>>();
        private float _licenseSyncTimer = -3.7f;
        private double _lastLicenseBuyTime = -999.0;
        private string _lastLightJson;
        private float _lightHeal;
        private bool _lightForceResend;
        private bool _observedShopLight;
        private bool _observedNightLight;
        private bool _observedSunlight;
        private bool _observedLightState;
        private bool _clientDayResetPending;
        private bool _clientDayResetInFlight;
        private float _clientDayResetDeadline;
        private bool _clientClockFrozen;
        private float _clientClockOriginalSpeed = 1f;
        private int _lastLicenseHash;
        private float _licenseHeal;

        // per-frame stages run through cached delegates: a fresh closure per stage per
        // frame was ~600 allocations/second of GC pressure that only existed in-session
        private float _dt;
        private bool _syncActive;
        private Action _actNetPump, _actAvatars, _actWorld, _actCardShelves, _actObjMoves,
            _actMovePreview,
            _actBoxes, _actPopulation, _actNpcPuppets, _actNpcSweep,
            _actStateSend, _actNpcCollect, _actModules, _actCardPriceRetry,
            _actFrameCardWork;
        private CustomerManager _cmSweep;
        private CustomerManager _cmSpray; // host-side: real customer list for replayed guest deodorant sprays
        private bool _renamerHandled;
        // FIX E2: the 3D shop-name sign (ShopRenamer.m_ShopName). The client disables the
        // renamer trigger on join, which also unhooks the one-shot OnGameDataFinishLoaded
        // listener that repaints this sign - so a ShopName that arrives mid-join can leave
        // the sign showing the guest's own default. We cache the TMP before disabling and
        // write it directly. It lives on a SEPARATE GameObject from the renamer (verified:
        // the game deactivates the renamer in OnGameDataFinishLoaded AFTER setting this
        // sign, and it still shows), so writing .text after the disable renders fine.
        private TMPro.TMP_Text _shopSign;
        private string _lastShopNameApplied; // re-applied once when ClientReloading clears
        private int _heldBoxFrame = -1;
        private object _heldBoxA, _heldBoxB, _heldBoxC;

        /// <summary>True while a received world is loading (plus a settle grace):
        /// the game's own load-cleanup destroys objects and NOTHING destroyed in
        /// that window is a player action to forward.</summary>
        public static bool ClientReloading;
        private float _reloadStartedAt;
        private int _reloadStartedFrame;
        private bool _clientWorldArrived;
        private bool _sessionInGame;
        /// <summary>True for the entire borrowed-world load: the old world may still be
        /// live before the scene changes, and the new world is only partially constructed
        /// afterward. Hold client-side sync until ShelfManager reports completion.</summary>
        // The game's ShelfManager owns the load completion signal.  Do not use a
        // fixed grace period here: a large shop can legitimately take longer than
        // ten seconds to instantiate, and releasing the hold while it is still
        // rebuilding makes the guest compete with its own load on the main thread.
        private bool ClientPreloadHold => ClientReloading;
        private readonly System.Collections.Generic.List<InMsg> _dispatchBuf
            = new System.Collections.Generic.List<InMsg>(64);
        private readonly System.Collections.Generic.List<InMsg> _dispatchRetryNextFrame
            = new System.Collections.Generic.List<InMsg>(8);
        private readonly System.Collections.Generic.List<InMsg> _dispatchHeldTransfers
            = new System.Collections.Generic.List<InMsg>(1);
        private bool _dispatchBacklogWarned;
        private bool _dispatchDeferredWarned;
        private bool _dispatchRoleDeniedWarned;
        private bool _dispatchTransferOverflowWarned;
        private bool _dispatchTransferHardDropWarned;
        private int _dispatchDeferredFrames;
        private const int MaxDispatchDeferredFrames = 600;
        private readonly MessageRouter _messageRouter = new MessageRouter();
        private readonly System.Collections.Generic.HashSet<long> _dispatchSeen
            = new System.Collections.Generic.HashSet<long>();
        /// <summary>Work UNITS dispatched per frame. The whole Incoming queue is still drained
        /// into _dispatchBuf (the coalescer needs the full picture), but applying an unbounded
        /// backlog in one frame is the hitch itself; the remainder keeps its order and waits.</summary>
        private const int DispatchBudget = 256;
        private const int DispatchBacklogCap = DispatchBudget * 8;
        private const byte MaxDispatchRetries = 3;
        private const int MainThreadActionBudget = 64;

        private sealed class MainThreadWork
        {
            public readonly string Stage;
            public readonly Action Action;
            public readonly bool Retryable;
            public int Attempts;
            public int NotBeforeFrame;

            public MainThreadWork(string stage, Action action, bool retryable)
            {
                Stage = stage;
                Action = action;
                Retryable = retryable;
            }
        }

        // headless auto-test / shortcut args: -coopautohost=SLOT  -coopautojoin=IP
        private int _autoHostSlot = -1;
        private string _autoJoinIp;
        private int _autoPhase;
        private float _autoTimer;

        private void Awake()
        {
            Instance = this;
            if (Util.PlayerModelStore.TryLoad(out var savedModel))
            {
                _localPlayerModel = savedModel;
                CoopPlugin.Log.LogInfo("loaded local co-op player appearance");
            }
            _messageRouter.Register<PingMessage>((context, message) =>
            {
                context.Transport.Send(context.ConnectionId, new PongMessage());
            });
            _messageRouter.Register<PongMessage>((context, message) => { });
            _messageRouter.Register<EmoteMessage>((context, message) =>
            {
                _avatars.ShowEmote(context.ConnectionId);
                RelayTagToOthers(context.ConnectionId, 0);
            });
            _messageRouter.Register<ActivityMessage>((context, message) =>
            {
                _avatars.ShowTag(context.ConnectionId, "opening a pack!", 3f);
                _avatars.ShowPackOpen(context.ConnectionId, (int)message.Pack);
                RelayTagToOthers(context.ConnectionId, 1, (int)message.Pack);
            });
            _messageRouter.Register<CoinSetMessage>((context, message) =>
            {
                if (!_loggedEconLink)
                {
                    _loggedEconLink = true;
                    CoopPlugin.Log.LogInfo("Economy link active (host wallet mirrored)");
                }
                if (Math.Abs(CPlayerData.m_CoinAmountDouble - message.Coin) > 0.0001)
                    CEventManager.QueueEvent(new CEventPlayer_SetCoin(message.CoinFloat, message.Coin));
            });
            _messageRouter.Register<ProgressSetMessage>((context, message) =>
            {
                int previous = CPlayerData.m_ShopLevel;
                CPlayerData.m_ShopLevel = message.Level;
                CEventManager.QueueEvent(new CEventPlayer_SetShopExp(message.Experience));
                CEventManager.QueueEvent(new CEventPlayer_SetFame(message.Fame));
                if (message.Level > previous)
                    CEventManager.QueueEvent(new CEventPlayer_ShopLeveledUp(message.Level));
            });
            _messageRouter.Register<ShopNameMessage>((context, message) => ApplyShopName(message.Name));
            _messageRouter.Register<RosterMessage>((context, message) => ApplyRoster(message));
            _messageRouter.Register<PlayerStateMessage>((context, message) => ApplyPlayerState(context.ConnectionId, message, true));
            _messageRouter.Register<RelayStateMessage>((context, message) =>
                ApplyPlayerState(1000 + message.SenderId, message.State, false));
            _messageRouter.Register<PlayerModelRequestMessage>((context, message) =>
                ApplyPlayerModelRequest(context.ConnectionId, message));
            _messageRouter.Register<PlayerModelStateMessage>((context, message) =>
                ApplyPlayerModelState(message));
            _messageRouter.Register<EconContributionMessage>((context, message) => ApplyEconomyContribution(context.ConnectionId, message),
                retryable: true, heal: () => { _coinHeal = 999f; _progressHeal = 999f; });
            _messageRouter.Register<EconDeltaMessage>((context, message) =>
                EconDeltaSync.Apply(message.Kind, message.Value));
            _messageRouter.Register<MovePreviewMessage>((context, message) =>
                ApplyMovePreview(context.ConnectionId, message));
            _messageRouter.Register<PurchaseRequestMessage>((context, message) => ApplyPurchaseRequest(context.ConnectionId, message),
                retryable: true, heal: () => { _coinHeal = 999f; _progressHeal = 999f; });
            _messageRouter.Register<PurchaseResultMessage>((context, message) => ApplyPurchaseResult(message));
            _messageRouter.Register<SprayHitMessage>((context, message) => ApplySprayHit(message),
                retryable: true, heal: () => { _coinHeal = 999f; _progressHeal = 999f; });
            _messageRouter.Register<GradedRemoveMessage>((context, message) => ApplyGradedRemove(context.ConnectionId, message),
                retryable: true, heal: () => CoopPlugin.Log.LogError("Binder heal deferred: GradedRemove requires a full binder resync message, which is a wire change."));
            _ui = new UI.CoopUI();
            _world.OnLocalChanges = OnLocalWorldChanges;
            _world.SendResult = (result, connId) => Send(connId, result);
            _world.RequestResync = () =>
            {
                CoopPlugin.Log.LogWarning("WorldSync requested authoritative shelf resync");
                if (Role == CoopRole.Host)
                    Broadcast(new ShelfDeltaMessage { Entries = _world.BuildFullState() });
                else if (Role == CoopRole.Client)
                    Send(1, new JoinResyncRequestMessage());
            };
            _cardShelves.OnLocalChanges = changes =>
            {
                if (Role == CoopRole.Host)
                    Broadcast(new CardShelfDeltaMessage { Entries = changes });
                else if (Role == CoopRole.Client)
                    Send(1, new CardShelfRequestMessage { Entries = changes });
            };
            _objMoves.OnLocalChanges = changes =>
            {
                if (Role == CoopRole.Host)
                    Broadcast(new ObjMoveDeltaMessage { Entries = changes });
                else if (Role == CoopRole.Client)
                    Send(1, new ObjMoveRequestMessage { Entries = changes });
            };
            _population.OnHostSnapshot = all =>
                Broadcast(new PopStateMessage { Entries = all });
            ItemBoxFamily.IsLocallyCarried = box =>
            {
                if (_playerIpc == null || box == null)
                    return false;
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
                catch (System.Exception e) { Swallow.Log(e); return false; }
            };
            BoxShared.LocalBoxDestroyed = box =>
            {
                if (IsTearingDown || !InGameLevel() || ClientReloading)
                    return;
                if (Role == CoopRole.Client)
                    _boxEngine?.ClientNotifyLocalDestroyed(box);
                else if (Role == CoopRole.Host)
                    _boxEngine?.HostNotifyLocalDestroyed(box);
            };
            _actCardPriceRetry = CardPriceRetryTick;
            _actFrameCardWork = FlushFrameCardWork;
            _actNetPump = () => _net.PumpMainThread();
            _actAvatars = () =>
            {
                AvatarManager.ViewCamera = _playerCamTf; // the camera the player SEES through
                _avatars.Tick(_dt);
            };
            // Population must be broadcast before any index-keyed content state. A shelf
            // can register in the host's list before the client has created its mirror;
            // sending ShelfDelta/CardShelfDelta first makes the client discard the update
            // and the host's diff baseline then prevents it from being sent again.
            _actPopulation = () => { if (Role == CoopRole.Host) _population.HostTick(_dt, _syncActive); };
            _actWorld = () => _world.Tick(_dt, _syncActive);
            _actCardShelves = () =>
            {
                _cardShelves.IsClientRole = Role == CoopRole.Client;
                _cardShelves.Tick(_dt, _syncActive);
            };
            _actObjMoves = () => _objMoves.Tick(_dt, _syncActive);
            _actMovePreview = () => _movePreview.Tick(_dt);
            _actBoxes = () =>
            {
                if (Role == CoopRole.Host)
                    _boxEngine.HostTick(_dt, _syncActive);
                else if (Role == CoopRole.Client)
                {
                    _boxEngine.ClientTick(_dt, _syncActive && !ClientPreloadHold);
                    HandEscrow.Tick();
                }
                // Push motion is symmetric on both roles: the local player's physical push is
                // pruned then streamed (PushTick), and the smoothed followers (arc + dead-reckon
                // pushed boxes) advance exactly once per frame here for whichever role is running
                // (moved out of the client-only digest tick).
                _pushProbe?.Tick(_dt);
                _boxEngine.PushTick(_dt, _syncActive);
                BoxPlacement.TickVanillaPlacement(_dt);
                BoxPlacement.TickRemoteMotions(_dt);
            };
            _actNpcPuppets = () => _npcs.TickPuppets(_dt, InGameLevel());
            _actNpcSweep = NpcSweepTick;
            _actStateSend = StateSendTick;
            _actNpcCollect = NpcCollectTick;

            _grading.SendOp = Send(1);
            _grading.BroadcastState = Broadcast;
            _trades.SendOp = Send(1);
            _trades.BroadcastState = Broadcast;
            _tables.BroadcastState = Broadcast;
            _tables.RegisterIntents(_intents);
            _intents.SendOp = Send(1);
            _register.SendOp = Send(1);
            _register.BroadcastState = Broadcast;
            _register.BroadcastCart = Broadcast;
            _register.BroadcastChange = Broadcast;
            _register.SendToClient = Send;
            _staff.SendOp = Send(1);
            _staff.BroadcastState = Broadcast;
            _staff.SendToClient = Send;
            _staff.BroadcastInteraction = Broadcast;
            _shopState.SendOp = Send(1);
            _shopState.BroadcastState = Broadcast;
            _settings.SendOp = Send(1);
            _settings.BroadcastState = Broadcast;
            _market.BroadcastState = Broadcast;
            _market.RequestResync = () =>
            {
                if (Role == CoopRole.Client)
                    Send(1, new JoinResyncRequestMessage());
                else
                    _market.ForceResend();
            };
            _report.BroadcastState = Broadcast;
            _containers.SendOp = Send(1);
            _containers.BroadcastState = Broadcast;
            _containers.RequestBoxResync = () => _boxEngine?.RequestFullSnapshot();
            _containers.SendToClient = Send;
            _containers.HoldClientBox = TryHoldClientBox;
            _tournament.BroadcastState = Broadcast;
            _tv.SendOp = Send(1);
            _tv.BroadcastState = Broadcast;
            _tv.PeerCount = () => _net == null ? 0 : _net.ConnectionCount;

            FurnitureBoxOps.SendOp = Send(1);
            FurnitureBoxOps.IsLocallyCarried = box =>
            {
                if (_playerIpc == null || box == null)
                    return false;
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
                catch (System.Exception e) { Swallow.Log(e); return false; }
            };

            _boxEngine = new BoxEngine(new IBoxFamily[]
            {
                _itemBoxFamily, _cardBoxFamily, _furnBoxFamily,
            });
            _boxEngine.SendSnapshot = snap => Broadcast(snap);
            _boxEngine.SendUpdate = update => Send(1, update);
            _boxEngine.SendTransferResult = (result, connId) => Send(connId, result);
            // Client: an authoritative re-assert is the only way to converge a box whose
            // content a pending local add had to protect from a stale snapshot.
            _boxEngine.RequestBoxResync = () => Send(1, new JoinResyncRequestMessage());
            _boxEngine.OnClientBoxSpawned = box => _containers.TryAutoHoldTakenBox(box);
            _boxEngine.LocalHeld = () =>
            {
                if (_playerIpc == null)
                    return null;
                try
                {
                    return FiHoldBox?.GetValue(_playerIpc) as InteractablePackagingBox;
                }
                catch (System.Exception e) { Swallow.Log(e); return null; }
            };
            _boxEngine.LocalPushed = () => _pushProbe != null ? _pushProbe.Pushed : null;
            // Client -> host: one transient push-motion frame for a locally pushed box.
            _boxEngine.SendMotion = msg =>
            {
                if (_net != null)
                    _net.SendTransient(1, msg);
            };
            // Host -> all except the given conn id (-1 = all): relay the pushed box's motion so
            // every non-driver peer smooths it. The transient lane is safe: a lost frame is
            // replaced by the next one.
            _boxEngine.RelayMotion = (msg, exclude) =>
            {
                if (Role != CoopRole.Host || _net == null)
                    return;
                foreach (int cid in _net.ConnIds())
                    if (cid != exclude)
                        _net.SendTransient(cid, msg);
            };
            CardBoxFamily.IsLocallyCarried = box =>
            {
                if (_playerIpc == null || box == null)
                    return false;
                try
                {
                    if (_heldBoxFrame != Time.frameCount)
                    {
                        _heldBoxFrame = Time.frameCount;
                        _heldBoxB = FiHoldBox?.GetValue(_playerIpc);
                        _heldBoxC = FiHoldBoxCard?.GetValue(_playerIpc);
                    }
                    return ReferenceEquals(_heldBoxC, box) || ReferenceEquals(_heldBoxB, box);
                }
                catch (System.Exception e) { Swallow.Log(e); return false; }
            };
            CardBoxOps.IsLocallyCarried = CardBoxFamily.IsLocallyCarried;
            CardBoxOps.SendCollect = msg => Send(1, msg);
            CardBoxOps.SendResult = (connId, msg) => Send(connId, msg);
            _moduleCatalog = BuildModuleCatalog();
            var modules = new List<ICoopModule>();
            var patches = new List<Sync.CoopModulePatch>();
            for (int i = 0; i < _moduleCatalog.Length; i++)
            {
                Sync.CoopModuleEntry entry = _moduleCatalog[i];
                if ((entry.HostSlot >= 0 || entry.ClientSlot >= 0) &&
                    !(entry.Module is ITickableCoopModule))
                    throw new InvalidOperationException("Tick pipeline entry is not tickable: " + entry.Name);
                if (entry.Module != null)
                    modules.Add(entry.Module);
                if (entry.Patches != null)
                    patches.Add(new Sync.CoopModulePatch(entry.Name, entry.Patches));
            }
            _allModules = modules.ToArray();
            PatchCatalog = patches.ToArray();
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
            if (_autoHostSlot >= 0)
                CoopPlugin.Log.LogInfo($"AUTO: will load slot {_autoHostSlot} and host");
            if (_autoJoinIp != null)
                CoopPlugin.Log.LogInfo($"AUTO: will join {_autoJoinIp}");
            if (_autoJoinSteamLobby != 0)
                CoopPlugin.Log.LogInfo($"AUTO: will join Steam lobby {_autoJoinSteamLobby}");

            // Steam, if this build has any. TryCreate returns null - silently, and without
            // ever touching a Steamworks type - on Game Pass / DRM-free installs, and the
            // whole plugin simply runs LAN-only from there. The transport wiring these
            // callbacks used to do now lives inside the bridge (SteamBridgeImpl.Init),
            // because it is Steam-typed; only the Steam-free outcome arrives here.
            _steam = SteamBridge.TryCreate();

            // THE PLATFORM PROMISE IS MADE HERE, NOT IN CoopPlugin.Awake. The startup line up
            // there can only report whether the ASSEMBLY resolved, and 1.0.39's field reports
            // showed that answering TRUE and Steam actually working are different things (a
            // Game Pass install with a stray com.rlabrecque.steamworks.net.dll bundled by
            // another mod). This is the first point in startup that knows the real answer,
            // because TryCreate has now both loaded the type AND probed the native runtime
            // (SteamBridgeImpl's constructor) - so this is where a player is told what they
            // actually have. TryCreate's own warning is deliberately left alone: it carries the
            // exception detail, and these two lines carry the verdict.
            if (_steam == null && PlatformProbe.SteamworksPresent)
            {
                // The assembly loaded but the bridge did not: a stripped Steamworks (Game Pass
                // 0.70 has SteamAPI but no Callback`1) or a complete stray dll with no native
                // steam_api64.dll behind it. Either way Steam is unreachable here and the UI
                // stays hidden - say so plainly rather than leaving the startup line's
                // "assembly detected" as the last word on the subject.
                CoopPlugin.Log.LogInfo("Steamworks assembly present but NOT functional (stripped assembly or no Steam runtime - Game Pass build?) - LAN and direct IP only.");
            }

            if (_steam != null)
            {
                CoopPlugin.Log.LogInfo("Steam bridge ready - lobbies, invites and P2P available.");
                _steam.Init();
                _steam.OnError = err => { ErrorLine = err; CoopPlugin.Log.LogWarning(err); };
                _steam.OnLobbyLive = lobby =>
                {
                    StatusLine = "Hosting via Steam - click 'Invite friend'";
                    // The id is the one thing a support log needs from this line: it is what
                    // the joiner's "+connect_lobby <id>" and invite-accept both name, so
                    // without it the two halves of a failed join cannot be matched up.
                    CoopPlugin.Log.LogInfo("steam: lobby live " + lobby);
                };
                _steam.OnConnectedToHost = () =>
                {
                    // THE ROLE GUARD LIVES HERE NOW (it used to sit beside the transport
                    // wiring, inside the OnEnteredLobby handler). Do not drop it: it is
                    // what stops a stray lobby-enter from starting a handshake while we
                    // are hosting or idle.
                    if (Role != CoopRole.Client)
                        return;
                    StatusLine = "Connected via Steam - requesting world...";
                    SendHello();
                };
                _steam.OnInviteAccepted = lobby =>
                {
                    CoopPlugin.Log.LogInfo("steam: invite accepted -> lobby " + lobby);
                    JoinSteam(lobby);
                };
            }

            CEventManager.AddListener<CEventPlayer_OnOpenCardPack>(OnLocalPackOpened);

            // FIX A-hook: warn ONCE per game session if the on-disk custom-card registry is
            // still a HOST-synced copy from a previous co-op join. The warning itself lives in
            // EnumLendState() now - see there for why Awake is too early to ask. This call is
            // kept only as the fast path for the case where EPL IS already chainloaded: it emits
            // the warning at boot (where a player looking for it expects it) and sets the memo so
            // EnumLendState cannot repeat it.
            try
            {
                EnumLendState();
            }
            catch (System.Exception e) { Swallow.Log(e); }
            RegisterDomainRoutes();
        }

        public string HostPassword = "";        // required from joiners when non-empty
        private string _joinPassword = "";       // sent in our Hello when joining
        /// <summary>Raw lobby id (0 = none) for the wrong-password retry flow. A ulong, NOT
        /// a CSteamID: a Steamworks STRUCT field here would stop CoopCore loading at all on
        /// a build without the Steamworks assembly.</summary>
        public ulong LastFailedLobby;

        /// <summary>The Steam facade, or NULL when this build has no Steamworks assembly at
        /// all. The UI uses `Steam == null` as its single "hide every Steam control" test.</summary>
        public ISteamBridge Steam => _steam;

        /// <summary>Join a host through Steam (invite accept, browser, +connect_lobby).</summary>
        public void JoinSteam(ulong lobby, string password = "")
        {
            ErrorLine = "";
            if (Role != CoopRole.None)
            {
                ErrorLine = "Already in a session.";
                return;
            }
            if (InGameLevel())
            {
                ErrorLine = "Go to the main menu first, then accept the invite again.";
                return;
            }
            // Two DIFFERENT failures, two different messages: no assembly means Steam can
            // never work on this install (nothing the player can do), whereas the client
            // simply not running is fixable. Never collapse these into one line.
            if (_steam == null)
            {
                ErrorLine = "This build has no Steam support - use LAN or direct IP.";
                return;
            }
            if (!_steam.SteamAvailable())
            {
                ErrorLine = "Steam isn't running.";
                return;
            }
            // FIX C: the card database on disk was replaced by the host's copy this session,
            // but THIS process is still running the old one (the registry is read once at
            // startup). Joining now would hand the host our stale ids and earn another
            // rejection - say so plainly instead of burning a whole handshake on it. Only the
            // INSTALL raises this; restoring your own backup is a solo-save matter and must
            // not cost a second restart before you can join (see ModParity's two flags).
            if (Util.ModParity.RestartRequiredForJoin)
            {
                ErrorLine = "the host's card database was installed on this PC - RESTART the game before joining";
                return;
            }
            Role = CoopRole.Client;
            try
            {
                ActivateLiveModuleHooks();
                GuestBorrowedWorld = true; // block ALL saves until we're back at the title screen
                IsSteamSession = true;
                _joinPassword = password ?? "";
                LastFailedLobby = lobby;
                // ORDER IS LOAD-BEARING: the transport must exist before Join(), because the
                // bridge's lobby-entered callback wires the host connection into it.
                _net = LagTransport.Wrap(_steam.CreateTransport(false, new PingMessage()));
                StatusLine = "Joining Steam lobby...";
                _steam.Join(lobby);
            }
            catch (Exception e)
            {
                AbortSessionStart("Could not join: " + e.Message);
            }
        }

        /// <summary>Host through Steam: friends-only (invite) or public (lobby browser).</summary>
        public void StartHostingSteam(bool isPublic, string lobbyName, string password)
        {
            ErrorLine = "";
            if (Role != CoopRole.None)
            {
                ErrorLine = "Already in a session.";
                return;
            }
            if (!InGameLevel())
            {
                ErrorLine = "Load your shop first, then host.";
                return;
            }
            // Same two-step check as JoinSteam: missing assembly vs. client not running.
            if (_steam == null)
            {
                ErrorLine = "This build has no Steam support - use LAN instead.";
                return;
            }
            if (!_steam.SteamAvailable())
            {
                ErrorLine = "Steam isn't running - use LAN instead.";
                return;
            }
            // a HOST must never translate: drop any table a previous session left behind
            Util.EnumMap.Clear();
            Role = CoopRole.Host;
            try
            {
                ActivateLiveModuleHooks();
                IsSteamSession = true;
                HostPassword = password ?? "";
                // ORDER IS LOAD-BEARING: transport first, then Host() - the bridge's
                // lobby-created callback stamps the new lobby id onto this transport.
                _net = LagTransport.Wrap(_steam.CreateTransport(true, new PingMessage()));
                StatusLine = "Creating Steam lobby...";
                _steam.Host(isPublic, lobbyName, HostPassword.Length > 0);
            }
            catch (Exception e)
            {
                AbortSessionStart("Could not host: " + e.Message);
            }
        }

        public void OpenSteamInvite()
        {
            _steam?.OpenInviteDialog();
        }

        private void SendHello()
        {
            // Logged on BOTH sides of every Hello (the host logs the pair when it reads
            // them): when a cross-play session misbehaves, the two logs together say
            // immediately whether the two PCs are even on the same game build.
            CoopPlugin.Log.LogInfo($"game build: sending {Application.version} / Unity {Application.unityVersion}");
            byte[] gzEnum;
            try
            {
                var enumRaw = System.Text.Encoding.UTF8.GetBytes(
                    string.Join("\n", Util.ModParity.EnumLines()));
                gzEnum = Msg.Gzip(enumRaw);
            }
            catch (Exception e)
            {
                // never let a registry read failure abort the handshake: an EMPTY blob
                // reads on the host as "no modded entries", which conflicts with nobody -
                // exactly how the old "none" hash failed open.
                CoopPlugin.Log.LogWarning("enum lines for Hello: " + e.Message);
                gzEnum = Msg.Gzip(new byte[0]);
            }
            Send(1, new HelloMessage
            {
                WireVersion = Msg.WireVersion,
                Version = CoopPlugin.Version,
                PlayerName = EffectivePlayerName,
                SteamId = _steam == null ? 0 : _steam.LocalSteamId,
                Password = _joinPassword ?? "",
                PluginHash = Util.ModParity.PluginHash(),
                EnumHash = Util.ModParity.EnumHash(),
                CardsHash = Util.ModParity.CardsHash(),
                // FIX E3: append the actual plugin + custom-card lists so a rejecting host
                // can NAME what differs (a hash alone can't). Cap 256 each.
                PluginList = Util.ModParity.PluginList(),
                CardsList = Util.ModParity.CardsList(),
                EnumBlob = gzEnum,
                GameVersion = Application.version ?? "",
                UnityVersion = Application.unityVersion ?? "",
            });
        }

        // Bye must actually reach the peer before the connection dies; on Steam, sends
        // drain on later frames, so the kick is deferred a moment.
        private readonly List<KeyValuePair<int, float>> _pendingKicks = new List<KeyValuePair<int, float>>();

        private void RejectConn(int connId, string reason)
        {
            CoopPlugin.Log.LogWarning($"rejected connection {connId}: {reason}");
            Send(connId, new ByeMessage { Reason = reason });
            _pendingKicks.Add(new KeyValuePair<int, float>(connId, 1.5f));
        }

        private void RelayTagToOthers(int senderConn, byte kind, int extra = -1)
        {
            if (Role != CoopRole.Host || _net == null || _net.ConnectionCount <= 1)
                return;
            // extra is the pack's EItemType for kind 1, and the unused -1 for an emote - which is
            // below the modded floor and so passes through the helper untouched. Host-side this
            // write is the identity function either way.
            var relay = new RelayTagMessage { SenderId = senderConn, Kind = kind, Extra = (EItemType)extra };
            foreach (int cid in _net.ConnIds())
                if (cid != senderConn)
                    _net.Send(cid, relay);
        }

        private void BroadcastRoster()
        {
            if (Role != CoopRole.Host)
                return;
            var entries = new List<KeyValuePair<int, string>>(PeerNames);
            var roster = new RosterMessage();
            foreach (var e in entries)
            {
                _peerWireNames.TryGetValue(e.Key, out var wireName);
                if (string.IsNullOrEmpty(wireName))
                    wireName = e.Value;
                _peerSteamIds.TryGetValue(e.Key, out var steamId);
                roster.Entries.Add(new RosterEntry { Id = e.Key, Name = wireName, SteamId = steamId });
            }
            Broadcast(roster);
        }

        internal BoxEngine Boxes => _boxEngine;

        private int _selfId = -1; // our connId on the host, from Welcome
        internal static int LocalConnectionId
        {
            get
            {
                var core = Instance;
                return core == null ? -1 : core._selfId;
            }
        }
        private readonly HashSet<int> _relayIds = new HashSet<int>(); // other clients we render

        private void OnLocalPackOpened(CEventPlayer_OnOpenCardPack evt)
        {
            if (Role != CoopRole.None && _net != null && _net.ConnectionCount > 0)
                // m_PackIndex is named like an index but is spent as an EItemType on the far
                // side (AvatarManager.ShowPackOpen -> GetItemMeshData((EItemType)packIndex)),
                // so a modded pack needs translating like any other item id or the peer's
                // avatar holds up whichever product wears that number locally.
                Broadcast(new ActivityMessage { Activity = 1, Pack = (EItemType)evt.m_PackIndex });
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
            if (ClientReloading && scene.name != "Title")
            {
                _clientWorldArrived = true;
                _reloadStartedAt = Time.realtimeSinceStartup;
                _reloadStartedFrame = Time.frameCount;
            }
            PlayerModelGeneration++;
            _avatars.Clear();
            if (_moduleRegistry != null)
            {
                _moduleRegistry.ResetState();
            }
            else
            {
                ResetAllModules();
            }
            _lightManager = null;
            _clientClockFrozen = false;
            // Unity stops scene-owned coroutines during a load. If that interrupted the
            // mirrored morning reset, make it retry against the newly loaded manager.
            if (_clientDayResetInFlight)
            {
                _clientDayResetInFlight = false;
                MarkClientDayResetPending();
            }
            _cmSweep = null;
            _cmSpray = null;
            _inventory = null;
            _renamerHandled = false;
            _catalogSent = false;
            // A new world means a new album: the pairing hints and the whole diff describe one
            // that no longer exists, and a stale hint would suppress a legitimate add.
            ClearGradedSkipMemory();
            _gradedSent = false;
            _lastGradedHash = -1;
            _gradedPeerOnly.Clear();
            _gradedAlertEverShown.Clear();
            _gradedAlertStanding.Clear();
            GradedAdoptOffers.Clear();
            _playerTf = null;
            _playerCamTf = null;
            _playerIpc = null;
            _localModelAppliedRoot = null;
            _localPlayerModelReady = false;
            if (scene.name == "Title" && Role == CoopRole.Client && _net != null)
            {
                // client backed out to the main menu -> leave the session
                Shutdown("left the session");
            }
            // A game-level scene loading (not "Title") while a session is live and it was
            // NOT the mod's own join reload means someone loaded a DIFFERENT world out from
            // under the session: the guest hit pause -> Load Game -> its own save
            // (SaveLoadGameSlotSelectScreen loads "Start" mid-session), or the host loaded
            // another slot. The socket would otherwise stay open with the peer standing in a
            // world we no longer share, and the other side is never told. Shut the session
            // down cleanly, same path as the Title back-out above.
            //   - scene.name != "Title": the Title branch already handled the clean exit.
            //   - Role != None && _net != null: a session must actually be active (guards the
            //     HOST's own INITIAL world load, which happens from TitleScreen BEFORE
            //     StartHosting sets Role=Host - Role is still None there, so this can't fire).
            //   - !ClientReloading: the guest's mod-driven join reload (BundleDone sets this,
            //     and it also loads "Start") is the mod's OWN transition - never a leave.
            else if (scene.name != "Title" && Role != CoopRole.None && _net != null && !ClientReloading)
            {
                Shutdown("left the session (world reloaded)");
            }
        }

        private bool InGameLevel()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel;
        }

        public PlayerModelEntry GetLocalPlayerModel()
        {
            EnsureLocalPlayerModel();
            return ClonePlayerModel(_localPlayerModel);
        }

        public bool CanUndoPlayerModel
        {
            get
            {
                return _playerModelUndo.Count > 0;
            }
        }
        public bool CanRedoPlayerModel
        {
            get
            {
                return _playerModelRedo.Count > 0;
            }
        }

        public void SetCharacterPreview(bool active)
        {
            _characterPreviewActive = active;
        }

        public List<CC.CC_Property> GetLocalModelSliders()
        {
            EnsureLocalPlayerModel();
            return _avatars.GetBlendshapes(GetLocalCustomization());
        }

        public CC.CharacterCustomization GetLocalCustomization()
        {
            EnsureLocalPlayerModel();
            var custom = _avatars.GetEditorCustomization(_localPlayerModel.Female);
            if (custom != null && _localModelAppliedRoot != custom.transform)
            {
                _avatars.ApplyLocalModel(custom, _localPlayerModel);
                _localModelAppliedRoot = custom.transform;
            }
            return custom;
        }

        public void CommitLocalCustomization()
        {
            EnsureLocalPlayerModel();
            _localModelAuto = false;
            RecordLocalModelChange();
            var custom = GetLocalCustomization();
            _localPlayerModel = _avatars.CaptureLocalModel(custom, _localPlayerModel.Female, _localPlayerModel.ModelIndex);
            _localPlayerModelReady = true;
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
        }

        public bool UndoPlayerModel()
        {
            EnsureLocalPlayerModel();
            _localModelAuto = false;
            if (_playerModelUndo.Count == 0)
                return false;
            PushHistory(_playerModelRedo, _lastCommittedPlayerModel ?? _localPlayerModel);
            _localPlayerModel = _playerModelUndo[_playerModelUndo.Count - 1];
            _playerModelUndo.RemoveAt(_playerModelUndo.Count - 1);
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            _localModelAppliedRoot = null;
            GetLocalCustomization();
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
            return true;
        }

        public bool RedoPlayerModel()
        {
            EnsureLocalPlayerModel();
            _localModelAuto = false;
            if (_playerModelRedo.Count == 0)
                return false;
            PushHistory(_playerModelUndo, _lastCommittedPlayerModel ?? _localPlayerModel);
            _localPlayerModel = _playerModelRedo[_playerModelRedo.Count - 1];
            _playerModelRedo.RemoveAt(_playerModelRedo.Count - 1);
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            _localModelAppliedRoot = null;
            GetLocalCustomization();
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
            return true;
        }

        public List<string> GetLocalPresetNames()
        {
            var result = new List<string>();
            var custom = GetLocalCustomization();
            if (custom == null || custom.Presets == null || custom.Presets.Presets == null)
                return result;
            string prefix = _localPlayerModel.Female ? "Female" : "Male";
            foreach (var preset in custom.Presets.Presets)
                if (preset != null && (preset.CharacterName ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(preset.CharacterName);
            return result;
        }

        public void ApplyLocalPreset(string presetName)
        {
            EnsureLocalPlayerModel();
            var custom = GetLocalCustomization();
            if (custom == null || custom.Presets == null || custom.Presets.Presets == null)
                return;
            CC.CC_CharacterData preset = null;
            foreach (var candidate in custom.Presets.Presets)
                if (candidate != null && candidate.CharacterName == presetName)
                {
                    preset = candidate;
                    break;
                }
            if (preset == null)
                return;
            var entry = AvatarManager.ModelFromPreset(preset);
            if (entry == null)
                return;
            RecordLocalModelChange();
            _localModelAuto = false;
            _localPlayerModel.Female = entry.Female;
            _localPlayerModel.ModelIndex = entry.ModelIndex;
            _localPlayerModel.CustomizationJson = entry.CustomizationJson;
            _localModelAppliedRoot = null;
            GetLocalCustomization();
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
        }

        private void RecordLocalModelChange()
        {
            EnsureLocalPlayerModel();
            if (_lastCommittedPlayerModel == null)
                _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            PushHistory(_playerModelUndo, _lastCommittedPlayerModel);
            _playerModelRedo.Clear();
        }

        private static void PushHistory(List<PlayerModelEntry> history, PlayerModelEntry model)
        {
            if (model == null)
                return;
            history.Add(ClonePlayerModel(model));
            if (history.Count > PlayerModelHistoryLimit)
                history.RemoveAt(0);
        }

        public void ClearLocalHair(int slot)
        {
            if (_avatars.ClearHair(GetLocalCustomization(), slot))
                CommitLocalCustomization();
        }

        public void ClearLocalApparel(int slot)
        {
            if (_avatars.ClearApparel(GetLocalCustomization(), slot))
                CommitLocalCustomization();
        }

        public void SetLocalHairColor(int slot, Color color)
        {
            if (_avatars.SetHairColor(GetLocalCustomization(), slot, color))
                CommitLocalCustomization();
        }

        public void SetLocalApparelTint(int slot, Color color)
        {
            if (_avatars.SetApparelTint(GetLocalCustomization(), slot, color))
                CommitLocalCustomization();
        }

        public void SetLocalModelSlider(string propertyName, float value)
        {
            EnsureLocalPlayerModel();
            _localModelAuto = false;
            RecordLocalModelChange();
            if (!_avatars.SetBlendshape(GetLocalCustomization(), propertyName, value))
                return;
            var custom = GetLocalCustomization();
            _localPlayerModel = _avatars.CaptureLocalModel(custom, _localPlayerModel.Female, _localPlayerModel.ModelIndex);
            _localPlayerModelReady = true;
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
        }

        public void SetLocalPlayerModel(bool female, int modelIndex)
        {
            EnsureLocalPlayerModel();
            RecordLocalModelChange();
            bool genderChanged = _localPlayerModel.Female != female;
            _localPlayerModel.Female = female;
            _localPlayerModel.ModelIndex = Mathf.Max(0, modelIndex);
            if (genderChanged)
            {
                // A new gender must never start nude: use a random clothed preset of that
                // gender, or fall back to the initialized default preset.
                var preset = _avatars.TryCreateRandomPresetModel(female);
                if (preset != null)
                {
                    _localPlayerModel.ModelIndex = preset.ModelIndex;
                    _localPlayerModel.CustomizationJson = preset.CustomizationJson;
                }
                else
                {
                    _localPlayerModel.CustomizationJson = null;
                }
            }
            _localModelAuto = false;
            _avatars.ApplyLocalModel(GetLocalCustomization(), _localPlayerModel);
            var editor = _avatars.GetEditorCustomization(_localPlayerModel.Female);
            if (editor != null)
                _localModelAppliedRoot = editor.transform;
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
        }

        /// <summary>Live NSFW toggle. Disabling it re-dresses a nude local model and rebuilds
        /// every remote avatar so the filter applies immediately.</summary>
        public void SetNsfwAllowed(bool allowed)
        {
            if (CoopPlugin.AllowNsfw.Value == allowed)
                return;
            CoopPlugin.AllowNsfw.Value = allowed;
            CoopPlugin.Log.LogInfo("NSFW appearances " + (allowed ? "allowed" : "blocked"));
            EnsureLocalPlayerModel();
            if (!allowed && _localPlayerModel != null && AvatarManager.IsNude(_localPlayerModel))
            {
                var preset = _avatars.TryCreateRandomPresetModel(_localPlayerModel.Female);
                if (preset != null)
                {
                    RecordLocalModelChange();
                    _localPlayerModel = preset;
                    _localModelAuto = false;
                    _localModelAppliedRoot = null;
                    _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
                    QueueLocalModelSave();
                    SubmitLocalPlayerModel();
                }
                else
                {
                    _localModelAuto = true; // retry once preset data is available
                }
            }
            _avatars.RefreshRemoteAvatars();
            PlayerModelGeneration++;
        }

        private void EnsureLocalPlayerModel()
        {
            if (!_localPlayerModelReady)
            {
                bool loaded = _localPlayerModel != null || Util.PlayerModelStore.TryLoad(out _localPlayerModel);
                _localPlayerModelReady = true;
                if (!loaded)
                {
                    // A vanilla character can exist before the co-op model store has ever
                    // been written. Capture it before falling back to a generated preset, or
                    // first-run co-op would silently randomize an already dressed player.
                    _localPlayerModel = _playerTf != null
                        ? _avatars.CaptureLocalModel(_playerTf)
                        : null;
                    if (_localPlayerModel != null)
                        _localModelAuto = NeedsAutoRepair(_localPlayerModel);
                    else
                    {
                        _localPlayerModel = _avatars.CreateDefaultModel();
                        _localModelAuto = true;
                    }
                }
                else
                {
                    _localModelAuto = NeedsAutoRepair(_localPlayerModel);
                }
            }

            if (_localModelAuto)
                TryUpgradeAutoModel();

            var editor = _avatars.GetEditorCustomization(_localPlayerModel.Female);
            if (editor != null && _localModelAppliedRoot != editor.transform)
            {
                string customizationBeforeApply = _localPlayerModel.CustomizationJson;
                _avatars.ApplyLocalModel(editor, _localPlayerModel);
                _localModelAppliedRoot = editor.transform;
                if (customizationBeforeApply != _localPlayerModel.CustomizationJson)
                {
                    // A malformed/stale appearance is repaired to the template default by
                    // AvatarManager. Persist that repair so every later handshake uses the
                    // safe model instead of retrying the bad payload.
                    _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
                    QueueLocalModelSave();
                }
            }
            if (_lastCommittedPlayerModel == null)
                _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
        }

        /// <summary>A model needs a random clothed preset when it has no saved appearance yet,
        /// or when it is nude while NSFW is disabled. Kept false once the player makes an
        /// explicit wardrobe choice.</summary>
        private static bool NeedsAutoRepair(PlayerModelEntry model)
        {
            if (model == null)
                return true;
            if (string.IsNullOrEmpty(model.CustomizationJson))
                return true;
            return !CoopPlugin.AllowNsfw.Value && AvatarManager.IsNude(model);
        }

        /// <summary>Assigns a random clothed preset of the model's current gender. Does
        /// nothing until the game's preset data is available, and is retried later.</summary>
        private void TryUpgradeAutoModel()
        {
            if (_localPlayerModel == null)
            {
                _localPlayerModel = _avatars.CreateDefaultModel();
                return;
            }
            var preset = _avatars.TryCreateRandomPresetModel(_localPlayerModel.Female);
            if (preset == null)
                return;
            _localPlayerModel = preset;
            _localModelAuto = false;
            _localModelAppliedRoot = null;
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
            CoopPlugin.Log.LogInfo($"default appearance assigned: {(preset.Female ? "female" : "male")} model {preset.ModelIndex}");
        }

        private void QueueLocalModelSave()
        {
            _localModelSavePending = true;
            _localModelSaveTimer = 0.4f;
        }

        private void FlushLocalModelSave(float dt)
        {
            if (!_localModelSavePending || _localPlayerModel == null)
                return;
            _localModelSaveTimer -= dt;
            if (_localModelSaveTimer > 0f)
                return;
            _localModelSavePending = false;
            Util.PlayerModelStore.Save(_localPlayerModel);
        }

        private void SubmitLocalPlayerModel()
        {
            if (Role == CoopRole.Host)
            {
                _playerModels[0] = ClonePlayerModel(_localPlayerModel);
                BroadcastPlayerModelState();
            }
            else if (Role == CoopRole.Client && _net != null)
            {
                Send(1, new PlayerModelRequestMessage
                {
                    Female = _localPlayerModel.Female,
                    ModelIndex = _localPlayerModel.ModelIndex,
                    CustomizationJson = _localPlayerModel.CustomizationJson
                });
            }
        }

        private void ApplyPlayerModelRequest(int connId, PlayerModelRequestMessage request)
        {
            if (Role != CoopRole.Host || request == null || connId <= 0)
                return;
            _playerModels[connId] = new PlayerModelEntry
            {
                Id = (byte)connId,
                Female = request.Female,
                ModelIndex = Mathf.Max(0, request.ModelIndex),
                CustomizationJson = request.CustomizationJson
            };
            _avatars.SetModel(connId, request.Female, request.ModelIndex, request.CustomizationJson);
            CoopPlugin.Log.LogInfo($"appearance update from player {connId}: {(request.Female ? "female" : "male")} {request.ModelIndex}");
            BroadcastPlayerModelState();
        }

        private void BroadcastPlayerModelState()
        {
            if (Role != CoopRole.Host)
                return;
            Broadcast(BuildPlayerModelState());
        }

        private PlayerModelStateMessage BuildPlayerModelState()
        {
            EnsureLocalPlayerModel();
            _playerModels[0] = ClonePlayerModel(_localPlayerModel);
            var state = new PlayerModelStateMessage();
            foreach (var pair in _playerModels)
            {
                var model = ClonePlayerModel(pair.Value);
                model.Id = pair.Key;
                state.Entries.Add(model);
            }
            return state;
        }

        private void ApplyPlayerModelState(PlayerModelStateMessage state)
        {
            if (Role != CoopRole.Client || state == null)
                return;
            foreach (var entry in state.Entries)
            {
                if (entry == null)
                    continue;
                if (entry.Id == _selfId)
                {
                    _localPlayerModel = ClonePlayerModel(entry);
                    _localPlayerModelReady = true;
                    _localModelAuto = false;
                    continue;
                }
                int avatarId = entry.Id == 0 ? 1 : 1000 + entry.Id;
                _avatars.SetModel(avatarId, entry.Female, entry.ModelIndex, entry.CustomizationJson);
            }
        }

        private static PlayerModelEntry ClonePlayerModel(PlayerModelEntry source)
        {
            if (source == null)
                return new PlayerModelEntry();
            return new PlayerModelEntry
            {
                Id = source.Id,
                Female = source.Female,
                ModelIndex = source.ModelIndex,
                CustomizationJson = source.CustomizationJson
            };
        }

        /// <summary>
        /// Ends the guest's load hold from the game's actual completion signal rather than
        /// from a guessed number of seconds.  FindObjectOfType is deliberate here: asking
        /// CSingleton&lt;ShelfManager&gt;.Instance during a scene transition can create a fake,
        /// empty manager and make the readiness check lie.
        /// </summary>
        private bool TryFinishClientReload()
        {
            if (!InGameLevel())
                return false;
            if (!_clientWorldArrived)
                return false;
            if (Time.frameCount <= _reloadStartedFrame || Time.realtimeSinceStartup - _reloadStartedAt < 0.25f)
                return false;

            var shelfManager = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            if (shelfManager == null || !shelfManager.m_FinishLoadingObjectData)
                return false;

            float elapsed = Time.realtimeSinceStartup - _reloadStartedAt;
            ClientReloading = false;
            _clientWorldArrived = false;
            CoopPlugin.Log.LogInfo($"Join world load completed in {elapsed:F2}s; resuming co-op sync");

            // The join-time authoritative snapshots are emitted during world transfer, before
            // this world exists, so ask the host to reconverge all state now that it can apply it.
            if (Role == CoopRole.Client && _net != null)
                Send(1, new JoinResyncRequestMessage());

            // A shop name that arrived while loading may have been painted onto a sign
            // that the reload then rebuilt. Re-stamp it now that the real world is ready.
            if (_shopSign != null && !string.IsNullOrEmpty(_lastShopNameApplied))
            {
                try
                {
                    _shopSign.text = _lastShopNameApplied;
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            return true;
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
            if (_inventory == null)
                _inventory = FindObjectOfType<InventoryBase>();
            return _inventory;
        }

        private void ModulesTick()
        {
            if (_moduleRegistry == null)
                return;
            var frame = new Sync.SyncFrame(_dt, InGameLevel(), ClientPreloadHold);
            if (Role == CoopRole.Host)
            {
                for (int i = 0; i < _hostTickOrder.Length; i++)
                    TickModule(_hostTickOrder[i], in frame);
            }
            else if (Role == CoopRole.Client)
            {
                // ORDER IS LOAD-BEARING: the market flush applies any snapshot that arrived
                // while the world loaded (after CGameData.PropagateLoadData swapped the card
                // tables in), then tv/trades/containers run, and the cosmetic box trajectories
                // plus the catalog/graded digests come last - exactly as before the migration.
                for (int i = 0; i < _clientTickOrder.Length; i++)
                    TickModule(_clientTickOrder[i], in frame);
                ClientDigestTick(in frame);
            }
        }

        /// <summary>Runs one pipeline entry with per-stage armor and allocation-free probing.
        /// Unlike the old Guarded("modules", ...) wrapper, one failure degrades a single
        /// subsystem instead of aborting the rest of the frame's pipeline.</summary>
        private void TickModule(Sync.TickEntry entry, in Sync.SyncFrame frame)
        {
            long t = Util.PerfProbe.Start();
            try
            {
                entry.Module.Tick(frame);
            }
            catch (Exception e)
            {
                Sync.ModuleGuard.Log(entry.Probe, e);
            }
            Util.PerfProbe.End(entry.Probe, t);
        }

        /// <summary>Client-only per-frame work that is not owned by a module: the
        /// periodically re-digested catalog and graded-album hashes.</summary>
        private void ClientDigestTick(in Sync.SyncFrame frame)
        {
            bool inGame = frame.InGame;
            float dt = frame.Dt;
            long t;

            // content mods register their products SECONDS after the scene loads
            // (and per-save: a host mid-tutorial has none yet) - keep re-digesting
            // as our catalog changes so the comparison never goes stale
            t = Util.PerfProbe.Start();
            _catalogTimer += dt;
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
            Util.PerfProbe.End("mod.catalogDigest", t);
            // ...and the graded-cert digest on the same gating for the same reason: the album
            // changes constantly (every pack opened, every card graded), so a one-shot send
            // would be stale within a minute. Built once and reused for both the hash test
            // and the send - the union walk is the expensive half, not the write.
            t = Util.PerfProbe.Start();
            _gradedTimer += dt;
            if (inGame && (_gradedTimer >= 45f || !_gradedSent))
            {
                var inv = Util.GradingInterop.BuildGradedCertInventory();
                // NULL IS NOT AN EMPTY ALBUM - it is "the world here is still spawning", and
                // sending a SHORT union is the one direction that hurts: the host would read
                // every graded card on our own shelves as one-sided and offer them back for
                // adoption. InGameLevel() cannot tell us apart from a loaded world (it stays
                // true through the client reload screen), so this is the gate. Do not mark
                // the send done; come back in a second rather than in 45.
                if (inv == null)
                {
                    _gradedTimer = 44f;
                }
                else
                {
                    _gradedTimer = 0f;
                    _gradedSent = true;
                    int gh = GradedHash(inv);
                    if (gh != _lastGradedHash)
                    {
                        _lastGradedHash = gh;
                        SendGradedDigest(1, inv);
                    }
                }
            }
            Util.PerfProbe.End("mod.gradedDigest", t);
        }

        /// <summary>The catalog is the single source of truth for lifecycle order, registry
        /// membership, tick pipelines, and static patch registration. This is the v1.1 reset
        /// order (with the unified box engine replacing the old box modules). Disposal walks it
        /// in reverse, so live hooks detach first; join-heal and live-hooks therefore remain
        /// last. Actual population/index dependencies remain explicit in the per-role slots.</summary>
        private Sync.CoopModuleEntry[] BuildModuleCatalog()
        {
            return new[]
            {
                new Sync.CoopModuleEntry(_world, "world"),
                new Sync.CoopModuleEntry(_npcs, "npcs"),
                new Sync.CoopModuleEntry(_cardShelves, "cardshelves"),
                new Sync.CoopModuleEntry(_objMoves, "objmoves"),
                new Sync.CoopModuleEntry(_movePreview, "movepreview"),
                new Sync.CoopModuleEntry(_boxEngine, "boxes"),
                new Sync.CoopModuleEntry(_population, "population"),
                new Sync.CoopModuleEntry(_grading, "grading", 0, -1, Sync.GradingSync.ApplyPatches),
                new Sync.CoopModuleEntry(_trades, "trades", 1, 2, Sync.TradeServe.ApplyPatches),
                new Sync.CoopModuleEntry(_tables, "tables", 2, -1, Sync.PlayTableSync.ApplyPatches),
                new Sync.CoopModuleEntry(_staff, "staff", 3, -1, Sync.StaffSync.ApplyPatches),
                new Sync.CoopModuleEntry(_shopState, "shopState", 4, -1, Sync.ShopStateSync.ApplyPatches),
                new Sync.CoopModuleEntry(_settings, "settings", 5, -1, Sync.SettingsSync.ApplyPatches),
                new Sync.CoopModuleEntry(_market, "market", 6, 0, Sync.MarketSync.ApplyPatches),
                new Sync.CoopModuleEntry(_report, "report", 7, -1, Sync.ReportSync.ApplyPatches),
                new Sync.CoopModuleEntry(_containers, "containers", 8, 3, Sync.ContainerSync.ApplyPatches),
                new Sync.CoopModuleEntry(_tournament, "tournament", 9, -1, Sync.TournamentSync.ApplyPatches),
                new Sync.CoopModuleEntry(_register, "register", 10, -1, Sync.RegisterSync.ApplyPatches),
                new Sync.CoopModuleEntry(_tv, "tv", 11, 1, Sync.TvSync.ApplyPatches),
                new Sync.CoopModuleEntry(new Sync.DelegateCoopModule("join-heal", null, null,
                    () => _priceFullPending = true), "join-heal"),
                new Sync.CoopModuleEntry(new Sync.DelegateCoopModule("live-hooks",
                    InstallLiveModuleHooks, null, null, ClearLiveModuleHooks), "live-hooks"),
                new Sync.CoopModuleEntry(null, "cardboxes", patches: Sync.CardBoxOps.ApplyPatches),
                new Sync.CoopModuleEntry(null, "furnboxes", patches: Sync.FurnitureBoxOps.ApplyPatches),
                new Sync.CoopModuleEntry(null, "hand-protection", patches: Sync.HandProtection.ApplyPatches),
            };
        }

        private Sync.CoopModuleRegistry CreateModuleRegistry()
        {
            return new Sync.CoopModuleRegistry(_allModules);
        }

        /// <summary>Builds the explicit per-role tick pipelines. The two roles need different
        /// orders, and several comments in the synchronizers depend on that order (market
        /// flush before the other client modules; the client digests last), so it is declared
        /// here rather than inferred from the lifecycle list.</summary>
        private void BuildTickOrders()
        {
            _hostTickOrder = BuildTickOrder(host: true);
            _clientTickOrder = BuildTickOrder(host: false);
        }

        private Sync.TickEntry[] BuildTickOrder(bool host)
        {
            var ordered = new List<Sync.TickEntry>();
            int maxSlot = -1;
            for (int i = 0; i < _moduleCatalog.Length; i++)
            {
                int slot = host ? _moduleCatalog[i].HostSlot : _moduleCatalog[i].ClientSlot;
                if (slot > maxSlot)
                    maxSlot = slot;
            }
            for (int slot = 0; slot <= maxSlot; slot++)
                for (int i = 0; i < _moduleCatalog.Length; i++)
                {
                    Sync.CoopModuleEntry entry = _moduleCatalog[i];
                    if ((host ? entry.HostSlot : entry.ClientSlot) == slot)
                        ordered.Add(new Sync.TickEntry((ITickableCoopModule)entry.Module,
                            "mod." + entry.Name));
                }
            return ordered.ToArray();
        }

        private void ResetAllModules()
        {
            for (int i = 0; i < _allModules.Length; i++)
                _allModules[i].ResetState();
        }

        private void ActivateLiveModuleHooks()
        {
            if (_moduleRegistry != null)
                throw new InvalidOperationException("Cannot activate co-op module hooks twice.");
            BuildTickOrders();
            _moduleRegistry = CreateModuleRegistry();
            try
            {
                _moduleRegistry.Start();
            }
            catch
            {
                // A module Start() fault would otherwise leave a half-started registry
                // installed and block every retry behind the double-activation guard.
                DeactivateLiveModuleHooks();
                throw;
            }
        }

        /// <summary>Tears the live registry down without touching transport or world state.
        /// Failed-start paths use this so a retry cannot inherit installed Harmony hooks or
        /// trip the double-activation guard.</summary>
        private void DeactivateLiveModuleHooks()
        {
            if (_moduleRegistry == null)
                return;
            _moduleRegistry.Dispose();
            _moduleRegistry = null;
        }

        /// <summary>Undoes the partial session setup a failed host/join start can leave
        /// behind: stops any transport, detaches the module hooks, and returns to the idle
        /// role so the player can retry without restarting.</summary>
        private void AbortSessionStart(string error)
        {
            ErrorLine = error;
            try
            {
                _net?.Stop();
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("transport stop during aborted session start: " + e.Message);
            }
            _net = null;
            DeactivateLiveModuleHooks();
            Role = CoopRole.None;
            _sessionInGame = false;
            _dispatchDeferredFrames = 0;
            _dispatchBuf.Clear();
            _dispatchRetryNextFrame.Clear();
            _dispatchHeldTransfers.Clear();
            IsSteamSession = false;
            GuestBorrowedWorld = false;
            HostPassword = "";
            _joinPassword = "";
        }

        private void InstallLiveModuleHooks()
        {
            PopulationSync.OnClientStructureChanged = kind =>
            {
                if (Role != CoopRole.Client)
                    return;
                // The client's placed-object roster just changed (a shelf was removed or
                // spawned). Every index-keyed mirror's baseline is now keyed against stale
                // indexes, so read the structure back fresh instead of diffing against
                // garbage. Repaired objects start with loader defaults; those defaults must
                // not be reported as guest edits through any index-based mirror.
                _world.InvalidateBaseline();
                _objMoves.Reset();
                if (kind == 2 || kind == 3 || kind == 14)
                    _cardShelves.InvalidateBaseline();
                // container stations (card storage, cleansers, pack openers, box storage,
                // donation boxes) and play tables are index-keyed too; their per-index
                // mirrors/caches go stale the moment a machine/table of that kind shifts.
                if (kind >= 9 && kind <= 13)
                {
                    _containers.Reset();
                    _containers.ForceResend();
                }
                if (kind == 6)
                {
                    _tables.Reset();
                    _tables.ForceResend();
                }
                // Population reconciliation removes/inserts list elements, so every
                // index-keyed mirror needs an immediate authoritative snapshot rather than
                // waiting for its normal heal interval.
                _boxEngine?.ForceNextTick();
                _register.ForceResend();
            };
            Util.GradingInterop.Reset();
            NpcSync.ActivateLive(_npcs);
            TradeServe.ActivateLive(_trades);
            RegisterSync.ActivateLive(_register);
            ContainerSync.ActivateLive(_containers);

            GradingSync.ActivateLive(_grading);
        }

        private static void ClearLiveModuleHooks()
        {
            HandEscrow.Reset();
            PopulationSync.OnClientStructureChanged = null;
            NpcSync.ClearLive();
            TradeServe.ClearLive();
            RegisterSync.ClearLive();
            BoxShared.ApplyingRemote = false;
            BoxPlacement.Reset();
            ContainerSync.ClearLive();

            GradingSync.ClearLive();
            Util.GradingInterop.Reset();
        }

        private void ModulesForceResend()
        {
            if (_moduleRegistry != null)
            {
                _moduleRegistry.ForceResend();
                return;
            }

            // Process-lifetime fallback (no live registry): mirror the registry's module set
            // so a joiner still gets a full authoritative baseline.
            for (int i = 0; i < _allModules.Length; i++)
                _allModules[i].ForceResend();
            _priceFullPending = true; // fresh joiner gets the authoritative price table
        }

        /// <summary>Host: give one freshly-joined connection whatever per-conn catch-up its
        /// modules need, after the broadcast baselines were armed.</summary>
        private void ModulesOnFullyJoin(int connId)
        {
            if (_moduleRegistry != null)
            {
                _moduleRegistry.OnFullyJoin(connId);
                return;
            }
            for (int i = 0; i < _allModules.Length; i++)
                _allModules[i].OnFullyJoin(connId);
        }

        internal void SendTvOp(TvOpMessage message)
        {
            if (message != null && Role == CoopRole.Client)
                Send(1, message);
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
                    // FIX E2: cache the 3D sign TMP BEFORE disabling - once the renamer
                    // GameObject is inactive, FindObjectOfType can't reach it again.
                    try
                    {
                        _shopSign = renamer.m_ShopName;
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    renamer.gameObject.SetActive(false);
                    CoopPlugin.Log.LogInfo("disabled shop-renamer trigger (host names the shop)");
                    // a ShopName may have already arrived while the renamer was still live
                    // (its listener overwrote the sign with the local default) - re-stamp
                    if (_shopSign != null && !string.IsNullOrEmpty(_lastShopNameApplied))
                    {
                        try
                        {
                            _shopSign.text = _lastShopNameApplied;
                        }
                        catch (System.Exception e) { Swallow.Log(e); }
                    }
                }
            }
            if (_cmSweep == null)
                _cmSweep = FindObjectOfType<CustomerManager>();
            if (_cmSweep != null)
            {
                var list = _cmSweep.GetCustomerList();
                for (int i = 0; i < list.Count; i++)
                {
                    var cust = list[i];
                    if (cust == null || Sync.RegisterSync.IsCarrier(cust)
                        || Sync.NpcSync.IsExistingCustomer(cust))
                        continue;
                    if (cust.gameObject.activeSelf)
                        cust.gameObject.SetActive(false);
                }
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
            if (playerTf == null)
                return;
            _stateKeepalive += _dt;
            if (_stateTimer < interval)
                return;
            Vector3 pos = playerTf.position;
            float yaw = _playerCamTf != null ? _playerCamTf.eulerAngles.y
                : (Camera.main != null ? Camera.main.transform.eulerAngles.y : playerTf.eulerAngles.y);
            Transform camera = _playerCamTf != null ? _playerCamTf : Camera.main != null ? Camera.main.transform : null;
            byte hold = ComputeHoldState();
            Vector3 cameraPos = camera != null ? camera.position : pos;
            Quaternion cameraRot = camera != null ? camera.rotation : Quaternion.Euler(0f, yaw, 0f);
            int holdSig = HoldPayloadSignature();

            // Change-gate: only send when pose/camera/hold actually moved, plus a slow
            // keepalive so a standing-still player still refreshes the far side.
            bool changed = !_hasSentState
                || (pos - _lastSentPos).sqrMagnitude > 0.0004f   // > 2 cm
                || Mathf.Abs(Mathf.DeltaAngle(yaw, _lastSentCamYaw)) > 1f
                || hold != _lastSentHold
                || (cameraPos - _lastSentCamPos).sqrMagnitude > 0.0004f
                || Quaternion.Angle(cameraRot, _lastSentCamRot) > 0.5f
                || holdSig != _lastSentHoldSig;
            if (!changed && _stateKeepalive < 5f)
            {
                _stateTimer = 0f; // consumed this interval
                return;
            }
            _stateKeepalive = 0f;

            float speed = 0f;
            if (_hasLastPos)
            {
                Vector3 delta = pos - _lastPos;
                delta.y = 0f;
                speed = Mathf.Clamp(delta.magnitude / Mathf.Max(_stateTimer, 0.0001f), 0f, 6f);
            }
            _lastPos = pos;
            _hasLastPos = true;
            _lastSentPos = pos;
            _lastSentCamYaw = yaw;
            _lastSentCamPos = cameraPos;
            _lastSentCamRot = cameraRot;
            _lastSentHold = hold;
            _lastSentHoldSig = holdSig;
            _hasSentState = true;
            BroadcastTransient(new PlayerStateMessage
            {
                Position = pos,
                Yaw = yaw,
                CameraPosition = cameraPos,
                CameraRotation = cameraRot,
                Speed = speed,
                Hold = hold,
                HoldTypes = hold == 3 ? null : new List<int>(_holdTypesBuf),
                HoldCards = hold == 3 ? new List<CardData>(_holdCardsBuf) : null
            });
            _diagSent++;
            _stateTimer = 0f; // full reset: the speed estimate divides by this elapsed time
        }

        private void NpcCollectTick()
        {
            var chunks = _npcs.HostCollect(_dt);
            if (chunks == null)
                return;
            for (int i = 0; i < chunks.Count; i++)
            {
                BroadcastTransient(chunks[i]);
            }
        }

        /// <summary>The game assigns neither CGameManager.Player nor
        /// InteractionPlayerController.m_Instance (both are dead statics), so find the
        /// player controller in the scene once and cache its transform. FindObjectOfType
        /// never auto-creates, unlike CSingleton&lt;T&gt;.Instance.</summary>
        private Transform _playerTf;   // the MOVING body: IPC.m_WalkerCtrl (CMF walker)
        private Transform _playerCamTf; // player camera, for look yaw
        private InteractionPlayerController _playerIpc;
        private Sync.BoxPushProbe _pushProbe; // detects the boxes the local player is pushing
        private bool _uiModeHeldByWindow;
        private InteractionPlayerController _uiModeController;

        /// <summary>InteractionPlayerController itself sits on a stationary manager object -
        /// its transform never moves (that was the frozen-avatar bug). The walking body is
        /// its public m_WalkerCtrl (the game's own teleport code moves the player by setting
        /// m_WalkerCtrl.transform.position), and look direction lives on m_Cam.</summary>
        private Transform ResolvePlayer()
        {
            if (_playerTf != null)
                return _playerTf;
            var ipc = InteractionPlayerController.m_Instance;
            if (ipc == null)
                ipc = FindObjectOfType<InteractionPlayerController>();
            if (ipc != null)
            {
                _playerIpc = ipc;
                _playerTf = ipc.m_WalkerCtrl != null ? ipc.m_WalkerCtrl.transform : ipc.transform;
                _playerCamTf = ipc.m_Cam != null ? ipc.m_Cam.transform : null;
                CoopPlugin.Log.LogInfo($"Player body resolved: {_playerTf.name} at {_playerTf.position}, cam={(_playerCamTf != null ? _playerCamTf.name : "none")}");
                AttachPushProbe();
            }
            return _playerTf;
        }

        /// <summary>
        /// Idempotently attach and initialize <see cref="Sync.BoxPushProbe"/> on the player
        /// walker's rigidbody, so the local player's own pushing feeds the box engine. Safe
        /// to call whenever a player is resolved; it no-ops once the probe exists, and the
        /// probe is surfaced to <see cref="BoxEngine"/> through the <c>LocalPushed</c>
        /// delegate wired in the constructor (which reads this field dynamically).
        /// </summary>
        private void AttachPushProbe()
        {
            if (_pushProbe != null)
                return;
            if (_playerIpc == null || _playerIpc.m_PlayerRigidbody == null)
            {
                CoopPlugin.Log.LogWarning("push probe NOT attached: "
                    + (_playerIpc == null ? "no InteractionPlayerController" : "m_PlayerRigidbody is null"));
                return;
            }
            _pushProbe = _playerIpc.m_PlayerRigidbody.GetComponent<Sync.BoxPushProbe>()
                ?? _playerIpc.m_PlayerRigidbody.gameObject.AddComponent<Sync.BoxPushProbe>();
            _pushProbe.Init(_playerIpc.m_PlayerRigidbody);
            CoopPlugin.Log.LogInfo($"push probe attached to {_playerIpc.m_PlayerRigidbody.gameObject.name} (kinematic={_playerIpc.m_PlayerRigidbody.isKinematic})");
        }

        /// <summary>True if the collider belongs to the LOCAL player's body. Box-side contact
        /// probes use this to tell a real player push from any other box collision.</summary>
        internal static bool IsLocalPlayerCollider(Collider collider)
        {
            var core = Instance;
            if (core == null || collider == null || core._playerIpc == null)
                return false;
            var playerCollider = core._playerIpc.m_PlayerCollider;
            if (playerCollider == null)
                return false;
            return collider == playerCollider
                || collider.transform.IsChildOf(playerCollider.transform)
                || playerCollider.transform.IsChildOf(collider.transform);
        }

        /// <summary>Make the co-op window modal while it is visible in a game level.
        /// Unity IMGUI handles its own controls, but the game reads world input from
        /// the legacy Input API, so IMGUI alone does not prevent a click from also
        /// interacting with the shop. The controller's own UI mode is the game's
        /// supported input gate.</summary>
        private void SyncWindowUIMode()
        {
            bool want = _ui.Visible && InGameLevel();

            if (want)
            {
                // A scene transition can destroy the controller while the window
                // remains visible. Do not retain ownership of the destroyed object;
                // this lets the replacement controller be gated when it appears.
                if (_uiModeHeldByWindow && _uiModeController != null)
                {
                    // Another game flow may have called ExitUIMode while the
                    // co-op window stayed open. Reassert our modal state, but do
                    // not call EnterUIMode every frame.
                    if (!_uiModeController.IsInUIMode())
                        _uiModeController.EnterUIMode();
                    return;
                }

                var ipc = InteractionPlayerController.m_Instance;
                if (ipc == null)
                    ipc = FindObjectOfType<InteractionPlayerController>();
                if (ipc == null)
                    return;

                // Preserve a modal state that belongs to the game; only undo UI
                // mode that this window entered.
                if (ipc.IsInUIMode())
                {
                    _uiModeHeldByWindow = false;
                    _uiModeController = null;
                    return;
                }

                CoopPlugin.Log.LogInfo("Co-op window opened: entering game UI mode to block gameplay input");
                ipc.EnterUIMode();
                _uiModeHeldByWindow = true;
                _uiModeController = ipc;
                return;
            }

            if (!_uiModeHeldByWindow)
            {
                _uiModeController = null;
                return;
            }

            if (_uiModeController != null)
            {
                CoopPlugin.Log.LogInfo("Co-op window closed: restoring gameplay input");
                _uiModeController.ExitUIMode();
            }
            _uiModeHeldByWindow = false;
            _uiModeController = null;
        }

        /// <summary>Client-side half of an acknowledged empty-box take. Reuse the game's own
        /// StartHoldBox recipe so the controller enters HoldingBoxState, the box physics are
        /// disabled, and the normal carry report reaches the host on the next box-engine tick.</summary>
        private static bool TryHoldClientBox(InteractablePackagingBox_Item box)
        {
            if (Role != CoopRole.Client || box == null)
                return false;
            var core = Instance;
            if (core == null)
                return false;
            try
            {
                if (core._playerIpc == null)
                    core.ResolvePlayer();
                var ipc = core._playerIpc;
                if (ipc == null || ipc.m_HoldItemPos == null)
                    return false;
                if (FiIsHoldBoxMode?.GetValue(ipc) is bool held && held)
                    return false;
                box.StartHoldBox(isPlayer: true, ipc.m_HoldItemPos);
                // CoopCore caches the controller's held-box fields for the current frame.
                // Invalidate that cache because this handoff changes those fields inside the
                // snapshot apply itself.
                core._heldBoxFrame = -1;
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("client empty-box hand-off: " + e.Message);
                return false;
            }
        }

        private static readonly FieldInfo FiHoldBox = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBox");
        private static readonly FieldInfo FiHoldItemBox = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingItemBox");
        private static readonly FieldInfo FiHoldBoxShelf = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBoxShelf");
        private static readonly FieldInfo FiHoldBoxCard = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBoxCard");
        private static readonly FieldInfo FiHoldItemList = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_HoldItemList");
        private static readonly FieldInfo FiIsHoldBoxMode = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_IsHoldBoxMode");

        /// <summary>A box the local player is actively holding is about to be destroyed by a
        /// reconcile. The game's InteractablePackagingBox.OnDestroyed does NOT exit hold-box
        /// mode (only Throw/Place/Store/Discard do), so m_IsHoldBoxMode + the HoldingBoxState
        /// would stay set forever pointing at a fake-null box - and Update() then ONLY
        /// dispatches RaycastHoldBoxState, so the player can't interact with anything, not even
        /// the trash (the guest soft-lock report). Call the game's own public exit first so the
        /// state clears cleanly. No-op unless the box really is one the player holds.</summary>
        public static void ForceExitHoldBox(UnityEngine.Object heldBox)
        {
            var self = Instance;
            var ipc = self != null ? self._playerIpc : null;
            if (ipc == null)
                return;
            try
            {
                var a = FiHoldItemBox?.GetValue(ipc);
                var b = FiHoldBox?.GetValue(ipc);
                var c = FiHoldBoxCard?.GetValue(ipc);
                if (!ReferenceEquals(a, heldBox) && !ReferenceEquals(b, heldBox) && !ReferenceEquals(c, heldBox))
                    return;
                ipc.OnExitHoldBoxMode();
                CoopPlugin.Log.LogInfo("ForceExitHoldBox: released hold-box mode for a box being retired by reconcile");
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ForceExitHoldBox: " + e.Message); }
        }

        /// <summary>Per-frame safety net: if the game is stuck in hold-box mode but the held
        /// box is gone (fake-null) - e.g. a player who hit the soft-lock before this fix -
        /// exit hold-box mode so they can interact again.</summary>
        private void RecoverStuckHoldBox()
        {
            if (_playerIpc == null)
                return;
            try
            {
                if (FiIsHoldBoxMode?.GetValue(_playerIpc) is bool held && held)
                {
                    var box = FiHoldBox?.GetValue(_playerIpc) as UnityEngine.Object;
                    var itemBox = FiHoldItemBox?.GetValue(_playerIpc) as UnityEngine.Object;
                    var cardBox = FiHoldBoxCard?.GetValue(_playerIpc) as UnityEngine.Object;
                    // Unity fake-null: a destroyed object == null. If hold-box mode is on but
                    // every candidate box is null/destroyed, nothing can ever clear it.
                    if (box == null && itemBox == null && cardBox == null)
                    {
                        _playerIpc.OnExitHoldBoxMode();
                        CoopPlugin.Log.LogInfo("RecoverStuckHoldBox: cleared a stranded hold-box lock (no live held box)");
                    }
                }
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        private static readonly MethodInfo MiRemoveHoldItem =
            HarmonyLib.AccessTools.Method(typeof(InteractionPlayerController), "RemoveHoldItem");

        /// <summary>Controller used by the local hand primitives. Keep this accessor here so
        /// sync policies do not need to know how the player controller is discovered.</summary>
        internal static InteractionPlayerController PlayerIpc
        {
            get
            {
                return Instance != null ? Instance._playerIpc : null;
            }
        }

        internal static List<Item> GetHeldItemList(InteractionPlayerController ipc)
        {
            return ipc != null ? FiHoldItemList?.GetValue(ipc) as List<Item> : null;
        }

        private static readonly FieldInfo FiHoldCardMode =
            HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_IsHoldCardMode");

        /// <summary>Whether vanilla is currently able to accept an item into the hand. Escrow
        /// must not race modal hold states: those states can consume or reposition the item.</summary>
        internal static bool HandAcceptsItems()
        {
            var ipc = PlayerIpc;
            if (ipc == null)
                return false;
            if (FiIsHoldBoxMode?.GetValue(ipc) is bool boxMode && boxMode)
                return false;
            if (FiHoldCardMode?.GetValue(ipc) is bool cardMode && cardMode)
                return false;
            if (FiViewAlbum?.GetValue(ipc) is bool albumMode && albumMode)
                return false;
            return true;
        }

        /// <summary>Remove one item from the local hand through the game's own path, keeping
        /// the hand and its parallel type list in sync. Returns false when the item is still
        /// present afterwards (a suppressed RemoveHoldItem prefix), so callers never destroy
        /// an object that is still in the hand.</summary>
        internal static bool RemoveHeldItemFromHand(Item item)
        {
            var ipc = PlayerIpc;
            if (item == null || ipc == null || !(FiHoldItemList?.GetValue(ipc) is List<Item> items))
                return false;
            RemoveHeldItemAt(ipc, items, item);
            if (items.IndexOf(item) >= 0)
            {
                CoopPlugin.Log.LogError("RemoveHeldItemFromHand: RemoveHoldItem was suppressed or failed; item remains in hand during rollback");
                return false;
            }
            return true;
        }

        /// <summary>Attach a returned item through the game's own hand path. The explicit
        /// activation is required because AddHoldItemToFront only updates hand bookkeeping.</summary>
        internal static bool TryAttachHeldItem(Item item)
        {
            if (item == null || !HandAcceptsItems())
                return false;
            var ipc = PlayerIpc;
            if (ipc == null || ipc.GetHoldItemCount() >= HandProtection.HandCapacity)
                return false;
            item.gameObject.SetActive(true);
            ipc.AddHoldItemToFront(item);
            return true;
        }

        internal static void DestroyDetachedItem(Item item)
        {
            if (item == null)
                return;
            try
            {
                item.DisableItem();
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogError("DestroyDetachedItem: DisableItem failed; phantom item may remain: " + e);
            }
        }

        /// <summary>Remove one held item using the game's own RemoveHoldItem, so the hold state,
        /// game state, and the remaining items' HUD positions stay correct. Vanilla always
        /// removes the FRONT type entry from the parallel type list, so move the target to the
        /// front in both lists first.</summary>
        private static void RemoveHeldItemAt(InteractionPlayerController ipc, List<Item> items, Item item)
        {
            int index = items.IndexOf(item);
            if (index < 0)
                return;
            var types = CPlayerData.m_HoldItemTypeList;
            if (index != 0)
            {
                var front = items[0];
                items[0] = item;
                items[index] = front;
                if (types != null && types.Count > index)
                {
                    var frontType = types[0];
                    types[0] = types[index];
                    types[index] = frontType;
                }
            }
            if (MiRemoveHoldItem != null)
            {
                try
                {
                    MiRemoveHoldItem.Invoke(ipc, new object[] { item });
                    if (items.IndexOf(item) < 0)
                        return;
                }
                catch (System.Exception e)
                {
                    CoopPlugin.Log.LogWarning("RemoveHoldItem failed, removing manually: " + e.Message);
                }
            }
            int idx = items.IndexOf(item);
            if (idx >= 0)
            {
                items.RemoveAt(idx);
                if (types != null && idx < types.Count)
                    types.RemoveAt(idx);
            }
        }


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
            if (_playerIpc == null)
                return 0;
            try
            {
                // The generic field is the only authoritative hold marker. The game does
                // not clear the type-specific shelf/card fields when Q enters move-box
                // mode, so consulting them creates a duplicate held visual.
                if (IsAlive(FiHoldBox))
                {
                    // describe the box (size + contents) so the avatar shows the real thing
                    if (FiHoldItemBox?.GetValue(_playerIpc) is InteractablePackagingBox_Item ib && ib != null)
                    {
                        _holdTypesBuf.Add(ib.m_IsBigBox ? 1 : 0);
                        try
                        {
                            _holdTypesBuf.Add((int)ib.m_ItemCompartment.GetItemType());
                        }
                        catch { _holdTypesBuf.Add(0); }
                    }
                    return 1; // carrying a box
                }
                if (FiHoldItemList?.GetValue(_playerIpc) is List<Item> items && items.Count > 0)
                {
                    for (int i = 0; i < items.Count && _holdTypesBuf.Count < 6; i++)
                        if (items[i] != null)
                            _holdTypesBuf.Add((int)items[i].GetItemType());
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
                    if (_holdCardsBuf.Count > 0)
                        return 3; // loose cards fanned in hand
                }
                if (FiViewAlbum?.GetValue(_playerIpc) is bool album && album)
                    return 4; // reading the collection binder
            }
            catch (System.Exception e) { Swallow.Log(e); }
            return 0;
        }

        private int HoldPayloadSignature()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + _holdTypesBuf.Count;
                for (int i = 0; i < _holdTypesBuf.Count; i++)
                    hash = hash * 31 + _holdTypesBuf[i];
                hash = hash * 31 + _holdCardsBuf.Count;
                for (int i = 0; i < _holdCardsBuf.Count; i++)
                {
                    CardData card = _holdCardsBuf[i];
                    if (card == null)
                    {
                        hash = hash * 31;
                        continue;
                    }
                    hash = hash * 31 + (int)card.monsterType;
                    hash = hash * 31 + (int)card.expansionType;
                    hash = hash * 31 + (card.isFoil ? 1 : 0);
                    hash = hash * 31 + card.cardGrade;
                    hash = hash * 31 + card.gradedCardIndex;
                }
                return hash;
            }
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
            if (Role != CoopRole.None)
            {
                ErrorLine = "Already in a session.";
                return;
            }
            if (!InGameLevel())
            {
                ErrorLine = "Load your shop first, then host.";
                return;
            }
            // a HOST must never translate: drop any table a previous session left behind
            Util.EnumMap.Clear();
            try
            {
                var tcp = new Transport { KeepaliveMessage = new PingMessage() };
                tcp.StartHost(CoopPlugin.Port.Value);
                _net = LagTransport.Wrap(tcp);
                Role = CoopRole.Host;
                ActivateLiveModuleHooks();
                StatusLine = "Hosting - waiting for a player...";
                CoopPlugin.Log.LogInfo($"Hosting on port {CoopPlugin.Port.Value}");
                // THE PORT AND THE PASSWORD ARE ONE DECISION, so this sits here rather than in
                // the UI: LAN hosting may be about to ask the router to open this port to the
                // whole internet (AutoPortForward), and an open port with no password is an
                // open door into the player's game and save. Generated BEFORE
                // BeginInviteResolve, which snapshots HostPassword into the invite code - get
                // this order wrong and the code carries "" while the Hello gate demands the
                // password, i.e. a code that cannot join its own host.
                if (CoopPlugin.AutoLanPassword.Value && string.IsNullOrEmpty(HostPassword))
                    HostPassword = GenerateSessionPassword();
                BeginInviteResolve();
            }
            catch (Exception e)
            {
                // Tear the registry down too: leaving it installed would keep the static
                // Harmony hooks live and make the next StartHosting attempt throw the
                // double-activation guard, permanently wedging hosting until a restart.
                AbortSessionStart("Could not host: " + e.Message);
            }
        }

        /// <summary>Eight Crockford base32 characters from the crypto RNG - 40 bits, which is
        /// far beyond anything guessable through a TCP handshake, and short enough to read out
        /// loud. Same alphabet as the invite code on purpose: this string is meant to be spoken
        /// over voice chat when a friend types the IP by hand, and dropping I/L/O/U is exactly
        /// what makes that survivable. 32 divides 256, so the byte-to-symbol reduction below is
        /// unbiased.</summary>
        private static string GenerateSessionPassword()
        {
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            var bytes = new byte[8];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                chars[i] = alphabet[bytes[i] & 31];
            return new string(chars);
        }

        /// <summary>LAN hosting just started: work out the one string a friend can paste, on a
        /// worker thread, and (unless the player turned it off) ask the router to open the port
        /// while we're at it.
        ///
        /// EVERY LINE OF THIS RUNS OFF THE MAIN THREAD, and it must: SSDP discovery alone is a
        /// 3-second blocking wait, the router's HTTP can take another 4, and STUN 2 more. On
        /// the main thread that is a nine-second freeze of the whole GAME at the exact moment
        /// the player pressed Host. Hosting itself is already live before this starts - the
        /// invite code is a convenience layered on top, and nothing in here can fail in a way
        /// that touches the session.
        ///
        /// SEQUENCE: router mapping (optional) -> the router's own external address -> STUN as
        /// the fallback -> compose. A router that reports a PRIVATE external address is on
        /// carrier-grade NAT, and no amount of port forwarding at this end will make it
        /// reachable; that case is called out by name instead of being papered over with the
        /// carrier's address, which would produce a code that silently never connects.</summary>
        private void BeginInviteResolve()
        {
            InviteCodeText = null;
            InviteReason = null;
            PortForwardState = 0;
            InviteStatus = InviteState.Resolving;

            int port = CoopPlugin.Port.Value;
            string password = HostPassword ?? "";
            bool forward = CoopPlugin.AutoPortForward.Value;
            int gen = Interlocked.Increment(ref _inviteGen);

            new Thread(() =>
            {
                string lan = null, publicIp = null, reason = null;
                try
                {
                    NetHelpers.BeginDiscovery(); // one SSDP sweep per run, not one per call

                    if (forward)
                    {
                        bool mapped = NetHelpers.TryMapPort(port, out string why, out long epoch);
                        if (!mapped)
                            CoopPlugin.Log.LogInfo("automatic port forwarding did not happen: " + (why ?? "unknown"));

                        // ORPHAN GUARD. The gen check at the BOTTOM of this worker stops a dead
                        // session's code from being published, but a mapping is not a field we
                        // can decline to publish - it is a hole already open in the router. If
                        // the session ended while TryMapPort was blocking (host stopped, or
                        // stopped and re-hosted), nothing downstream would ever remove it:
                        // Shutdown's own unmap ran BEFORE this mapping existed. Close it here,
                        // on this thread, and stop - blocking is what worker threads are for.
                        //
                        // BY EPOCH, not just by port. A stop-and-re-host maps the SAME port
                        // again, and this worker can be finishing while the new session's
                        // worker is already inside NetHelpers; a plain "remove the mapping for
                        // port 27886" would then delete the LIVE session's forward and leave it
                        // reporting success. The epoch names the mapping THIS call made, and
                        // NetHelpers no-ops when a newer one has replaced it.
                        if (Volatile.Read(ref _inviteGen) != gen)
                        {
                            if (mapped)
                            {
                                CoopPlugin.Log.LogInfo("port mapping completed after its session ended - removing it again");
                                NetHelpers.RemoveMapping(epoch);
                            }
                            return;
                        }
                        Publish(gen, () => PortForwardState = mapped ? 1 : 2);

                        string ext = NetHelpers.UpnpExternalIp();
                        if (ext != null && !NetHelpers.IsPublicIPv4(ext))
                            reason = $"your router's own internet address ({ext}) is a private one - your line is behind carrier-grade NAT, so no port forward at this end can reach you";
                        else
                            publicIp = ext;
                    }

                    if (publicIp == null && reason == null)
                    {
                        publicIp = NetHelpers.StunPublicIp();
                        if (publicIp != null && !NetHelpers.IsPublicIPv4(publicIp))
                            publicIp = null;
                        if (publicIp == null)
                            reason = "couldn't reach the internet resolver";
                    }

                    // THE LAN FALLBACK ADDRESS, AND WHY IT IS COMPUTED HERE rather than at the
                    // top of the worker. Once a gateway has been discovered we know which
                    // adapter actually talks to it, and that is the address the code must
                    // carry: LocalIPv4() only RANKS adapters by prefix, so a VirtualBox, VPN or
                    // Hyper-V adapter on 192.168.x can outrank the real LAN card - and then the
                    // code names one adapter while the port mapping points at another, which
                    // cannot work from either side of the router. No gateway (auto-forwarding
                    // off, or nothing answered) means there is nothing to be route-correct
                    // about, and the ranking is the best guess there is.
                    lan = NetHelpers.LocalIPv4ForGateway() ?? NetHelpers.LocalIPv4();
                }
                catch (Exception e)
                {
                    // Belt and braces - every helper already swallows its own failures, so
                    // landing here means something genuinely unexpected. The session is
                    // untouched either way; we just fall back to the LAN code.
                    reason = e.Message;
                    CoopPlugin.Log.LogWarning("invite code resolve failed: " + e.GetType().Name + ": " + e.Message);
                    // ...and the LAN address is computed inside the try now, so a throw above
                    // would otherwise take the LAN-only code down with it.
                    if (lan == null)
                    {
                        try
                        {
                            lan = NetHelpers.LocalIPv4();
                        }
                        catch (System.Exception ex) { Swallow.Log(ex); }
                    }
                }

                string chosen = publicIp ?? lan;
                string code = chosen != null ? InviteCode.Encode(chosen, port, password) : null;
                if (code == null && reason == null)
                    reason = "this PC has no usable network address";

                string finalReason = reason;
                bool ready = publicIp != null && code != null;
                Publish(gen, () =>
                {
                    InviteCodeText = code;
                    InviteReason = finalReason;
                    InviteStatus = ready ? InviteState.Ready : InviteState.LanOnly;
                });
                CoopPlugin.Log.LogInfo(publicIp != null
                    ? "invite code ready (internet address)"
                    : "invite code is LAN-only: " + (reason ?? "no public address"));
            })
            {
                IsBackground = true,
                Name = "CoopInvite"
            }.Start();
        }

        /// <summary>Hands one invite-code field write back to the MAIN THREAD, and drops it if
        /// the session it belongs to has ended in the meantime.
        ///
        /// The fields are still volatile, and the UI still latches them once per Layout pass -
        /// this is the third leg of the same stool and the only one that removes the race
        /// rather than tolerating it. Worker-side publishing had two problems: OnGUI could see
        /// InviteCodeText and InviteStatus from DIFFERENT instants (a code with a status that
        /// disagrees), and the gen check could pass a nanosecond before Shutdown bumped it,
        /// resurrecting a dead session's code. Both disappear when the write happens on the
        /// same thread as Shutdown and the UI: the check and the assignment are then in the
        /// same single-threaded order as everything else.</summary>
        private void Publish(int gen, Action write)
        {
            QueueMainThread("invite-publish", () =>
            {
                if (Volatile.Read(ref _inviteGen) != gen)
                {
                    CoopPlugin.Log.LogInfo("invite code resolve finished after its session ended - discarded");
                    return;
                }
                write();
            }, false);
        }

        public void Join(string ip)
        {
            Join(ip, CoopPlugin.Port.Value, "");
        }

        /// <summary>The LAN join every path ends up in. The port and password arguments exist
        /// for the invite code, which carries a host's ACTUAL port rather than assuming both
        /// PCs left the config alone.</summary>
        public void Join(string ip, int joinPort, string password)
        {
            ErrorLine = "";
            if (Role != CoopRole.None)
            {
                ErrorLine = "Already in a session.";
                return;
            }
            if (InGameLevel())
            {
                ErrorLine = "Join from the main menu (Title screen).";
                return;
            }
            ip = (ip ?? "").Trim();
            if (ip.Length == 0)
            {
                ErrorLine = "Enter the host's IP address.";
                return;
            }
            // FIX C: same restart gate as JoinSteam - a HOST's registry was installed over ours,
            // but this process still has the OLD one loaded, so a join can only end in another
            // rejection until the game is restarted. (A restore does NOT gate here.)
            if (Util.ModParity.RestartRequiredForJoin)
            {
                ErrorLine = "the host's card database was installed on this PC - RESTART the game before joining";
                return;
            }

            CoopPlugin.LastJoinIP.Value = ip;
            Role = CoopRole.Client;
            try
            {
                ActivateLiveModuleHooks();
                GuestBorrowedWorld = true; // block ALL saves until we're back at the title screen
                // Sent in our Hello; empty for a plain "Join LAN", non-empty only when an invite
                // code carried the host's lobby password.
                _joinPassword = password ?? "";
                StatusLine = "Connecting to " + ip + "...";
                var net = new Transport { KeepaliveMessage = new PingMessage() };
                _net = LagTransport.Wrap(net);
                // A code from a host on a non-default port has to win over our own config; a
                // nonsense value falls back rather than throwing at the socket.
                int port = (joinPort > 0 && joinPort <= 65535) ? joinPort : CoopPlugin.Port.Value;
                new Thread(() =>
                {
                    try
                    {
                        net.StartClient(ip, port);
                        QueueMainThread("connect-established", () =>
                        {
                            StatusLine = "Connected - requesting world...";
                            SendHello();
                        }, true);
                    }
                    catch (Exception e)
                    {
                        QueueMainThread("connect-failed", () =>
                        {
                            ErrorLine = "Could not connect: " + e.Message;
                            Shutdown(null);
                        }, false);
                    }
                })
                {
                    IsBackground = true,
                    Name = "CoopConnect"
                }.Start();
            }
            catch (Exception e)
            {
                AbortSessionStart("Could not connect: " + e.Message);
            }
        }

        public void Disconnect()
        {
            Shutdown("disconnected");
        }

        public void SendEmote()
        {
            if (_net != null && Role != CoopRole.None)
                Broadcast(new EmoteMessage { Emote = 1 });
        }

        /// <summary>Client: route a locally-earned gain/spend to the host's real economy.
        /// kinds: 1 AddCoin, 2 ReduceCoin, 3 AddShopExp, 4 AddFame.</summary>
        public void ForwardContribution(byte kind, float value)
        {
            if (Role != CoopRole.Client || _net == null)
                return;
            Send(1, new EconContributionMessage { Kind = kind, Value = value });
        }

        /// <summary>Host: broadcast a delta after vanilla has accepted it and updated the
        /// host HUD. Clients use the same value to drive their vanilla HUD queues.</summary>
        public void BroadcastEconDelta(byte kind, float value)
        {
            if (Role != CoopRole.Host || _net == null)
                return;
            Broadcast(new EconDeltaMessage { Kind = kind, Value = value });
        }

        internal void SendMovePreview(MovePreviewMessage message)
        {
            if (_net == null || Role == CoopRole.None)
                return;
            if (Role == CoopRole.Host)
                _net.BroadcastTransient(message);
            else
                _net.SendTransient(1, message);
        }

        internal void BeginMovePreview(InteractableObject obj)
        {
            _movePreview.BeginLocal(obj);
        }

        internal void UpdateMovePreviewValidity(bool valid)
        {
            _movePreview.UpdateLocalValidity(valid);
        }

        internal void EndMovePreview()
        {
            _movePreview.EndLocal();
        }

        /// <summary>Client: replay a handheld-deodorant hold-spray against the HOST'S real
        /// customers. On the guest the sprayed customers are collider-less render puppets, so
        /// the vanilla RaycastHoldSprayState loop (InteractionPlayerController ~1628-1631)
        /// hits only inert local copies and the spray does nothing. We forward the spray
        /// origin + range + potency so the host runs the exact same DeodorantSprayCheck against
        /// its authoritative customers; the smelly-flag change then mirrors back to every guest
        /// through NpcSync automatically. No-op unless we're the guest.
        /// CALL CONTRACT: the GamePatches hold-spray hook (another file) invokes this once per
        /// vanilla spray tick with the SAME arguments the vanilla body passes -
        /// m_CurrentHoldSprayItem.transform.position, range 2.5f, potency 1.</summary>
        public void ForwardSprayHit(Vector3 pos, float range, int potency)
        {
            if (Role != CoopRole.Client || _net == null)
                return;
            Send(1, new SprayHitMessage { Position = pos, Range = range, Potency = potency });
        }

        public void SendDecorationPlacement(EDecoObject type, Vector3 pos, Quaternion rot)
        {
            if (Role != CoopRole.Client || _net == null)
                return;
            Send(1, new SettingsOpMessage { Op = 8, DecoType = type, Position = pos, Rotation = rot });
        }

        public void SendDecorationRemoval(int objectKey)
        {
            if (Role != CoopRole.Client || _net == null)
                return;
            Send(1, new SettingsOpMessage { Op = 9, ObjectKey = objectKey });
        }

        /// <summary>Single marshalling point for transfer workers. Unity/game APIs must only be
        /// touched by actions drained from Update.</summary>
        public void ForwardNpcSpeech(ushort index, int identity, string text, float offsetUp)
        {
            if (Role != CoopRole.Host || _net == null || string.IsNullOrEmpty(text))
                return;
            Broadcast(new NpcSpeechMessage
            {
                Kind = 0,
                Index = index,
                Identity = identity,
                Text = text,
                OffsetUp = offsetUp,
            });
        }

        /// <summary>Single marshalling point for the host-side green play-table money popup.</summary>
        public void ForwardNpcMoneyPopup(ushort index, int identity, float amount, float offsetUp)
        {
            if (Role != CoopRole.Host || _net == null || amount <= 0f)
                return;
            Broadcast(new NpcMoneyPopupMessage
            {
                Index = index,
                Identity = identity,
                Amount = amount,
                OffsetUp = offsetUp,
            });
        }

        /// <summary>Both roles: mirror a collection change (pack pull, trash, sale) to the
        /// other side so there is one shared binder.</summary>
        public void ForwardCardDelta(CardData card, int amount, bool isAdd)
        {
            if (Role == CoopRole.None || _net == null || card == null || amount <= 0)
                return;
            // QUEUED, not sent: a bulk "collect all machines" fires hundreds of these in one
            // frame and one reliable frame each overran the send lane. FlushCardDeltaOutbox
            // ships them as CardDeltaBatch at the end of the frame - or right now, if anything
            // else tries to send first (see the send helpers). The card is SNAPSHOT because
            // the postfix restores the live object's encoded grade the moment we return.
            _cardDeltaOutbox.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = SnapshotCard(card) });
        }

        /// <summary>Host: send a card delta to ONE peer instead of broadcasting - for when
        /// only the sender's local state needs repair (e.g. a rejected re-grade submission,
        /// where the submitting guest alone removed the graded card from its album and no
        /// other peer's collection changed).</summary>
        public void SendCardDeltaTo(int connId, CardData card, int amount, bool isAdd)
        {
            if (Role != CoopRole.Host || _net == null || card == null || amount <= 0)
                return;
            Send(connId, new CardDeltaMessage { IsAdd = isAdd, Amount = amount, Card = card });
        }

        /// <summary>Both roles: mirror a GRADED-card removal (trade-in, donation, re-grade).
        /// Graded cards live in a separate album ReduceCard/CardDelta never touch, so this
        /// is its own message; the receiver applies RemoveGradedCard by identity.</summary>
        public void ForwardGradedRemoval(CardData card)
        {
            if (Role == CoopRole.None || _net == null || card == null || card.cardGrade <= 0)
                return;
            Broadcast(new GradedRemoveMessage { Card = card });
        }

        /// <summary>Either side bought a product license: share it by identity.</summary>
        public void ForwardLicense(int restockIndex)
        {
            if (Role == CoopRole.None || _net == null)
                return;
            RestockData rd = null;
            try
            {
                rd = InventoryBase.GetRestockData(restockIndex);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            if (rd == null)
                return;
            _lastLicenseBuyTime = UnityEngine.Time.realtimeSinceStartupAsDouble;
            int itemType = (int)rd.itemType;
            bool isBig = rd.isBigBox;
            string rdName = rd.name ?? "";
            // rdName is the durable identity (ApplyLicenseUnlock falls back to it), but the id
            // is tried FIRST, so an untranslated modded id from a permuted registry unlocks the
            // wrong product on the peer. Host-side these writes are the identity function.
            var license = new LicenseUnlockMessage { ItemType = (EItemType)itemType, IsBig = isBig, RestockName = rdName };
            if (Role == CoopRole.Host)
                Broadcast(license);
            else
                Send(1, license);
        }

        private bool ApplyLicenseUnlock(int itemType, bool isBig, string name)
        {
            int idx = ResolveRestockIndex(itemType, isBig, name, out _);
            if (idx < 0)
            {
                CoopPlugin.Log.LogWarning($"license unlock: no local product for type {itemType} big={isBig} '{name}'");
                return false;
            }
            if (CPlayerData.GetIsItemLicenseUnlocked(idx))
                return true; // already ours
            Patches.GamePatches.ApplyingRemoteLicense = true;
            try
            {
                CPlayerData.SetUnlockItemLicense(idx);
                // the vanilla purchase's non-UI side effects: achievements, the global
                // flag, and the TUTORIAL TASK credit - without the last one the host's
                // "Unlock Basic Card Box" task never cleared when the joiner bought it
                try
                {
                    AchievementManager.OnItemLicenseUnlocked((EItemType)itemType);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                try
                {
                    GameInstance.m_IsItemLicenseUnlocked = true;
                }
                catch (System.Exception e) { Swallow.Log(e); }
                try
                {
                    if ((EItemType)itemType == EItemType.BasicCardBox)
                        TutorialManager.AddTaskValue(ETutorialTaskCondition.UnlockBasicCardBox, 1f);
                }
                catch (System.Exception e) { Swallow.Log(e); }
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
                    if (!(FiPanelIndex?.GetValue(p) is int idx) || idx < 0)
                        continue;
                    // no raw-list bounds check: modded panels carry VIRTUAL indexes
                    // beyond the raw flag list; the game's accessor handles them
                    bool on = false;
                    try
                    {
                        on = CPlayerData.GetIsItemLicenseUnlocked(idx);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    if (!on)
                        continue;
                    (FiPanelLicGrp?.GetValue(p) as GameObject)?.SetActive(false);
                    (FiPanelUIGrp?.GetValue(p) as GameObject)?.SetActive(true);
                }
            }
            catch (System.Exception e) { Swallow.Log(e); }
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

        private string ResolvePeerName(ulong steamId, string wireName)
        {
            string fallback = string.IsNullOrWhiteSpace(wireName) ? "Player" : wireName;
            if (steamId == 0 || _steam == null)
                return fallback;
            string nickname = _steam.FriendNickname(steamId);
            return string.IsNullOrWhiteSpace(nickname) ? fallback : nickname;
        }

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
                    if (rd != null && !string.IsNullOrEmpty(rd.name))
                        entries.Add(rd);
                }
                var digest = new CatalogDigestMessage();
                int cnt = Mathf.Min(entries.Count, ushort.MaxValue);
                for (int i = 0; i < cnt; i++)
                {
                    // The name hash rides along, but CatalogKey mixes the ID INTO the same
                    // key rather than falling back to the name, so an untranslated modded id
                    // makes an identical product read as "differs". That is exactly the false
                    // report this diagnostic produced in the field (a constant +6 offset on
                    // four of five "conflicts"), so the id is translated like any other.
                    digest.Entries.Add(new CatalogDigestEntry
                    {
                        ItemType = entries[i].itemType,
                        IsBigBox = entries[i].isBigBox,
                        NameHash = Fnv(entries[i].name ?? ""),
                    });
                }
                Send(1, digest);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("catalog digest: " + e.Message); }
        }

        private void CompareCatalogs(CatalogDigestMessage message, int connId)
        {
            var joiner = new HashSet<long>();
            for (int i = 0; i < message.Entries.Count; i++)
            {
                var e = message.Entries[i];
                // Host side, so this is the identity function; the joiner already translated.
                // A product the host does not have arrives as None and simply fails to match
                // any host row - which is the truth this diagnostic is trying to report.
                int t = (int)e.ItemType;
                bool big = e.IsBigBox;
                int nameHash = e.NameHash;
                joiner.Add(CatalogKey(t, big, nameHash));
            }
            if (Inv() == null)
                return; // no live world to compare against; the next digest retries
            int total = CatalogCount(); // vanilla + EPL virtual entries
            int hostOnly = 0, shared = 0;
            var examples = new List<string>();
            for (int i = 0; i < total; i++)
            {
                var rd = CatalogAt(i);
                if (rd == null || string.IsNullOrEmpty(rd.name))
                    continue; // placeholder rows, see SendCatalogDigest
                if (joiner.Contains(CatalogKey((int)rd.itemType, rd.isBigBox, Fnv(rd.name))))
                {
                    shared++;
                    continue;
                }
                hostOnly++;
                if (examples.Count < 6)
                    examples.Add(rd.name);
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
                    Send(connId, new ToastMessage { Text = clear });
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
            Send(connId, new ToastMessage { Text = summary });
        }

        // ---- graded-album divergence report (report-only) + opt-in one-way adopt ----
        //
        // Modelled line for line on the catalog digest above, for the same reason it exists: two
        // peers can pass every handshake and still hold different graded cards, and until now the
        // only trace was a "graded remove ... not in this album" warning that named one card at a
        // time and never said how deep the difference went.
        //
        // NOTHING HERE CHANGES A CARD BY ITSELF. The digest reports; the adopt button is a human
        // pressing a button, one-way, add-only. That is deliberate and the two rejected
        // alternatives are worth naming: auto-adopting the HOST's superset would have DELETED
        // roughly eight graded cards from the guest in the field case that prompted this (the host
        // was the deficient side), and a two-way merge hand-feeds GO's duplicate-cert anti-cheat -
        // put two different cards carrying one cert on one machine and BOTH get flagged FAKE, so a
        // merge converts real cards into fakes and is strictly worse than doing nothing.

        private static string FileStamp(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return "-";
                var bytes = File.ReadAllBytes(path);
                unchecked
                {
                    ulong h = 14695981039346656037UL;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        h ^= bytes[i];
                        h *= 1099511628211UL;
                    }
                    return bytes.Length + "#" + h.ToString("x16");
                }
            }
            catch (System.Exception e) { Swallow.Log(e); return "?"; }
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
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619;
                }
                return (int)h;
            }
        }

        /// <summary>Client: the joiner set an item price - the host's table is authoritative.</summary>
        public void ForwardItemPrice(EItemType itemType, float price)
        {
            if (Role != CoopRole.Client || _net == null)
                return;
            Send(1, new ItemPriceContribMessage { ItemType = itemType, Price = price });
            // Stamp it: the host's next PriceList was built BEFORE this contribution landed,
            // and applying that full table would visibly repaint our fresh price back.
            _myItemPriceEdits[(int)itemType] = new MyItemPrice { Value = price, At = Time.realtimeSinceStartupAsDouble };
            TrimMyItemPriceEdits();
        }

        /// <summary>Both roles: mirror a marked-card-price change.</summary>
        public void ForwardCardPrice(CardData card, float price)
        {
            if (Role == CoopRole.None || _net == null || card == null)
                return;
            Broadcast(new CardPriceSetMessage { Card = card, Price = price });
            if (Role == CoopRole.Host)
                _cardPriceHealDirty = true;
            // GUEST convergence. One fire-and-forget frame was all a card price ever got, and a
            // reliable frame the transport drops (the CardDelta flood) is gone for good - the
            // host's 3s price heal then "repaired" the card by broadcasting its own STALE value
            // back over the edit. Remember what we asked for, retry until the host confirms it,
            // and ignore any different value for this card until then (see the CardPriceSet
            // handler). The host needs none of this: its own write IS the authority, and
            // tracking it there would make the host permanently ignore guest edits.
            if (Role != CoopRole.Client)
                return;
            string key = CardPriceKey(card);
            if (key == null)
                return;
            _myCardPrices[key] = new MyCardPrice
            {
                Card = SnapshotCard(card), // the postfix restores the live object's encoded grade
                Value = price,
                Acked = false,
                LastSend = Time.realtimeSinceStartupAsDouble,
                Attempts = 1,
            };
            TrimMyCardPrices();
        }

        /// <summary>Cap the in-flight card-price table: the oldest ACKED entry goes first (it
        /// has nothing left to converge); only if nothing is acked do we drop an entry that is
        /// still trying.</summary>
        private void TrimMyCardPrices()
        {
            while (_myCardPrices.Count > MyCardPriceMax)
            {
                string victim = null;
                double oldest = double.MaxValue;
                foreach (var kv in _myCardPrices)
                    if (kv.Value.Acked && kv.Value.LastSend < oldest)
                    {
                        oldest = kv.Value.LastSend;
                        victim = kv.Key;
                    }
                if (victim == null)
                    foreach (var kv in _myCardPrices)
                        if (kv.Value.LastSend < oldest)
                        {
                            oldest = kv.Value.LastSend;
                            victim = kv.Key;
                        }
                if (victim == null)
                    break;
                _myCardPrices.Remove(victim);
            }
        }

        private void TrimMyItemPriceEdits()
        {
            while (_myItemPriceEdits.Count > MyItemPriceMax)
            {
                int victim = 0;
                bool found = false;
                double oldest = double.MaxValue;
                foreach (var kv in _myItemPriceEdits)
                    if (!found || kv.Value.At < oldest)
                    {
                        oldest = kv.Value.At;
                        victim = kv.Key;
                        found = true;
                    }
                if (!found)
                    break;
                _myItemPriceEdits.Remove(victim);
            }
        }

        /// <summary>True when WE set this item's price within the hold window and the host's
        /// bulk table still disagrees - that PriceList was built before our edit arrived, so
        /// applying it would undo the edit in front of the player. Expired stamps are dropped
        /// here so the host's table goes back to winning.</summary>
        private bool HeldLocalItemPrice(int itemType, float incoming)
        {
            if (_myItemPriceEdits.Count == 0)
                return false;
            if (!_myItemPriceEdits.TryGetValue(itemType, out var e))
                return false;
            if (Time.realtimeSinceStartupAsDouble - e.At >= ItemPriceHoldSeconds)
            {
                _myItemPriceEdits.Remove(itemType);
                return false;
            }
            // same tolerance as card prices, and for the same reason: the store rounds by up
            // to half a display quantum and the quantum differs per machine's local currency,
            // so a 0.0001f compare called an identical price "different" across a mixed pair
            return Math.Abs(e.Value - incoming) > CardPriceEpsilon;
        }

        /// <summary>Client, ~1Hz: re-send any card price the host has not confirmed. Nothing
        /// ever acked a CardPriceSet, so an edit lost with a dropped frame simply vanished.
        /// After CardPriceMaxAttempts we SURRENDER - drop our tracking entry so the host's
        /// heals take over again - and say so once, instead of retrying for the session.</summary>
        private void CardPriceRetryTick()
        {
            if (Role != CoopRole.Client || _net == null || _myCardPrices.Count == 0)
                return;
            // Mid-scene-load a guest would spend all 12 attempts against a world that isn't up
            // yet and surrender before the first one could ever be confirmed. LastSend keeps
            // aging while we're out, so retries resume immediately once the level lands.
            if (!InGameLevel())
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            _cardPriceRetryKeys.Clear();
            foreach (var kv in _myCardPrices)
                if (!kv.Value.Acked && now - kv.Value.LastSend >= 3.0)
                    _cardPriceRetryKeys.Add(kv.Key);
            for (int i = 0; i < _cardPriceRetryKeys.Count; i++)
            {
                string key = _cardPriceRetryKeys[i];
                if (!_myCardPrices.TryGetValue(key, out var e))
                    continue;
                if (e.Attempts >= CardPriceMaxAttempts)
                {
                    // SURRENDER by forgetting the card, not by faking an ack. An untracked card
                    // adopts the host's next heal cleanly through the normal path; a fake
                    // "acked" entry only kept bookkeeping (and a stale Value) alive forever.
                    // Safe to remove here: we iterate the _cardPriceRetryKeys scratch list, not
                    // the dictionary.
                    _myCardPrices.Remove(key);
                    CoopPlugin.Log.LogWarning($"card price for {key} never confirmed - keeping the local value until the host's next price sync");
                    continue;
                }
                var card = e.Card;
                float value = e.Value;
                Broadcast(new CardPriceSetMessage { Card = card, Price = value });
                e.LastSend = now;
                e.Attempts++;
                _myCardPrices[key] = e;
            }
        }

        private void Shutdown(string reason)
        {
            if (_localPlayerModel != null)
                Util.PlayerModelStore.Save(_localPlayerModel);
            _localModelSavePending = false;
            PlayerModelGeneration++;
            IsTearingDown = true;
            // Harmony callbacks can arrive while transport and world teardown are in progress.
            // Drop all static module entry points first so they cannot touch the old instance
            // state (or a newly loaded world's objects).
            bool hadModuleRegistry = _moduleRegistry != null;
            if (hadModuleRegistry)
            {
                _moduleRegistry.Dispose();
                _moduleRegistry = null;
            }
            if (_net != null)
            {
                try
                {
                    Broadcast(new ByeMessage { Reason = "session ended" });
                }
                catch (System.Exception e) { Swallow.Log(e); }
                _net.Stop();
                _net = null;
            }
            _avatars.Clear();
            PeerNames.Clear();
            _peerWireNames.Clear();
            _peerSteamIds.Clear();
            _rosterNames.Clear();
            _enumSyncSentTo.Clear(); // FIX C: the loop-breaker memory is per hosting session
            _enumSyncSentToPeer.Clear(); // ...and its digest-free companion ceiling
            // The canonical id space was the HOST's, and it died with the session. Clearing is
            // not housekeeping, it is correctness: the next session may be one where WE host,
            // and a host must translate nothing.
            Util.EnumMap.Clear();
            // Same reasoning for the per-expansion shown-monster membership sets: they describe
            // THIS install's content as it stood during the dead session.
            ClearCardSetCache();
            // The graded pairing hints and the graded diff die with the session too - they
            // describe the DEAD session's peer, and the adopt offers must not survive it.
            ClearGradedSkipMemory();
            _gradedSent = false;
            _lastGradedHash = -1;
            _gradedPeerOnly.Clear();
            _gradedAlertEverShown.Clear();
            _gradedAlertStanding.Clear();
            GradedAdoptOffers.Clear();
            // The PriceList swap-buffer pair, for the same reason and in the same breath: both
            // hold LOCAL item ids from the DEAD session's translation. Left behind, _clientPriced
            // is read by the next session's clear pass ("the host cleared everything not in this
            // list") and would zero real prices on a later host whose ids mean something else.
            _clientPriced.Clear();
            _incomingPriced.Clear();
            // 1.0.35 per-frame/per-session card state: retry stamps, an undelivered outbox and
            // a half-drained dispatch buffer must never leak into the NEXT session
            _cardDeltaOutbox.Clear();
            _batchRelayBuf.Clear();
            _flushingCardDeltas = false;
            _binderRefreshPending = false;
            _deltaLogBuf.Clear();
            _deltaAppliedThisFrame = 0;
            // RECEIVED but not-yet-applied card work from the dead session. Leaving these
            // queued replays the old world's AddCards/prices into whatever save loads next,
            // because FlushPendingCardWork drains them the moment ANY level is in-game again.
            _pendingCardDeltas.Clear();
            _pendingCardPrices.Clear();
            _myCardPrices.Clear();
            _cardPriceRetryKeys.Clear();
            _cardPriceRetryTimer = 0f;
            _myItemPriceEdits.Clear();
            _priceWarnedKeys.Clear(); // the once-per-session warn memo is per session
            _dispatchBuf.Clear();   // leftovers held back by the per-frame dispatch budget
            _dispatchRetryNextFrame.Clear();
            _dispatchHeldTransfers.Clear();
            _dispatchSeen.Clear();
            _gotStateFrom.Clear();
            _saveBuf = null;
            _saveExpected = -1;
            _pendingSave = null;
            _bundleBuf = null;
            _bundleExpected = -1;
            _worldRequested = false;
            _hasLastPos = false;
            _lastCoinSent = double.MinValue;
            _priceFullPending = true; // first host tick after a session reset sends the whole table
            _pricePending.Clear();
            // card-price heal change-gate: a re-host inheriting the PREVIOUS world's hash
            // would gate away the new world's very first price sync (the guests would sit on
            // whatever they had until something moved), so it resets with the session.
            _cardPriceBuf.Clear();
            _lastCardPriceHash = 0;
            _cardPriceHealBeat = 0f;
            _cardPriceHealDirty = true; // preserve the first post-join price sync
            _cardPriceHealTimer = -2.1f;
            _lastProgressSent = long.MinValue;
            if (!hadModuleRegistry)
            {
                // No live registry (e.g. quitting from the title screen): fall back to the
                // per-instance resets. A session that had a registry was already reset by
                // CoopModuleRegistry.Dispose, so re-resetting here would be redundant.
                ResetAllModules();
            }
            _lastShopNameSent = null;
            // UNCONDITIONAL PATH: Shutdown runs from OnDestroy and OnApplicationQuit, i.e.
            // on EVERY game exit and every LAN session too. The null-conditional is what
            // keeps this whole method JIT-clean on a Steamworks-less build.
            //
            // ORDERING: THIS MUST STAY ABOVE `Role = CoopRole.None` BELOW. The bridge's
            // lobby-entered path wires the transport and raises OnConnectedToHost BEFORE
            // CoopCore's role guard on that handler gets to run, so the only thing that
            // actually closes the stray-enter window is Leave() clearing the lobby's
            // _joining flag and dropping _tx. Clear Role first and there is a gap in which
            // a late lobby-enter callback can still land on a live bridge with the role
            // already reset - a connection nobody owns.
            _steam?.Leave();
            IsSteamSession = false;
            HostPassword = "";
            _joinPassword = "";
            _selfId = -1;
            _relayIds.Clear();
            _playerModels.Clear();
            _localPlayerModel = null;
            _localPlayerModelReady = false;
            _localModelAppliedRoot = null;
            _lastCommittedPlayerModel = null;
            _playerModelUndo.Clear();
            _playerModelRedo.Clear();
            _pendingKicks.Clear();
            // The hole we asked the router to open closes with the session. FIRE AND FORGET on
            // a worker, because Shutdown runs from OnDestroy and OnApplicationQuit - blocking
            // the main thread on a SOAP round trip there would hang the game on exit.
            //
            // THIS CALL IS THE CLEANUP, not a nicety on top of one. We ask for a 24h lease and
            // no longer retry as a PERMANENT mapping when the router refuses to lease (1.0.38 -
            // see NetHelpers.TryMapPort), so a mapping that outlives this delete should expire
            // on its own - but "should" is the router's opinion: nothing in UPnP stops it
            // clamping or ignoring the duration we asked for. Treat a failed delete as a hole
            // that stays open until someone clears it in the router's admin page, which is why
            // it now logs at Warning. HasMapping is false for every client and every Steam
            // session, so this costs those nothing.
            if (Net.NetHelpers.HasMapping)
                new Thread(Net.NetHelpers.RemoveMapping) { IsBackground = true, Name = "CoopUnmap" }.Start();
            Interlocked.Increment(ref _inviteGen); // a resolve still in flight belongs to a dead session
            Interlocked.Increment(ref _sessionGen); // invalidate workers from this session
            InviteStatus = InviteState.Off;
            InviteCodeText = null;
            InviteReason = null;
            PortForwardState = 0;
            Application.runInBackground = false; // back to the game's normal behavior
            Role = CoopRole.None;
            ClientReloading = false;
            _clientWorldArrived = false;
            _sessionInGame = false;
            _dispatchDeferredFrames = 0;
            IsTearingDown = false;
            // Only clear the save guard if we're NOT in a level - i.e. a join that failed at
            // the title before loading the host's world. A mid-session disconnect leaves the
            // guest standing in the borrowed world, so the guard MUST persist (a day-end
            // autosave or quit-save would otherwise write the host's shop to the guest's slot).
            // The title screen clears it on the clean way out.
            if (!InGameLevel())
                GuestBorrowedWorld = false;
            if (reason != null)
            {
                StatusLine = "Not connected (" + reason + ")";
                CoopPlugin.Log.LogInfo("Session ended: " + reason);
            }
        }

        // ------------------------------------------------ send helpers

        // ORDERING GUARANTEE for the deferred card-delta outbox: card deltas are queued now,
        // not sent, so any OTHER message leaving before the flush would overtake them and
        // break today's global ordering (a graded-card flow sends a delta and then a follow-up
        // message about the same card). Every send funnels through these three, so flushing
        // here first is enough. The flush is UNCONDITIONAL - a CardDeltaBatch exemption here
        // and in RelayRawToOthers let a RELAYED batch jump ahead of our own queued deltas -
        // and the recursion is stopped by the _flushingCardDeltas re-entrancy guard inside
        // FlushCardDeltaOutbox, which turns the nested call into a no-op.

        private void Send(int connId, INetMessage message)
        {
            FlushCardDeltaOutbox();
            _net?.Send(connId, message);
        }

        /// <summary>Returns an Action&lt;INetMessage&gt; bound to one connId, for wiring
        /// subsystem callbacks that send to a fixed peer (the host, connId 1).</summary>
        private Action<INetMessage> Send(int connId)
        {
            return message => Send(connId, message);
        }

        private void Broadcast(INetMessage message)
        {
            FlushCardDeltaOutbox();
            _net?.Broadcast(message);
        }

        /// <summary>Fast lane: transient state that must never wait behind bulk transfers
        /// (unreliable-no-delay on Steam; a lost packet is replaced by the next tick).</summary>
        private void BroadcastTransient(INetMessage message)
        {
            // The flush only ORDERS the LAN transport, where the transient lane maps onto the
            // SAME ordered TCP stream. On SteamTransport the transient lane is drained BEFORE
            // the reliable lane every pump (SteamNet.PumpMainThread), so transient messages
            // intentionally overtake card deltas there and no flush can prevent it.
            // THEREFORE: no card-coupled message may EVER use the transient lane - only
            // self-replacing state (positions, crowd, register) belongs here.
            FlushCardDeltaOutbox();
            _net?.BroadcastTransient(message);
        }

        /// <summary>Client: ask the host to perform one complete purchase. The client
        /// deliberately does not charge, spawn, unlock, or grant XP locally.</summary>
        public void RequestPurchase(byte kind, List<PurchaseLine> lines)
        {
            if (Role != CoopRole.Client || _net == null || lines == null || lines.Count == 0)
                return;
            Send(1, new PurchaseRequestMessage { Kind = kind, Lines = lines });
        }

        /// <summary>True when a NATIVE game TMP input field is ACTIVELY BEING EDITED.
        /// UI.CoopUI.TextFieldFocused only tracks the mod's own IMGUI "coop_" controls, so it
        /// can't see the game's TextMeshPro fields - e.g. the haggle-price box on the native
        /// trade/sell-in screen. Without this, tapping the serve key while typing a price would
        /// also fire a register serve.
        /// CRITICAL: must check isFocused (editing NOW), not mere EventSystem SELECTION -
        /// Unity leaves currentSelectedGameObject pointing at the last-clicked UI element
        /// FOREVER after its screen closes, so a selection-only check permanently ate the
        /// serve key once the guest had touched any game text field (price screen, phone
        /// app, grading site) - field report: "guest can't interact with npc".
        /// Null-safe: no EventSystem / nothing selected / not editing -> false.</summary>
        // ------------------------------------------------ per-frame

        private void Update()
        {
            int actionsRun = 0;
            while (actionsRun++ < MainThreadActionBudget && _mainThread.TryDequeue(out var act))
            {
                act();
            }

            // Release the guest save-guard only once we're safely back at the title: no
            // session AND out of any game level. Post-disconnect the guest is Role.None but
            // still standing in the host's world (InGameLevel true), so the guard persists
            // there and clears only after they actually return to the menu.
            if (GuestBorrowedWorld && Role == CoopRole.None && !InGameLevel())
                GuestBorrowedWorld = false;

            // guest soft-lock safety net: recover from a stranded hold-box mode
            if (Role == CoopRole.Client && InGameLevel())
                RecoverStuckHoldBox();

            AutoTick(Time.deltaTime);
            if (_characterPreviewActive && Role != CoopRole.None && InGameLevel())
            {
                EnsureLocalPlayerModel();
                _avatars.UpdatePreview(_localPlayerModel, _playerTf, _playerCamTf, true);
            }
            else
                _avatars.DestroyPreview();
            if (_localModelAuto)
            {
                _localModelAutoRetry -= Time.deltaTime;
                if (_localModelAutoRetry <= 0f && InGameLevel())
                {
                    _localModelAutoRetry = 1f;
                    EnsureLocalPlayerModel();
                }
            }
            FlushLocalModelSave(Time.deltaTime);

            // A day transition can arrive while the guest is changing scenes or has a
            // report screen open. Retry once the actual game level is ready instead of
            // permanently leaving the guest's sky behind.
            TryStartClientDayReset();

            if (Input.GetKeyDown(CoopPlugin.UiToggleKey.Value))
                _ui.Visible = !_ui.Visible;
            SyncWindowUIMode();
            if (Role != CoopRole.None && Input.GetKeyDown(CoopPlugin.EmoteKey.Value) && !UI.CoopUI.TextFieldFocused)
                SendEmote();

            if (RegisterLineTimer > 0f)
            {
                RegisterLineTimer -= Time.deltaTime;
                if (RegisterLineTimer <= 0f)
                    RegisterLine = "";
            }
            if (_net == null)
                return;

            if (!_sessionInGame && InGameLevel())
            {
                _sessionInGame = true;
                _dispatchDeferredFrames = 0;
                CoopPlugin.Log.LogInfo("Dispatch session gate opened after entering the game world");
            }

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
                _peerWireNames.Remove(left);
                _peerSteamIds.Remove(left);
                _avatars.Remove(left);
                _movePreview.RemoveSource(left);
                if (Role == CoopRole.Host)
                    _playerModels.Remove(left);
                if (Role == CoopRole.Host)
                {
                    _world.HostReleaseConn(left);
                    // release anything the departed guest was CARRYING: the set-down
                    // request is never coming, and without this the boxes stay hidden /
                    // worker-locked / carried-frozen on every peer until a full shutdown
                    // (verified stranding: item boxes hidden + m_PreventWorkerTakeBox
                    // pinned forever). Releasing all client-carried boxes is correct for
                    // 2-player and self-heals with 3+ (a survivor still carrying one
                    // re-asserts its carry on its next ~0.5s report).
                    try
                    {
                        _boxEngine?.HostReleaseConn(left);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    try
                    {
                        _register.HostReleaseConn(left);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    try
                    {
                        _staff.HostReleaseConn(left);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    try
                    {
                        _containers.HostReleaseConn(left);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    try
                    {
                        _trades.HostReleaseConn(left);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    // and DROP any product still held for the departed guest: its charge
                    // is never coming, and the fail-open pump would otherwise deliver the
                    // product chargeless 1.5s from now. Its charge verdict goes too.
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

            // Once the borrowed-world load starts, leave gameplay messages queued and let
            // vanilla finish constructing the scene without the mod walking its partial
            // shelf/box lists.  BundleDone can set ClientReloading during the dispatch pass
            // below; the second check after Dispatch handles that same-frame transition.
            if (ClientReloading && !TryFinishClientReload())
                return;

            // Drain with coalescing: after any hitch the backlog holds dozens of stale
            // full-state packets; applying each in one frame turns one slow frame into
            // a cascade. For snapshot types only the NEWEST per (type, sender) matters.
            // (NpcState is chunked - every chunk carries different NPCs - and RelayState
            // multiplexes senders inside the payload, so neither may be coalesced.)
            _pendingReduceThisFrame = 0.0; // reset the per-frame guest-spend accumulator
            if (_dispatchRetryNextFrame.Count > 0)
            {
                _dispatchBuf.AddRange(_dispatchRetryNextFrame);
                _dispatchRetryNextFrame.Clear();
            }
            // Anything last frame's budget held back is still at the FRONT of _dispatchBuf, in
            // order; the fresh drain appends after it. The coalescer then re-runs over the
            // combined buffer, so a stale leftover snapshot still loses to a newer one.
            if (_dispatchHeldTransfers.Count > 0 && _dispatchBuf.Count < DispatchBacklogCap)
            {
                _dispatchBuf.Add(_dispatchHeldTransfers[0]);
                _dispatchHeldTransfers.RemoveAt(0);
            }
            bool canDrainIncoming = _dispatchHeldTransfers.Count == 0;
            while (canDrainIncoming && _net != null && _net.Incoming.TryDequeue(out var msg))
            {
                if (_dispatchBuf.Count >= DispatchBacklogCap)
                {
                    int drop = FindBufferedSnapshot();
                    if (drop >= 0)
                        _dispatchBuf.RemoveAt(drop);
                    else
                    {
                        bool transferMessage = IsTransferMessage(msg.Type);
                        if (transferMessage)
                        {
                            if (!_dispatchTransferOverflowWarned)
                            {
                                _dispatchTransferOverflowWarned = true;
                                CoopPlugin.Log.LogWarning($"Dispatch backlog cap reached; bounding transfer admission for {msg.Type}");
                            }
                            _dispatchHeldTransfers.Add(msg);
                            break;
                        }
                        if (!_dispatchBacklogWarned)
                        {
                            _dispatchBacklogWarned = true;
                            CoopPlugin.Log.LogWarning("Dispatch backlog cap reached; dropping incoming traffic until it drains");
                        }
                        if (!_dispatchTransferHardDropWarned)
                        {
                            _dispatchTransferHardDropWarned = true;
                            CoopPlugin.Log.LogError($"Dispatch backlog hard-drop path consumed non-transfer message {msg.Type}");
                        }
                        // A dropped BoxSnapshot is not self-healing (the host's content hash
                        // already advanced and it is never re-sent), so ask for a fresh one.
                        // Use the coalesced/rate-limited request so a sustained flood cannot
                        // form a request/response loop.
                        if (msg.Type == MsgType.BoxSnapshot)
                            _boxEngine?.RequestResyncCoalesced();
                        else if (IsSingleShotOp(msg.Type))
                            _messageRouter.Heal(msg.Type);
                        continue;
                    }
                }
                _dispatchBuf.Add(msg);
            }
            if (_dispatchBuf.Count < DispatchBacklogCap / 2)
            {
                _dispatchBacklogWarned = false;
                _dispatchTransferOverflowWarned = false;
                _dispatchTransferHardDropWarned = false;
            }
            if (_dispatchBuf.Count > 8)
            {
                _dispatchSeen.Clear();
                for (int i = _dispatchBuf.Count - 1; i >= 0; i--)
                {
                    var t = _dispatchBuf[i].Type;
                    // Only full-replacement state may be coalesced: PlayerState is the latest
                    // pose, RegisterState/RegisterCart and PopState are complete rosters, so the
                    // newest frame fully supersedes older ones. BoxSnapshot is deliberately NOT
                    // here: a Full carries every box while a Partial carries only changes, and
                    // the box engine has no periodic full scan. Dropping a Full strands every
                    // unchanged box; dropping an intermediate Partial strands that box's change
                    // (the host's hash already advanced, so it is never re-sent). Apply the whole
                    // reliable sequence in order instead.
                    if (t != MsgType.PlayerState && t != MsgType.RegisterState
                        && t != MsgType.RegisterCart && t != MsgType.PopState)
                        continue;
                    long key = ((long)t << 32) | (uint)_dispatchBuf[i].ConnId;
                    if (!_dispatchSeen.Add(key))
                        _dispatchBuf[i] = default; // superseded
                }
            }
            // BUDGET, in WORK UNITS not messages: coalescing can't help the types that carry
            // real work (a CardDeltaBatch flood, box/container ops), and applying an unbounded
            // backlog in one frame is the hitch we're trying to kill. Every message costs 1
            // unit EXCEPT a CardDeltaBatch, which is charged per delta it carries - counting a
            // 200-delta batch as one message meant the budget bounded nothing. Spend at most
            // DispatchBudget units; the rest keeps its place in line for the next frame. The
            // first message of a frame always goes through even if it alone busts the budget,
            // so an oversized batch can never wedge the queue.
            int consumed = 0;
            int dispatched = 0;
            int unitsSpent = 0;
            for (int i = 0; i < _dispatchBuf.Count; i++)
            {
                if (_dispatchBuf[i].Type == 0)
                {
                    consumed = i + 1;
                    continue;
                } // coalesced away
                int cost = DispatchCost(_dispatchBuf[i]);
                if (unitsSpent + cost > DispatchBudget && dispatched > 0)
                    break; // next frame's work
                dispatched++;
                unitsSpent += cost;
                InMsg current = _dispatchBuf[i];
                try
                {
                    if (!Dispatch(current))
                    {
                        if (!_sessionInGame)
                        {
                            consumed = i + 1;
                            if (current.Type == MsgType.BoxSnapshot)
                                _boxEngine?.RequestResyncCoalesced();
                            continue;
                        }
                        if (!_messageRouter.IsTransientInGameGate(new MessageContext
                        {
                            ConnectionId = current.ConnId,
                            Role = Role,
                            InGame = InGameLevel(),
                            Transport = _net
                        }, current.Message))
                        {
                            if (!_dispatchRoleDeniedWarned)
                            {
                                _dispatchRoleDeniedWarned = true;
                                CoopPlugin.Log.LogWarning($"Dispatch: dropping {current.Type} due to role mismatch");
                            }
                            consumed = i + 1;
                            continue;
                        }
                        _dispatchDeferredFrames++;
                        if (_dispatchDeferredFrames > MaxDispatchDeferredFrames)
                        {
                            CoopPlugin.Log.LogError($"Dispatch: dropping gated head {current.Type} after {MaxDispatchDeferredFrames} deferred frames");
                            _messageRouter.Heal(current.Type);
                            consumed = i + 1;
                            _dispatchDeferredFrames = 0;
                            continue;
                        }
                        if (!_dispatchDeferredWarned)
                        {
                            _dispatchDeferredWarned = true;
                            CoopPlugin.Log.LogWarning($"Dispatch queue waiting for in-game gate before applying {current.Type}");
                        }
                        consumed = i;
                        break;
                    }
                    _dispatchDeferredWarned = false;
                    _dispatchDeferredFrames = 0;
                    consumed = i + 1;
                }
                catch (Exception e)
                {
                    consumed = i + 1;
                    bool retryable = _messageRouter.IsRetryable(current.Type);
                    CoopPlugin.Log.LogError($"Dispatch conn={current.ConnId} type={current.Type} "
                        + (retryable ? "delta/op" : "snapshot") + " failed: " + e);
                    if (retryable && current.DispatchAttempts < MaxDispatchRetries)
                    {
                        current.DispatchAttempts++;
                        _dispatchRetryNextFrame.Add(current);
                    }
                    else
                    {
                        // No retries happen for a non-retryable type, so do not report it as
                        // one: the old wording claimed "retries" for every failure.
                        string detail = retryable
                            ? $"dropped after {current.DispatchAttempts} retry attempt(s)"
                            : "failed (non-retryable)";
                        CoopPlugin.Log.LogError($"Dispatch conn={current.ConnId} type={current.Type} "
                            + detail + "; requesting authoritative heal");
                        _messageRouter.Heal(current.Type);
                    }
                }
                if (_net == null)
                    break; // a Bye may have shut us down mid-drain
                if (ClientReloading)
                    break; // BundleDone started the borrowed-world load
            }
            // (a Bye already cleared the buffer in Shutdown, hence the >= Count branch)
            if (consumed >= _dispatchBuf.Count)
                _dispatchBuf.Clear();
            else if (consumed > 0)
                _dispatchBuf.RemoveRange(0, consumed);
            if (_dispatchBuf.Count == 0)
            {
                _dispatchDeferredWarned = false;
                _dispatchRoleDeniedWarned = false;
            }
            if (_net == null)
                return;

            float dt = Time.deltaTime;

            // Every stage is individually armored: one failing subsystem must degrade
            // that feature only, never kill position sync for the whole session.
            if (!ClientReloading)
                FlushPendingCardWork();

            _dt = dt;
            if (ClientReloading)
            {
                // Do not let the co-op tick walk partially-created shelves, boxes or
                // machines while vanilla is rebuilding the borrowed world.  The incoming
                // network queue is intentionally left untouched below; it will be applied
                // after the game's own load-complete flag is raised.
                if (!TryFinishClientReload())
                    return;
            }
            Guarded("avatars", _actAvatars);
            _syncActive = Role != CoopRole.None && _net.ConnectionCount > 0 && InGameLevel()
                && !ClientPreloadHold;
            Guarded("population", _actPopulation);
            Guarded("world", _actWorld);
            Guarded("cardshelves", _actCardShelves);
            Guarded("objmoves", _actObjMoves);
            Guarded("move-preview", _actMovePreview);
            Guarded("boxes", _actBoxes);
            Guarded("modules", _actModules);

            if (Role == CoopRole.Client)
            {
                Guarded("npc-puppets", _actNpcPuppets);

                // chase any card price the host hasn't confirmed yet (the retry has its own
                // 3s per-entry cooldown; this is just the polling cadence)
                _cardPriceRetryTimer += dt;
                if (_cardPriceRetryTimer >= 1f)
                {
                    _cardPriceRetryTimer = 0f;
                    Guarded("card-price-retry", _actCardPriceRetry);
                }

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
                            ? $" puppets={_npcs.PuppetCount} localNpcs={NpcSync.CountUnexpectedActiveNpcs()}(should be 0) carriers={RegisterSync.CarrierCount} mirrors={NpcSync.ExistingMirrorCount}"
                            : $" liveNpcs={local}";
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
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
                else
                    _pendingKicks[i] = new KeyValuePair<int, float>(_pendingKicks[i].Key, left);
            }

            // heartbeat + timeout
            _pingTimer += dt;
            if (_pingTimer >= 2f)
            {
                _pingTimer = 0f;
                Broadcast(new PingMessage());
                foreach (int id in _net.ConnIds())
                {
                    if (_net.SecondsSinceLastRecv(id) > _net.TimeoutSeconds)
                    {
                        CoopPlugin.Log.LogWarning("Connection " + id + " timed out");
                        _net.Kick(id);
                    }
                }
            }

            if (Role == CoopRole.Host)
                HostTick(dt);

            // LAST: one binder relayout + the folded delta log for everything applied this
            // frame, then the batched card-delta outbox. Everything above has had its chance
            // to apply or produce a delta by now.
            Guarded("frame-card-work", _actFrameCardWork);
        }

        /// <summary>Drives the -coopautohost / -coopautojoin command-line flows.</summary>
        private void AutoTick(float dt)
        {
            if (_autoHostSlot < 0 && _autoJoinIp == null)
                return;
            if (_autoPhase >= 99)
                return;
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
            // AutoTick runs unconditionally every frame from Update, so ANY Steamworks token
            // in this method body would fault at JIT on a Steamworks-less build - which is
            // why the CSteamID construction that used to live below had to go.
            else if (_autoJoinSteamLobby != 0 && _steam != null)
            {
                if (_autoPhase == 0 && _autoTimer > 10f && !InGameLevel()
                    && CSingleton<CGameManager>.Instance != null)
                {
                    CoopPlugin.Log.LogInfo($"AUTO: joining Steam lobby {_autoJoinSteamLobby}...");
                    JoinSteam(_autoJoinSteamLobby);
                    _autoPhase = 99;
                }
            }
        }

        /// <summary>Local snapshot diffs: host broadcasts authoritative state, client requests.</summary>
        private void OnLocalWorldChanges(System.Collections.Generic.List<WorldSync.Entry> changes)
        {
            if (Role == CoopRole.Host)
                Broadcast(new ShelfDeltaMessage { Entries = changes });
            else if (Role == CoopRole.Client)
                Send(1, new ShelfRequestMessage { Entries = changes });
        }

        private int FindBufferedSnapshot()
        {
            for (int i = 0; i < _dispatchBuf.Count; i++)
            {
                var type = _dispatchBuf[i].Type;
                if (type == MsgType.PlayerState || type == MsgType.RegisterState
                    || type == MsgType.RegisterCart || type == MsgType.PopState)
                    return i;
            }
            return -1;
        }

        private static bool IsTransferMessage(MsgType type)
        {
            return type == MsgType.BoxUpdate || type == MsgType.BoxTransferResult
                || type == MsgType.ShelfRequest || type == MsgType.ShelfTransferResult;
        }

        /// <summary>Host: one item price entry changed (player/worker/EPL, or a joiner
        /// contribution we just applied); batch it for a partial broadcast next tick.</summary>
        public void NoteItemPriceChanged(EItemType itemType, float price)
        {
            int t = (int)itemType;
            if (t < 0 || t > 500000)
                return;
            _pricePending[t] = price;
        }

        private void HostTick(float dt)
        {
            if (_net.ConnectionCount == 0)
                return;

            if (InGameLevel())
            {
                Guarded("npc-collect", _actNpcCollect);
            }

            if (_priceFullPending)
            {
                _priceFullPending = false;
                _pricePending.Clear(); // a full table supersedes any queued partials
                long tPrice = Util.PerfProbe.Start();
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
                    for (int i = 0; i < n; i++)
                    {
                        var rd = CatalogAt(i);
                        if (rd == null)
                            continue;
                        int t = (int)rd.itemType;
                        if (!seenTypes.Add(t))
                            continue; // big/small share one price row
                        float v = 0f;
                        try
                        {
                            v = CPlayerData.GetItemPrice(rd.itemType, preventZero: false);
                        }
                        catch (System.Exception e) { Swallow.Log(e); }
                        if (v == 0f)
                            continue;
                        _priceBuf.Add(new KeyValuePair<int, float>(t, v));
                    }
                    var fullList = new PriceListMessage { Full = true };
                    for (int i = 0; i < _priceBuf.Count; i++)
                        fullList.Prices.Add(new PriceEntry { ItemType = _priceBuf[i].Key, Price = _priceBuf[i].Value });
                    Broadcast(fullList);
                    Util.PerfProbe.End("price-sync", tPrice);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("price sync: " + e.Message); }
            }
            else if (_pricePending.Count > 0)
            {
                // Per-change partial: the SetItemPrice hook already names the exact entry that
                // moved, so there is no catalog walk. Reliable, so a send is a delivery.
                var partial = new PriceListMessage { Full = false };
                foreach (var kv in _pricePending)
                    partial.Prices.Add(new PriceEntry { ItemType = kv.Key, Price = kv.Value });
                _pricePending.Clear();
                Broadcast(partial);
            }

            _shopNameTimer += dt;
            if (_shopNameTimer >= 3f)
            {
                _shopNameTimer -= 3f;
                string name = CPlayerData.GetPlayerName();
                if (name != _lastShopNameSent)
                {
                    _lastShopNameSent = name;
                    Broadcast(new ShopNameMessage { Name = name });
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
                    Broadcast(new CoinSetMessage { Coin = coin, CoinFloat = coinF });
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
                    Broadcast(new ProgressSetMessage { Experience = exp, Level = level, Fame = fame });
                }
            }

            // lighting-state heal: normal light changes are event-driven; this slow fallback
            // repairs scene-loads, unusual mods, or a missed change notification
            _lightSyncTimer += dt;
            if (_lightSyncTimer >= 30f || _lightForceResend)
            {
                bool forced = _lightForceResend;
                _lightSyncTimer = forced ? 0f : _lightSyncTimer - 30f;
                _lightForceResend = false;
                try
                {
                    if (_lightManager == null)
                        _lightManager = FindObjectOfType<LightManager>();
                    if (_lightManager != null && MiUpdateLightData != null && CPlayerData.m_LightTimeData != null)
                    {
                        MiUpdateLightData.Invoke(_lightManager, null); // refresh bundle from live state
                        string lightJson = JsonUtility.ToJson(CPlayerData.m_LightTimeData);
                        // the client CORRECTS ITS DRIFT only when a packet arrives - a pure
                        // changed-only gate silenced the corrector whenever the host's sky
                        // was static (pre-open mornings) and the joiner drifted to sunset
                        _lightHeal += 30f;
                        if (lightJson != _lastLightJson || _lightHeal >= 60f)
                        {
                            _lastLightJson = lightJson;
                            _lightHeal = 0f;
                            Broadcast(new LightStateMessage
                            {
                                LightJson = lightJson,
                                Day = CPlayerData.m_CurrentDay,
                                HasDay = true,
                            });
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
                            Broadcast(new CardShelfDeltaMessage { Entries = full });
                        }
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("card resync: " + e.Message); }

                // ITEM-STOCK full-truth heal on the same 12s beat: item stock previously had
                // NO periodic resync (only host local diffs), so a client that silently
                // adopted a slightly-wrong baseline at join (WorldSync's first-sighting
                // adoption - the mid-day-join wipe fix) stayed wrong until the host touched
                // that compartment. A full-state broadcast IS just a ShelfDelta carrying
                // every compartment (the client handler routes it through ApplyRemote), so
                // no new wire type. Change-gated on the payload bytes + a 36s forced heal.
                try
                {
                    var full = _world.BuildFullState();
                    if (full != null && full.Count > 0)
                    {
                        var fullMessage = new ShelfDeltaMessage { Entries = full };
                        // Hash the actual state fields. Serializing the complete shelf payload
                        // merely to decide whether it changed created a large allocation every
                        // 12 seconds in otherwise idle shops.
                        int h = 17;
                        for (int i = 0; i < full.Count; i++)
                        {
                            var e = full[i];
                            h = h * 31 + e.Key;
                            h = h * 31 + e.Type;
                            h = h * 31 + e.Count;
                        }
                        _stockResyncHeal += 12f;
                        if (h != _lastStockResyncHash || _stockResyncHeal >= 36f)
                        {
                            _lastStockResyncHash = h;
                            _stockResyncHeal = 0f;
                            Broadcast(fullMessage);
                        }
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("stock resync: " + e.Message); }
            }

            // card PRICE heal: card prices sync only via a single MsgType.CardPriceSet
            // broadcast with no re-send, so one dropped frame stranded a displayed card's
            // price forever (guest set a price on a display, host never saw it). Item prices
            // already self-heal (PriceList); give card prices the same change-gated beat.
            _cardPriceHealTimer += dt;
            if (_cardPriceHealTimer >= 3f && InGameLevel())
            {
                _cardPriceHealTimer -= 3f;
                _cardPriceHealBeat += 3f;
                bool scanCardPrices = _cardPriceHealDirty || _cardPriceHealBeat >= 30f;
                if (scanCardPrices)
                {
                    _cardPriceHealDirty = false;
                    try
                    {
                        var full = _cardShelves.BuildFullState();
                        int h = 17;
                        _cardPriceBuf.Clear();
                        foreach (var e in full)
                        {
                            if (!e.Occupied || e.Card == null)
                                continue;
                            // an encoded (>10) grade only prices via Grading Overhaul's own store
                            // (its GetCardPrice patch reads it); without GO it would IndexOutOfRange
                            if (e.Card.cardGrade > 10 && !Util.GradingInterop.Present)
                                continue;
                            float p;
                            try
                            {
                                p = CPlayerData.GetCardPrice(e.Card);
                            }
                            catch { continue; }
                            if (p <= 0f)
                                continue;
                            _cardPriceBuf.Add(new KeyValuePair<CardData, float>(e.Card, p));
                            h = h * 31 + e.Key;
                            h = h * 31 + p.GetHashCode();
                        }
                        bool priceStateChanged = h != _lastCardPriceHash;
                        bool forcedPriceHeal = _cardPriceHealBeat >= 30f;
                        if (priceStateChanged || forcedPriceHeal)
                        {
                            _lastCardPriceHash = h;
                            _cardPriceHealBeat = 0f;
                            // one message per card (CardPriceSet is a single-card frame); small
                            // and change-gated, so this only fires when a displayed price moved
                            // or the periodic recovery heal is due.
                            for (int i = 0; i < _cardPriceBuf.Count; i++)
                            {
                                var kv = _cardPriceBuf[i];
                                Broadcast(new CardPriceSetMessage { Card = kv.Key, Price = kv.Value });
                            }
                        }
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("card price heal: " + e.Message); }
                }
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
                        try
                        {
                            on = CPlayerData.GetIsItemLicenseUnlocked(i);
                        }
                        catch (System.Exception e) { Swallow.Log(e); }
                        if (!on)
                            continue;
                        var rd = CatalogAt(i);
                        if (rd != null)
                            unlocked.Add(rd);
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
                        var licenseState = new LicenseStateMessage { Scanner = scanner };
                        foreach (var rd in unlocked)
                        {
                            licenseState.Entries.Add(new LicenseEntry
                            {
                                ItemType = rd.itemType,
                                Big = rd.isBigBox,
                                // NAME identity: modded enum ints can drift between
                                // machines; the heal must still map the unlock
                                NameFnv = Fnv(rd.name ?? ""),
                            });
                        }
                        Broadcast(licenseState);
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
                    if (_lightManager == null)
                        _lightManager = FindObjectOfType<LightManager>();
                    if (_lightManager != null)
                    {
                        if (FiTimeHour != null)
                            hour = (int)FiTimeHour.GetValue(_lightManager);
                        if (FiTimeMin != null)
                            min = (int)FiTimeMin.GetValue(_lightManager);
                    }
                }
                catch (System.Exception e) { Swallow.Log(e); }
                int day = CPlayerData.m_CurrentDay;
                float minFloat = min;
                try
                {
                    if (_lightManager != null && FiTimeMinFloat != null)
                        minFloat = (float)FiTimeMinFloat.GetValue(_lightManager);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                bool shopOnceOpen = CPlayerData.m_IsShopOnceOpen;
                Broadcast(new DayTimeMessage { Day = day, Hour = hour, Minute = min, MinuteFloat = minFloat, ShopOnceOpen = shopOnceOpen });
            }
        }

        private bool Dispatch(InMsg msg)
        {
            if (msg.Message != null)
            {
                bool routed = _messageRouter.Dispatch(new MessageContext
                {
                    ConnectionId = msg.ConnId,
                    Role = Role,
                    InGame = InGameLevel(),
                    Transport = _net
                }, msg.Message);
                if (routed)
                    return true;
                if (_messageRouter.IsRegistered(msg.Message))
                    return false;
            }
            switch (msg.Type)
            {
                case MsgType.Hello:
                    {
                        if (Role != CoopRole.Host)
                            break;
                        if (msg.Message is HelloMessage hello)
                        {
                            int wireVersion = hello.WireVersion;
                            if (wireVersion != Msg.WireVersion)
                            {
                                RejectConn(msg.ConnId, $"wire protocol mismatch - host uses protocol {Msg.WireVersion}, you use {wireVersion}");
                                break;
                            }
                            // Version is checked FIRST so a peer on a different version (which
                            // may not send the newer handshake fields) is rejected before we
                            // try to read them.
                            string version = hello.Version ?? "";
                            if (version != CoopPlugin.Version)
                            {
                                RejectConn(msg.ConnId, $"version mismatch - host runs {CoopPlugin.Name} {CoopPlugin.Version}, you have {version}");
                                break;
                            }
                            string name = hello.PlayerName ?? "";
                            string password = hello.Password ?? "";
                            string pluginHash = hello.PluginHash ?? "";
                            string enumHash = hello.EnumHash ?? "";
                            string cardsHash = hello.CardsHash ?? "";
                            // FIX E3: these follow cardsHash (append-only wire). Same-version
                            // peers always send them; older/absent -> empty list -> generic
                            // wording below.
                            var theirPlugins = hello.PluginList;
                            var theirCards = hello.CardsList;
                            // FIX C: ...and the joiner's runtime registry lines follow those. Read
                            // in WIRE ORDER here even though the conflict check that uses them sits
                            // further down with the other parity gates.
                            var theirEnumLines = ReadCappedEnumBlob(hello.EnumBlob, out string theirEnumDigest);
                            // GAME-BUILD FINGERPRINT: last two fields of the Hello (see SendHello
                            // for why appending at the END is what makes this wire-safe).
                            string theirGameVersion = hello.GameVersion ?? "";
                            string theirUnityVersion = hello.UnityVersion ?? "";
                            CoopPlugin.Log.LogInfo($"game build: host is {Application.version} / Unity {Application.unityVersion}; {name} is {theirGameVersion} / Unity {theirUnityVersion}");
                            if (HostPassword.Length > 0 && password != HostPassword)
                            {
                                RejectConn(msg.ConnId, "wrong password");
                                break;
                            }
                            // Cross-play between the Steam and Game Pass releases works ONLY when
                            // both PCs run the same game build - the two storefronts ship updates
                            // at different times, and a version skew shows up as inexplicable
                            // desyncs rather than an obvious failure. Runs AFTER the mod-version
                            // check above (both peers are 1.0.38+, so these fields always exist).
                            if (theirGameVersion != Application.version || theirUnityVersion != Application.unityVersion)
                            {
                                // ESCAPE HATCH: a host who has deliberately set AllowCrossBuildJoin
                                // takes the risk knowingly (supervised Steam <-> Game Pass testing),
                                // so we let the join through and shout about it instead. Rejecting
                                // stays the default; only the HOST's config can open this door.
                                if (!CoopPlugin.AllowCrossBuildJoin.Value)
                                {
                                    RejectConn(msg.ConnId,
                                        $"your GAME build doesn't match the host's (host: {Application.version} / Unity {Application.unityVersion}, you: {theirGameVersion} / Unity {theirUnityVersion}) - the Steam and Game Pass versions of the game can only play together when both are on the same game version (a host who understands the risk can enable AllowCrossBuildJoin in the config)");
                                    break;
                                }
                                CoopPlugin.Log.LogWarning($"AllowCrossBuildJoin is ON - letting {name} in on a DIFFERENT game build (host: {Application.version} / Unity {Application.unityVersion}, {name}: {theirGameVersion} / Unity {theirUnityVersion}). Two different game builds can corrupt each other's saves - back up before playing.");
                            }
                            if (pluginHash != Util.ModParity.PluginHash())
                            {
                                // FIX E3: name the differing mods when we can; fall back to the
                                // old generic wording if the lists are absent/agree.
                                string detail = DescribeModDiff(theirPlugins, Util.ModParity.PluginList(),
                                    "mod set differs - ", "version differs");
                                RejectConn(msg.ConnId, detail
                                    ?? "your mod set differs from the host's - both players need identical mods (same versions)");
                                break;
                            }
                            // FIX C: registries no longer have to be IDENTICAL, only
                            // NON-CONFLICTING. Extra entries on either side are fine - neither
                            // player can spawn what the other doesn't know about, and the
                            // catalog-differs warning already says the sets differ. What actually
                            // corrupts a shared world is the SAME "Type:Name" bound to DIFFERENT
                            // ids on the two PCs, so that is the only thing we reject on now.
                            var ourEnumLines = SafeEnumLines();
                            if (ourEnumLines.Count == 0)
                            {
                                // DEAD-GATE VISIBILITY. EnumConflicts short-circuits to "no
                                // conflicts" whenever either side is empty (correct: a machine with
                                // no modded ids can't clash with anyone), so an empty list HERE means
                                // the ID-conflict check waved this joiner through without comparing
                                // anything. On a vanilla host that is the intended answer; on a
                                // modded one it means our own registry walk came up empty and the
                                // only thing standing between two conflicting id spaces just went
                                // quiet - while the log line below still prints two plausible hashes.
                                CoopPlugin.Log.LogWarning($"enum check: the host has NO modded enum ids to compare against, so {name} was not ID-checked at all (expected on a vanilla host; on a modded one see the 'enum identity source' line at startup)");
                            }
                            var conflicts = EnumConflicts(theirEnumLines, ourEnumLines);
                            if (conflicts.Count > 0)
                            {
                                CoopPlugin.Log.LogInfo($"enum check: {name} hash {enumHash} vs host {Util.ModParity.EnumHash()} - {conflicts.Count} real conflict(s)");
                                // Can we honestly ship our registry file? The old test here was
                                // "is it a BORROWED copy?" (ModParity.HostEnumInstalled - the
                                // .hostlend marker). That was the wrong question, and an expensive
                                // one: the marker survives restarts, so a host who joined somebody
                                // once, restarted, and has been happily RUNNING that registry ever
                                // since - a perfectly coherent id space, and exactly the file this
                                // guest needs - still hard-rejected every conflicting guest AND
                                // skipped the send that would have fixed them. That PC could never
                                // auto-sync anyone again.
                                //
                                // The real hazard is narrower: the bytes on disk are not what this
                                // process actually loaded, so shipping them promises the guest an id
                                // space nobody is running. That is tracked precisely by our own two
                                // writes - see ModParity.RegistryFileMatchesRuntime.
                                if (!Util.ModParity.RegistryFileMatchesRuntime())
                                {
                                    RejectConn(msg.ConnId,
                                        "your custom-card database conflicts with the host's, and the host's card-database FILE was changed this session so it no longer matches what the host is running - the HOST has to RESTART the game before it can be auto-synced to you (conflicting: "
                                        + DescribeConflicts(conflicts) + ")");
                                    break;
                                }
                                // BOUNDED RETRY (1.0.36; was a one-shot loop-breaker). The premise
                                // this block was built on turned out to be wrong: EPL does NOT
                                // re-mint the registry from local content every boot, it LOADS the
                                // file and reuses every saved id verbatim (see
                                // ModParity.EnumFilePath). So our copy DOES survive the restart it
                                // demands, and sending it really is the fix - the mismatch is
                                // usually nothing but the same content packs installed in a
                                // different ORDER. What the old one-shot rule actually caught was
                                // guests who never APPLIED the file (no full restart, a failed
                                // write, or a host gated by the borrowed-registry branch above), and
                                // those deserve another copy plus a blunter instruction, not a
                                // permanent refusal. So: up to EnumSyncMaxSends attempts per (peer,
                                // registry), then the honest terminal message - the loop-breaker's
                                // original job, which is to never promise a restart forever.
                                string peerKey = PeerSyncKey(name, theirEnumDigest);
                                int sentBefore;
                                _enumSyncSentTo.TryGetValue(peerKey, out sentBefore);
                                // ...and the digest-free ceiling. A guest whose EPL re-mints ids on
                                // every boot presents a NEW digest each time, so the key above is
                                // fresh each time and its budget never runs out - see
                                // _enumSyncSentToPeer for why both counters are needed.
                                string peerNameKey = PeerSyncKey(name, null);
                                int sentToPeer;
                                _enumSyncSentToPeer.TryGetValue(peerNameKey, out sentToPeer);
                                if (sentBefore < EnumSyncMaxSends && sentToPeer < EnumSyncMaxSendsPerPeer)
                                {
                                    // send our registry along with the rejection: the client
                                    // backs theirs up, installs ours, and only has to restart -
                                    // no more hand-copying enum_values.json between PCs
                                    bool sent = false;
                                    string failReason = null;
                                    try
                                    {
                                        string enumPath = Util.ModParity.EnumFilePath();
                                        if (!System.IO.File.Exists(enumPath))
                                        {
                                            // Distinct from an IO failure and worth naming: a host
                                            // whose registry file is missing has nothing to give.
                                            failReason = "the host has no card-database file on disk to send";
                                        }
                                        else
                                        {
                                            var enumBytes = System.IO.File.ReadAllBytes(enumPath);
                                            var gz = Msg.Gzip(enumBytes);
                                            Send(msg.ConnId, new EnumSyncMessage { Data = gz });
                                            // ONLY a peer we actually shipped the file to counts as
                                            // synced. Counting a failed read/send (missing file,
                                            // locked by EPL or antivirus, permissions) promised a
                                            // restart that could not possibly help, and burned one
                                            // of the attempts on something that never happened.
                                            _enumSyncSentTo[peerKey] = sentBefore + 1;
                                            _enumSyncSentToPeer[peerNameKey] = sentToPeer + 1;
                                            sent = true;
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        failReason = e.Message;
                                        CoopPlugin.Log.LogWarning("enum sync send: " + e.Message);
                                    }
                                    string why = DescribeConflicts(conflicts);
                                    string reject;
                                    if (!sent)
                                    {
                                        // Name the actual reason when we know it - "match your
                                        // content packs manually" alone told the player nothing
                                        // about which machine had the problem.
                                        reject = "your custom-card database conflicts with the host's, and the host could not send its card database"
                                            + (failReason != null ? " (" + failReason + ")" : "")
                                            + " - ask the host to check that "
                                            + "AppData\\LocalLow\\OPNeonGames\\Card Shop Simulator\\PrefabLoader\\enum_values.json exists and is readable, or copy it across by hand (conflicting: "
                                            + why + ")";
                                    }
                                    else if (sentBefore == 0)
                                    {
                                        // Careful with the claims here: the guest only WRITES the
                                        // file if their AutoSyncCardDatabase option is on, and they
                                        // may have had no registry of their own to back up.
                                        reject = "your custom-card database conflicts with the host's - the host's copy has just been sent to you, and (unless you switched auto-sync off) saved on your PC with your old file backed up first. Now QUIT THE GAME TO DESKTOP, start it again, then join: the ids are only read while the game is booting, so nothing changes until you do (conflicting: "
                                            + why + ")";
                                    }
                                    else
                                    {
                                        // Same digest as last time: their registry is byte-for-byte
                                        // the identity they sent before, so the file we sent was
                                        // never loaded. Say that plainly rather than repeating the
                                        // first message word for word.
                                        reject = "your card database is UNCHANGED since the last sync, so the host's copy never took effect - usually because the game was not fully closed (returning to the title screen is not enough), or because auto-sync is switched off on your side. It has been sent again: QUIT TO DESKTOP, start the game, then join (conflicting: "
                                            + why + ")";
                                    }
                                    RejectConn(msg.ConnId, reject);
                                }
                                else
                                {
                                    // Out of attempts. The terminal message must be TRUE: syncing
                                    // does work, so the fault is that it is not being applied - the
                                    // old wording ("the game rebuilds it from YOUR content packs, so
                                    // copying the host's file cannot fix this") was wrong in both
                                    // halves and sent people off to reinstall content packs that
                                    // were never the problem.
                                    //
                                    // WHAT THIS MESSAGE MUST NOT SAY: "check the host isn't on a
                                    // borrowed card database". We only get here with a registry that
                                    // RegistryFileMatchesRuntime() already vouched for - the file we
                                    // sent IS the id space this host is running - so that advice is
                                    // inapplicable, and a host who follows it restores a registry
                                    // their own shop save was never written under and corrupts it.
                                    // The guest-side cause the two-send messages already name is the
                                    // one that belongs here: auto-sync switched off means the file
                                    // never landed at all.
                                    //
                                    // Two ways to land here and the wording has to be true for both:
                                    // the per-digest budget ran out (their registry never changed), or
                                    // the per-peer ceiling did (their registry changes every boot, so
                                    // the per-digest budget alone would never have ended). Only the
                                    // first may claim the file is unchanged.
                                    bool digestHeld = sentBefore >= EnumSyncMaxSends;
                                    int sendsMade = digestHeld ? sentBefore : sentToPeer;
                                    RejectConn(msg.ConnId,
                                        "your card database still conflicts after " + sendsMade + " sync"
                                        + (sendsMade == 1 ? "" : "s") + " from the host"
                                        + (digestHeld
                                            ? " and has not changed at all"
                                            : " and keeps coming back DIFFERENT each time (your card ids are being re-minted every boot)")
                                        + ", so the host's copy is not being applied on your PC. Syncing normally DOES fix this - the ids usually differ only because the same content packs were installed in a different order. Check, in this order: (1) you fully quit the game to DESKTOP after the sync and started it again (returning to the title screen is not enough); (2) " + CoopPlugin.Name + "'s AutoSyncCardDatabase option is ON on YOUR side - with it off nothing is ever written to your PC; (3) failing that, copy the host's enum_values.json from AppData\\LocalLow\\OPNeonGames\\Card Shop Simulator\\PrefabLoader into the same folder on your PC by hand and restart (conflicting: "
                                        + DescribeConflicts(conflicts) + ")");
                                }
                                break;
                            }
                            // Custom CreateCards/CardForge cards aren't covered by the enum
                            // registry above, so check their ID mapping directly. These are
                            // loose files we can't auto-sync, so it's a clear hard stop.
                            if (cardsHash != Util.ModParity.CardsHash())
                            {
                                // FIX E3: name the differing custom cards when we can. Entries
                                // are "MonsterType=ID", so a same-name different-ID is an ID
                                // clash (the exact desync). Generic fallback keeps the actionable
                                // "share the CardForge package" guidance.
                                string detail = DescribeModDiff(theirCards, Util.ModParity.CardsList(),
                                    "custom cards differ - ", "ID differs");
                                RejectConn(msg.ConnId, detail
                                    ?? "your custom cards differ from the host's - both players need the same custom cards installed (identical files + IDs), then restart. Share the exact card package (e.g. from CardForge).");
                                break;
                            }

                            _peerWireNames[msg.ConnId] = name;
                            _peerSteamIds[msg.ConnId] = hello.SteamId;
                            name = ResolvePeerName(hello.SteamId, name);
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
                        if (Role != CoopRole.Client)
                            break;
                        if (msg.Message is WelcomeMessage welcome)
                        {
                            int wireVersion = welcome.WireVersion;
                            if (wireVersion != Msg.WireVersion)
                            {
                                Shutdown("wire protocol mismatch");
                                break;
                            }
                            if (!string.Equals(welcome.Version, CoopPlugin.Version, StringComparison.Ordinal))
                            {
                                Shutdown("plugin version mismatch (host " + welcome.Version + ", client " + CoopPlugin.Version + ")");
                                break;
                            }
                            if (welcome.SaveLength <= 0 || welcome.SaveLength > MaxWorldTransferBytes
                                || welcome.BundleLength < 0 || welcome.BundleLength > MaxWorldTransferBytes)
                            {
                                CoopPlugin.Log.LogError($"Welcome advertised invalid transfer lengths save={welcome.SaveLength} bundle={welcome.BundleLength} (cap {MaxWorldTransferBytes})");
                                Shutdown("invalid world download lengths");
                                break;
                            }
                            string hostName = ResolvePeerName(welcome.SteamId, welcome.HostName ?? "");
                            _saveExpected = welcome.SaveLength;
                            _hostSlot = welcome.HostSlot;
                            _bundleExpected = welcome.BundleLength;
                            _selfId = welcome.SelfId;
                            // ID TRANSLATION IS BUILT HERE. "Welcome is the first message of the
                            // session, so every later message is already translated" was the comment
                            // that used to sit here and it is FALSE - do not rely on it.
                            //
                            // THE REAL WINDOW: SendWorldTo snapshots the host's registry on the main
                            // thread but builds and sends the Welcome from a WORKER thread, while the
                            // main thread carries on broadcasting. Roster, PriceList and LicenseState
                            // can therefore reach a client BEFORE its Welcome, and on Steam a
                            // PlayerState riding the transient lane can overtake the reliable one and
                            // do the same. Those messages are dispatched with EnumMap inactive, i.e.
                            // UNTRANSLATED.
                            //
                            // WHY THAT IS ACCEPTABLE TODAY: a session only gets this far when the
                            // Hello conflict gate found no "same name, different id" between the two
                            // registries, so on every ALLOWED session the ids in that pre-Welcome
                            // traffic mean the same thing on both PCs - the translation would be the
                            // identity function anyway. This is exactly 1.0.35 behavior, unchanged.
                            //
                            // Both blobs fail to an EMPTY list (missing, truncated, not gzip, over the
                            // cap), and an empty list leaves that half of the map unbuilt, which is
                            // the identity function - today's behavior, never garbage.
                            var hostEnumLines = ReadCappedEnumBlob(welcome.HostEnumBlob, out _);
                            var hostCardLines = ReadCappedEnumBlob(welcome.HostCardsBlob, out _);
                            if (hostEnumLines.Count == 0 && hostCardLines.Count == 0)
                                CoopPlugin.Log.LogWarning("no registry from the host - modded ids will NOT be translated this session. Causes, in order of likelihood: the host is vanilla (fine, nothing to translate); or the host's registry was too big for the wire cap or could not be read/unpacked (see the host's log for 'registry blob over cap' / 'over-cap' - in that case ids must already match on both PCs)");
                            Util.EnumMap.Build(hostEnumLines, hostCardLines);
                            // The PriceList swap buffers must start EMPTY under this session's brand-
                            // new id map. Shutdown clears them too, but a PriceList can legitimately
                            // land BEFORE this Welcome (see the window described above), and anything
                            // it recorded was recorded UNTRANSLATED - i.e. in the host's ids, read as
                            // ours. Residue like that survives into the first translated PriceList,
                            // where the clear pass would zero real local prices for ids that never
                            // meant what they looked like. Dropping it costs nothing: the very next
                            // PriceList is a full sparse snapshot.
                            _clientPriced.Clear();
                            _incomingPriced.Clear();
                            PeerNames[msg.ConnId] = hostName;
                            _avatars.SetName(msg.ConnId, hostName);
                            // Publish the local appearance immediately. This gives the host and
                            // other clients a deterministic model even when the selector is never
                            // opened, while the UI can later submit richer CC slider data.
                            EnsureLocalPlayerModel();
                            SubmitLocalPlayerModel();
                            _saveBuf = new MemoryStream(1024);
                            _bundleBuf = new MemoryStream(1024);
                            StatusLine = $"Downloading {hostName}'s shop ({(_saveExpected + _bundleExpected) / 1024} KB)...";
                        }
                        break;
                    }
                case MsgType.SaveChunk:
                    {
                        if (Role != CoopRole.Client || _saveBuf == null)
                            break;
                        if (msg.Message is SaveChunkMessage saveChunk)
                        {
                            var bytes = saveChunk.Data ?? new byte[0];
                            if (_saveExpected >= 0 && _saveBuf.Length + bytes.Length > _saveExpected)
                            {
                                CoopPlugin.Log.LogError($"Save download exceeded advertised length {_saveExpected}; rejecting session");
                                Shutdown("bad download");
                                break;
                            }
                            _saveBuf.Write(bytes, 0, bytes.Length);
                            if (_saveExpected > 0)
                                StatusLine = $"downloading shop... {Math.Min(100, _saveBuf.Length * 100 / _saveExpected)}%";
                        }
                        break;
                    }
                case MsgType.SaveDone:
                    {
                        if (Role != CoopRole.Client || _saveBuf == null || _worldRequested)
                            break;
                        var data = _saveBuf.ToArray();
                        _saveBuf = null;
                        if (_saveExpected >= 0 && data.Length != _saveExpected)
                        {
                            ErrorLine = $"World download looked corrupted ({data.Length}/{_saveExpected} bytes) - try again.";
                            Shutdown("bad download");
                            break;
                        }
                        try
                        {
                            data = GunzipCappedBytes(data, Msg.MaxFrameSize);
                            if (data == null)
                                throw new InvalidDataException("save exceeded decompression cap");
                        }
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
                        if (Role != CoopRole.Client || _bundleBuf == null)
                            break;
                        if (msg.Message is BundleChunkMessage bundleChunk)
                        {
                            var bytes = bundleChunk.Data ?? new byte[0];
                            if (_bundleExpected >= 0 && _bundleBuf.Length + bytes.Length > _bundleExpected)
                            {
                                CoopPlugin.Log.LogError($"Bundle download exceeded advertised length {_bundleExpected}; rejecting session");
                                Shutdown("bad download");
                                break;
                            }
                            _bundleBuf.Write(bytes, 0, bytes.Length);
                            if (_bundleExpected > 0)
                                StatusLine = $"downloading mod data... {Math.Min(100, _bundleBuf.Length * 100 / _bundleExpected)}%";
                        }
                        break;
                    }
                case MsgType.BundleDone:
                    {
                        if (Role != CoopRole.Client || _worldRequested || _pendingSave == null)
                            break;
                        var bundle = _bundleBuf != null ? _bundleBuf.ToArray() : new byte[0];
                        _bundleBuf = null;
                        _worldRequested = true;
                        int transferGen = SessionGeneration;
                        byte[] saveBytes = _pendingSave;
                        _pendingSave = null;
                        StatusLine = "World received - loading...";
                        // GRADING OVERHAUL'S CERT STORE IS IN THAT BUNDLE, and its replacement is the
                        // single most consequential thing the sidecar does that nobody can see. GO
                        // keeps burned serials and cert->card bindings in
                        // <persistentDataPath>/Grading - Overhaul/GradingOverhaul_<slot>.json
                        // (decompiled-grading GetSaveFilePath/GetSaveDataDirectory :482-499), which
                        // matches the sidecar's slot pattern, so joining someone REPLACES it with
                        // theirs. GO then re-scans certs on every save load and rewrites any row it
                        // cannot vouch for to its FAKE encoding - which from this mod's side of the
                        // fence looks like graded cards silently vanishing. Stamped before and after
                        // rather than logged from inside ApplyBundle, which stays a dumb file copier -
                        // and stamped by CONTENT, because ApplyBundle rewrites this file on every join
                        // regardless of whether the host's copy differs, so any mtime-based test warns
                        // every single time and means nothing (see FileStamp).
                        string goStore = Path.Combine(Path.Combine(Application.persistentDataPath, "Grading - Overhaul"),
                            "GradingOverhaul_" + SaveTransfer.CoopSlot + ".json");
                        string goBefore = FileStamp(goStore);
                        try
                        {
                            if (bundle.Length > 0)
                                bundle = GunzipCappedBytes(bundle, Msg.MaxFrameSize);
                            if (bundle == null)
                                throw new InvalidDataException("bundle exceeded decompression cap");
                        }
                        catch (Exception e)
                        {
                            ErrorLine = "Mod data could not be unpacked - try again.";
                            CoopPlugin.Log.LogError("coop: sidecar unpack failed: " + e);
                            Shutdown("bad sidecar download");
                            break;
                        }
                        SidecarTransfer.ApplyBundleAsync(bundle, _hostSlot, SaveTransfer.CoopSlot, transferGen,
                            () =>
                            {
                                if (FileStamp(goStore) != goBefore)
                                    CoopPlugin.Log.LogWarning("Grading Overhaul cert store replaced by the host's copy for the borrowed world: "
                                        + goStore + " - your own SOLO save slots are untouched, but graded cards in THIS co-op slot are now judged "
                                        + "against the host's burned serials and cert bindings, and any this PC issued itself can be flagged FAKE on the next load. "
                                        + "The previous file was kept once as .coopbak beside it.");
                                SaveTransfer.ApplyAndLoadAsync(saveBytes, transferGen,
                                    () => { },
                                    e =>
                                    {
                                        ErrorLine = "Could not apply the received world: " + e.Message;
                                        CoopPlugin.Log.LogError("coop: world apply failed: " + e);
                                        Shutdown("world apply failed");
                                    });
                            },
                            e =>
                            {
                                ErrorLine = "Could not apply mod data: " + e.Message;
                                CoopPlugin.Log.LogError("coop: sidecar apply failed: " + e);
                                Shutdown("sidecar apply failed");
                            });
                        // the game's world-(re)load teardown (LoadInteractableObjectData ->
                        // RestockManager.DestroyAllObject) destroys every existing box via
                        // OnDestroyed - if a world was live (rejoin, or solo save loaded
                        // while waiting for the invite) a 1.0.7 client forwarded all ~250
                        // as player trash actions, wiping the HOST's boxes (first field
                        // report). Suppress until vanilla reports that the world is settled.
                        ClientReloading = true;
                        _clientWorldArrived = false;
                        _reloadStartedAt = Time.realtimeSinceStartup;
                        _reloadStartedFrame = Time.frameCount;
                        break;
                    }
                case MsgType.EnumSync:
                    {
                        if (Role != CoopRole.Client)
                            break;
                        if (msg.Message is EnumSyncMessage enumSync)
                        {
                            var hostBytes = GunzipCappedBytes(enumSync.Data, Msg.MaxFrameSize);
                            if (hostBytes == null)
                                throw new InvalidDataException("enum sync exceeded decompression cap");
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
                case MsgType.Bye:
                    {
                        string reason = "the host ended the session";
                        var bye = msg.Message as ByeMessage;
                        if (bye != null && !string.IsNullOrEmpty(bye.Reason))
                            reason = bye.Reason;
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
            return true;
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
                // the base save is snapshotted to the THROWAWAY slot (SaveTransfer.HostSnapshotSlot)
                // and the sidecar-mod files must come from that SAME slot: SaveGameData(6) fires
                // the sidecar mods' own save hooks (they key off the active slot), so slot 6
                // holds the FRESH per-save mod data matching the just-written world - while the
                // host's real slot may be an hour stale. Mismatched halves shipped stale card
                // prices/shop config to the joiner. The same slot digit rides the wire so the
                // client's rename (slot 6 -> its coop slot) stays symmetric.
                rawSave = SaveTransfer.BuildHostPayload();
                hostSlot = SaveTransfer.HostSnapshotSlot;
                try
                {
                    rawBundle = SidecarTransfer.BuildBundle(hostSlot);
                }
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

            // The HOST's id space is the canonical one for this session, so the client needs our
            // registry (EPL enums) and our custom-card list (CreateCards/CardForge) to translate
            // by NAME at the wire boundary - see Util.EnumMap. Snapshotted HERE, on the main
            // thread, because both readers walk loaded types and the BepInEx plugin list; the
            // send itself runs on the worker below. Same encoding as the Hello blob, same cap,
            // same fail-to-empty behavior (an empty blob leaves the client on identity, i.e. on
            // exactly what this build did before translation existed).
            byte[] gzHostEnum = GzipLines(SafeEnumLines());
            byte[] gzHostCards = GzipLines(SafeCardsList());
            PlayerModelStateMessage modelState = Role == CoopRole.Host ? BuildPlayerModelState() : null;

            var net = _net;
            new Thread(() =>
            {
                const int chunk = 128 * 1024;
                try
                {
                    byte[] payload = Msg.Gzip(rawSave);
                    byte[] bundle = rawBundle.Length > 0 ? Msg.Gzip(rawBundle) : rawBundle;
                    CoopPlugin.Log.LogInfo($"transfer: save {payload.Length / 1024} KB, mod data {bundle.Length / 1024} KB (compressed)");

                    net.Send(connId, new WelcomeMessage
                    {
                        WireVersion = Msg.WireVersion,
                        Version = CoopPlugin.Version,
                        HostName = EffectivePlayerName,
                        SteamId = _steam == null ? 0 : _steam.LocalSteamId,
                        SaveLength = payload.Length,
                        HostSlot = hostSlot,
                        BundleLength = bundle.Length,
                        SelfId = connId,
                        HostEnumBlob = gzHostEnum,
                        HostCardsBlob = gzHostCards,
                    });

                    for (int off = 0; off < payload.Length; off += chunk)
                    {
                        int len = Math.Min(chunk, payload.Length - off);
                        int o = off;
                        var chunkBytes = new byte[len];
                        Buffer.BlockCopy(payload, o, chunkBytes, 0, len);
                        net.Send(connId, new SaveChunkMessage { Offset = o, Data = chunkBytes });
                    }
                    net.Send(connId, new SaveDoneMessage { TotalLength = payload.Length });

                    for (int off = 0; off < bundle.Length; off += chunk)
                    {
                        int len = Math.Min(chunk, bundle.Length - off);
                        int o = off;
                        var chunkBytes = new byte[len];
                        Buffer.BlockCopy(bundle, o, chunkBytes, 0, len);
                        net.Send(connId, new BundleChunkMessage { Offset = o, Data = chunkBytes });
                    }
                    net.Send(connId, new BundleDoneMessage { TotalLength = bundle.Length });
                    if (modelState != null)
                        net.Send(connId, modelState);
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("World send failed: " + e.Message);
                }
            })
            {
                IsBackground = true,
                Name = "CoopWorldSend"
            }.Start();
        }

        private void OnGUI()
        {
            _ui.Draw(this, _net);
        }

        private void ApplyShopName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return;
            if (CPlayerData.GetPlayerName() != name)
            {
                CPlayerData.PlayerName = name;
                CoopPlugin.Log.LogInfo("shop name synced: " + name);
            }
            _lastShopNameApplied = name;
            if (_shopSign != null)
            {
                try
                {
                    _shopSign.text = name;
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
        }

        private void ApplyRoster(RosterMessage roster)
        {
            if (Role != CoopRole.Client)
                return;
            var seen = new HashSet<int>();
            foreach (var entry in roster.Entries)
            {
                int id = entry.Id;
                if (id == _selfId)
                    continue;
                seen.Add(id);
                string name = ResolvePeerName(entry.SteamId, entry.Name ?? "");
                _rosterNames[id] = name;
                if (_relayIds.Add(id))
                    CoopPlugin.Log.LogInfo("peer in shop: " + name);
                _avatars.SetName(1000 + id, name);
            }
            _relayIds.RemoveWhere(id =>
            {
                if (seen.Contains(id))
                    return false;
                _avatars.Remove(1000 + id);
                return true;
            });
        }

        private void ApplyPlayerState(int avatarId, PlayerStateMessage state, bool directPeer)
        {
            _diagRecvStates++;
            _avatars.UpdateState(avatarId, state.Position, state.Yaw,
                state.Hold, state.CameraPosition, state.CameraRotation, state.HoldTypes, state.HoldCards);
            string peerName = null;
            if (!PeerNames.TryGetValue(avatarId, out peerName) && avatarId >= 1000)
                _rosterNames.TryGetValue(avatarId - 1000, out peerName);
            if (!string.IsNullOrEmpty(peerName))
                _avatars.SetName(avatarId, peerName);

            if (directPeer && Role == CoopRole.Host && _net.ConnectionCount > 1)
            {
                var relay = new RelayStateMessage
                {
                    SenderId = avatarId,
                    State = state
                };
                foreach (int cid in _net.ConnIds())
                    if (cid != avatarId)
                        _net.SendTransient(cid, relay);
            }

            if (directPeer && _gotStateFrom.Add(avatarId))
            {
                string who = PeerNames.TryGetValue(avatarId, out var name) ? name : ("player " + avatarId);
                CoopPlugin.Log.LogInfo("Position link active with " + who);
                if (Role == CoopRole.Host)
                    StatusLine = "Hosting - " + who + " is in your shop!";
            }
        }

        public bool TryGetAvatarCamera(int avatarId, out Vector3 position, out Quaternion rotation)
        {
            return _avatars.TryGetPlacementCamera(avatarId, out position, out rotation);
        }

        private void ApplyPurchaseRequest(int connectionId, PurchaseRequestMessage request)
        {
            if (Role != CoopRole.Host || !InGameLevel() || request == null
                || request.Lines == null || request.Lines.Count == 0)
                return;
            if (request.Kind > 2)
            {
                CoopPlugin.Log.LogWarning($"purchase request from conn {connectionId} has invalid kind {request.Kind}");
                return;
            }

            Action<bool, string> result = (success, text) =>
                Send(connectionId, new PurchaseResultMessage { Kind = request.Kind, Success = success, Text = text });

            var resolved = new List<int>(request.Lines.Count);
            var linePrices = new List<double>(request.Lines.Count);
            double total = 0.0;
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (line == null || line.Count <= 0)
                {
                    result(false, "purchase cancelled - invalid request");
                    return;
                }
                int index = -1;
                float price = 0f;
                try
                {
                    if (request.Kind == 0)
                    {
                        index = ResolveRestockIndex(line.ItemType, line.IsBig, line.Name ?? "", out _);
                        if (index >= 0)
                        {
                            var rd = CatalogAt(index);
                            price = CPlayerData.GetItemCost(rd.itemType) * RestockManager.GetMaxItemCountInBox(rd.itemType, rd.isBigBox) * line.Count;
                        }
                    }
                    else if (request.Kind == 1)
                    {
                        var fp = InventoryBase.GetFurniturePurchaseData((EObjectType)line.ItemType);
                        if (fp != null && InventoryBase.GetSpawnInteractableObjectPrefab((EObjectType)line.ItemType) != null)
                        {
                            index = line.ItemType;
                            price = fp.price;
                        }
                    }
                    else
                    {
                        index = ResolveRestockIndex(line.ItemType, line.IsBig, line.Name ?? "", out _);
                        if (index >= 0)
                        {
                            var rd = CatalogAt(index);
                            if (CPlayerData.GetIsItemLicenseUnlocked(index))
                                price = 0f;
                            else
                                price = rd.licensePrice;
                        }
                    }
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning($"purchase preflight failed for conn {connectionId}, line {i}: {e.Message}");
                    index = -1;
                }
                if (index < 0)
                {
                    result(false, "purchase cancelled - the item is not in the host's catalog");
                    return;
                }
                resolved.Add(index);
                double linePrice = Math.Max(0f, price);
                if (double.IsNaN(linePrice) || double.IsInfinity(linePrice))
                {
                    CoopPlugin.Log.LogWarning($"purchase preflight produced invalid price for conn {connectionId}, line {i}");
                    result(false, "purchase cancelled - the host returned an invalid price");
                    return;
                }
                linePrices.Add(linePrice);
                total += linePrice;
            }

            double available = CPlayerData.m_CoinAmountDouble - _pendingReduceThisFrame;
            if (total > available + 0.0001)
            {
                CoopPlugin.Log.LogInfo($"purchase from conn {connectionId} declined: need {total:F2}, available {available:F2}");
                result(false, "not enough money - the purchase was cancelled");
                return;
            }

            string who = PeerNames.TryGetValue(connectionId, out var name) ? name : "player";
            var failures = new List<string>();
            // Reserve the validated amount before delivery. Dispatch is serialized on
            // the Unity thread, but other operations can be accepted in this same frame;
            // reserving here prevents a successful delivery from later becoming a free
            // delivery when the wallet is checked again after spawning.
            _pendingReduceThisFrame += total;
            double deliveredTotal = 0.0;
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                try
                {
                    if (request.Kind == 0)
                    {
                        RestockManager.SpawnPackageBoxItemMultipleFrame(resolved[i], line.Count);
                        CEventManager.QueueEvent(new CEventPlayer_AddShopExp(line.Count * 5));
                    }
                    else if (request.Kind == 1)
                    {
                        CoopPlugin.Log.LogInfo($"{who} bought furniture: {(EObjectType)line.ItemType}");
                        ShelfManager.SpawnInteractableObjectInPackageBox((EObjectType)line.ItemType, line.Position, line.Rotation);
                    }
                    else if (!CPlayerData.GetIsItemLicenseUnlocked(resolved[i]))
                    {
                        if (!ApplyLicenseUnlock(line.ItemType, line.IsBig, line.Name ?? ""))
                            throw new InvalidOperationException("license unlock was not applied");
                        Broadcast(new LicenseUnlockMessage { ItemType = (EItemType)line.ItemType, IsBig = line.IsBig, RestockName = line.Name ?? "" });
                    }
                    deliveredTotal += linePrices[i];
                }
                catch (Exception e)
                {
                    string item = string.IsNullOrEmpty(line.Name) ? line.ItemType.ToString() : line.Name;
                    failures.Add($"'{item}' ({e.Message})");
                    CoopPlugin.Log.LogWarning($"purchase delivery failed for conn {connectionId}, line {i} ({item}): {e.Message}");
                }
            }

            // Release the reservation for failed lines; the remaining reservation is the
            // amount that will be backed by the queued authoritative coin reduction.
            _pendingReduceThisFrame -= total - deliveredTotal;
            bool charged = false;
            if (deliveredTotal > 0.0)
            {
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)deliveredTotal));
                charged = true;
            }

            if (failures.Count == 0)
            {
                result(true, "purchase accepted");
            }
            else
            {
                string detail = string.Join(", ", failures);
                string chargeNote = charged ? $" charged ${deliveredTotal:F0}" : " (not charged)";
                result(false, $"purchase partially completed - {detail}; delivered items{chargeNote}");
            }
        }

        private void ApplyPurchaseResult(PurchaseResultMessage result)
        {
            if (Role != CoopRole.Client || result == null)
                return;
            if (result.Success)
                Patches.GamePatches.ClientPurchaseAccepted(result.Kind);
            RegisterLine = result.Text ?? "";
            RegisterLineTimer = 8f;
            CoopPlugin.Log.LogInfo("host says: " + RegisterLine);
        }

        private void ApplyEconomyContribution(int connectionId, EconContributionMessage message)
        {
            // Client-supplied deltas are accepted by design (the joiner earns/spends locally
            // and forwards the result), but a non-finite value must never reach the shared
            // wallet: NaN fails every comparison, so it would slip past the affordability
            // guard below and permanently poison CPlayerData.m_CoinAmountDouble.
            float value = message.Value;
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                CoopPlugin.Log.LogWarning(
                    $"rejected non-finite economy contribution kind={message.Kind} conn={connectionId} value={value}");
                return;
            }
            switch (message.Kind)
            {
                case 1:
                    // An "add coin" of zero or less is not a contribution; a negative one
                    // would be a reduce with no affordability guard, so refuse it.
                    if (value <= 0f)
                        break;
                    CEventManager.QueueEvent(new CEventPlayer_AddCoin(value));
                    break;
                case 2:
                    if (value <= 0f)
                        break;
                    double balance = CPlayerData.m_CoinAmountDouble - _pendingReduceThisFrame;
                    if ((double)value > balance + 0.0001)
                    {
                        Send(connectionId, new ToastMessage { Text = "purchase declined - the shared wallet is short" });
                        _lastCoinSent = double.MinValue;
                    }
                    else
                    {
                        _pendingReduceThisFrame += value;
                        CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(value));
                    }
                    break;
                case 3:
                    CEventManager.QueueEvent(new CEventPlayer_AddShopExp((int)value));
                    break;
                case 4:
                    CEventManager.QueueEvent(new CEventPlayer_AddFame((int)value));
                    break;
            }
        }

        private void ApplyMovePreview(int connectionId, MovePreviewMessage message)
        {
            if (message == null)
                return;
            if (Role == CoopRole.Host)
            {
                // Never trust the sender-provided identity: the transport connection is the
                // authority for which player's preview this is.
                message.SourceId = connectionId;
                _movePreview.ApplyRemote(message, connectionId);
                if (_net != null)
                    foreach (int cid in _net.ConnIds())
                        if (cid != connectionId)
                            _net.SendTransient(cid, message);
            }
            else if (Role == CoopRole.Client)
            {
                _movePreview.ApplyRemote(message, message.SourceId);
            }
        }

        private void ApplySprayHit(SprayHitMessage message)
        {
            if (_cmSpray == null)
                _cmSpray = FindObjectOfType<CustomerManager>();
            if (_cmSpray == null)
                return;
            var customers = _cmSpray.GetCustomerList();
            for (int i = 0; i < customers.Count; i++)
                if (customers[i] != null)
                    customers[i].DeodorantSprayCheck(message.Position, message.Range, message.Potency);
        }

        private void ApplyGradedRemove(int connectionId, GradedRemoveMessage message)
        {
            var card = message.Card;
            if (card == null)
                return;
            if (!InGameLevel())
            {
                _pendingCardDeltas.Add(new PendingCard { IsAdd = false, Amount = 1, Card = card });
                return;
            }
            if (!ApplyCardDelta(false, 1, card, out bool relayAnyway) && !relayAnyway)
                return;
            if (_net == null)
                return;
            foreach (int id in _net.ConnIds())
                if (id != connectionId)
                    _net.Send(id, message);
        }
        private static bool IsSingleShotOp(MsgType type)
        {
            return type == MsgType.FurnitureBoxOp || type == MsgType.TradeOp
                || type == MsgType.RegisterOp || type == MsgType.RegisterRejected
                || type == MsgType.ShopOp
                || type == MsgType.SettingsOp || type == MsgType.TvOp
                || type == MsgType.StaffOp || type == MsgType.GradingOp
                || type == MsgType.ContainerOp;
        }

        private static byte[] GunzipCappedBytes(byte[] data, int cap)
        {
            using (var src = new MemoryStream(data, false))
            using (var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                var buffer = new byte[8192];
                int n;
                while ((n = gz.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (dst.Length + n > cap)
                        throw new InvalidDataException("decompressed payload exceeds cap " + cap);
                    dst.Write(buffer, 0, n);
                }
                return dst.ToArray();
            }
        }
    }
}
