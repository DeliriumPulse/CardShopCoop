using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Steam P2P transport: friends-list invites, no IPs, no port forwarding. Rides the
    /// game's own Steamworks.NET (initialized and pumped by its Heathen integration).
    /// Uses classic ISteamNetworking P2P with relay fallback (the protocol codec owns the
    /// 4-byte length prefix). Two outgoing lanes:
    /// transients (UnreliableNoDelay, newest-wins, dropped on refusal) drain before the
    /// stall-retried reliable lane, so a clogged bulk transfer can never delay position
    /// updates. All Steam calls happen in PumpMainThread; Send()/SendTransient() from
    /// worker threads only enqueue.
    /// </summary>
    public class SteamTransport : ICoopTransport
    {
        private const int Channel = 71; // stay clear of channel 0 (other mods)

        public ConcurrentQueue<InMsg> Incoming { get; } = new ConcurrentQueue<InMsg>();
        public ConcurrentQueue<int> Disconnects { get; } = new ConcurrentQueue<int>();
        public ConcurrentQueue<int> Connects { get; } = new ConcurrentQueue<int>();

        public INetMessage KeepaliveMessage;
        public double TimeoutSeconds => 180.0; // keepalives freeze with the main thread

        private readonly bool _isHost;
        private readonly Dictionary<int, CSteamID> _peers = new Dictionary<int, CSteamID>();
        private readonly Dictionary<CSteamID, int> _ids = new Dictionary<CSteamID, int>();
        private readonly Dictionary<int, double> _lastRecv = new Dictionary<int, double>();
        private struct Outgoing
        {
            public int ConnId; public byte[] Frame;
        }

        private readonly ConcurrentQueue<Outgoing> _transientOutbox = new ConcurrentQueue<Outgoing>();
        private readonly ConcurrentQueue<Outgoing> _reliableOutbox = new ConcurrentQueue<Outgoing>();
        private Outgoing? _stalled; // reliable frame Steam refused; retried first
        private int _stallRetries;  // consecutive refusals of _stalled; drop after N so one
                                    // doomed (e.g. >1MB) frame can't wedge the whole lane

        // transient coalescing scratch, reused every pump (main thread only)
        private readonly List<Outgoing> _transientScratch = new List<Outgoing>(32);
        private readonly Dictionary<int, int> _newestTransient = new Dictionary<int, int>(32);
        private double _lastTransientRefusedLog = -10.0;

        private List<int> _connIdsCache; // snapshot handed out by ConnIds(); rebuilt on membership change
        private int _nextConnId = 1;
        private byte[] _readBuf = new byte[600 * 1024];
        private float _keepaliveTimer;
        private bool _stopped;

        private Callback<P2PSessionRequest_t> _cbSessionReq;
        private Callback<P2PSessionConnectFail_t> _cbSessionFail;

        /// <summary>Lobby whose members are allowed to open sessions with us (host side).</summary>
        public CSteamID LobbyId = CSteamID.Nil;

        public SteamTransport(bool isHost)
        {
            _isHost = isHost;
            SteamNetworking.AllowP2PPacketRelay(true);
            _cbSessionReq = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            _cbSessionFail = Callback<P2PSessionConnectFail_t>.Create(OnSessionFail);
        }

        private void OnSessionRequest(P2PSessionRequest_t req)
        {
            if (_stopped)
                return;
            bool allowed;
            if (_isHost)
            {
                allowed = LobbyId != CSteamID.Nil && IsLobbyMember(req.m_steamIDRemote);
            }
            else
            {
                allowed = _ids.ContainsKey(req.m_steamIDRemote); // only the host we connected to
            }
            if (!allowed)
            {
                CoopPlugin.Log.LogWarning($"steam: rejected session from {req.m_steamIDRemote}");
                return;
            }
            SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
            if (!_ids.ContainsKey(req.m_steamIDRemote))
                AddPeer(req.m_steamIDRemote);
        }

        private bool IsLobbyMember(CSteamID user)
        {
            int n = SteamMatchmaking.GetNumLobbyMembers(LobbyId);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i) == user)
                    return true;
            return false;
        }

        private void OnSessionFail(P2PSessionConnectFail_t fail)
        {
            if (_ids.TryGetValue(fail.m_steamIDRemote, out int cid))
            {
                CoopPlugin.Log.LogWarning($"steam: session failed with {fail.m_steamIDRemote} (err {fail.m_eP2PSessionError})");
                Kick(cid);
            }
        }

        private int AddPeer(CSteamID sid)
        {
            int cid = _nextConnId++;
            _peers[cid] = sid;
            _ids[sid] = cid;
            _connIdsCache = null;
            _lastRecv[cid] = Time.realtimeSinceStartupAsDouble;
            Connects.Enqueue(cid);
            CoopPlugin.Log.LogInfo($"steam: peer {sid} connected as {cid}");
            return cid;
        }

        /// <summary>Client: bind connId 1 to the lobby owner. The first Send opens the session.</summary>
        public void ConnectToHost(CSteamID host)
        {
            AddPeer(host);
        }

        private void SendFrame(int connId, byte[] frame)
        {
            _reliableOutbox.Enqueue(new Outgoing { ConnId = connId, Frame = frame });
        }

        public void Send(int connId, INetMessage message)
        {
            SendFrame(connId, NetMessageCodec.Encode(message));
        }

        /// <summary>Main thread only: iterates _peers directly to avoid a per-call snapshot.</summary>
        private void BroadcastFrame(byte[] frame)
        {
            foreach (var kv in _peers)
                _reliableOutbox.Enqueue(new Outgoing { ConnId = kv.Key, Frame = frame });
        }

        public void Broadcast(INetMessage message)
        {
            BroadcastFrame(NetMessageCodec.Encode(message));
        }

        private void SendTransientFrame(int connId, byte[] frame)
        {
            _transientOutbox.Enqueue(new Outgoing { ConnId = connId, Frame = frame });
        }

        public void SendTransient(int connId, INetMessage message)
        {
            SendTransientFrame(connId, NetMessageCodec.Encode(message));
        }

        /// <summary>Main thread only: iterates _peers directly to avoid a per-call snapshot.</summary>
        private void BroadcastTransientFrame(byte[] frame)
        {
            foreach (var kv in _peers)
                _transientOutbox.Enqueue(new Outgoing { ConnId = kv.Key, Frame = frame });
        }

        public void BroadcastTransient(INetMessage message)
        {
            BroadcastTransientFrame(NetMessageCodec.Encode(message));
        }

        public void PumpMainThread()
        {
            if (_stopped)
                return;

            // ---- transient lane: drained fully every frame, ahead of the reliable lane,
            // so a clogged bulk transfer can never delay position updates. States replace
            // themselves (PlayerState/NpcState), so when several frames for the same
            // (conn, MsgType) are queued only the newest is worth sending. A refused send
            // is dropped - the next state tick supersedes it - but logged (throttled) so
            // oversized packets are visible instead of a silent crowd freeze.
            _transientScratch.Clear();
            _newestTransient.Clear();
            while (_transientOutbox.TryDequeue(out var tr))
            {
                if (!Msg.TryGetType(tr.Frame, out MsgType transientType))
                    continue; // malformed outbound data must not affect lane scheduling
                byte msgType = (byte)transientType;
                // CHUNKED transients carry a DIFFERENT slice of data per frame, so
                // newest-wins coalescing (right for a single replaceable state like
                // PlayerState) would drop every chunk but the last. NpcState splits a
                // big crowd across several packets - collapsing them capped the guest
                // at one chunk (~20 of 55 customers, trade-waiters among the lost);
                // RelayState multiplexes several senders and must survive whole too.
                if (msgType == (byte)MsgType.NpcState || msgType == (byte)MsgType.RelayState)
                {
                    _transientScratch.Add(tr);
                    continue;
                }
                int key = (tr.ConnId << 8) | msgType;
                if (_newestTransient.TryGetValue(key, out int prev))
                    _transientScratch[prev] = new Outgoing(); // superseded; null Frame skips it below
                _newestTransient[key] = _transientScratch.Count;
                _transientScratch.Add(tr);
            }
            for (int i = 0; i < _transientScratch.Count; i++)
            {
                var t = _transientScratch[i];
                if (t.Frame == null)
                    continue;
                if (!_peers.TryGetValue(t.ConnId, out var tsid))
                    continue; // peer gone
                if (!SteamNetworking.SendP2PPacket(tsid, t.Frame, (uint)t.Frame.Length,
                        EP2PSend.k_EP2PSendUnreliableNoDelay, Channel))
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (now - _lastTransientRefusedLog >= 10.0)
                    {
                        _lastTransientRefusedLog = now;
                        CoopPlugin.Log.LogWarning($"steam: transient packet refused (size {t.Frame.Length})");
                    }
                }
            }
            _transientScratch.Clear();

            // ---- reliable lane (byte-budgeted per frame; a refused frame stalls until
            // Steam accepts it). Plain Reliable (NOT WithBuffering, which adds ~200ms).
            int budget = 1024 * 1024;
            while (budget > 0)
            {
                Outgoing entry;
                if (_stalled.HasValue)
                {
                    entry = _stalled.Value;
                    _stalled = null;
                }
                else if (!_reliableOutbox.TryDequeue(out entry))
                    break;

                if (!_peers.TryGetValue(entry.ConnId, out var sid))
                {
                    _stallRetries = 0;
                    continue;
                } // peer gone
                if (!SteamNetworking.SendP2PPacket(sid, entry.Frame, (uint)entry.Frame.Length,
                        EP2PSend.k_EP2PSendReliable, Channel))
                {
                    // Steam refused it. Transient backpressure clears in a frame or two, but a
                    // frame Steam will NEVER accept (e.g. one that exceeds its ~1MB reliable
                    // ceiling) would retry forever and strand every prices/licenses/shelf/state
                    // packet behind it. Drop it after ~30 frames; change-gated state reheals.
                    if (++_stallRetries > 30)
                    {
                        CoopPlugin.Log.LogWarning($"steam: dropping stuck reliable frame (size {entry.Frame.Length}); lane reconverges on next heal");
                        _stalled = null;
                        _stallRetries = 0;
                        continue;
                    }
                    _stalled = entry; // retry next frame
                    break;
                }
                _stallRetries = 0; // success: clear the refusal streak
                budget -= entry.Frame.Length;
            }

            // ---- keepalive ----
            _keepaliveTimer += Time.unscaledDeltaTime;
            if (_keepaliveTimer >= 2f && KeepaliveMessage != null && _peers.Count > 0)
            {
                _keepaliveTimer = 0f;
                var frame = NetMessageCodec.Encode(KeepaliveMessage);
                foreach (var kv in _peers)
                    SteamNetworking.SendP2PPacket(kv.Value, frame, (uint)frame.Length,
                        EP2PSend.k_EP2PSendReliable, Channel);
            }

            // ---- receives ----
            while (SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
            {
                if (size > _readBuf.Length)
                    _readBuf = new byte[size];
                if (!SteamNetworking.ReadP2PPacket(_readBuf, (uint)_readBuf.Length, out uint msgSize, out CSteamID remote, Channel))
                    break;
                if (msgSize < Msg.MinimumFrameSize)
                    continue;

                if (!_ids.TryGetValue(remote, out int cid))
                {
                    // packet can beat the session callback on the host side
                    if (_isHost && LobbyId != CSteamID.Nil && IsLobbyMember(remote))
                        cid = AddPeer(remote);
                    else
                        continue;
                }
                _lastRecv[cid] = Time.realtimeSinceStartupAsDouble;

                if (Msg.TryDecodeFrame(_readBuf, 0, (int)msgSize, cid,
                    Msg.MaxFrameSize, out var message))
                    Incoming.Enqueue(message);
            }
        }

        public int ConnectionCount => _peers.Count;

        public double SecondsSinceLastRecv(int connId)
        {
            return _lastRecv.TryGetValue(connId, out double t)
                ? Time.realtimeSinceStartupAsDouble - t
                : double.MaxValue;
        }

        /// <summary>Returns a snapshot that is never mutated (callers Kick mid-iteration);
        /// membership changes swap in a fresh list instead of touching the old one.</summary>
        public List<int> ConnIds()
        {
            return _connIdsCache ?? (_connIdsCache = new List<int>(_peers.Keys));
        }

        public void Kick(int connId)
        {
            if (!_peers.TryGetValue(connId, out var sid))
                return;
            SteamNetworking.CloseP2PSessionWithUser(sid);
            _peers.Remove(connId);
            _ids.Remove(sid);
            _connIdsCache = null;
            _lastRecv.Remove(connId);
            Disconnects.Enqueue(connId);
        }

        public void Stop()
        {
            if (_stopped)
                return;
            _stopped = true;
            foreach (var kv in _peers)
                SteamNetworking.CloseP2PSessionWithUser(kv.Value);
            _peers.Clear();
            _ids.Clear();
            _connIdsCache = null;
            if (LobbyId != CSteamID.Nil)
            {
                try
                {
                    SteamMatchmaking.LeaveLobby(LobbyId);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                LobbyId = CSteamID.Nil;
            }
            _cbSessionReq?.Dispose();
            _cbSessionReq = null;
            _cbSessionFail?.Dispose();
            _cbSessionFail = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    /// <summary>
    /// Lobby lifecycle + invites. One long-lived instance: the GameLobbyJoinRequested
    /// callback must be listening from startup so accepting a Steam invite at any moment
    /// (or launching the game via an invite: +connect_lobby) starts the join flow.
    /// </summary>
    public class SteamLobby
    {
        private Callback<LobbyCreated_t> _cbCreated;
        private Callback<LobbyEnter_t> _cbEnter;
        private Callback<GameLobbyJoinRequested_t> _cbJoinRequested;
        private CallResult<LobbyMatchList_t> _lobbyList;

        public CSteamID LobbyId = CSteamID.Nil;
        private bool _joining;
        private bool _pendingPublic;
        private string _pendingName = "";
        private bool _pendingHasPw;

        public Action<CSteamID> OnLobbyCreated;   // host: lobby is live
        public Action<CSteamID> OnEnteredLobby;   // client: joined; arg = lobby owner
        public Action<CSteamID> OnInviteAccepted; // local player accepted someone's invite
        public Action<string> OnError;
        public Action OnListUpdated;

        public struct LobbyRow
        {
            public CSteamID Id;
            public string Name;
            public int Players;
            public int Max;
            public bool HasPw;
            public string Ver;
        }

        public readonly List<LobbyRow> Lobbies = new List<LobbyRow>();
        public bool ListRefreshing
        {
            get; private set;
        }

        public void Init()
        {
            _cbCreated = Callback<LobbyCreated_t>.Create(e =>
            {
                if (e.m_eResult != EResult.k_EResultOK)
                {
                    OnError?.Invoke("Steam lobby creation failed: " + e.m_eResult);
                    return;
                }
                LobbyId = new CSteamID(e.m_ulSteamIDLobby);
                SteamMatchmaking.SetLobbyData(LobbyId, "coopmod", "communitymultiplayer");
                SteamMatchmaking.SetLobbyData(LobbyId, "coopver", CoopPlugin.Version);
                string ownerName = CoopCore.Instance == null
                    ? CoopPlugin.PlayerName.Value
                    : CoopCore.Instance.EffectivePlayerName;
                SteamMatchmaking.SetLobbyData(LobbyId, "name",
                    string.IsNullOrEmpty(_pendingName) ? (ownerName + "'s shop") : _pendingName);
                SteamMatchmaking.SetLobbyData(LobbyId, "pw", _pendingHasPw ? "1" : "0");
                OnLobbyCreated?.Invoke(LobbyId);
            });
            _lobbyList = CallResult<LobbyMatchList_t>.Create((e, ioFail) =>
            {
                ListRefreshing = false;
                Lobbies.Clear();
                if (ioFail)
                {
                    OnError?.Invoke("Steam lobby list failed");
                    return;
                }
                for (int i = 0; i < e.m_nLobbiesMatching; i++)
                {
                    var id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id == CSteamID.Nil)
                        continue;
                    Lobbies.Add(new LobbyRow
                    {
                        Id = id,
                        Name = SteamMatchmaking.GetLobbyData(id, "name"),
                        Players = SteamMatchmaking.GetNumLobbyMembers(id),
                        Max = SteamMatchmaking.GetLobbyMemberLimit(id),
                        HasPw = SteamMatchmaking.GetLobbyData(id, "pw") == "1",
                        Ver = SteamMatchmaking.GetLobbyData(id, "coopver"),
                    });
                }
                OnListUpdated?.Invoke();
            });
            _cbEnter = Callback<LobbyEnter_t>.Create(e =>
            {
                if (!_joining)
                    return; // our own host-side enter
                _joining = false;
                LobbyId = new CSteamID(e.m_ulSteamIDLobby);
                var owner = SteamMatchmaking.GetLobbyOwner(LobbyId);
                OnEnteredLobby?.Invoke(owner);
            });
            _cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(e =>
            {
                OnInviteAccepted?.Invoke(e.m_steamIDLobby);
            });
        }

        public bool SteamAvailable()
        {
            try
            {
                return SteamAPI.IsSteamRunning();
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        public void Host(bool isPublic, string lobbyName, bool hasPassword)
        {
            _pendingPublic = isPublic;
            _pendingName = lobbyName ?? "";
            _pendingHasPw = hasPassword;
            SteamMatchmaking.CreateLobby(
                isPublic ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeFriendsOnly, 4);
        }

        /// <summary>Fetch public lobbies of THIS mod (server-side filtered by our key).</summary>
        public void RefreshList()
        {
            if (ListRefreshing)
                return;
            ListRefreshing = true;
            SteamMatchmaking.AddRequestLobbyListStringFilter("coopmod", "communitymultiplayer", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(100);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            var call = SteamMatchmaking.RequestLobbyList();
            _lobbyList.Set(call);
        }

        public void Join(CSteamID lobby)
        {
            _joining = true;
            SteamMatchmaking.JoinLobby(lobby);
        }

        public void OpenInviteDialog()
        {
            if (LobbyId != CSteamID.Nil)
                SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
        }

        public void Leave()
        {
            if (LobbyId != CSteamID.Nil)
            {
                try
                {
                    SteamMatchmaking.LeaveLobby(LobbyId);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                LobbyId = CSteamID.Nil;
            }
            _joining = false;
        }
    }

    /// <summary>
    /// The one and only <see cref="ISteamBridge"/> implementation, and the designated
    /// FAILURE ZONE. This type owns the SteamLobby and the SteamTransport it hands out,
    /// does every CSteamID&lt;-&gt;ulong conversion, and is allowed to fail to load: on a
    /// build with no com.rlabrecque.steamworks.net.dll (Game Pass, DRM-free) nothing ever
    /// reaches it, because <see cref="SteamBridge.TryCreate"/> checks
    /// <see cref="PlatformProbe.SteamworksPresent"/> first and is the only caller.
    ///
    /// Since 1.0.39 it also fails DELIBERATELY: the constructor probes the native Steam
    /// runtime and throws when there isn't one, so a Game Pass install carrying a stray but
    /// COMPLETE Steamworks dll (which loads perfectly, being pure managed code) degrades to
    /// the same null bridge as a build with no dll at all. See the constructor.
    ///
    /// NOTHING OUTSIDE THIS FILE MAY NAME THIS TYPE except SteamBridge.Create(). Referencing
    /// it from CoopCore/CoopUI/CoopPlugin would drag the Steamworks metadata straight back
    /// into an always-loaded type, which is exactly the bug the bridge exists to prevent.
    ///
    /// It also absorbs the lobby-callback wiring that used to live in CoopCore.Awake: the
    /// transport plumbing (which is Steam-typed) stays here, and only the Steam-free
    /// outcome - "lobby is live", "we're connected" - is raised to CoopCore.
    /// </summary>
    internal sealed class SteamBridgeImpl : ISteamBridge
    {
        private readonly SteamLobby _lobby = new SteamLobby();
        private SteamTransport _tx;

        /// <summary>Reused across calls: CoopUI's browser polls Lobbies every OnGUI frame
        /// (which runs 2+ times a frame), and a fresh list each time is pure garbage.</summary>
        private readonly List<LobbyRow> _rows = new List<LobbyRow>();

        /// <summary>
        /// RUNTIME VIABILITY PROBE (1.0.39). Constructing this type is NOT proof that Steam
        /// can work here. The field turned up two different Game Pass cohorts, and only one
        /// of them was already handled:
        ///
        ///  (1) STRIPPED ASSEMBLY - the stock Game Pass 0.70 build ships a cut-down
        ///      com.rlabrecque.steamworks.net where Steamworks.SteamAPI still exists (so
        ///      PlatformProbe.SteamworksPresent answers TRUE) but Callback`1 is gone. Every
        ///      field of this type and of SteamLobby is Steam-typed, so `new SteamBridgeImpl()`
        ///      dies at TYPE LOAD and SteamBridge.TryCreate already turns that into a null
        ///      bridge. Field-proven, verbatim: "Steam bridge unavailable: Could not load
        ///      type ... Callback`1". Nothing below is needed for this cohort.
        ///
        ///  (2) COMPLETE STRAY COPY - a Game Pass install where ANOTHER MOD bundled a full,
        ///      unstripped Steamworks.NET dll. Nothing faults, because the whole wrapper is
        ///      pure managed code: the bridge constructs, Init() wires its callbacks, and the
        ///      Steam UI comes alive on a build where Steam can never run. That is strictly
        ///      worse than hiding it - a dead "Host via Steam" button, a lobby browser that
        ///      P/Invokes unguarded and latches ListRefreshing forever, and "Steam isn't
        ///      running" messages that tell the player to go fix something they cannot fix.
        ///
        /// So ask the NATIVE layer, which is precisely what cohort (2) is missing.
        ///
        /// WHY SteamAPI.IsSteamRunning - VERIFIED, NOT ASSUMED. Checked against the shipped
        /// com.rlabrecque.steamworks.net.dll rather than taken on faith, because a wrapper that
        /// could answer from managed state would probe nothing at all. Its IL body is 11 bytes:
        /// `call InteropHelp.TestIfPlatformSupported; call NativeMethods.SteamAPI_IsSteamRunning;
        /// ret` - and that second method carries [DllImport("steam_api64", EntryPoint =
        /// "SteamAPI_IsSteamRunning", CallingConvention = Cdecl)]. It therefore CANNOT return
        /// without binding steam_api64.dll, which is the property we want. (SteamAPI.Init would
        /// bind it too, and is exactly the wrong call to make here: we RIDE the game's own
        /// Heathen-initialized Steamworks and must never fight it for ownership - see the
        /// SteamTransport remark at the top of this file.)
        ///
        /// ANY exception becomes a plain InvalidOperationException - DllNotFoundException (no
        /// steam_api64.dll beside the executable: the Game Pass case), EntryPointNotFoundException,
        /// a TypeLoadException out of a half-stripped InteropHelp, anything at all. The thrown
        /// type names nothing Steam-shaped, so SteamBridge.TryCreate's EXISTING catch handles it
        /// unchanged and every stray-dll machine degrades to the same clean LAN-only state the
        /// stripped-assembly machines already get, carrying the same one warning line.
        ///
        /// A NORMAL RETURN OF false IS DELIBERATELY NOT AN ERROR. That means "Steam is installed
        /// but the client isn't running" - a state the player can actually fix, and the exact
        /// distinction ISteamBridge documents and SteamAvailable() exists to report. Only a
        /// THROW proves the runtime isn't there at all. (Residual, and accepted: a Game Pass
        /// install carrying both the stray managed wrapper AND a stray steam_api64.dll, on a PC
        /// where the Steam client happens to be running, still answers true and still gets the
        /// Steam UI. No probe short of SteamAPI.Init can separate that case, and Init is the one
        /// call we are not allowed to make.)
        ///
        /// THIS MUST STAY IN THE CONSTRUCTOR AND MUST NOT MOVE INTO Init(). TryCreate's
        /// try/catch wraps only Create() - that is, `new SteamBridgeImpl()`. CoopCore calls
        /// Init() afterwards, outside any catch, so a throw from there would take CoopCore.Awake
        /// down with it and kill the whole mod instead of degrading it.
        /// </summary>
        public SteamBridgeImpl()
        {
            try
            {
                SteamAPI.IsSteamRunning();
            }
            catch (Exception e)
            {
                // include the real reason: TryCreate logs this message, and "which exception"
                // is what separates a Game Pass stray dll from a genuinely broken install.
                throw new InvalidOperationException(
                    "Steamworks runtime is not functional on this install (" +
                    e.GetType().Name + ": " + e.Message + ")");
            }
        }

        public Action<string> OnError
        {
            get; set;
        }
        public Action<ulong> OnLobbyLive
        {
            get; set;
        }
        public Action OnConnectedToHost
        {
            get; set;
        }
        public Action<ulong> OnInviteAccepted
        {
            get; set;
        }

        public void Init()
        {
            _lobby.Init();
            _lobby.OnError = e => OnError?.Invoke(e);
            _lobby.OnLobbyCreated = id =>
            {
                // Host side. The transport is always created BEFORE Host() is called
                // (see CreateTransport's contract), so _tx is non-null here in every
                // real flow; the guard is only for a Leave() racing the callback.
                if (_tx != null)
                    _tx.LobbyId = id;
                // .m_SteamID, not the struct: the boundary is what keeps CoopCore's handler
                // free of Steamworks metadata (see ISteamBridge).
                OnLobbyLive?.Invoke(id.m_SteamID);
            };
            _lobby.OnEnteredLobby = owner =>
            {
                // Client side. NOTE: the Role != Client guard that used to sit here now
                // lives on CoopCore's OnConnectedToHost handler - the bridge has no idea
                // what a CoopRole is. SteamLobby._joining already filters the host's own
                // lobby-enter, so this only fires on a real join.
                if (_tx == null)
                    return;
                _tx.LobbyId = _lobby.LobbyId;
                _tx.ConnectToHost(owner);
                OnConnectedToHost?.Invoke();
            };
            _lobby.OnInviteAccepted = id => OnInviteAccepted?.Invoke(id.m_SteamID);
            _lobby.OnListUpdated = () => { };
        }

        public bool SteamAvailable()
        {
            return _lobby.SteamAvailable();
        }

        public string LocalPersonaName
        {
            get
            {
                try
                {
                    return SteamAvailable() ? (SteamFriends.GetPersonaName() ?? "") : "";
                }
                catch (System.Exception e) { Swallow.Log(e); return ""; }
            }
        }

        public ulong LocalSteamId
        {
            get
            {
                try
                {
                    if (!SteamAvailable())
                        return 0;
                    return SteamUser.GetSteamID().m_SteamID;
                }
                catch (System.Exception e) { Swallow.Log(e); return 0; }
            }
        }

        public string FriendNickname(ulong steamId)
        {
            if (steamId == 0)
                return "";
            try
            {
                if (!SteamAvailable())
                    return "";
                var friend = new CSteamID(steamId);
                if (!SteamFriends.HasFriend(friend, EFriendFlags.k_EFriendFlagImmediate))
                    return "";
                return SteamFriends.GetFriendPersonaName(friend) ?? "";
            }
            catch (System.Exception e) { Swallow.Log(e); return ""; }
        }

        public ICoopTransport CreateTransport(bool isHost, INetMessage keepalive)
        {
            _tx = new SteamTransport(isHost) { KeepaliveMessage = keepalive };
            return _tx;
        }

        public void Host(bool isPublic, string lobbyName, bool hasPassword)
        {
            _lobby.Host(isPublic, lobbyName, hasPassword);
        }

        public void Join(ulong lobbyId)
        {
            _lobby.Join(new CSteamID(lobbyId));
        }

        public void Leave()
        {
            _lobby.Leave();
            // Drop the transport reference too: CoopCore disposes it separately, and a
            // stale one here would let a late lobby callback write into a dead transport.
            _tx = null;
        }

        public void OpenInviteDialog()
        {
            _lobby.OpenInviteDialog();
        }
        public void RefreshList()
        {
            _lobby.RefreshList();
        }
        public bool ListRefreshing
        {
            get
            {
                return _lobby.ListRefreshing;
            }
        }

        public List<LobbyRow> Lobbies
        {
            get
            {
                _rows.Clear();
                foreach (var r in _lobby.Lobbies)
                    _rows.Add(new LobbyRow
                    {
                        Id = r.Id.m_SteamID,
                        Name = r.Name,
                        Players = r.Players,
                        Max = r.Max,
                        HasPw = r.HasPw,
                        Ver = r.Ver,
                    });
                return _rows;
            }
        }
    }
}
