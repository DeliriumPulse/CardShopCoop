using System;
using System.Collections.Generic;
using Steamworks;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Lobby lifecycle + invites. One long-lived instance: the GameLobbyJoinRequested
    /// callback must be listening from startup so accepting a Steam invite at any moment
    /// (or launching the game via an invite: +connect_lobby) starts the join flow.
    /// </summary>
    public class SteamLobby
    {
        private readonly LobbyRequestLifecycle _requests = new LobbyRequestLifecycle(LeaveMembership);
        private Callback<GameLobbyJoinRequested_t> _cbJoinRequested;
        private CallResult<LobbyMatchList_t> _lobbyList;

        public CSteamID LobbyId => new CSteamID(_requests.CurrentLobby);

        public Action<CSteamID> OnLobbyCreated;   // host: lobby is live
        public Action<CSteamID> OnEnteredLobby;   // client: joined; arg = lobby owner
        public Action<CSteamID> OnInviteAccepted; // local player accepted someone's invite
        public Action<string> OnSessionStartFailed;
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
            long attempt = _requests.Begin();
            CallResult<LobbyCreated_t> result = null;
            result = CallResult<LobbyCreated_t>.Create((e, ioFailure) =>
            {
                try
                {
                    var created = new CSteamID(e.m_ulSteamIDLobby);
                    bool success = !ioFailure && e.m_eResult == EResult.k_EResultOK
                        && created != CSteamID.Nil && created.IsValid();
                    if (!_requests.Complete(attempt, created.m_SteamID, success))
                        return;
                    if (!success)
                    {
                        string reason = ioFailure ? "Steam connection error"
                            : e.m_eResult != EResult.k_EResultOK ? e.m_eResult.ToString()
                            : "Steam returned an invalid lobby";
                        OnSessionStartFailed?.Invoke("Steam lobby creation failed: " + reason);
                        return;
                    }
                    SteamMatchmaking.SetLobbyData(created, "coopmod", "communitymultiplayer");
                    SteamMatchmaking.SetLobbyData(created, "coopver", CoopPlugin.Version);
                    string ownerName = CoopCore.Instance == null
                        ? CoopPlugin.PlayerName.Value
                        : CoopCore.Instance.EffectivePlayerName;
                    SteamMatchmaking.SetLobbyData(created, "name",
                        string.IsNullOrEmpty(lobbyName) ? (ownerName + "'s shop") : lobbyName);
                    SteamMatchmaking.SetLobbyData(created, "pw", hasPassword ? "1" : "0");
                    OnLobbyCreated?.Invoke(created);
                }
                finally
                {
                    result.Dispose();
                }
            });
            try
            {
                result.Set(SteamMatchmaking.CreateLobby(
                    isPublic ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeFriendsOnly, 4));
                if (!result.IsActive())
                    throw new InvalidOperationException("Steam did not start the lobby creation request. Please try again.");
            }
            catch
            {
                result.Dispose();
                _requests.Complete(attempt, 0, false);
                throw;
            }
        }

        /// <summary>Fetch public lobbies of THIS mod (server-side filtered by our key).</summary>
        public void RefreshList()
        {
            if (ListRefreshing)
                return;
            ListRefreshing = true;
            try
            {
                SteamMatchmaking.AddRequestLobbyListStringFilter("coopmod", "communitymultiplayer", ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListResultCountFilter(100);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
                var call = SteamMatchmaking.RequestLobbyList();
                _lobbyList.Set(call);
                if (!_lobbyList.IsActive())
                    throw new InvalidOperationException("Steam did not start the lobby search. Please try again.");
            }
            catch (Exception e)
            {
                ListRefreshing = false;
                OnError?.Invoke("Steam lobby list failed: " + e.Message);
            }
        }

        public void Join(CSteamID lobby)
        {
            long attempt = _requests.Begin(lobby.m_SteamID);
            // Bind to this API call, not the global LobbyEnter callback. A reply
            // from an abandoned invite must not attach to a later transport.
            CallResult<LobbyEnter_t> result = null;
            result = CallResult<LobbyEnter_t>.Create((e, ioFailure) =>
            {
                try
                {
                    var entered = new CSteamID(e.m_ulSteamIDLobby);
                    var response = (EChatRoomEnterResponse)e.m_EChatRoomEnterResponse;
                    bool success = !ioFailure && response == EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess
                        && entered != CSteamID.Nil && entered.IsValid();
                    if (!_requests.Complete(attempt, entered.m_SteamID, success))
                        return;
                    if (!success)
                    {
                        string reason = ioFailure ? "Steam connection error"
                            : response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess
                                ? response.ToString().Replace("k_EChatRoomEnterResponse", "")
                                : "Steam returned an invalid lobby";
                        OnSessionStartFailed?.Invoke("Could not enter Steam lobby: " + reason + ". Ask the host for a fresh invitation.");
                        return;
                    }
                    if (entered != lobby)
                    {
                        Leave();
                        OnSessionStartFailed?.Invoke("Steam returned a different lobby. Please accept a fresh invitation.");
                        return;
                    }
                    var owner = SteamMatchmaking.GetLobbyOwner(entered);
                    if (owner == CSteamID.Nil || !owner.IsValid() || owner == SteamUser.GetSteamID())
                    {
                        Leave();
                        OnSessionStartFailed?.Invoke("The Steam lobby no longer has a remote host. Ask your friend to host again.");
                        return;
                    }
                    OnEnteredLobby?.Invoke(owner);
                }
                finally
                {
                    result.Dispose();
                }
            });
            try
            {
                result.Set(SteamMatchmaking.JoinLobby(lobby));
                if (!result.IsActive())
                    throw new InvalidOperationException("Steam did not start the lobby join request. Please try again.");
            }
            catch
            {
                result.Dispose();
                _requests.Complete(attempt, 0, false);
                throw;
            }
        }

        public void OpenInviteDialog()
        {
            if (LobbyId != CSteamID.Nil)
                SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
        }

        public void Leave()
        {
            // A registered CallResult is held strongly by Steamworks' dispatcher.
            // Do not dispose pending results here: their eventual success must leave
            // the abandoned lobby. Each result disposes itself after delivery.
            _requests.Cancel();
        }

        private static void LeaveMembership(ulong lobby)
        {
            try
            {
                SteamMatchmaking.LeaveLobby(new CSteamID(lobby));
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }
    }
}
