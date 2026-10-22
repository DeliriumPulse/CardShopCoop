using CardShopCoop.Net;
using CardShopCoop.Sync;
using CardShopCoop.Util;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

var join = new JoinWatchdog();
Check(!join.TimedOut(1000), "idle session has no deadline");
join.Start(100);
Check(!join.RetryHello(102.99), "Hello retries are rate limited");
Check(join.RetryHello(103), "missing Hello is retried");
Check(!join.RetryHello(103), "only one retry per deadline");
for (int t = 106; t < 190; t += 3)
    join.RetryHello(t);
Check(join.TimedOut(190), "retries and pings cannot keep a broken join alive");
Check(!join.RetryHello(190), "no retry after deadline");
join.Start(200);
Check(!join.TimedOut(289), "rejoin has a fresh handshake deadline");
join.Progress(JoinWatchdog.Stage.Download, 210);
Check(!join.RetryHello(215), "Welcome stops handshake retries");
join.Progress(JoinWatchdog.Stage.Download, 320);
Check(!join.TimedOut(439), "received chunk extends download deadline");
Check(join.TimedOut(440), "stalled download terminates despite connection heartbeat");
join.Progress(JoinWatchdog.Stage.Loading, 500);
Check(!join.TimedOut(679), "scene load has its own longer deadline");
Check(join.TimedOut(680), "stuck scene does not wait indefinitely");
join.Reset();
Check(!join.TimedOut(10000) && !join.RetryHello(10000), "completed/cancelled joins stop timers");
Check(SteamTimeoutDecision.KeepCurrentPeer(true, true, true, 0, 2), "stale Steam timeout preserves a live session with fresh packets");
Check(!SteamTimeoutDecision.KeepCurrentPeer(true, true, false, 4, 2), "failed Steam session disconnects even after recent packets");
Check(!SteamTimeoutDecision.KeepCurrentPeer(true, true, true, 0, 11), "old receive activity cannot suppress a real timeout");
Check(!SteamTimeoutDecision.KeepCurrentPeer(false, true, true, 0, 2), "non-timeout Steam failures are not suppressed");

var transferRetries = new TransferRetryQueue<string>();
var resent = new List<(uint Sequence, string Payload)>();
transferRetries.Register(7, "original shelf request", 0);
Check(transferRetries.ResendDue(2.99, 8, (seq, payload) => resent.Add((seq, payload))) == 0,
    "transfer retry waits for its deadline");
Check(transferRetries.ResendDue(3, 8, (seq, payload) => resent.Add((seq, payload))) == 1
    && resent.Count == 1 && resent[0] == (7u, "original shelf request"),
    "retry repeats the original sequence and payload");
Check(transferRetries.ResendDue(3.1, 8, (seq, payload) => resent.Add((seq, payload))) == 0,
    "retry cannot flood the host every frame");
transferRetries.Remove(7);
Check(transferRetries.ResendDue(100, 8, (seq, payload) => resent.Add((seq, payload))) == 0,
    "transfer result stops retries");
transferRetries.Register(8, "box A", 0);
transferRetries.Register(9, "box B", 0);
transferRetries.Register(10, "box C", 0);
Check(transferRetries.ResendDue(3, 2, (seq, payload) => resent.Add((seq, payload))) == 2,
    "retry budget limits a burst of pending transfers");
Check(transferRetries.ResendDue(3, 2, (seq, payload) => resent.Add((seq, payload))) == 1,
    "later retry ticks serve transfers beyond the first budget");
Check(transferRetries.TotalRetries == 4, "connection diagnostics count actual item request retries");
transferRetries.RemoveWhere(payload => payload == "box B");
Check(transferRetries.Count == 2, "retiring a box removes only its pending retry");
transferRetries.Clear();
Check(transferRetries.Count == 0 && transferRetries.TotalRetries == 0
    && transferRetries.ResendDue(100, 8, (seq, payload) => resent.Add((seq, payload))) == 0,
    "session teardown clears transfer retries");

double billWallet = 100, billReserved = 0;
int billPaid = 0;
bool ReserveBillSpend(double amount)
{
    if (amount > billWallet - billReserved)
        return false;
    billReserved += amount;
    return true;
}
Check(BillPayment.TryPay(1, 80, 0, 0, ReserveBillSpend, kind => billPaid++)
    && billPaid == 1 && billReserved == 80, "guest bill reserves its charge before vanilla payment");
