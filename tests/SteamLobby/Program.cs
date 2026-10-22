using CardShopCoop.Net;
using Steamworks;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
LobbyCreated_t Created(ulong id, EResult result = EResult.k_EResultOK) => new() { m_ulSteamIDLobby = id, m_eResult = result };
LobbyEnter_t Entered(ulong id, EChatRoomEnterResponse response = EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) => new()
{
    m_ulSteamIDLobby = id,
    m_EChatRoomEnterResponse = (uint)response
};
SteamAPICall_t LastCall() => FakeSteam.Calls[^1].Handle;
bool ThrowsStartup(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (InvalidOperationException) { return true; }
}
var published = new List<ulong>();
var entered = new List<ulong>();
var errors = new List<string>();
var startupFailures = new List<string>();
SteamLobby Fresh()
{
    FakeSteam.Reset();
    published.Clear();
    entered.Clear();
    errors.Clear();
    startupFailures.Clear();
    var lobby = new SteamLobby
    {
        OnLobbyCreated = id => published.Add(id.m_SteamID),
        OnEnteredLobby = id => entered.Add(id.m_SteamID),
        OnError = errors.Add,
        OnSessionStartFailed = startupFailures.Add
    };
    lobby.Init();
    return lobby;
}

var session = Fresh();
session.Host(false, "Old shop", true);
var oldCreation = LastCall();
session.Leave();
session.Host(true, "New shop", false);
var newCreation = LastCall();
FakeSteam.Deliver(oldCreation, Created(10));
Check(published.Count == 0 && session.LobbyId == CSteamID.Nil && FakeSteam.Left.SequenceEqual(new ulong[] { 10 })
    && FakeSteam.Metadata.Count == 0, "cancelled host callback leaves the abandoned lobby without publishing or advertising it");
FakeSteam.Deliver(newCreation, Created(20));
Check(published.SequenceEqual(new ulong[] { 20 }) && session.LobbyId == new CSteamID(20)
    && FakeSteam.Metadata[(20, "name")] == "New shop" && FakeSteam.Metadata[(20, "pw")] == "0",
    "replacement hosting publishes only its own lobby and metadata");
session.Leave();
Check(FakeSteam.Left.SequenceEqual(new ulong[] { 10, 20 }) && FakeSteam.Results.Count == 0,
    "completed request handlers are released and Stop leaves the current host lobby");

session = Fresh();
session.Host(false, "Abandoned", false);
oldCreation = LastCall();
session.Leave();
session.Host(false, "Current", true);
newCreation = LastCall();
FakeSteam.Deliver(oldCreation, Created(0, EResult.k_EResultFail));
Check(startupFailures.Count == 0 && errors.Count == 0, "late creation failure cannot report failure for a new startup");
FakeSteam.Deliver(newCreation, Created(0, EResult.k_EResultFail));
Check(startupFailures.Count == 1 && errors.Count == 0 && session.LobbyId == CSteamID.Nil,
    "current creation failure uses the startup failure path rather than a nonfatal search error");
session.Host(false, "Retry", false);
FakeSteam.Deliver(LastCall(), Created(30));
Check(published.SequenceEqual(new ulong[] { 30 }), "failed creation can be retried successfully");

session = Fresh();
session.Host(false, "Shop", false);
FakeSteam.Deliver(LastCall(), Created(40), true);
Check(startupFailures.Count == 1 && published.Count == 0 && FakeSteam.Metadata.Count == 0,
    "creation I/O failure cannot publish a partially returned lobby");
session.Host(false, "Shop", false);
FakeSteam.Deliver(LastCall(), Created(0));
Check(startupFailures.Count == 2 && published.Count == 0 && session.LobbyId == CSteamID.Nil,
    "a nominally successful creation with no lobby ID is rejected");
FakeSteam.InvalidNextCall = true;
Check(ThrowsStartup(() => session.Host(false, "Shop", false)) && FakeSteam.Results.Count == 0,
    "invalid creation handle fails immediately instead of waiting forever");

session = Fresh();
session.Join(new CSteamID(50));
var oldJoin = LastCall();
session.Leave();
FakeSteam.Deliver(oldJoin, Entered(50));
Check(entered.Count == 0 && FakeSteam.Left.SequenceEqual(new ulong[] { 50 }) && session.LobbyId == CSteamID.Nil,
    "cancelled join still receives and cleans up a late successful Steam entry");
