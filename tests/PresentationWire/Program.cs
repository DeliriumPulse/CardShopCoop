using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using Newtonsoft.Json;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    passed++;
    Console.WriteLine("PASS " + name);
}
T RoundTrip<T>(T value) => JsonConvert.DeserializeObject<T>(
    JsonConvert.SerializeObject(value, WireSettings.Settings), WireSettings.Settings)!;

var rejected = RoundTrip(new RegisterRejectedMessage { Index = 2, CustomerId = -123, CustomerGeneration = 7 });
Check(rejected.Index == 2 && rejected.CustomerId == -123 && rejected.CustomerGeneration == 7,
    "checkout rejection retains the customer visit across the wire");

// Exercise the shipped custom converter: adding a DTO field without updating the
// converter silently loses crouching on both direct packets and host relays.
var player = new PlayerStateMessage { Crouching = true, Yaw = 75f, Hold = 1 };
var copy = RoundTrip(player);
Check(copy.Crouching && copy.Hold == 1 && copy.Yaw == 75f, "direct player crouch round trip");
Check(RoundTrip(new RelayStateMessage { SenderId = 7, State = player }).State.Crouching, "relayed crouch round trip");
player.Crouching = false;
Check(!RoundTrip(player).Crouching, "standing clears crouch");
Check(!JsonConvert.DeserializeObject<PlayerStateMessage>("{}", WireSettings.Settings)!.Crouching, "missing crouch defaults to standing");
var npc = new NpcStateMessage();
npc.Entries.Add(new NpcEntry
{
    Identity = 42,
    Flags = 12,
    Animation = new[] { new NpcAnimationLayer { State = -1234567, Time = 3.25f, Weight = 1f },
        new NpcAnimationLayer { State = 777, Time = 0.5f, Weight = 0.4f } }
});
var entry = RoundTrip(npc).Entries.Single();
Check(entry.Flags == 12 && entry.Identity == 42, "seated playing flags and NPC identity preserved");
Check(entry.Animation.Length == 2 && entry.Animation[0].State == -1234567, "animation layers and signed hash preserved");
Check(entry.Animation[0].Time == 3.25f && entry.Animation[1].Weight == 0.4f, "animation phase and layer weight preserved");
Check(RoundTrip(new NpcEntry()).Animation == null, "missing animator snapshot remains optional");
Check(RoundTrip(new ShopPurchaseCueMessage()).Type == MsgType.ShopPurchaseCue, "purchase cue round trip");
player.Hold = 5;
player.Spraying = true;
player.HoldTypes = new List<int> { 1 };
var spray = RoundTrip(player);
Check(spray.Spraying && spray.Hold == 5 && spray.HoldTypes!.Single() == 1, "active spray can and emission survive the custom player converter");
Check(RoundTrip(new RelayStateMessage { SenderId = 7, State = player }).State.Spraying, "spray survives host relay");
player.Spraying = false;
Check(!RoundTrip(player).Spraying, "release spray clears emission on the wire");
var environment = new EnvironmentState
{
    TimeOfDay = 3,
    SkyFrom = 1,
    SkyTo = 2,
    SkyBlend = 0.75f,
    SkyRotation = 42f,
    SunRotation = 0.9f,
    SunLerp = 0.5f,
    StartBrightness = 0.55f,
    Lerping = true,
    SunOn = false,
    NightOn = true,
    ShopOn = true,
    SunIntensity = new[] { 0.1f, 0.02f },
    SunShadows = new[] { 0, 2 },
    SunShadowStrength = new[] { 0f, 0.2f }
};
var sky = RoundTrip(new DayTimeMessage { Day = 3, Hour = 21, Environment = environment }).Environment;
Check(sky.SkyFrom == 1 && sky.SkyTo == 2 && sky.SkyBlend == 0.75f && sky.SkyRotation == 42f,
    "clock carries the actual sky textures and blend");
Check(sky.SunIntensity.SequenceEqual(environment.SunIntensity) && sky.SunShadows.SequenceEqual(environment.SunShadows)
    && sky.SunShadowStrength.SequenceEqual(environment.SunShadowStrength), "night sun intensity and shadows preserve all lights");
Check(!sky.SunOn && sky.NightOn && sky.ShopOn && sky.SunRotation == 0.9f && sky.Lerping,
    "light groups and transition state round trip");
Check(RoundTrip(new LightStateMessage { Day = 3, HasDay = true, LightJson = "{}", Environment = environment })
    .Environment.SkyTo == 2, "lighting heartbeat also carries environment repair");
var dayRequest = RoundTrip(new DayActionMessage { Day = 3, Advance = true, ConfirmedQueue = true });
Check(dayRequest.Day == 3 && dayRequest.Advance && dayRequest.ConfirmedQueue, "day action retains day binding and intent");
Check(RoundTrip(new DayActionReplyMessage { Day = 3, ConfirmQueue = true }).ConfirmQueue,
    "unfinished-checkout warning reaches the initiating guest");
