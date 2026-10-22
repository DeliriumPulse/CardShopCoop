// Only the Steam boundary is simulated. Tests compile the shipped lobby handlers
// and deliver API results in controlled orders without loading Steam or Unity.
namespace Steamworks
{
    public readonly record struct CSteamID(ulong m_SteamID)
    {
        public static CSteamID Nil => default;
        public bool IsValid() => m_SteamID != 0;
    }
    public readonly record struct SteamAPICall_t(ulong Value)
    {
        public static SteamAPICall_t Invalid => default;
    }
    public enum EResult
    {
        k_EResultOK = 1, k_EResultFail = 2
    }
    public enum EChatRoomEnterResponse
    {
        k_EChatRoomEnterResponseSuccess = 1, k_EChatRoomEnterResponseError = 2
    }
    public enum ELobbyType
    {
        k_ELobbyTypePublic, k_ELobbyTypeFriendsOnly
    }
    public enum ELobbyComparison
    {
        k_ELobbyComparisonEqual
    }
    public enum ELobbyDistanceFilter
    {
        k_ELobbyDistanceFilterWorldwide
    }
    public struct LobbyCreated_t
    {
        public EResult m_eResult; public ulong m_ulSteamIDLobby;
    }
    public struct LobbyEnter_t
    {
        public uint m_EChatRoomEnterResponse; public ulong m_ulSteamIDLobby;
    }
    public struct LobbyMatchList_t
    {
        public uint m_nLobbiesMatching;
    }
    public struct GameLobbyJoinRequested_t
    {
        public CSteamID m_steamIDLobby;
    }

    public static class FakeSteam
    {
        public static readonly Dictionary<ulong, Action<object, bool>> Results = new();
        public static readonly List<(string Kind, SteamAPICall_t Handle)> Calls = new();
        public static readonly List<ulong> Left = new();
        public static readonly Dictionary<(ulong Lobby, string Key), string> Metadata = new();
        public static readonly Dictionary<ulong, CSteamID> Owners = new();
        public static readonly List<Exception> Logged = new();
        public static Action<object> Invite;
        public static bool InvalidNextCall, ThrowSearch;
        private static ulong _next;
        public static SteamAPICall_t Request(string kind)
        {
            var handle = InvalidNextCall ? SteamAPICall_t.Invalid : new SteamAPICall_t(++_next);
            InvalidNextCall = false;
            Calls.Add((kind, handle));
            return handle;
        }
        public static void Deliver<T>(SteamAPICall_t handle, T value, bool ioFailure = false)
        {
            var callback = Results[handle.Value];
            Results.Remove(handle.Value);
            callback(value, ioFailure);
        }
        public static void Reset()
        {
            Results.Clear();
            Calls.Clear();
            Left.Clear();
            Metadata.Clear();
            Owners.Clear();
            Logged.Clear();
            InvalidNextCall = ThrowSearch = false;
            Invite = null;
        }
    }
    public sealed class CallResult<T> : IDisposable
    {
        private readonly Action<T, bool> _callback;
        private SteamAPICall_t _handle;
        private CallResult(Action<T, bool> callback) => _callback = callback;
        public static CallResult<T> Create(Action<T, bool> callback) => new(callback);
        public void Set(SteamAPICall_t handle)
        {
            FakeSteam.Results.Remove(_handle.Value);
            _handle = handle;
            if (handle != SteamAPICall_t.Invalid)
                FakeSteam.Results[handle.Value] = (value, failed) =>
                {
                    _handle = SteamAPICall_t.Invalid;
                    _callback((T)value, failed);
                };
        }
        public bool IsActive() => _handle != SteamAPICall_t.Invalid;
        public void Dispose()
        {
            FakeSteam.Results.Remove(_handle.Value);
            _handle = SteamAPICall_t.Invalid;
        }
    }
    public sealed class Callback<T>
    {
        public static Callback<T> Create(Action<T> callback)
        {
            FakeSteam.Invite = value => callback((T)value);
            return new();
        }
    }
    public static class SteamMatchmaking
    {
        public static SteamAPICall_t CreateLobby(ELobbyType type, int maximum) => FakeSteam.Request("create");
        public static SteamAPICall_t JoinLobby(CSteamID lobby) => FakeSteam.Request("join");
        public static void LeaveLobby(CSteamID lobby) => FakeSteam.Left.Add(lobby.m_SteamID);
        public static bool SetLobbyData(CSteamID lobby, string key, string value)
        {
            FakeSteam.Metadata[(lobby.m_SteamID, key)] = value;
            return true;
        }
        public static CSteamID GetLobbyOwner(CSteamID lobby) => FakeSteam.Owners.GetValueOrDefault(lobby.m_SteamID, new CSteamID(42));
        public static void AddRequestLobbyListStringFilter(string key, string value, ELobbyComparison comparison)
        {
            if (FakeSteam.ThrowSearch)
                throw new InvalidOperationException("Simulated Steam search failure");
        }
        public static void AddRequestLobbyListResultCountFilter(int count)
        {
        }
        public static void AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter distance)
        {
        }
        public static SteamAPICall_t RequestLobbyList() => FakeSteam.Request("search");
        public static CSteamID GetLobbyByIndex(int index) => new((ulong)index + 200);
        public static string GetLobbyData(CSteamID lobby, string key) => "test";
        public static int GetNumLobbyMembers(CSteamID lobby) => 2;
        public static int GetLobbyMemberLimit(CSteamID lobby) => 4;
    }
    public static class SteamUser
    {
        public static CSteamID GetSteamID() => new(1);
    }
    public static class SteamFriends
    {
        public static void ActivateGameOverlayInviteDialog(CSteamID lobby)
        {
        }
    }
    public static class SteamAPI
    {
        public static bool IsSteamRunning() => true;
    }
}

namespace CardShopCoop
{
    public static class CoopPlugin
    {
        public const string Version = "test-version";
        public static readonly Setting PlayerName = new("Host");
        public record Setting(string Value);
    }
    public sealed class CoopCore
    {
        public static CoopCore Instance => null;
        public string EffectivePlayerName => "Host";
    }
    internal static class Swallow
    {
        internal static void Log(Exception e) => Steamworks.FakeSteam.Logged.Add(e);
    }
}
