namespace SharpLink.FlowStatePhaseB;

internal static partial class Checks
{
    internal static int Passed;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection.");
    }

    internal static async Task RunAsync()
    {
        await RunFusedPublicationAsync();
        await Case("grant accounting and unsent exactly-once", async () =>
        {
            await using var owner = new GrantAuthority(1024, 2048, 256);
            var lease = await owner.OpenAsync(1);
            var receipt = await owner.AcquireAsync(lease, 16);
            var ledger = await owner.SnapshotAsync();
            Require(ledger is { Free: 1792, Unspent: 240, Pending: 16, Outstanding: 0 }, "grant ledger");
            await owner.ReturnUnsentAsync(receipt);
            await Reject(async () => await owner.ReturnUnsentAsync(receipt));
            _ = await owner.SnapshotAsync();
        });
        await Case("local hot path avoids owner handoff", async () =>
        {
            await using var owner = new GrantAuthority(1024, 2048, 256);
            var lease = await owner.OpenAsync(1);
            for (var i = 0; i < 16; i++) owner.Commit(await owner.AcquireAsync(lease, 16));
            Require(owner.AcquireSubmissions == 1, "16 local debits should consume one grant");
            var ledger = await owner.SnapshotAsync();
            Require(ledger is { Unspent: 0, Outstanding: 256 }, "local conservation");
        });
        await Case("per-item queue control does not hide handoffs", async () =>
        {
            await using var owner = new GrantAuthority(1024, 2048, 0);
            var lease = await owner.OpenAsync(1);
            for (var i = 0; i < 16; i++) owner.Commit(await owner.AcquireAsync(lease, 16));
            Require(owner.AcquireSubmissions == 16, "B1 must queue every item");
        });
        await Case("waiter pressure revokes another stream's unspent grant", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            owner.Commit(await owner.AcquireAsync(a, 16));
            var receipt = await owner.AcquireAsync(b, 32).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Require(owner.Revocations == 1, "must reclaim A's unused 48 bytes");
            owner.Commit(receipt);
            var ledger = await owner.SnapshotAsync();
            Require(ledger is { Free: 16, Unspent: 0, Outstanding: 48 }, "revocation conservation");
        });
        await Case("connection FIFO and cancellation behind blocked head", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            var c = await owner.OpenAsync(3);
            owner.Commit(await owner.AcquireAsync(a, 64));
            var first = owner.AcquireAsync(b, 32).AsTask();
            using var cts = new CancellationTokenSource();
            var second = owner.AcquireAsync(c, 16, cts.Token).AsTask();
            var pending = await owner.SnapshotAsync();
            Require(pending.Waiters == 2, "both requests must be queued");
            cts.Cancel();
            try { await second.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("not canceled"); }
            catch (OperationCanceledException) { }
            await owner.WindowUpdateAsync(a, 16);
            Require(!first.IsCompleted, "partial connection credit must not bypass FIFO head");
            await owner.WindowUpdateAsync(a, 16);
            owner.Commit(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            _ = await owner.SnapshotAsync();
        });
        await Case("stream-blocked head permits eligible stream", async () =>
        {
            await using var owner = new GrantAuthority(16, 64, 16);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            owner.Commit(await owner.AcquireAsync(a, 16));
            var head = owner.AcquireAsync(a, 1).AsTask();
            owner.Commit(await owner.AcquireAsync(b, 1).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Require(!head.IsCompleted, "stream credit is still blocked");
            await owner.WindowUpdateAsync(a, 16);
            owner.Commit(await head);
            _ = await owner.SnapshotAsync();
        });
        await Case("oversized item borrows once and repays", async () =>
        {
            await using var owner = new GrantAuthority(16, 32, 16);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            owner.Commit(await owner.AcquireAsync(a, 64));
            Require((await owner.SnapshotAsync()).Free == -32, "single oversized debt");
            var pending = owner.AcquireAsync(b, 1).AsTask();
            await owner.WindowUpdateAsync(a, 32);
            Require(!pending.IsCompleted, "debt must be repaid first");
            await owner.WindowUpdateAsync(a, 32);
            owner.Commit(await pending);
            _ = await owner.SnapshotAsync();
        });
        await Case("peer updates cannot return unspent grants", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64);
            var a = await owner.OpenAsync(1);
            var receipt = await owner.AcquireAsync(a, 16);
            await owner.WindowUpdateAsync(a, 64);
            Require((await owner.SnapshotAsync()).Free == 0, "unsent grants are not peer-consumed");
            owner.Commit(receipt);
            await owner.WindowUpdateAsync(a, 64);
            await owner.WindowUpdateAsync(a, 64);
            Require((await owner.SnapshotAsync()).Free == 16, "duplicate update must not inflate credit");
        });
        await Case("close returns unspent but retains sent tombstone", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64, maxStreams: 1);
            var a = await owner.OpenAsync(1);
            owner.Commit(await owner.AcquireAsync(a, 16));
            await owner.CloseAsync(a);
            var ledger = await owner.SnapshotAsync();
            Require(ledger is { Free: 48, Outstanding: 16, Retained: 1 }, "tombstone");
            await Reject(async () => await owner.OpenAsync(1));
            await owner.WindowUpdateAsync(a, 16);
            var replacement = await owner.OpenAsync(1);
            Require(ReferenceEquals(a.State, replacement.State), "exercise real pool reuse");
            await Reject(async () => await owner.AcquireAsync(a, 1));
            await owner.WindowUpdateAsync(a, 64);
            Require((await owner.SnapshotAsync()).Free == 64, "old update cannot reach reused state");
        });
        await Case("abort revokes pending receipt", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64);
            var a = await owner.OpenAsync(1);
            var receipt = await owner.AcquireAsync(a, 16);
            await owner.CloseAsync(a);
            await owner.ReturnUnsentAsync(receipt);
            await Reject(() => { owner.Commit(receipt); return Task.CompletedTask; });
            Require((await owner.SnapshotAsync()).Free == 64, "pending abort must return exactly once");
        });
        await Case("multiple handles cannot overlap or settle twice", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64);
            var a = await owner.OpenAsync(1);
            var copy = a;
            var receipt = await owner.AcquireAsync(a, 16);
            await Reject(async () => await owner.AcquireAsync(copy, 16));
            owner.Commit(receipt);
            await Reject(() => { owner.Commit(receipt); return Task.CompletedTask; });
            _ = await owner.SnapshotAsync();
        });
        await Case("foreign-owner lease rejected", async () =>
        {
            await using var a = new GrantAuthority(64, 64, 64);
            await using var b = new GrantAuthority(64, 64, 64);
            var lease = await a.OpenAsync(1);
            await Reject(async () => await b.AcquireAsync(lease, 1));
        });
        await Case("terminal rejects retained handles and releases waiters", async () =>
        {
            var owner = new GrantAuthority(16, 16, 16);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            owner.Commit(await owner.AcquireAsync(a, 16));
            var pending = owner.AcquireAsync(b, 1).AsTask();
            await owner.DisposeAsync();
            await Reject(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await Reject(async () => await owner.AcquireAsync(a, 1));
        });
        await Case("closed waiter behind credit-blocked head completes promptly", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            var c = await owner.OpenAsync(3);
            owner.Commit(await owner.AcquireAsync(a, 64));
            var head = owner.AcquireAsync(b, 32).AsTask();
            var tail = owner.AcquireAsync(c, 16).AsTask();
            Require((await owner.SnapshotAsync()).Waiters == 2, "queue both before close");
            await owner.CloseAsync(c);
            await Reject(async () => await tail.WaitAsync(TimeSpan.FromSeconds(5)));
            Require(!head.IsCompleted, "closing tail must not create connection credit");
            await owner.WindowUpdateAsync(a, 64);
            owner.Commit(await head);
        });
        await Case("concurrent small-window grants make progress without credit inflation", async () =>
        {
            await using var owner = new GrantAuthority(64, 256, 256, maxStreams: 32);
            var leases = new GrantAuthority.Lease[32];
            for (var i = 0; i < leases.Length; i++) leases[i] = await owner.OpenAsync(i + 1);
            var tasks = leases.Select(lease => Task.Run(async () =>
            {
                for (var i = 0; i < 64; i++)
                {
                    var receipt = await owner.AcquireAsync(lease, 16);
                    if (i % 3 == 0) await owner.ReturnUnsentAsync(receipt);
                    else
                    {
                        owner.Commit(receipt);
                        await owner.WindowUpdateAsync(lease, 16);
                    }
                }
                await owner.CloseAsync(lease);
            })).ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
            Require((await owner.SnapshotAsync()) is { Free: 256, Unspent: 0, Pending: 0, Outstanding: 0 },
                "all credit must return after concurrent completion");
        });
        await Case("100000 generations preserve ABA rejection", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16, maxStreams: 1);
            var stale = await owner.OpenAsync(1);
            await owner.CloseAsync(stale);
            for (var i = 0; i < 100000; i++)
            {
                var next = await owner.OpenAsync(1);
                owner.Commit(await owner.AcquireAsync(next, 1));
                await owner.WindowUpdateAsync(next, 1);
                await owner.CloseAsync(next);
            }
            await Reject(async () => await owner.AcquireAsync(stale, 1));
            Require((await owner.SnapshotAsync()).Free == 16, "pool credit conservation");
        });
        await Case("B0 retains receive threshold and terminal behavior", async () =>
        {
            var b0 = new SplitGate.StreamFlowController(4, 4, 1024);
            var a = b0.ResolveReceiveCreditLease(1, 1);
            var b = b0.ResolveReceiveCreditLease(2, 1);
            b0.AcceptReceived(in a, 1);
            Require(b0.RecordConsumed(in a, 1) == 0, "stream threshold");
            b0.AcceptReceived(in b, 1);
            Require(b0.RecordConsumed(in b, 1) == 1, "connection threshold");
            Require(b0.TryTakeConsumedCreditUpdate(out var id, out _, out var bytes) && id == 1 && bytes == 1,
                "cross-stream flush exact identity");
            b0.Complete(new InvalidOperationException("terminal"));
            await Reject(() => { b0.AcceptReceived(in a, 1); return Task.CompletedTask; });
        });
        await RunReusableAsync();
        await RunPublicationAsync();
        Console.WriteLine($"Phase B focused checks: {Passed} passed.");
    }

    private static async Task Case(string name, Func<Task> test)
    {
        await test().WaitAsync(TimeSpan.FromSeconds(60));
        Passed++;
        Console.WriteLine($"PASS: {name}");
    }
}
