using CardShopCoop.Net;
using CardShopCoop.Sync;

internal static class NetReliabilityChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new Exception(name);
            checks++;
            Console.WriteLine("PASS " + name);
        }
        double now = 0;
        var queue = new ReliableSendQueue(() => now);
        var sent = new List<(int Peer, byte Value)>();
        var failures = new List<int>();
        bool blocked = true;
        bool Send(int peer, byte[] data)
        {
            if (peer == 1 && blocked)
                return false;
            sent.Add((peer, data[0]));
            return true;
        }
        queue.Enqueue(1, new byte[] { 10 });
        queue.Enqueue(1, new byte[] { 11 });
        queue.Enqueue(2, new byte[] { 20 });
        queue.Enqueue(2, new byte[] { 21 });
        queue.Pump(1024, 256, Send, (peer, reason) => failures.Add(peer));
        Check(sent.SequenceEqual(new[] { (2, (byte)20), (2, (byte)21) }) && failures.Count == 0,
            "a refused reliable send stalls only its own peer while another player's FIFO continues");
        now = 4;
        var status = queue.Snapshot().Single();
        Check(status.ConnectionId == 1 && status.Frames == 2 && status.Bytes == 2
            && status.OldestSeconds == 4 && status.StalledSeconds == 4,
            "queue report measures actual pending bytes, age and per-peer refusal time");
        blocked = false;
        queue.Pump(1024, 256, Send, (peer, reason) => failures.Add(peer));
        Check(sent.Skip(2).SequenceEqual(new[] { (1, (byte)10), (1, (byte)11) }) && queue.Snapshot().Count == 0,
            "recovery sends the original refused frame before later operations without duplicating another peer");

        sent.Clear();
        for (byte i = 0; i < 6; i++)
        {
            queue.Enqueue(1, new byte[] { i });
            queue.Enqueue(2, new byte[] { (byte)(10 + i) });
        }
        queue.Pump(4, 2, Send, (peer, reason) => failures.Add(peer));
        Check(sent.Count(item => item.Peer == 1) == 2 && sent.Count(item => item.Peer == 2) == 2
            && queue.Snapshot().All(item => item.Frames == 4),
            "per-peer budgets prevent a bulk sender from consuming all reliable frame time");
        queue.Pump(1, 1, Send, (peer, reason) => failures.Add(peer));
        int firstLimited = sent[^1].Peer;
        queue.Pump(1, 1, Send, (peer, reason) => failures.Add(peer));
        Check(firstLimited != sent[^1].Peer, "a tiny global budget rotates which active peer is served first");
        queue.Clear();
        sent.Clear();
        blocked = true;
        now = 10;
        queue.Enqueue(1, new byte[] { 30 });
        queue.Pump(1024, 256, Send, (peer, reason) => failures.Add(peer));
        now = 24.9;
        queue.Pump(1024, 256, Send, (peer, reason) => failures.Add(peer));
        Check(failures.Count == 0, "a temporary refusal is not discarded before the per-peer deadline");
        now = 25;
        queue.Enqueue(2, new byte[] { 40 });
        queue.Pump(1024, 256, Send, (peer, reason) => failures.Add(peer));
        Check(failures.SequenceEqual(new[] { 1 }) && sent.Single() == (2, (byte)40) && queue.Snapshot().Count == 0,
            "persistent refusal explicitly fails only its peer and leaves other players usable");
        queue.Enqueue(1, new byte[] { 50 });
        queue.RemovePeer(1);
        Check(queue.Snapshot().Count == 0, "disconnect removes that peer's queued operations");

        failures.Clear();
        sent.Clear();
        for (int i = 0; i < 8193; i++)
            queue.Enqueue(3, new byte[] { 1 });
        Check(queue.Snapshot().Single().Overflowed && queue.Snapshot().Single().Frames == 8192,
            "reliable queue has a bounded per-peer frame limit");
        queue.Pump(1024, 256, Send, (peer, reason) => failures.Add(peer));
        Check(failures.SequenceEqual(new[] { 3 }) && sent.Count == 0 && queue.Snapshot().Count == 0,
            "queue overflow explicitly ends the affected connection before silently losing an operation");
        queue.Enqueue(4, new byte[] { 1 });
        queue.Pump(1024, 256, (peer, data) => { queue.RemovePeer(peer); return true; }, (peer, reason) => failures.Add(peer));
        Check(queue.Snapshot().Count == 0, "a disconnect inside a send cannot dequeue a replacement peer's frame");

        queue.Enqueue(1, new byte[] { 1 });
        queue.Enqueue(2, new byte[] { 2 });
        sent.Clear();
        failures.Clear();
        queue.Pump(1024, 256, (peer, data) =>
        {
            if (peer == 1)
                throw new InvalidOperationException("simulated native failure");
            sent.Add((peer, data[0]));
            return true;
        }, (peer, reason) => failures.Add(peer));
        Check(failures.SequenceEqual(new[] { 1 }) && sent.Single() == (2, (byte)2),
            "native send exception cannot abort every other peer's queue or silently retry an uncertain send");

        queue.Clear();
        var stockFrame = new byte[1024];
        for (int i = 0; i < 2000; i++)
            for (int peer = 1; peer <= 3; peer++)
                queue.Enqueue(peer, stockFrame);
        var busyDelivered = new int[4];
        int busyCycles = 0;
        while ((busyDelivered[2] < 2000 || busyDelivered[3] < 2000) && busyCycles < 20)
        {
            queue.Pump(1024 * 1024, 256 * 1024, (peer, data) =>
            {
                if (peer == 1)
                    return false;
                busyDelivered[peer]++;
                return true;
            }, (peer, reason) => throw new Exception(reason));
            busyCycles++;
        }
        Check(busyCycles == 8 && busyDelivered[2] == 2000 && busyDelivered[3] == 2000,
            "three-peer bulk simulation drains 4000 healthy-peer updates in eight bounded pumps despite one blocked peer");
        Check(queue.Snapshot().Single().ConnectionId == 1 && queue.Snapshot().Single().Frames == 2000,
            "bulk isolation retains every blocked peer frame for recovery rather than dropping its backlog");

        queue.Clear();
        Parallel.For(0, 1000, i => queue.Enqueue(i % 2 + 1, new byte[] { 1 }));
        Check(queue.Snapshot().Sum(item => item.Frames) == 1000 && queue.Snapshot().Sum(item => item.Bytes) == 1000,
            "worker-thread enqueues retain every reliable frame and consistent counters");
        queue.Clear();
        Check(SessionHealth.Describe(1, 20, 0, 0).Contains("Item sync is stalled"),
            "fresh movement alone cannot label a stuck item transfer healthy");
        Check(SessionHealth.Describe(0, 0, 8, 4).Contains("Sending is delayed"),
            "connection help distinguishes refused sends from item acknowledgements");
        Check(SessionHealth.Describe(0, 0, 8, 0).Contains("send queue"), "connection help reports outgoing backlog");
        Check(SessionHealth.Describe(0, 0, 0, 0).Contains("does not verify every shared object"),
            "an idle queue is not presented as proof that all game state agrees");
        Check(SessionHealth.Describe(1, double.NaN, 0, 0).Contains("unavailable"),
            "invalid measurements are unavailable rather than a false healthy signal");
        var spending = new QueuedSpendLedger();
        var debitA = new object();
        Check(spending.Track(debitA, 80) && spending.Available(100, 0) == 20,
            "native queued debit remains reserved after this frame's temporary reservation resets");
        Check(spending.Available(100, 0) < 30 && spending.Available(100, 10) == 10,
            "delayed native debit and a fresh guest reservation cannot spend the same remaining funds");
        var reusedEventLedger = new QueuedSpendLedger();
        reusedEventLedger.Track(debitA, 2);
        reusedEventLedger.Track(debitA, 2);
        Check(reusedEventLedger.Total == 4 && reusedEventLedger.Count == 2,
            "the same event object actually queued twice reserves both native debits");
        reusedEventLedger.Applied(debitA);
        Check(reusedEventLedger.Total == 2 && reusedEventLedger.Count == 1,
            "the first native callback cannot release a second queued instance of the same event");
        var debitB = new object();
        Check(spending.Track(debitB, 5) && spending.Total == 85,
            "a separately queued host purchase also reduces what guests can spend");
        spending.Applied(debitA);
        Check(spending.Available(20, 0) == 15 && spending.Count == 1,
            "applying the wallet reduction releases exactly its matching queued reservation");
        spending.Applied(debitA);
        Check(spending.Total == 5, "duplicate native application notice cannot release another debit");
        Check(!spending.Track(null, 10) && !spending.Track(new object(), double.NaN)
            && !spending.Track(new object(), -10) && !spending.Track(new object(), double.PositiveInfinity),
            "invalid queued debit values cannot corrupt the available shared balance");
        spending.Clear();
        Check(spending.Count == 0 && spending.Total == 0, "world/session teardown clears old native money reservations");
        var repairs = new TransferRetryQueue<ushort>();
        repairs.Register(3, 3, 0);
        repairs.ResendDue(20, 8, (sequence, id) => { });
        Check(repairs.Contains(3) && repairs.OldestAge(20) == 20,
            "retrying a targeted repair cannot reset its real age and hide a stalled repair");
        repairs.Remove(3);
        Check(!repairs.Contains(3) && repairs.OldestAge(20) == 0,
            "receiving the repaired object's snapshot clears its pending age and retry");
        return checks;
    }
}
