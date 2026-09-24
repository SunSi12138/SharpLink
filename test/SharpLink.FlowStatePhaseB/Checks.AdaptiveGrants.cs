namespace SharpLink.FlowStatePhaseB;

internal static partial class Checks
{
    private static async Task RunAdaptiveAsync()
    {
        await Case("adaptive pressure transition coalesces redundant sweeps", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64, 8, adaptiveGrants: true);
            var streams = new GrantAuthority.Lease[8];
            for (var i = 0; i < 8; i++) streams[i] = await owner.OpenAsync(i + 1);
            owner.Commit(await owner.AcquireAsync(streams[0], 64));
            var pending = new Task<GrantAuthority.Receipt>[7];
            for (var i = 0; i < 7; i++) pending[i] = owner.AcquireAsync(streams[i + 1], 1).AsTask();
            Require((await owner.SnapshotAsync()).Waiters == 7, "all requests must be queued");
            Require(owner.RevocationSweeps == 1, "one pressure transition must sweep once, not once per waiter");
            await owner.WindowUpdateAsync(streams[0], 64);
            foreach (var task in pending) owner.Commit(await task);
            _ = await owner.SnapshotAsync();
        });
        await Case("adaptive late unsent refund still wakes existing FIFO", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64, adaptiveGrants: true);
            var a = await owner.OpenAsync(1); var b = await owner.OpenAsync(2); var c = await owner.OpenAsync(3);
            var unsent = await owner.AcquireAsync(a, 64);
            var head = owner.AcquireAsync(b, 32).AsTask(); var tail = owner.AcquireAsync(c, 16).AsTask();
            Require((await owner.SnapshotAsync()).Waiters == 2 && owner.RevocationSweeps == 1, "initial pressure sweep");
            await owner.ReturnUnsentAsync(unsent);
            owner.Commit(await head); owner.Commit(await tail);
            var ledger = await owner.SnapshotAsync();
            Require(ledger.Free == 16 && ledger.Outstanding == 48 && ledger.Waiters == 0,
                "the refund's own owner command must reclaim credit and preserve progress");
            Require(owner.RevocationSweeps == 2, "late refund must still perform its required sweep");
        });
        await Case("adaptive initial grants leave refill headroom", async () =>
        {
            await using var owner = new GrantAuthority(8192, 8192, 4096, 8, adaptiveGrants: true);
            var streams = new GrantAuthority.Lease[8];
            for (var i = 0; i < 8; i++) streams[i] = await owner.OpenAsync(i + 1);
            foreach (var stream in streams) owner.Commit(await owner.AcquireAsync(stream, 16));
            var ledger = await owner.SnapshotAsync();
            Require(ledger.Free == 4096 && ledger.Unspent == 3968 && ledger.Outstanding == 128,
                "first wave must reserve half the connection window, not all of it");
            Require(owner.Revocations == 0, "first-wave grants must not steal each other's reservations");
        });
        await Case("adaptive default preserves fixed-grant control", async () =>
        {
            await using var owner = new GrantAuthority(8192, 8192, 4096, 8);
            var streams = new GrantAuthority.Lease[8];
            for (var i = 0; i < 8; i++) streams[i] = await owner.OpenAsync(i + 1);
            owner.Commit(await owner.AcquireAsync(streams[0], 16));
            var ledger = await owner.SnapshotAsync();
            Require(ledger.Free == 4096 && ledger.Unspent == 4080, "default mode must remain exactly the old control");
        });
        await Case("adaptive cap does not split a required item", async () =>
        {
            await using var owner = new GrantAuthority(8192, 8192, 4096, 8, adaptiveGrants: true);
            var streams = new GrantAuthority.Lease[8];
            for (var i = 0; i < 8; i++) streams[i] = await owner.OpenAsync(i + 1);
            var publication = await owner.AcquirePublicationAsync(streams[0], 4096);
            await owner.FinishPublicationAsync(publication, true);
            var ledger = await owner.SnapshotAsync();
            Require(ledger.Free == 4096 && ledger.Unspent == 0 && ledger.Outstanding == 4096,
                "large items must reserve their full byte count despite a smaller speculative cap");
            await owner.WindowUpdateAsync(streams[0], 4096);
            Require((await owner.SnapshotAsync()).Free == 8192, "large-item credit must be restored");
        });
        await Case("adaptive FIFO and cancellation remain authoritative", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64, adaptiveGrants: true);
            var a = await owner.OpenAsync(1); var b = await owner.OpenAsync(2); var c = await owner.OpenAsync(3);
            owner.Commit(await owner.AcquireAsync(a, 64));
            var first = owner.AcquireAsync(b, 32).AsTask();
            using var cts = new CancellationTokenSource();
            var second = owner.AcquireAsync(c, 16, cts.Token).AsTask();
            Require((await owner.SnapshotAsync()).Waiters == 2, "both requests must reach FIFO");
            await owner.WindowUpdateAsync(a, 16);
            Require(!first.IsCompleted && !second.IsCompleted, "small newcomer must not bypass credit-blocked head");
            cts.Cancel();
            try { await second; throw new Exception("expected canceled request"); } catch (OperationCanceledException) { }
            await owner.WindowUpdateAsync(a, 16);
            owner.Commit(await first);
            _ = await owner.SnapshotAsync();
        });
        await Case("adaptive preserves stream-blocked head skip", async () =>
        {
            await using var owner = new GrantAuthority(16, 64, 16, adaptiveGrants: true);
            var a = await owner.OpenAsync(1); var b = await owner.OpenAsync(2);
            owner.Commit(await owner.AcquireAsync(a, 16));
            var blocked = owner.AcquireAsync(a, 1).AsTask();
            owner.Commit(await owner.AcquireAsync(b, 1));
            Require(!blocked.IsCompleted, "stream exhaustion must remain distinct from connection credit");
            await owner.WindowUpdateAsync(a, 16);
            owner.Commit(await blocked);
            _ = await owner.SnapshotAsync();
        });
        await Case("adaptive oversized borrow repay remains unchanged", async () =>
        {
            await using var owner = new GrantAuthority(16, 32, 16, adaptiveGrants: true);
            var a = await owner.OpenAsync(1); var b = await owner.OpenAsync(2);
            owner.Commit(await owner.AcquireAsync(a, 64));
            Require((await owner.SnapshotAsync()).Free == -32, "oversized borrow must remain one indivisible item");
            var waiting = owner.AcquireAsync(b, 1).AsTask();
            await owner.WindowUpdateAsync(a, 32);
            Require(!waiting.IsCompleted, "connection debt has to be repaid");
            await owner.WindowUpdateAsync(a, 32);
            owner.Commit(await waiting);
            _ = await owner.SnapshotAsync();
        });
        await Case("adaptive held writer survives close and early credit", async () =>
        {
            await using var owner = new GrantAuthority(64, 128, 64, adaptiveGrants: true);
            var a = await owner.OpenAsync(1); _ = await owner.OpenAsync(2);
            var publication = await owner.AcquirePublicationAsync(a, 16);
            await owner.CloseAsync(a);
            await owner.ObserveWindowUpdateAsync(1, 1, 16);
            var pinned = await owner.SnapshotAsync();
            Require(pinned.Publications == 1 && pinned.Retained == 2, "credit return must not release writer pin");
            await owner.FinishPublicationAsync(publication, true);
            var reused = await owner.OpenAsync(1);
            Require(ReferenceEquals(a.State, reused.State) && a.Generation != reused.Generation, "actual state reuse required");
            await Reject(async () => await owner.FinishPublicationAsync(publication, true));
            _ = await owner.SnapshotAsync();
        });
        await Case("adaptive fixed ledger differential with balanced updates", async () =>
        {
            await using var fixedOwner = new GrantAuthority(8192, 65536, 4096, 8);
            await using var adaptive = new GrantAuthority(8192, 65536, 4096, 8, adaptiveGrants: true);
            var old = new GrantAuthority.Lease[8]; var next = new GrantAuthority.Lease[8];
            for (var i = 0; i < 8; i++) { old[i] = await fixedOwner.OpenAsync(i + 1); next[i] = await adaptive.OpenAsync(i + 1); }
            var random = new Random(735);
            for (var i = 0; i < 4096; i++)
            {
                var index = random.Next(8); var bytes = 1 + random.Next(4096);
                var before = await fixedOwner.AcquirePublicationAsync(old[index], bytes);
                var after = await adaptive.AcquirePublicationAsync(next[index], bytes);
                var accepted = i % 3 != 0;
                await fixedOwner.FinishPublicationAsync(before, accepted);
                await adaptive.FinishPublicationAsync(after, accepted);
                if (accepted)
                {
                    await fixedOwner.ObserveWindowUpdateAsync(index + 1, 1, bytes);
                    await adaptive.ObserveWindowUpdateAsync(index + 1, 1, bytes);
                }
                var a = await fixedOwner.SnapshotAsync(); var b = await adaptive.SnapshotAsync();
                Require(a.Free + a.Unspent == b.Free + b.Unspent && a.Outstanding == b.Outstanding && a.Pending == b.Pending,
                    "grant size must not create new permission or actual receipt debt");
            }
        });
    }
}
