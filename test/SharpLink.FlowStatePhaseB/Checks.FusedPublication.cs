namespace SharpLink.FlowStatePhaseB;

internal static partial class Checks
{
    private static async Task RunFusedPublicationAsync()
    {
        await Case("direct admission owns publication before a held result is consumed", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0, maxStreams: 1);
            var lease = await owner.OpenAsync(1);
            // First use always takes the owner queue. Close is the FIFO barrier:
            // do not await the result until after the owner has also processed close.
            var held = owner.AcquirePublicationAsync(lease, 16);
            await owner.CloseAsync(lease);
            var ledger = await owner.SnapshotAsync();
            Require(ledger is { Free: 0, Pending: 0, Outstanding: 16, Retained: 1, Publications: 1 },
                "admission must pin before completion, not lazily at GetResult");
            var publication = await held;
            await owner.WindowUpdateAsync(lease, 16);
            await owner.FinishPublicationAsync(publication, accepted: true);
            Require((await owner.SnapshotAsync()) is { Free: 16, Retained: 0, Publications: 0 }, "last writer releases tombstone");
        });
        await Case("direct local grants reuse commands without additional owner turns", async () =>
        {
            await using var owner = new GrantAuthority(8192, 8192, 4096);
            var lease = await owner.OpenAsync(1);
            var commands = owner.ReusableCommandAllocations;
            var submissions = owner.QueueSubmissions;
            for (var i = 1; i <= 4096; i++)
            {
                var publication = await owner.AcquirePublicationAsync(lease, 16);
                await owner.FinishPublicationAsync(publication, accepted: true);
                if (i % 64 == 0) await owner.WindowUpdateAsync(lease, 1024);
            }
            Require(owner.QueueSubmissions - submissions == 16 + 64, "only refills and peer updates reach the owner");
            Require(owner.ReusableCommandAllocations == commands, "no typed completion wrapper allocations");
            Require((await owner.SnapshotAsync()) is { Pending: 0, Outstanding: 0, Publications: 0, Waiters: 0 }, "all direct publications settled");
        });
        await Case("direct completion canceled after admission still conveys writer responsibility", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0);
            var lease = await owner.OpenAsync(1);
            using var cts = new CancellationTokenSource();
            var held = owner.AcquirePublicationAsync(lease, 16, cts.Token);
            _ = await owner.SnapshotAsync(); // command completed but not consumed
            cts.Cancel();
            await owner.CloseAsync(lease);
            Require(held.IsCompletedSuccessfully, "cancellation cannot undo a completed admission");
            var publication = await held;
            await owner.FinishPublicationAsync(publication, accepted: false);
            Require((await owner.SnapshotAsync()) is { Free: 16, Retained: 0, Publications: 0 }, "one writer refund after close");
        });
        await Case("close queued before direct admission must not publish or pin a recycled stream", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0, maxStreams: 1);
            var lease = await owner.OpenAsync(1);
            using var pause = new OwnerPause(owner);
            var closed = owner.CloseAsync(lease);
            var waiting = owner.AcquirePublicationAsync(lease, 16);
            pause.Release();
            await closed;
            var fresh = await owner.OpenAsync(1);
            Require(ReferenceEquals(lease.State, fresh.State), "actual reuse after close wins");
            var live = owner.AcquirePublicationAsync(fresh, 16);
            await Reject(async () => await waiting);
            await Reject(async () => await owner.AcquireAsync(fresh, 1));
            await owner.FinishPublicationAsync(await live, accepted: false);
            Require((await owner.SnapshotAsync()) is { Outstanding: 0, Publications: 0 }, "old failure cannot release replacement ownership");
        });
        await Case("direct FIFO admission cannot bypass an older connection-blocked request", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 256);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            var c = await owner.OpenAsync(3);
            var active = await owner.AcquirePublicationAsync(a, 16);
            await owner.FinishPublicationAsync(active, accepted: true);
            var older = owner.AcquirePublicationAsync(b, 16);
            var newer = owner.AcquirePublicationAsync(c, 1);
            Require((await owner.SnapshotAsync()).Waiters == 2, "both queued");
            await owner.WindowUpdateAsync(a, 8);
            Require(!older.IsCompleted && !newer.IsCompleted, "small caller cannot steal connection credit");
            await owner.WindowUpdateAsync(a, 8);
            await owner.CloseAsync(b); // also barriers the subsequent waiter drain
            Require(!newer.IsCompleted, "successful older admission owns credit even before result consumption");
            await owner.FinishPublicationAsync(await older, accepted: false);
            await owner.FinishPublicationAsync(await newer, accepted: false);
            Require((await owner.SnapshotAsync()) is { Publications: 0, Waiters: 0, Outstanding: 0 }, "FIFO refunds settle");
        });
        await Case("direct waiter cancellation under bounded queue pressure cannot leak a pin", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0, queueCapacity: 1);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            await owner.FinishPublicationAsync(await owner.AcquirePublicationAsync(a, 16), accepted: true);
            using var cts = new CancellationTokenSource();
            using var pause = new OwnerPause(owner);
            var update = owner.WindowUpdateAsync(a, 16); // fill queue
            var waiting = owner.AcquirePublicationAsync(b, 16, cts.Token);
            Require(owner.QueueBackpressureWaits == 1, "direct command waits for bounded capacity");
            cts.Cancel(); // wake coalesces; queued work guarantees an owner turn
            pause.Release();
            await update;
            try { _ = await waiting; throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require(!b.State.AcquirePending && !b.State.AcquireCommand.IsBusy, "canceled typed view consumes and releases the same slot");
            Require((await owner.SnapshotAsync()) is { Free: 16, Publications: 0, Pending: 0 }, "no ghost publication");
        });
        await Case("direct stream-blocked head permits the same fairness skip as ordinary admission", async () =>
        {
            await using var owner = new GrantAuthority(16, 32, 0);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            await owner.FinishPublicationAsync(await owner.AcquirePublicationAsync(a, 16), accepted: true);
            var head = owner.AcquirePublicationAsync(a, 1);
            Require((await owner.SnapshotAsync()).Waiters == 1, "head lacks only stream credit");
            var permitted = await owner.AcquirePublicationAsync(b, 16);
            Require(!head.IsCompleted, "skip does not satisfy blocked stream");
            await owner.FinishPublicationAsync(permitted, accepted: true);
            await owner.WindowUpdateAsync(a, 16);
            await owner.FinishPublicationAsync(await head, accepted: false);
            _ = await owner.SnapshotAsync();
        });
        await Case("receipt and publication typed views alternate on one reusable operation", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0);
            var lease = await owner.OpenAsync(1);
            var allocations = owner.ReusableCommandAllocations;
            for (var i = 0; i < 512; i++)
            {
                await owner.FinishPublicationAsync(await owner.AcquirePublicationAsync(lease, 16), accepted: true);
                await owner.WindowUpdateAsync(lease, 16);
                var receipt = await owner.AcquireAsync(lease, 16);
                Require((await owner.SnapshotAsync()) is { Pending: 16, Publications: 0 }, "ordinary view must reset direct mode");
                owner.Commit(receipt);
                await owner.WindowUpdateAsync(lease, 16);
            }
            Require(owner.ReusableCommandAllocations == allocations, "one preallocated command supports both views");
        });
        await Case("direct oversized borrow, observed rejection and exact refund keep the ledger", async () =>
        {
            await using var owner = new GrantAuthority(16, 32, 256);
            var lease = await owner.OpenAsync(1);
            var publication = await owner.AcquirePublicationAsync(lease, 64);
            Require((await owner.SnapshotAsync()) is { Free: -32, Pending: 0, Outstanding: 64, Publications: 1 }, "borrow only from a full window");
            await owner.WindowUpdateAsync(lease, 16);
            await Reject(async () => await owner.FinishPublicationAsync(publication, accepted: false));
            await owner.FinishPublicationAsync(publication, accepted: true);
            await owner.WindowUpdateAsync(lease, 48);
            publication = await owner.AcquirePublicationAsync(lease, 64);
            await owner.CloseAsync(lease);
            await owner.FinishPublicationAsync(publication, accepted: false);
            await Reject(async () => await owner.FinishPublicationAsync(publication, accepted: false));
            Require((await owner.SnapshotAsync()) is { Free: 32, Retained: 0, Publications: 0 }, "oversized unsent refund once");
        });
        await Case("concurrent direct callers have one winner and one publication pin", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64);
            var lease = await owner.OpenAsync(1);
            var winners = new System.Collections.Concurrent.ConcurrentBag<GrantAuthority.Publication>();
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            {
                try { winners.Add(await owner.AcquirePublicationAsync(lease, 1)); }
                catch (InvalidOperationException) { }
            })));
            Require(winners.Count == 1 && (await owner.SnapshotAsync()).Publications == 1, "only one caller may acquire this model's writer slot");
            foreach (var winner in winners) await owner.FinishPublicationAsync(winner, accepted: false);
        });
        await Case("direct late result can release a pin after terminal without restarting the owner", async () =>
        {
            var owner = new GrantAuthority(16, 16, 0);
            var lease = await owner.OpenAsync(1);
            var held = owner.AcquirePublicationAsync(lease, 16);
            await owner.DisposeAsync();
            await owner.FinishPublicationAsync(await held, accepted: false);
            Require(!lease.State.PublicationActive && !lease.State.AcquirePending, "terminal settlement releases both lifetimes");
            await Reject(async () => await owner.AcquirePublicationAsync(lease, 1));
        });
        await Case("direct and split publication produce identical settled ledgers", async () =>
        {
            await using var split = new GrantAuthority(64, 128, 64);
            await using var fused = new GrantAuthority(64, 128, 64);
            var a = await split.OpenAsync(1);
            var b = await fused.OpenAsync(1);
            var random = new Random(735);
            for (var i = 0; i < 1024; i++)
            {
                var size = random.Next(1, 65);
                var left = split.BeginPublication(await split.AcquireAsync(a, size));
                var right = await fused.AcquirePublicationAsync(b, size);
                var close = random.Next(4) == 0;
                var accepted = random.Next(2) == 0;
                if (close) { await split.CloseAsync(a); await fused.CloseAsync(b); }
                if (accepted) { await split.WindowUpdateAsync(a, size); await fused.WindowUpdateAsync(b, size); }
                Require(await split.SnapshotAsync() == await fused.SnapshotAsync(), "same live ownership ledger");
                await split.FinishPublicationAsync(left, accepted);
                await fused.FinishPublicationAsync(right, accepted);
                Require(await split.SnapshotAsync() == await fused.SnapshotAsync(), "same settlement ledger");
                if (close) { a = await split.OpenAsync(1); b = await fused.OpenAsync(1); }
            }
        });
        await Case("100000 direct held admissions never substitute a pooled generation", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0, maxStreams: 1);
            GrantAuthority.Publication old = default;
            GrantAuthority.State? first = null;
            for (var i = 0; i < 100000; i++)
            {
                var lease = await owner.OpenAsync(1);
                first ??= lease.State;
                Require(ReferenceEquals(first, lease.State), "reuse the same pooled state");
                var held = owner.AcquirePublicationAsync(lease, 1);
                await owner.CloseAsync(lease);
                await owner.WindowUpdateAsync(lease, 1);
                var publication = await held;
                if (i == 0) old = publication;
                await owner.FinishPublicationAsync(publication, accepted: true);
            }
            var fresh = await owner.OpenAsync(1);
            var live = await owner.AcquirePublicationAsync(fresh, 1);
            await Reject(async () => await owner.FinishPublicationAsync(old, accepted: false));
            await owner.FinishPublicationAsync(live, accepted: false);
            await owner.CloseAsync(fresh);
            Require((await owner.SnapshotAsync()) is { Free: 16, Retained: 0, Publications: 0 }, "no old settlement affects a fresh writer");
        });
    }
}