Check(!ReserveBillSpend(30), "a same-frame purchase cannot spend money already reserved for a bill");
billReserved = 70;
Check(!BillPayment.TryPay(2, 0, 40, 0, ReserveBillSpend, kind => billPaid++)
    && billPaid == 1 && billReserved == 70, "a guest bill cannot spend money reserved by an earlier purchase");
billReserved = 0;
byte paidKind = 255;
Check(BillPayment.TryPay(0, 20, 30, 10, ReserveBillSpend, kind => paidKind = kind)
    && billReserved == 60 && paidKind == 0, "pay-all reserves the combined live bill amount once");
Check(!BillPayment.TryPay(0, 0, 0, 0, ReserveBillSpend, kind => billPaid++),
    "a repeated request for already-cleared bills cannot pay again");
Check(!BillPayment.TryPay(9, 10, 10, 10, ReserveBillSpend, kind => billPaid++)
    && !BillPayment.TryPay(1, float.NaN, 0, 0, ReserveBillSpend, kind => billPaid++)
    && !BillPayment.TryPay(0, float.MaxValue, float.MaxValue, 0, ReserveBillSpend, kind => billPaid++),
    "invalid bill kinds and nonfinite totals never reach the wallet or payment action");

byte[] MakeSidecarBundle(params (string Path, byte[] Data)[] files)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(files.Length);
    foreach (var file in files)
    {
        writer.Write(file.Path);
        writer.Write(file.Data.Length);
        writer.Write(file.Data);
    }
    writer.Flush();
    return stream.ToArray();
}
bool RejectsSidecar(byte[] data)
{
    try
    {
        SidecarBundle.Read(data);
        return false;
    }
    catch (InvalidDataException) { return true; }
    catch (IOException) { return true; }
}
var sidecarBytes = MakeSidecarBundle(("Mod/Shop_6.json", new byte[] { 1, 2, 3 }),
    ("Mod/empty_6.json", Array.Empty<byte>()));
var sidecarFiles = SidecarBundle.Read(sidecarBytes);
Check(sidecarFiles.Count == 2 && sidecarFiles[0].Path == "Mod/Shop_6.json"
    && sidecarBytes.Skip(sidecarFiles[0].Offset).Take(sidecarFiles[0].Length).SequenceEqual(new byte[] { 1, 2, 3 })
    && sidecarFiles[1].Length == 0, "mod-data validation preserves file names, exact payload offsets and empty files");
Check(SidecarBundle.Read(Array.Empty<byte>()).Count == 0 && SidecarBundle.Read(MakeSidecarBundle()).Count == 0,
    "a shop without additional mod data remains a valid join");
Check(RejectsSidecar(sidecarBytes[..^1]), "a truncated later entry fails the complete bundle before apply");
Check(RejectsSidecar(BitConverter.GetBytes(-1)) && RejectsSidecar(BitConverter.GetBytes(4097)),
    "invalid file counts cannot be accepted as an empty mod-data bundle");
Check(RejectsSidecar(sidecarBytes.Concat(new byte[] { 0 }).ToArray()),
    "unadvertised bytes after the file table are rejected");
using (var badSidecarStream = new MemoryStream())
using (var badSidecarWriter = new BinaryWriter(badSidecarStream))
{
    badSidecarWriter.Write(1);
    badSidecarWriter.Write("Mod/Shop_6.json");
    badSidecarWriter.Write(int.MaxValue);
    badSidecarWriter.Flush();
    Check(RejectsSidecar(badSidecarStream.ToArray()), "oversized file lengths are rejected before allocating or copying file contents");
}

var workerLeases = new WorkerInteractionLeases();
Check(workerLeases.TryAcquire(2, 7, out bool repeatedWorker) && !repeatedWorker,
    "first guest worker interaction acquires a new lease");
Check(workerLeases.TryAcquire(2, 7, out repeatedWorker) && repeatedWorker,
    "repeated request from the same guest remains granted for a response retry");
Check(!workerLeases.TryAcquire(2, 8, out repeatedWorker)
    && workerLeases.TryGetOwner(2, out int workerOwner) && workerOwner == 7,
    "another guest cannot replace an active worker interaction");
Check(!workerLeases.Release(2, 8) && workerLeases.Release(2, 7),
    "only the interaction's owner can release its worker");
workerLeases.TryAcquire(2, 7, out _);
workerLeases.TryAcquire(3, 8, out _);
Check(workerLeases.ReleaseConnection(7).SequenceEqual(new[] { 2 })
    && workerLeases.TryGetOwner(3, out workerOwner) && workerOwner == 8,
    "a disconnect releases only that guest's workers");