Check(RoundTrip(new ReportStateMessage { Day = 3, OpenScreen = true }).Day == 3, "shared recap carries its own day for stale-packet rejection");
var change = RoundTrip(new RegisterOpMessage { ChangeIndex = 7, ChangeValue = 0.01, TakingBack = true });
Check(change.ChangeIndex == 7 && change.ChangeValue == 0.01 && change.TakingBack,
    "penny removal retains complete drawer slot and exact denomination");
var table = RoundTrip(new SettingsOpMessage { Op = 10, ObjectKey = (6 << 24) | 550 });
Check(table.Op == 10 && table.ObjectKey == ((6 << 24) | 550), "Auto table request retains stable object identity");
var tableState = new SettingsStateMessage();
tableState.TableIds.AddRange(new ushort[] { 550, 3 });
tableState.TableNumbers.AddRange(new byte[] { 2, 1 });
var tables = RoundTrip(tableState);
Check(tables.TableIds.SequenceEqual(tableState.TableIds) && tables.TableNumbers.SequenceEqual(tableState.TableNumbers),
    "table numbers remain paired with identities despite local registry order");
var cue = RoundTrip(new ShelfBoxCueMessage
{
    BoxId = 12,
    Sequence = 35,
    ItemType = (EItemType)1,
    From = new UnityEngine.Vector3(1f, 2f, 3f),
    Scale = new UnityEngine.Vector3(1f, 1f, 1f),
    DestinationLocal = new UnityEngine.Vector3(0.1f, 0.2f, 0.3f)
});
Check(cue.BoxId == 12 && cue.Sequence == 35 && cue.From.y == 2f && cue.DestinationLocal.z == 0.3f,
    "successful shelf pull carries moving-box animation endpoints");
