using CardShopCoop;
using CardShopCoop.Sync;
using UnityEngine;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    passed++;
    Console.WriteLine("PASS " + name);
}
void Reset()
{
    CoopCore.BlockRemoval = false;
    HandEscrow.Reset();
    CoopCore.PlayerIpc = new();
    Time.realtimeSinceStartupAsDouble = 0;
}
Item PickUp(EItemType type = EItemType.Pack)
{
    var item = new Item { Type = type };
    CoopCore.PlayerIpc.Held.Add(item);
    HandEscrow.NoteTakenItem(item);
    return item;
}

Check(ItemTransferRules.TryDelta(10, 9, out int take) && take == -1,
    "request retains a one-item delta independently of the later host count");
Check(!ItemTransferRules.TryDelta(-1, 2, out _) && !ItemTransferRules.TryDelta(2, -1, out _),
    "negative container counts cannot overflow into a different operation");
Check(ItemTransferRules.TryPlan(8, 0, 0, -1, 20, out int accepted, out int type) && accepted == -1,
    "guest can take one pack after a customer changed the host count");
Check(ItemTransferRules.TryPlan(1, 0, 0, -3, 20, out accepted, out _) && accepted == -1,
    "simultaneous takes accept only the remaining current stock");
Check(!ItemTransferRules.TryPlan(5, 1, 0, -1, 20, out accepted, out _) && accepted == 0,
    "old-view pickup cannot remove a different product after a refill");
Check(ItemTransferRules.TryPlan(0, 1, 0, 3, 0, out accepted, out type) && accepted == 3 && type == 0,
    "empty locked label can rebind to an incoming product before measuring native capacity");
Check(ItemTransferRules.TryPlan(7, 0, 0, 3, 8, out accepted, out _) && accepted == 1,
    "restocking uses current capacity and returns only the unaccepted remainder");
Check(!ItemTransferRules.TryPlan(1, 1, 0, 1, 8, out _, out _), "restock cannot replace a different live product");
Check(ItemTransferRules.TryPlan(int.MaxValue - 1, 0, 0, 5, 0, out accepted, out _) && accepted == 1,
    "unbuilt capacity cannot overflow a target count");
Check(!ItemTransferRules.ValidResult(10, 11, -1, -1)
    && !ItemTransferRules.ValidResult(10, 10, -1, 1)
    && !ItemTransferRules.ValidResult(10, 10, -1, -2)
    && !ItemTransferRules.ValidResult(10, 10, 1, 2),
    "wrong object, wrong direction and excessive acceptance cannot release a pending operation");

Reset();
var pending = new PendingTransferLedger<int>();
var retry = new TransferRetryQueue<(int Target, int Delta)>();
var hostAcks = new HostTransferAcks();
var pack = PickUp();
uint sequence = pending.Begin(101, -1, 0, out _, true, pack);
retry.Register(sequence, (101, -1), 0);
Check(sequence != 0 && HandEscrow.IsReserved(pack) && pending.IsTakeReserved(101),
    "actual pickup escrow protects the exact item before the host replies");
int hostStock = 8, hostMutations = 0;
void DeliverToHost(uint seq, (int Target, int Delta) request, bool loseReply)
{
    if (!hostAcks.TryGet(1, seq, out int amount))
    {
        ItemTransferRules.TryPlan(hostStock, 0, 0, request.Delta, 20, out amount, out _);
        hostStock += amount;
        hostMutations++;
        hostAcks.Store(1, seq, amount);
    }
    if (loseReply || !pending.TryGet(seq, out var entry))
        return;
    if (!ItemTransferRules.ValidResult(entry.Target, request.Target, entry.RequestedDelta, amount))
        return;
    pending.ResolveTake(entry, amount);
    pending.TryResolve(seq, out _);
    retry.Remove(seq);
}
DeliverToHost(sequence, (101, -1), true);
Time.realtimeSinceStartupAsDouble = 30;
HandEscrow.Tick();
Check(HandEscrow.IsReserved(pack) && !pack.Destroyed && pending.Count == 1,
    "a delayed or lost reply never turns into an untracked usable item just because time passed");
retry.ResendDue(30, 8, (seq, request) => DeliverToHost(seq, request, false));
Check(hostMutations == 1 && hostStock == 7 && pending.Count == 0 && retry.Count == 0
    && !HandEscrow.IsReserved(pack) && !pack.Destroyed && CoopCore.PlayerIpc.Held.Contains(pack),
    "lost reply is recovered by the original request without moving stock twice or leaving the guest locked");
DeliverToHost(sequence, (101, -1), false);
Check(hostMutations == 1 && CoopCore.PlayerIpc.Held.Count == 1,
    "duplicate reply cannot create another item or resolve a later operation");
Check(hostStock + CoopCore.PlayerIpc.Held.Count + 2 == 10,
    "two customer purchases plus the guest pickup preserve original stock");

Reset();
pending.Clear();
var first = PickUp();
var second = PickUp();
uint firstSeq = pending.Begin(102, -1, 0, out _, true, first);
uint secondSeq = pending.Begin(102, -1, 0, out _, true, second);
pending.TryGet(secondSeq, out var secondEntry);
pending.ResolveTake(secondEntry, -1);
pending.TryResolve(secondSeq, out _);
Check(!HandEscrow.IsReserved(second) && HandEscrow.IsReserved(first) && pending.IsTakeReserved(102),
    "out-of-order replies release their exact item while the earlier item stays protected");