workerLeases.TryAcquire(1, 0, out _);
Check(workerLeases.Reset().SequenceEqual(new[] { 3 }),
    "session cleanup resumes guest-paused workers while preserving the host's native menu pause");
Check(!workerLeases.TryGetOwner(1, out _) && workerLeases.Reset().Count == 0,
    "worker lease cleanup clears session bookkeeping and is safe to repeat");
Check(workerLeases.TryAcquire(3, 9, out repeatedWorker) && !repeatedWorker,
    "a new session can interact with a worker released by the previous session");

var appearance = new AppearanceDelivery();
appearance.Sent("first edit", 0);
appearance.Sent("second edit", 1);
appearance.Acknowledge("first edit");
Check(!appearance.NeedsRetry(3.9), "old appearance echo does not change retry interval");
Check(appearance.NeedsRetry(4), "old appearance echo cannot acknowledge newer edit");
appearance.Sent("second edit", 4);
Check(!appearance.NeedsRetry(6), "appearance retry is throttled");
appearance.Acknowledge("second edit");
Check(!appearance.NeedsRetry(100), "matching appearance echo stops retries");
appearance.Sent("third edit", 101);
appearance.Reset();
Check(!appearance.NeedsRetry(200), "disconnect clears appearance retries");

var ledger = new RegisterOperationLedger();
decimal money = 0;
void Sale(int peer, long sequence, decimal value)
{
    if (ledger.Accept(peer, sequence))
        money += value;
}
Sale(1, 1, 25);
Sale(1, 1, 25);
Check(money == 25, "duplicate checkout credits money once");
Sale(1, 3, 10);
Sale(1, 2, 100);
Check(money == 35, "delayed older operation cannot mutate money");
Sale(2, 1, 5);
Check(money == 40, "different guests have independent operation sequences");
Check(!ledger.Accept(1, 0) && !ledger.Accept(1, -1), "missing/invalid sequence is refused");
Check(ledger.Accept(1, 4), "operation reserved before vanilla execution");
// Model vanilla throwing after a partial side effect: retries must not re-execute it.
Check(!ledger.Accept(1, 4), "partially failed operation cannot execute twice");
ledger.Clear();
Check(ledger.Accept(1, 1), "new session resets connection sequence state");

join.StartLobby(100);
Check(!join.RetryHello(110), "lobby entry does not send Hello before a host exists");
Check(!join.TimedOut(129.99) && join.TimedOut(130), "unanswered Steam lobby request times out after 30 seconds");
join.Start(125);
Check(!join.TimedOut(214.99) && join.TimedOut(215), "successful lobby entry starts a fresh handshake deadline");
join.Reset();
Check(!join.TimedOut(1000), "cancelled lobby join stops its deadline");

var attempt = new LobbyJoinAttempt();
long first = attempt.Begin();
attempt.Cancel();
Check(!attempt.TryComplete(first), "cancelled join ignores late Steam success");
long second = attempt.Begin();
long third = attempt.Begin();
Check(!attempt.TryComplete(second), "superseded invitation cannot complete the new join");
Check(attempt.TryComplete(third), "stale result does not consume current invitation");
Check(!attempt.TryComplete(third), "Steam join result cannot connect twice");

var leftLobbies = new List<ulong>();
var lobbyRequests = new LobbyRequestLifecycle(leftLobbies.Add);
long stoppedHost = lobbyRequests.Begin();
lobbyRequests.Cancel();
Check(!lobbyRequests.Complete(stoppedHost, 10, true)
    && lobbyRequests.CurrentLobby == 0 && leftLobbies.SequenceEqual(new ulong[] { 10 }),
    "a late host creation after Stop leaves its lobby and cannot resurrect hosting");
leftLobbies.Clear();
long oldHost = lobbyRequests.Begin();
long newHost = lobbyRequests.Begin();
Check(!lobbyRequests.Complete(oldHost, 20, true) && lobbyRequests.CurrentLobby == 0
    && leftLobbies.SequenceEqual(new ulong[] { 20 }),
    "an old host creation cannot attach to the replacement transport");
Check(lobbyRequests.Complete(newHost, 30, true) && lobbyRequests.CurrentLobby == 30,
    "the current host creation still publishes its own lobby after a stale reply");
Check(!lobbyRequests.Complete(oldHost, 20, false) && lobbyRequests.CurrentLobby == 30,
    "a late failure cannot stop the replacement lobby");
lobbyRequests.Cancel();
lobbyRequests.Cancel();
Check(leftLobbies.SequenceEqual(new ulong[] { 20, 30 }), "Stop leaves current lobby membership exactly once");

