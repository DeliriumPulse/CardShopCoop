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
    /// Uses classic ISteamNetworking P2P with relay fallback; every frame is one reliable
    /// P2P message (Msg.Build's 4-byte length prefix is kept for wire compatibility and
    /// stripped on receive). All Steam calls happen in PumpMainThread; Send() from worker
    /// threads only enqueues.
    /// </summary>
    public class SteamTransport : ICoopTransport
    {
        private const int Channel = 71; // stay clear of channel 0 (other mods)

        public ConcurrentQueue<InMsg> Incoming { get; } = new ConcurrentQueue<InMsg>();
        public ConcurrentQueue<int> Disconnects { get; } = new ConcurrentQueue<int>();
        public ConcurrentQueue<int> Connects { get; } = new ConcurrentQueue<int>();

        public byte[] KeepaliveFrame;
        public double TimeoutSeconds => 180.0; // keepalives freeze with the main thread

        private readonly bool _isHost;
        private readonly Dictionary<int, CSteamID> _peers = new Dictionary<int, CSteamID>();
        private readonly Dictionary<CSteamID, int> _ids = new Dictionary<CSteamID, int>();
        private readonly Dictionary<int, double> _lastRecv = new Dictionary<int, double>();
        private readonly ConcurrentQueue<KeyValuePair<int, byte[]>> _outbox = new ConcurrentQueue<KeyValuePair<int, byte[]>>();
        private KeyValuePair<int, byte[]>? _stalled; // frame Steam refused; retried first
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
            if (_stopped) return;
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
                if (SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i) == user) return true;
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

        public void Send(int connId, byte[] frame)
        {
            _outbox.Enqueue(new KeyValuePair<int, byte[]>(connId, frame));
        }

        public void Broadcast(byte[] frame)
        {
            foreach (int cid in ConnIds())
                _outbox.Enqueue(new KeyValuePair<int, byte[]>(cid, frame));
        }

        public void PumpMainThread()
        {
            if (_stopped) return;

            // ---- sends (byte-budgeted per frame; a refused frame stalls until Steam accepts) ----
            int budget = 1024 * 1024;
            while (budget > 0)
            {
                KeyValuePair<int, byte[]> entry;
                if (_stalled.HasValue) { entry = _stalled.Value; _stalled = null; }
                else if (!_outbox.TryDequeue(out entry)) break;

                if (!_peers.TryGetValue(entry.Key, out var sid)) continue; // peer gone
                if (!SteamNetworking.SendP2PPacket(sid, entry.Value, (uint)entry.Value.Length,
                        EP2PSend.k_EP2PSendReliableWithBuffering, Channel))
                {
                    _stalled = entry; // Steam's queue is full; retry next frame
                    break;
                }
                budget -= entry.Value.Length;
            }

            // ---- keepalive ----
            _keepaliveTimer += Time.unscaledDeltaTime;
            if (_keepaliveTimer >= 2f && KeepaliveFrame != null && _peers.Count > 0)
            {
                _keepaliveTimer = 0f;
                foreach (var kv in _peers)
                    SteamNetworking.SendP2PPacket(kv.Value, KeepaliveFrame, (uint)KeepaliveFrame.Length,
                        EP2PSend.k_EP2PSendReliableWithBuffering, Channel);
            }

            // ---- receives ----
            while (SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
            {
                if (size > _readBuf.Length) _readBuf = new byte[size];
                if (!SteamNetworking.ReadP2PPacket(_readBuf, (uint)_readBuf.Length, out uint msgSize, out CSteamID remote, Channel))
                    break;
                if (msgSize < 5) continue;

                if (!_ids.TryGetValue(remote, out int cid))
                {
                    // packet can beat the session callback on the host side
                    if (_isHost && LobbyId != CSteamID.Nil && IsLobbyMember(remote)) cid = AddPeer(remote);
                    else continue;
                }
                _lastRecv[cid] = Time.realtimeSinceStartupAsDouble;

                int frameLen = BitConverter.ToInt32(_readBuf, 0);
                if (frameLen != (int)msgSize - 4 || frameLen < 1) continue;
                var payload = new byte[frameLen - 1];
                Buffer.BlockCopy(_readBuf, 5, payload, 0, frameLen - 1);
                Incoming.Enqueue(new InMsg { ConnId = cid, Type = (MsgType)_readBuf[4], Payload = payload });
            }
        }

        public int ConnectionCount => _peers.Count;

        public double SecondsSinceLastRecv(int connId)
        {
            return _lastRecv.TryGetValue(connId, out double t)
                ? Time.realtimeSinceStartupAsDouble - t
                : double.MaxValue;
        }

        public List<int> ConnIds() { return new List<int>(_peers.Keys); }

        public void Kick(int connId)
        {
            if (!_peers.TryGetValue(connId, out var sid)) return;
            SteamNetworking.CloseP2PSessionWithUser(sid);
            _peers.Remove(connId);
            _ids.Remove(sid);
            _lastRecv.Remove(connId);
            Disconnects.Enqueue(connId);
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            foreach (var kv in _peers)
                SteamNetworking.CloseP2PSessionWithUser(kv.Value);
            _peers.Clear();
            _ids.Clear();
            if (LobbyId != CSteamID.Nil)
            {
                try { SteamMatchmaking.LeaveLobby(LobbyId); } catch { }
                LobbyId = CSteamID.Nil;
            }
            _cbSessionReq?.Dispose(); _cbSessionReq = null;
            _cbSessionFail?.Dispose(); _cbSessionFail = null;
        }

        public void Dispose() { Stop(); }
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
        public bool ListRefreshing { get; private set; }

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
                SteamMatchmaking.SetLobbyData(LobbyId, "coopmod", "cardshopcoop");
                SteamMatchmaking.SetLobbyData(LobbyId, "coopver", CoopPlugin.Version);
                SteamMatchmaking.SetLobbyData(LobbyId, "name",
                    string.IsNullOrEmpty(_pendingName) ? (CoopPlugin.PlayerName.Value + "'s shop") : _pendingName);
                SteamMatchmaking.SetLobbyData(LobbyId, "pw", _pendingHasPw ? "1" : "0");
                OnLobbyCreated?.Invoke(LobbyId);
            });
            _lobbyList = CallResult<LobbyMatchList_t>.Create((e, ioFail) =>
            {
                ListRefreshing = false;
                Lobbies.Clear();
                if (ioFail) { OnError?.Invoke("Steam lobby list failed"); return; }
                for (int i = 0; i < e.m_nLobbiesMatching; i++)
                {
                    var id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id == CSteamID.Nil) continue;
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
                if (!_joining) return; // our own host-side enter
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
            try { return SteamAPI.IsSteamRunning(); }
            catch { return false; }
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
            if (ListRefreshing) return;
            ListRefreshing = true;
            SteamMatchmaking.AddRequestLobbyListStringFilter("coopmod", "cardshopcoop", ELobbyComparison.k_ELobbyComparisonEqual);
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
                try { SteamMatchmaking.LeaveLobby(LobbyId); } catch { }
                LobbyId = CSteamID.Nil;
            }
            _joining = false;
        }
    }
}