pending.TryGet(firstSeq, out var firstEntry);
pending.ResolveTake(firstEntry, 0);
pending.TryResolve(firstSeq, out _);
Check(first.Destroyed && !second.Destroyed && !pending.IsTakeReserved(102) && pending.Count == 0,
    "rejected rapid pickup removes only its own optimistic item and releases the final source guard");

Reset();
var rejected = PickUp();
int token = HandEscrow.ReserveTake(rejected, out _);
CoopCore.BlockRemoval = true;
Check(!HandEscrow.ResolveTake(token, 0) && HandEscrow.IsReserved(rejected)
    && HandEscrow.DeferredRejectedItemCount == 1 && !rejected.Destroyed,
    "failed native hand removal keeps a rejected item safe for deferred cleanup");
CoopCore.BlockRemoval = false;
HandEscrow.Tick();
Check(rejected.Destroyed && !HandEscrow.IsReserved(rejected) && HandEscrow.DeferredRejectedItemCount == 0,
    "deferred cleanup recovers a rejected item once native removal becomes available");

Reset();
var compartment = new ShelfCompartment { Type = EItemType.Pack };
var returned = new Item { Type = EItemType.Pack };
compartment.Items.Add(returned);
CoopCore.PlayerIpc.HoldingBox = true;
Check(HandEscrow.EscrowAdded(compartment, 0, 1) == 1 && compartment.GetItemCount() == 0
    && HandEscrow.QueuedToHandCount == 1 && !returned.gameObject.Active,
    "rejected restock is preserved while holding a box prevents returning it to hand");
HandEscrow.Tick();
Check(CoopCore.PlayerIpc.Held.Count == 0 && HandEscrow.QueuedToHandCount == 1,
    "a modal box hold cannot consume or lose the queued return");
CoopCore.PlayerIpc.HoldingBox = false;
HandEscrow.Tick();
Check(HandEscrow.QueuedToHandCount == 0 && CoopCore.PlayerIpc.Held.Contains(returned)
    && !HandEscrow.IsReserved(returned) && returned.gameObject.Active,
    "returned item becomes immediately usable without becoming a new pickup transfer");

Reset();
var spray = PickUp(EItemType.Spray);
sequence = pending.Begin(103, -1, 2, out _, true, spray);
pending.TryGet(sequence, out var sprayEntry);
pending.ResolveTake(sprayEntry, -1);
pending.TryResolve(sequence, out _);
Check(!HandEscrow.IsFrontReserved(CoopCore.PlayerIpc) && !HandEscrow.IsReserved(spray),
    "accepted spray pickup releases the real escrow guard used by item interaction");
uint previousSeq = sequence;
pack = PickUp();
sequence = pending.Begin(104, -1, 0, out _, true, pack);
pending.Clear();
retry.Clear();
HandEscrow.Reset();
Check(pending.Count == 0 && !HandEscrow.IsReserved(pack), "session cleanup leaves no old hand or source reservation");
pack = PickUp();
uint nextSessionSeq = pending.Begin(104, -1, 0, out _, true, pack);
Check(nextSessionSeq > sequence && !pending.TryGet(previousSeq, out _) && !pending.TryGet(sequence, out _),
    "scene/session cleanup does not reuse an old transfer sequence");

hostAcks.Clear();
hostAcks.Store(1, 1, -1);
hostAcks.Store(1, 1, 0);
Check(hostAcks.TryGet(1, 1, out int firstOutcome) && firstOutcome == -1, "first transfer outcome wins even after a conflicting retry");
for (uint seq = 2; seq <= 8193; seq++) hostAcks.Store(1, seq, -1);
Check(!hostAcks.TryGet(1, 1, out _) && hostAcks.IsExpired(1, 1) && !hostAcks.IsExpired(1, 8194),
    "evicted old requests are detected rather than being applied again as new moves");
hostAcks.ReleaseConn(1);
Check(!hostAcks.IsExpired(1, 1), "a disconnected peer's expired history cannot poison a new connection");

PlacedObjectIdentity.Reset();
CoopCore.Role = CoopRole.Host;
var counterA = new InteractableObject { Name = "Counter" };
var counterB = new InteractableObject { Name = "Counter" };
ushort idA = PlacedObjectIdentity.AssignHost(counterA), idB = PlacedObjectIdentity.AssignHost(counterB);
Check(idA != idB, "identical furniture names retain separate shared identities");
var reorderedShop = new ShelfManager();
reorderedShop.Lists[4] = new List<InteractableObject> { counterB, counterA };
Check(PlacedObjectIdentity.TryResolve(reorderedShop, 4, idA, out var selected) && ReferenceEquals(selected, counterA),
    "counter toggle identity resolves correctly despite reversed local list order");
((List<InteractableObject>)reorderedShop.Lists[4]).Remove(counterA);
Check(!PlacedObjectIdentity.TryResolve(reorderedShop, 4, idA, out _),
    "removing a counter cannot redirect a delayed action to the counter that replaced its index");
reorderedShop.Lists[6] = new List<InteractableObject> { counterB };
Check(PlacedObjectIdentity.TryResolve(reorderedShop, 6, idB, out selected) && ReferenceEquals(selected, counterB),
    "play-table identity uses the current roster rather than the sender's index");
Check(!PlacedObjectIdentity.TryResolve(reorderedShop, 4, 0, out _) && !PlacedObjectIdentity.TryResolve(reorderedShop, 5, idB, out _),
    "invalid or wrong-kind object identities have no index fallback");

Console.WriteLine($"{passed} item transfer and identity checks passed. Native interaction, physics and two-PC rendering require live tests.");