leftLobbies.Clear();
long abandonedJoin = lobbyRequests.Begin(40);
long replacementJoin = lobbyRequests.Begin(50);
Check(!lobbyRequests.Complete(abandonedJoin, 40, true)
    && leftLobbies.SequenceEqual(new ulong[] { 40 }), "a replaced join cleans up membership in the old shop");
Check(lobbyRequests.Complete(replacementJoin, 50, true) && lobbyRequests.CurrentLobby == 50,
    "a replaced join cannot consume the new shop's result");
lobbyRequests.Cancel();
leftLobbies.Clear();
long firstSameShop = lobbyRequests.Begin(60);
long secondSameShop = lobbyRequests.Begin(60);
Check(!lobbyRequests.Complete(firstSameShop, 60, true) && leftLobbies.Count == 0,
    "a stale success does not leave the same lobby while the new join is pending");
Check(lobbyRequests.Complete(secondSameShop, 60, true) && lobbyRequests.CurrentLobby == 60
    && leftLobbies.Count == 0, "a same-shop retry retains the current Steam membership");
Check(!lobbyRequests.Complete(firstSameShop, 60, true) && leftLobbies.Count == 0,
    "a stale same-shop result cannot disconnect an already completed retry");
lobbyRequests.Cancel();
Check(leftLobbies.SequenceEqual(new ulong[] { 60 }), "same-shop retries still release membership on Stop");

leftLobbies.Clear();
long firstFailedRetry = lobbyRequests.Begin(70);
long failedRetry = lobbyRequests.Begin(70);
lobbyRequests.Complete(firstFailedRetry, 70, true);
Check(lobbyRequests.Complete(failedRetry, 0, false) && lobbyRequests.CurrentLobby == 0
    && leftLobbies.SequenceEqual(new ulong[] { 70 }),
    "a failed same-shop retry releases membership retained for its pending request");
leftLobbies.Clear();
long olderResult = lobbyRequests.Begin(80);
long latestResult = lobbyRequests.Begin(90);
lobbyRequests.Complete(latestResult, 90, true);
Check(!lobbyRequests.Complete(olderResult, 80, true) && lobbyRequests.CurrentLobby == 90
    && leftLobbies.SequenceEqual(new ulong[] { 80 }),
    "out-of-order success releases only the obsolete lobby");
lobbyRequests.Cancel();
leftLobbies.Clear();
long stoppingSameShop = lobbyRequests.Begin(100);
long pendingSameShop = lobbyRequests.Begin(100);
lobbyRequests.Complete(stoppingSameShop, 100, true);
lobbyRequests.Cancel();
Check(leftLobbies.SequenceEqual(new ulong[] { 100 }),
    "cancelling a same-shop retry also releases deferred stale membership");
Check(!lobbyRequests.Complete(pendingSameShop, 100, true) && lobbyRequests.CurrentLobby == 0
    && leftLobbies.SequenceEqual(new ulong[] { 100, 100 }),
    "a new membership created by a late result after cancellation is released again");

long pendingCreate = lobbyRequests.Begin();
long switchingToJoin = lobbyRequests.Begin(110);
Check(!lobbyRequests.Complete(pendingCreate, 120, true)
    && lobbyRequests.Complete(switchingToJoin, 110, true) && lobbyRequests.CurrentLobby == 110,
    "switching from hosting to joining cannot publish an abandoned creation");
lobbyRequests.Cancel();