// Exercise the shipped replay boundary without calling Unity scene APIs. A restored
// scan must stop before currency capture or network delivery, even when serving locally.
var register = new CardShopCoop.Sync.RegisterSync();
var restoring = typeof(CardShopCoop.Sync.RegisterSync).GetField("_applyingCart",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
var sendLocal = typeof(CardShopCoop.Sync.RegisterSync).GetMethod("SendLocalOp",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
int forwarded = 0;
register.SendOp = _ => forwarded++;
restoring.SetValue(register, true);
foreach (byte operation in new byte[] { 3, 4, 5, 6, 7, 8, 9 })
    sendLocal.Invoke(register, new object[] { new RegisterOpMessage { Op = operation } });
Check(forwarded == 0, "restored checkout actions never emit new network requests");
restoring.SetValue(register, false);
CardShopCoop.Sync.RegisterSync.ApplyingAuthoritativePayment = true;
sendLocal.Invoke(register, new object[] { new RegisterOpMessage { Op = CardShopCoop.Sync.RegisterSync.OpTookPayment } });
CardShopCoop.Sync.RegisterSync.ApplyingAuthoritativePayment = false;
Check(forwarded == 0, "mirrored payment cannot echo a new payment request");
try { register.ClientApplyCart(null!); } catch (NullReferenceException) { }
Check(!(bool)restoring.GetValue(register)!, "failed cart reconstruction releases its replay and sound guard");
restoring.SetValue(register, true);
try { register.ClientApplyCart(null!); } catch (NullReferenceException) { }
Check((bool)restoring.GetValue(register)!, "nested cart failure preserves the outer replay guard");
restoring.SetValue(register, false);
var mute = typeof(SoundManager).GetField("m_IsMute", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
Check(mute?.FieldType == typeof(bool), "installed game exposes the pause mute flag used by remote audio");
var playAudio = typeof(SoundManager).GetMethod("PlayAudio", new[] { typeof(string), typeof(float), typeof(float) });
Check(playAudio?.GetParameters()[0].Name == "audioName", "checkout sound patch matches the installed game method and argument");
var bundleApply = typeof(CardShopCoop.Sync.SidecarTransfer).GetMethod("ApplyBundle",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
    null, new[] { typeof(byte[]), typeof(int), typeof(int), typeof(string) }, null)!;
string bundleTestRoot = Path.Combine(Path.GetTempPath(), "CardShopCoop-bundle-check-" + Guid.NewGuid().ToString("N"));
string bundleTestDir = Path.Combine(bundleTestRoot, "Mod");
string bundleTestFile = Path.Combine(bundleTestDir, "Shop_7.json");
Directory.CreateDirectory(bundleTestDir);
try
{
    File.WriteAllText(bundleTestFile, "existing slot data");
    using var corruptStream = new MemoryStream();
    using var corruptWriter = new BinaryWriter(corruptStream);
    corruptWriter.Write(2);
    corruptWriter.Write("Mod/Shop_6.json");
    corruptWriter.Write(3);
    corruptWriter.Write(new byte[] { 1, 2, 3 });
    corruptWriter.Write("Mod/Other_6.json");
    corruptWriter.Write(100); // truncated second file, after a complete first file
    corruptWriter.Flush();
    bool corruptRejected = false;
    try
    {
        bundleApply.Invoke(null, new object[] { corruptStream.ToArray(), 6, 7, bundleTestRoot });
    }
    catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidDataException or EndOfStreamException)
    {
        corruptRejected = true;
    }
    Check(corruptRejected && File.ReadAllText(bundleTestFile) == "existing slot data"
        && !File.Exists(bundleTestFile + ".coopbak"), "a truncated mod-data bundle leaves all existing destination files untouched");
}
finally
{
    File.Delete(bundleTestFile);
    File.Delete(bundleTestFile + ".coopbak");
    Directory.Delete(bundleTestDir);
    Directory.Delete(bundleTestRoot);
}
var shelfOutcome = RoundTrip(new ShelfTransferResultMessage
{
    Key = 101,
    TransferSeq = 2,
    AcceptedDelta = -1,
    State = new CardShopCoop.Sync.WorldSync.Entry { Key = 101, Type = 0, Count = 7 }
});
Check(shelfOutcome.State.HasValue && shelfOutcome.State.Value.Key == 101
    && shelfOutcome.State.Value.Count == 7 && shelfOutcome.AcceptedDelta == -1,
    "shelf outcome carries its own targeted authoritative state through the production converter");
Check(!RoundTrip(new ShelfTransferResultMessage { Key = 102, TransferSeq = 3 }).State.HasValue,
    "a missing shelf can return an explicit absent state rather than fabricated zero stock");
var stableCounters = RoundTrip(new SettingsStateMessage
{
    CashierFlags = new List<byte> { 1, 2 },
    CashierIds = new List<ushort> { 20, 10 }
});
Check(stableCounters.CashierIds.SequenceEqual(new ushort[] { 20, 10 })
    && stableCounters.CashierFlags.SequenceEqual(new byte[] { 1, 2 }),
    "counter settings retain their stable object identities alongside their flags");
var stableTables = RoundTrip(new TableStateMessage
{
    Tables = new List<TableEntry> { new() { ObjectId = 37, Occupied = true } }
});
Check(stableTables.Tables[0].ObjectId == 37 && stableTables.Tables[0].Occupied,
    "play-table state retains stable identity rather than relying on list position");
var counterIntent = RoundTrip(new SettingsOpMessage { Op = 5, ObjectKey = (4 << 24) | 19, CashierFlags = 2 });
Check(counterIntent.ObjectKey == ((4 << 24) | 19) && counterIntent.CashierFlags == 2,
    "counter toggle targets a stable identity on the wire");
Check(RoundTrip(new BoxRepairRequestMessage { BoxId = 92 }).BoxId == 92,
    "targeted box repair request retains the shared box ID");
var queueSpendTarget = typeof(CEventManager).GetMethod("QueueEventPrivate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
Check(queueSpendTarget != null && queueSpendTarget.ReturnType == typeof(bool)
    && queueSpendTarget.GetParameters().Single().ParameterType == typeof(CEvent),
    "queued money reservation hook matches the installed game's accepted-event method");
var appliedSpendTarget = typeof(CPlayerData).GetMethod("CPlayer_OnReduceCoin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
Check(appliedSpendTarget != null && appliedSpendTarget.GetParameters().Single().ParameterType == typeof(CEventPlayer_ReduceCoin),
    "queued money release hook matches the installed game's actual wallet update method");
var mutationRouter = new MessageRouter();
int chargedSideEffects = 0, mutationHeals = 0;
mutationRouter.Register<StaffOpMessage>((context, message) =>
{
    chargedSideEffects++;
    throw new InvalidOperationException("simulated native failure after a charge");
}, retryable: true, heal: () => mutationHeals++);
try
{
    mutationRouter.Dispatch(new MessageContext { Role = CardShopCoop.CoopRole.Host, InGame = true }, new StaffOpMessage());
}
catch (InvalidOperationException)
{
    if (!mutationRouter.IsRetryable(MsgType.StaffOp))
        mutationRouter.Heal(MsgType.StaffOp);
}
Check(!mutationRouter.IsRetryable(MsgType.StaffOp) && chargedSideEffects == 1 && mutationHeals == 1,
    "a failed money operation refreshes state rather than automatically repeating a partial charge");
mutationRouter.Register<ShelfRequestMessage>((context, message) => { }, retryable: true);
Check(mutationRouter.IsRetryable(MsgType.ShelfRequest), "sequence-protected item operations retain their recovery retries");
Console.WriteLine($"{passed} presentation checks passed. Unity playback requires an in-game test.");