session.Join(new CSteamID(60));
oldJoin = LastCall();
session.Join(new CSteamID(70));
var newJoin = LastCall();
FakeSteam.Deliver(newJoin, Entered(70));
FakeSteam.Deliver(oldJoin, Entered(60));
Check(entered.SequenceEqual(new ulong[] { 42 }) && session.LobbyId == new CSteamID(70)
    && FakeSteam.Left.SequenceEqual(new ulong[] { 50, 60 }), "out-of-order join replies connect only to the latest shop");

session = Fresh();
session.Join(new CSteamID(80));
oldJoin = LastCall();
session.Leave();
session.Join(new CSteamID(80));
newJoin = LastCall();
FakeSteam.Deliver(oldJoin, Entered(80));
Check(FakeSteam.Left.Count == 0 && entered.Count == 0, "same-shop retry preserves membership needed by the pending new request");
FakeSteam.Deliver(newJoin, Entered(80));
Check(FakeSteam.Left.Count == 0 && entered.Count == 1 && session.LobbyId == new CSteamID(80),
    "same-shop retry connects once without a stale callback disconnecting it");
session.Leave();
Check(FakeSteam.Left.SequenceEqual(new ulong[] { 80 }), "same-shop retry still leaves on explicit Stop");

session = Fresh();
session.Join(new CSteamID(90));
oldJoin = LastCall();
session.Join(new CSteamID(90));
newJoin = LastCall();
FakeSteam.Deliver(oldJoin, Entered(90));
FakeSteam.Deliver(newJoin, Entered(90, EChatRoomEnterResponse.k_EChatRoomEnterResponseError));
Check(FakeSteam.Left.SequenceEqual(new ulong[] { 90 }) && startupFailures.Count == 1 && entered.Count == 0,
    "failed same-shop retry releases membership retained for its pending request");

session = Fresh();
FakeSteam.Owners[100] = SteamUser.GetSteamID();
session.Join(new CSteamID(100));
FakeSteam.Deliver(LastCall(), Entered(100));
Check(startupFailures.Count == 1 && entered.Count == 0 && FakeSteam.Left.SequenceEqual(new ulong[] { 100 }),
    "joining a lobby whose host has left and made us owner fails and cleans up");
session.Join(new CSteamID(110));
FakeSteam.Deliver(LastCall(), Entered(111));
Check(startupFailures.Count == 2 && entered.Count == 0 && FakeSteam.Left.Contains(111),
    "a mismatched lobby response is left without connecting to an unintended host");
FakeSteam.InvalidNextCall = true;
Check(ThrowsStartup(() => session.Join(new CSteamID(120))) && FakeSteam.Results.Count == 0,
    "invalid join handle fails immediately and releases its result handler");

session = Fresh();
FakeSteam.ThrowSearch = true;
session.RefreshList();
Check(!session.ListRefreshing && errors.Count == 1 && startupFailures.Count == 0,
    "search API exception clears the busy flag and reports a nonfatal error without throwing through UI");
FakeSteam.ThrowSearch = false;
session.RefreshList();
var search = LastCall();
Check(session.ListRefreshing, "search can start again after an exception");
session.Join(new CSteamID(130));
newJoin = LastCall();
FakeSteam.Deliver(search, new LobbyMatchList_t(), true);
Check(!session.ListRefreshing && errors.Count == 2 && startupFailures.Count == 0,
    "a late failed browser search cannot invoke the join startup failure path");
FakeSteam.Deliver(newJoin, Entered(130));
Check(entered.Count == 1 && session.LobbyId == new CSteamID(130), "join remains completable after a browser error");
FakeSteam.InvalidNextCall = true;
session.RefreshList();
Check(!session.ListRefreshing && errors.Count == 3 && startupFailures.Count == 0,
    "invalid search handle clears busy state and remains nonfatal");
int updates = 0;
session.OnListUpdated = () => updates++;
session.RefreshList();
FakeSteam.Deliver(LastCall(), new LobbyMatchList_t { m_nLobbiesMatching = 1 });
Check(!session.ListRefreshing && updates == 1 && session.Lobbies.Count == 1,
    "successful search after failures still fills the browser and signals completion");
ulong invited = 0;
session.OnInviteAccepted = id => invited = id.m_SteamID;
session.Leave();
FakeSteam.Invite(new GameLobbyJoinRequested_t { m_steamIDLobby = new CSteamID(140) });
Check(invited == 140 && FakeSteam.Logged.Count == 0, "invite callback survives leaving a session");

Console.WriteLine($"{checks} Steam lobby callback checks passed.");