CurrencyProfile Currency(int id, double rate) => new() { CurrencyId = id, ConversionRate = rate };
Check(CurrencyProfile.Matches(Currency(1, 100), Currency(1, 100)), "identical currency profiles can join");
Check(CurrencyProfile.Matches(Currency(2, 2.5), Currency(2, 2.5)), "fractional conversion rates can join");
Check(!CurrencyProfile.Matches(Currency(1, 1), Currency(2, 1)), "same rate with different currency identity is rejected");
Check(!CurrencyProfile.Matches(Currency(1, 1), Currency(1, 100)), "different conversion rates are rejected");
Check(!CurrencyProfile.Matches(null, Currency(1, 1)) && !CurrencyProfile.Matches(Currency(1, 1), null), "missing handshake currency is rejected");
Check(!CurrencyProfile.Matches(new CurrencyProfile(), new CurrencyProfile()), "default profiles cannot bypass compatibility");
Check(!CurrencyProfile.Matches(Currency(-1, 1), Currency(-1, 1)), "negative currency identity is rejected");
foreach (double invalidRate in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    Check(!CurrencyProfile.Matches(Currency(1, invalidRate), Currency(1, invalidRate)), $"invalid conversion rate {invalidRate} is rejected");
Check(CurrencyProfile.MatchesDenomination(0.001, 0.001), "small converted denomination matches");
Check(CurrencyProfile.MatchesDenomination(0.1 + 0.2, 0.3), "floating point rounding does not reject equivalent change");
Check(!CurrencyProfile.MatchesDenomination(1, 100), "same button with different monetary value is rejected");
Check(!CurrencyProfile.MatchesDenomination(0.001, 0.002), "small denomination differences are rejected");
foreach (double invalidAmount in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    Check(!CurrencyProfile.MatchesDenomination(invalidAmount, 1) && !CurrencyProfile.MatchesDenomination(1, invalidAmount), $"invalid denomination {invalidAmount} is rejected");
var recovery = new RecoveryStatus();
recovery.ReceivedSnapshot();
Check(recovery.Message == "", "unsolicited snapshot does not claim a manual recovery");
Check(recovery.Begin(100), "first manual refresh starts immediately");
Check(!recovery.Begin(101) && !recovery.Begin(111), "pending refresh cannot flood host even after cooldown");
Check(!recovery.Tick(114.9) && recovery.Tick(115), "missing recovery response becomes a visible timeout");
Check(!recovery.Pending && !recovery.Tick(116), "recovery timeout emits only once");
recovery.ReceivedSnapshot();
Check(recovery.Message.Contains("15 seconds"), "late snapshot does not erase timeout evidence");
Check(recovery.Begin(116), "timed out refresh can be retried");
recovery.ReceivedSnapshot();
Check(!recovery.Pending && recovery.Message.Contains("Pending item transfers"), "snapshot receipt never claims unresolved transfers are fixed");
Check(!recovery.CanRequest(125.9) && recovery.CanRequest(126), "successful refresh still enforces ten second cooldown");
recovery.Reset();
Check(recovery.CanRequest(0) && recovery.Message == "", "new session clears pending recovery and cooldown");

var history = new DiagnosticHistory();
for (int i = 0; i < 20; i++) history.Add("error " + i);
var recent = history.Snapshot();
Check(recent.Length == 12 && recent[0] == "error 8", "diagnostics retain only the twelve newest problems");
history.Add("error 19");
Check(history.Snapshot().Length == 12 && history.Snapshot()[0] == "error 8", "repeated errors do not evict useful context");
recent[0] = "changed by reader";
Check(history.Snapshot()[0] == "error 8", "report snapshot cannot mutate stored history");
history.Add("first\nsecond\rthird");
Check(history.Snapshot().Last() == "first second third", "multiline errors remain a single report entry");
history.Add(new string('x', 10000));
Check(history.Snapshot().Last().Length == 803, "large errors cannot grow report memory without bounds");
var picker = new CardShopCoop.UI.HsvPickerState();
picker.Reset(0, 0, 0);
picker.SelectHue(0.6f);
Check(picker.Hue == 0.6f && picker.Saturation == 1 && picker.Value == 1, "hue selection makes black hair visibly colored");
picker.Reset(0, 0, 1);
picker.SelectHue(0.3f);
Check(picker.Saturation == 1 && picker.Value == 1, "hue selection colors white and gray instead of doing nothing");
picker.Observe(0, 0, 0.5f);
Check(picker.Hue == 0.3f && picker.Saturation == 0, "gray retains selected hue while allowing neutral colors");
picker.Reset(0.7f, 0.8f, 0);
picker.Observe(0, 0, 0);
Check(picker.Hue == 0.7f && picker.Saturation == 0.8f && picker.Value == 0, "black RGB round trip retains hue and saturation");
picker.Reset(0.2f, 0.4f, 0.3f);
picker.SelectHue(0.8f);
Check(picker.Saturation == 0.4f && picker.Value == 0.3f, "normal hue edits preserve chosen saturation and brightness");
picker.Reset(0, 0, 1);
Check(picker.Hue == 0 && picker.Saturation == 0, "switching picker slots resets the previous slot state");
// Match the supported direct-action condition IDs; purchases/checkouts are credited
// through their existing host operations and must never enter this route.
var tasks = new TutorialContributionLedger(12, 2, 3, 4, 8, 12, 15);
Check(tasks.Accept(1, 1, 4, 1), "guest price action contributes to shared tutorial");
Check(!tasks.Accept(1, 1, 4, 1), "replayed price contribution does not count twice");
Check(tasks.Accept(2, 1, 4, 1), "two guests independently contribute to task progress");
Check(!tasks.Accept(1, 2, 1, 1) && !tasks.Accept(1, 2, 6, 1), "purchases and checkouts cannot be double credited through generic tutorial messages");
Check(tasks.Accept(1, 2, 8, 1), "invalid contribution does not consume the next valid pack action");
Check(tasks.Accept(1, 3, 12, -1), "removing a displayed card preserves vanilla negative task progress");
Check(!tasks.Accept(1, 4, 4, -1), "negative price contribution is rejected");
Check(!tasks.Accept(1, 4, 4, float.NaN) && !tasks.Accept(1, 4, 4, float.PositiveInfinity), "invalid task amounts cannot corrupt shared progress");
Check(!tasks.Accept(1, 4, 4, 1000), "direct task action cannot submit an arbitrary completion amount");
Check(!tasks.Accept(1, 0, 4, 1) && !tasks.Accept(0, 4, 4, 1), "tutorial contributions require a connection and sequence");
tasks.Clear();
Check(tasks.Accept(1, 1, 2, 1), "fresh session accepts the first pickup contribution");
var oldTableSave = new List<string> { "old A" };
var liveTables = new List<string> { "A", "B", "C" };
var rebuiltTables = LiveSaveSnapshot.Capture(liveTables, x => x != null, x => "saved " + x);
Check(rebuiltTables.SequenceEqual(new[] { "saved A", "saved B", "saved C" }), "new play tables do not index into a shorter old save");
Check(oldTableSave.SequenceEqual(new[] { "old A" }), "building a table snapshot leaves the old save intact");
Check(LiveSaveSnapshot.Capture(new[] { "C", "A" }, x => x != null, x => x).SequenceEqual(new[] { "C", "A" }), "removed and reordered tables rebuild in current registry order");
Check(LiveSaveSnapshot.Capture(new[] { "A", null, "C" }, x => x != null, x => x).SequenceEqual(new[] { "A", "C" }), "destroyed table slots do not create invalid save placeholders");
Check(LiveSaveSnapshot.Capture(Array.Empty<string>(), x => x != null, x => x).Count == 0, "empty table registry produces an empty snapshot");
try
{
    oldTableSave = LiveSaveSnapshot.Capture(liveTables, x => x != null,
        x => x == "B" ? throw new InvalidOperationException("capture failed") : x);
    throw new Exception("expected capture failure");
}
catch (InvalidOperationException)
{
    Check(oldTableSave.SequenceEqual(new[] { "old A" }), "failed table capture cannot publish a partial replacement save");
}
Check(BoxRecoveryRules.CanRefreshHeldContents(true, true, true, false, false), "host rack-pull result refreshes a held item box");
Check(!BoxRecoveryRules.CanRefreshHeldContents(false, true, true, false, false), "stale or different-owner snapshot cannot repaint a held box");
Check(!BoxRecoveryRules.CanRefreshHeldContents(true, true, true, true, false), "unsent local stock change is preserved while held");
Check(!BoxRecoveryRules.CanRefreshHeldContents(true, true, true, false, true), "pending hand transfer blocks held content refresh");
Check(!BoxRecoveryRules.CanRefreshHeldContents(true, true, false, false, false), "unknown baseline cannot overwrite held contents");
Check(!BoxRecoveryRules.CanRefreshHeldContents(true, false, true, false, false), "held item refresh does not change card or furniture box rules");
Check(BoxRecoveryRules.PredictionSeconds(0.04f) == 0.04f, "short box motion gap can be predicted");
Check(BoxRecoveryRules.PredictionSeconds(2f) == 0.1f, "missing motion packets cannot extrapolate for the entire lease");
Check(BoxRecoveryRules.PredictionSeconds(-1f) == 0f && BoxRecoveryRules.PredictionSeconds(float.NaN) == 0f, "invalid prediction age causes no extrapolation");
var bill = new { DisplayIndex = 0, Value = 1.0 };
var coin = new { DisplayIndex = 0, Value = 0.01 };
var drawer = new[] { bill, coin };
Check(RegisterChangeIdentity.Slot(drawer, bill) == 0 && RegisterChangeIdentity.Slot(drawer, coin) == 1,
    "bill and coin sharing display index 0 have distinct wire slots");
Check(ReferenceEquals(drawer[RegisterChangeIdentity.Slot(drawer, coin)], coin), "receiver resolves penny click to the penny control");
Check(RegisterChangeIdentity.Slot(drawer, new
{
    DisplayIndex = 0,
    Value = 0.01
}) == -1,
    "a control from another register cannot masquerade as a local denomination");
Check(RegisterChangeIdentity.Slot(drawer, null) == -1, "missing change button has no wire identity");
Check(RegisterChangeIdentity.Changed(0, 1, false) && RegisterChangeIdentity.Changed(1, 0, true),
    "successful give and take-back actions each emit a change");
Check(!RegisterChangeIdentity.Changed(0, 0, true), "right click on empty denomination emits nothing");
Check(!RegisterChangeIdentity.Changed(100, 100, false), "drawer cap emits no phantom give or pending acknowledgement");
Check(!RegisterChangeIdentity.Changed(1, 1, false) && !RegisterChangeIdentity.Changed(1, 1, true),
    "clicks outside giving-change phase emit nothing");
Check(SharedDayRules.Decide(2, 2, true, false, false, false, false, false) == SharedDayAction.OpenReport,
    "guest can open the finished day's shared recap");
Check(SharedDayRules.Decide(2, 2, true, false, false, false, true, false) == SharedDayAction.ConfirmQueue,
    "unfinished checkout warning is shown to the requesting guest");
Check(SharedDayRules.Decide(2, 2, true, false, false, false, true, true) == SharedDayAction.OpenReport,
    "guest can confirm unfinished checkout without host permission");
Check(SharedDayRules.Decide(2, 2, true, false, true, false, true, false) == SharedDayAction.ResendReport,
    "simultaneous recap requests cannot toggle the report closed");
Check(SharedDayRules.Decide(2, 2, true, false, true, true, false, false) == SharedDayAction.Advance,
    "guest continue starts the shared next day");
Check(SharedDayRules.Decide(2, 2, true, true, true, true, false, false) == SharedDayAction.Ignore,
    "second continue while vanilla loading flag is set cannot charge or advance twice");
Check(SharedDayRules.Decide(2, 3, true, false, true, true, false, false) == SharedDayAction.Ignore,
    "delayed old-day continue cannot advance a later day");
Check(SharedDayRules.Decide(3, 2, true, false, true, true, false, false) == SharedDayAction.Ignore,
    "future day request is ignored");
Check(SharedDayRules.Decide(2, 2, false, false, false, false, false, true) == SharedDayAction.Ignore,
    "day cannot be ended before closing time");
Check(SharedDayRules.Decide(2, 2, true, false, false, true, false, false) == SharedDayAction.Ignore,
    "advance cannot bypass the shared recap");
var recap = new DeferredDayReport<string>();
Check(recap.Next(0, false) == ReportDeliveryAction.Wait, "no recap opens without a host report");
Check(recap.Offer(0, 0, "day zero totals"), "first day recap is retained");
Check(recap.Next(0, true) == ReportDeliveryAction.Wait, "phone or menu defers recap instead of dropping it");
Check(recap.Next(0, false) == ReportDeliveryAction.Open, "closing the phone delivers the queued recap");
recap.MarkOpened();
Check(recap.Next(0, false) == ReportDeliveryAction.Wait, "opened recap cannot toggle closed on retry");
Check(recap.Offer(0, 0, "reset totals") && recap.Value == "day zero totals", "duplicate report cannot replace frozen displayed totals");
Check(recap.Next(1, true) == ReportDeliveryAction.Archive, "next day closes recap even while UI is busy");
recap.Finish();
Check(!recap.Offer(0, 1, "late totals") && recap.Next(1, false) == ReportDeliveryAction.Wait,
    "filed recap cannot reopen or duplicate its history");
Check(recap.Offer(1, 1, "phone day") && recap.Next(2, true) == ReportDeliveryAction.Archive,
    "unopened recap is still filed when host advances while guest stays on phone");
recap.Finish();
Check(recap.Offer(3, 2, "future report") && recap.Next(2, false) == ReportDeliveryAction.Wait,
    "recap arriving ahead of clock waits for matching day");
Check(recap.Next(3, false) == ReportDeliveryAction.Open, "clock catch-up releases future report");
recap.Reset();
Check(recap.Day == -1 && recap.LastFiledDay == -1 && recap.Offer(0, 0, "new save"),
    "new session clears both pending recap and old day deduplication");

int[] numbers = { 1, 2, 3, 0 };
var swapped = TableNumberRules.Plan(numbers, 0, TableNumberAction.Set, 3, false);
Check(swapped.SequenceEqual(new[] { 3, 2, 1, 0 }) && numbers.SequenceEqual(new[] { 1, 2, 3, 0 }),
    "guest number swap is planned in full without mutating the live layout");
var concurrent = TableNumberRules.Plan(swapped, 1, TableNumberAction.Set, 3, false);
Check(concurrent.SequenceEqual(new[] { 2, 3, 1, 0 }), "next player's edit uses the changed host layout and preserves unique numbers");
Check(TableNumberRules.Plan(concurrent, 0, TableNumberAction.Clear, 99, false).SequenceEqual(new[] { 0, 2, 1, 0 }),
    "clear compacts numbers using current table number, ignoring stale guest selection");
var numbered = TableNumberRules.Plan(numbers, 3, TableNumberAction.Auto, 0, false);
Check(numbered.SequenceEqual(new[] { 1, 2, 3, 4 }), "Auto allocates from current host maximum");
Check(TableNumberRules.Plan(numbered, 3, TableNumberAction.Auto, 0, false) == null,
    "duplicate Auto cannot renumber its already assigned table");
Check(TableNumberRules.Plan(numbers, 3, TableNumberAction.Clear, 0, false).SequenceEqual(numbers),
    "clearing an unnumbered table leaves other table numbers alone");
Check(TableNumberRules.Plan(numbers, 0, TableNumberAction.Clear, 0, true) == null,
    "tournament table clear is rejected before any compaction");
Check(TableNumberRules.Plan(numbers, 3, TableNumberAction.Set, 2, true) == null,
    "unnumbered table cannot indirectly displace an active tournament table");
Check(TableNumberRules.Plan(numbers, 3, TableNumberAction.Auto, 0, true).SequenceEqual(numbered),
    "new table can still receive a free number during tournament");
Check(TableNumberRules.Plan(numbers, 0, TableNumberAction.Set, -1, false) == null
    && TableNumberRules.Plan(numbers, 0, TableNumberAction.Set, 5, false) == null,
    "manual number outside native UI range cannot partially edit tables");
Check(TableNumberRules.Plan(new[] { 99, 0 }, 1, TableNumberAction.Auto, 0, false) == null,
    "Auto respects the two-digit number limit");
Check(TableNumberRules.Plan(numbers, -1, TableNumberAction.Clear, 0, false) == null,
    "removed target cannot modify a different table");

Check(RegisterVisitIdentity.Matches(-123, 4, -123, 4), "checkout rejection can reset exactly the current visit");
Check(!RegisterVisitIdentity.Matches(-123, 4, -123, 5), "pooled customer reuse rejects stale checkout recovery");
Check(!RegisterVisitIdentity.Matches(-123, 4, -456, 4), "another customer cannot be reset by delayed checkout rejection");
Check(!RegisterVisitIdentity.Matches(0, 0, 0, 0), "empty counter is not a valid checkout recovery target");
Check(RemoteAudioRules.Volume(0.7f, false) == 0.7f, "remote spray follows configured SFX volume");
Check(RemoteAudioRules.Volume(0.7f, true) == 0f, "pause mute silences remote spray without changing volume setting");
Check(RemoteAudioRules.Volume(0f, false) == 0f, "zero SFX volume silences remote spray");
Check(RemoteAudioRules.Volume(float.NaN, false) == 0f && RemoteAudioRules.Volume(-1f, false) == 0f,
    "invalid negative or NaN volume cannot reach the audio source");
Check(RemoteAudioRules.Volume(2f, false) == 1f, "remote volume is bounded to the audio source range");
Check(RemoteAudioRules.IsFresh(10f, 10.25f), "normal spray keepalive maintains emission");
Check(!RemoteAudioRules.IsFresh(10f, 11f), "missing spray update expires at one real second");
Check(!RemoteAudioRules.IsFresh(10f, 100f), "paused game time cannot keep an old spray alive with a realtime clock");
Check(!RemoteAudioRules.IsFresh(10f, 9f) && !RemoteAudioRules.IsFresh(float.NaN, 10f),
    "invalid or reversed effect timestamps fail closed");
foreach (string clip in new[] { "SFX_CheckoutScan", "SFX_CheckoutRegisterOpen", "SFX_CheckoutDone", "SFX_WhipSoft" })
{
    Check(!RemoteAudioRules.AllowCheckoutSound(true, clip), "checkout reconstruction silences " + clip);
    Check(RemoteAudioRules.AllowCheckoutSound(false, clip), "normal checkout keeps " + clip);
}
Check(RemoteAudioRules.AllowCheckoutSound(true, "SFX_CustomerBuy")
    && RemoteAudioRules.AllowCheckoutSound(true, "SFX_ButtonLightTap"), "checkout recovery does not mute unrelated confirmation or menu sounds");
checks += NetReliabilityChecks.Run();
Console.WriteLine($"{checks} custom regression checks passed.");
