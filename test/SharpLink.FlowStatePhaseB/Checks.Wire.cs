namespace SharpLink.FlowStatePhaseB;

internal static partial class Checks
{
    private static async Task RunWireAsync()
    {
        await Case("wire identity includes request and stream", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 0);
            var a = await owner.OpenAsync(7, 0);
            var b = await owner.OpenAsync(7, ushort.MaxValue);
            owner.Commit(await owner.AcquireAsync(a, 16));
            owner.Commit(await owner.AcquireAsync(b, 16));
            var result = await owner.ObserveWindowUpdateAsync(7, 0, 16);
            Require(result is { Matched: true, Returned: 16, Excess: 0 }, "exact stream route");
            Require((await owner.SnapshotAsync()) is { Free: 48, Outstanding: 16, Retained: 2 }, "other stream retains debt");
            Require(! (await owner.ObserveWindowUpdateAsync(8, 0, 16)).Matched, "different request must not match");
        });
        await Case("wire excess never credits an unpublished receipt or unspent grant", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64);
            var lease = await owner.OpenAsync(1);
            var receipt = await owner.AcquireAsync(lease, 16);
            Require((await owner.ObserveWindowUpdateAsync(1, 1, int.MaxValue)) is
                { Matched: true, Returned: 0, Excess: int.MaxValue }, "expose ignored credit");
            Require((await owner.SnapshotAsync()) is { Free: 0, Unspent: 48, Pending: 16, Outstanding: 0 }, "no manufactured credit");
            await owner.ReturnUnsentAsync(receipt);
        });
        await Case("wire key closes and retains a pinned publication after early credit", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16, maxStreams: 1);
            var lease = await owner.OpenAsync(11, 2);
            var pin = await owner.AcquirePublicationAsync(lease, 16);
            await owner.CloseAsync(lease);
            await owner.ObserveWindowUpdateAsync(11, 2, 16);
            Require((await owner.SnapshotAsync()) is { Free: 16, Outstanding: 0, Publications: 1, Retained: 1 }, "wire cannot release writer pin");
            await Reject(async () => await owner.OpenAsync(11, 2));
            await owner.FinishPublicationAsync(pin, true);
            Require(!(await owner.ObserveWindowUpdateAsync(11, 2, 16)).Matched, "fully retired late credit is obsolete");
            var next = await owner.OpenAsync(11, 2);
            Require(ReferenceEquals(next.State, lease.State), "actual state pool reuse");
            await Reject(async () => await owner.AcquirePublicationAsync(lease, 1));
        });
        await Case("wire updates wake connection FIFO without bypass", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 64);
            var a = await owner.OpenAsync(1, 0);
            var b = await owner.OpenAsync(1, 1);
            var c = await owner.OpenAsync(2, 0);
            owner.Commit(await owner.AcquireAsync(a, 64));
            var head = owner.AcquireAsync(b, 32).AsTask();
            var tail = owner.AcquireAsync(c, 16).AsTask();
            Require((await owner.SnapshotAsync()).Waiters == 2, "both waiters queued");
            await owner.ObserveWindowUpdateAsync(1, 0, 16);
            Require((await owner.SnapshotAsync()).Waiters == 2 && !tail.IsCompleted, "partial credit does not bypass head");
            await owner.ObserveWindowUpdateAsync(1, 0, 16);
            owner.Commit(await head);
            await owner.ObserveWindowUpdateAsync(1, 0, 16);
            owner.Commit(await tail);
            Require((await owner.SnapshotAsync()).Waiters == 0, "wire credit drives progress");
        });
        await Case("wire returns oversized borrowed credit incrementally", async () =>
        {
            await using var owner = new GrantAuthority(16, 32, 16);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            owner.Commit(await owner.AcquireAsync(a, 64));
            var pending = owner.AcquireAsync(b, 1).AsTask();
            await owner.ObserveWindowUpdateAsync(1, 1, 32);
            Require((await owner.SnapshotAsync()).Free == 0 && !pending.IsCompleted, "borrow not yet repaid");
            await owner.ObserveWindowUpdateAsync(1, 1, 32);
            owner.Commit(await pending);
            Require((await owner.SnapshotAsync()).Outstanding == 1, "only admitted byte remains");
        });
        await Case("wire command held results preserve identity and completion", async () =>
        {
            await using var owner = new GrantAuthority(16, 32, 16);
            var a = await owner.OpenAsync(1, 0);
            var b = await owner.OpenAsync(1, 1);
            owner.Commit(await owner.AcquireAsync(a, 16));
            owner.Commit(await owner.AcquireAsync(b, 16));
            var first = owner.ObserveWindowUpdateAsync(1, 0, 4);
            await owner.SnapshotAsync(); // completed but deliberately not consumed
            var second = owner.ObserveWindowUpdateAsync(1, 1, 16);
            Require((await second).Returned == 16 && (await first).Returned == 4, "no overwritten held result");
            Require((await owner.SnapshotAsync()).Outstanding == 12, "each event targets its own stream");
        });
        await Case("settled wire commands reuse after warmup", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16);
            var a = await owner.OpenAsync(1);
            await owner.ObserveWindowUpdateAsync(1, 1, 1);
            var allocations = owner.ReusableCommandAllocations;
            for (var i = 0; i < 1024; i++)
            {
                var publication = await owner.AcquirePublicationAsync(a, 1);
                await owner.FinishPublicationAsync(publication, true);
                Require((await owner.ObserveWindowUpdateAsync(1, 1, 1)).Returned == 1, "settled byte returned once");
            }
            Require(owner.ReusableCommandAllocations == allocations, "no per-update command object allocation");
        });
        await Case("key-only wire cannot distinguish old frames after key reuse", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16, maxStreams: 1);
            var old = await owner.OpenAsync(1, 0);
            await owner.CloseAsync(old);
            var next = await owner.OpenAsync(1, 0);
            owner.Commit(await owner.AcquireAsync(next, 8));
            // A key-only frame has no generation field. Claiming it belongs to 'old'
            // cannot make it distinguishable on wire; retain this scope limitation.
            Require((await owner.ObserveWindowUpdateAsync(1, 0, 8)).Returned == 8, "wire resolves current owner-ordered identity");
            await Reject(async () => await owner.AcquireAsync(old, 1));
        });
        await Case("wire events follow queued lifecycle ordering", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16, maxStreams: 1);
            var old = await owner.OpenAsync(1, 0);
            owner.Commit(await owner.AcquireAsync(old, 16));
            var update = owner.ObserveWindowUpdateAsync(1, 0, 16);
            var close = owner.CloseAsync(old);
            var next = owner.OpenAsync(1, 0);
            Require((await update).Returned == 16, "earlier queued update credits old lifecycle");
            await close;
            var current = await next;
            Require(current.Generation != old.Generation && (await owner.SnapshotAsync()).Free == 16, "reuse occurs after prior wire event");
        });
        await Case("wire invalid zero credit is rejected before submission", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16);
            var submissions = owner.QueueSubmissions;
            try { await owner.ObserveWindowUpdateAsync(1, 1, 0); throw new Exception("zero accepted"); }
            catch (ArgumentOutOfRangeException) { }
            Require(owner.QueueSubmissions == submissions, "invalid credit not published");
        });
    }
}
