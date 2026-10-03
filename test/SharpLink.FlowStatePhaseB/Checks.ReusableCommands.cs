namespace SharpLink.FlowStatePhaseB;

internal static partial class Checks
{
    private static async Task RunReusableAsync()
    {
        await Case("refill and update commands reuse slots without per-command allocation", async () =>
        {
            await using var owner = new GrantAuthority(64, 128, 64);
            var lease = await owner.OpenAsync(1);
            var allocations = owner.ReusableCommandAllocations;
            for (var i = 0; i < 4096; i++)
            {
                owner.Commit(await owner.AcquireAsync(lease, 16));
                await owner.WindowUpdateAsync(lease, 16);
            }
            Require(owner.ReusableCommandAllocations == allocations, "no new reusable command objects after setup");
            Require(owner.QueueBackpressureWaits == 0, "single-stream queue must not back up");
            Require((await owner.SnapshotAsync()) is { Pending: 0, Outstanding: 0, Waiters: 0 }, "settled ledger");
        });
        await Case("completed but unconsumed acquire is not reset by a new caller", async () =>
        {
            await using var owner = new GrantAuthority(64, 128, 0);
            var lease = await owner.OpenAsync(1);
            var held = owner.AcquireAsync(lease, 16);
            _ = await owner.SnapshotAsync(); // owner-side publication barrier, not a timed sleep
            Require(held.IsCompletedSuccessfully, "receipt must have been published");
            await Reject(async () => await owner.AcquireAsync(lease, 16));
            var receipt = await held;
            owner.Commit(receipt);
            await owner.WindowUpdateAsync(lease, 16);
            owner.Commit(await owner.AcquireAsync(lease, 16));
            _ = await owner.SnapshotAsync();
        });
        await Case("held first admission keeps its generation after close and real pool reuse", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 0, maxStreams: 1);
            var old = await owner.OpenAsync(1);
            var held = owner.AcquireAsync(old, 16);
            _ = await owner.SnapshotAsync();
            await owner.CloseAsync(old);
            var fresh = await owner.OpenAsync(1);
            Require(ReferenceEquals(old.State, fresh.State), "must exercise pooled object reuse");
            var newHeld = owner.AcquireAsync(fresh, 16);
            var staleReceipt = await held;
            await Reject(() => { owner.Commit(staleReceipt); return Task.CompletedTask; });
            await owner.ReturnUnsentAsync(staleReceipt);
            await Reject(async () => await owner.AcquireAsync(fresh, 1));
            owner.Commit(await newHeld);
            Require((await owner.SnapshotAsync()) is { Free: 48, Pending: 0, Outstanding: 16 }, "old consumer cannot release new ownership");
        });
        await Case("overlapping held updates retain independent completion tokens", async () =>
        {
            await using var owner = new GrantAuthority(64, 128, 0);
            var lease = await owner.OpenAsync(1);
            owner.Commit(await owner.AcquireAsync(lease, 32));
            var first = owner.WindowUpdateAsync(lease, 16);
            var second = owner.WindowUpdateAsync(lease, 16);
            _ = await owner.SnapshotAsync();
            Require(await second && await first, "both overlapping updates must complete");
            Require((await owner.SnapshotAsync()) is { Free: 128, Outstanding: 0 }, "independent updates must settle once");
        });
        await Case("held update cannot be overwritten by a new stream generation", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 0, maxStreams: 1);
            var old = await owner.OpenAsync(1);
            owner.Commit(await owner.AcquireAsync(old, 16));
            var held = owner.WindowUpdateAsync(old, 16);
            _ = await owner.SnapshotAsync();
            await owner.CloseAsync(old);
            var fresh = await owner.OpenAsync(1);
            Require(ReferenceEquals(old.State, fresh.State), "recycled state identity");
            owner.Commit(await owner.AcquireAsync(fresh, 32));
            Require(await held, "old update's result must be retained");
            Require(!await owner.WindowUpdateAsync(old, 64), "stale event must not touch replacement");
            Require((await owner.SnapshotAsync()) is { Free: 32, Outstanding: 32 }, "replacement credit must remain debited");
        });
        await Case("canceled held acquire does not clear replacement pending flag", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0);
            var occupied = await owner.OpenAsync(1);
            var old = await owner.OpenAsync(2);
            owner.Commit(await owner.AcquireAsync(occupied, 16));
            using var cts = new CancellationTokenSource();
            var held = owner.AcquireAsync(old, 1, cts.Token);
            Require((await owner.SnapshotAsync()).Waiters == 1, "request queued before cancellation");
            cts.Cancel();
            _ = await owner.SnapshotAsync();
            Require(held.IsCanceled, "cancellation published before reuse");
            await owner.CloseAsync(old);
            var fresh = await owner.OpenAsync(2);
            var next = owner.AcquireAsync(fresh, 1);
            try { _ = await held; throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            await Reject(async () => await owner.AcquireAsync(fresh, 1));
            await owner.WindowUpdateAsync(occupied, 16);
            owner.Commit(await next);
            _ = await owner.SnapshotAsync();
        });
        await Case("reused waiter nodes are detached before completion publication", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            var before = owner.ReusableCommandAllocations;
            for (var i = 0; i < 256; i++)
            {
                owner.Commit(await owner.AcquireAsync(a, 16));
                var waiting = owner.AcquireAsync(b, 16);
                Require((await owner.SnapshotAsync()).Waiters == 1, "B queued");
                await owner.WindowUpdateAsync(a, 16);
                owner.Commit(await waiting);
                await owner.WindowUpdateAsync(b, 16);
            }
            Require(owner.ReusableCommandAllocations == before, "pressure must reuse the request and linked node");
            Require((await owner.SnapshotAsync()) is { Free: 16, Waiters: 0 }, "all nodes detached");
        });
        await Case("bounded queue backpressure neither drops nor double-submits updates", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 0, queueCapacity: 1);
            var lease = await owner.OpenAsync(1);
            owner.Commit(await owner.AcquireAsync(lease, 32));
            using var pause = new OwnerPause(owner);
            var first = owner.WindowUpdateAsync(lease, 16);
            var second = owner.WindowUpdateAsync(lease, 16);
            Require(owner.QueueBackpressureWaits == 1, "second message must await bounded capacity");
            pause.Release();
            Require(await first && await second, "both enqueues must complete");
            Require((await owner.SnapshotAsync()) is { Free: 64, Outstanding: 0 }, "no duplicated/lost credit");
        });
        await Case("cancellation wake under a full queue is coalesced without losing cancellation", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0, queueCapacity: 1);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            owner.Commit(await owner.AcquireAsync(a, 16));
            using var cts = new CancellationTokenSource();
            var waiting = owner.AcquireAsync(b, 1, cts.Token);
            Require((await owner.SnapshotAsync()).Waiters == 1, "waiter registered before pausing");
            using var pause = new OwnerPause(owner);
            var update = owner.WindowUpdateAsync(a, 1); // fills the one-slot queue
            cts.Cancel(); // a failed TryWrite is safe only because the owner must run again
            pause.Release();
            await update;
            try { _ = await waiting; throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require((await owner.SnapshotAsync()).Waiters == 0, "cancellation scan must run despite dropped wake");
        });
        await Case("failed enqueue releases the reusable completion slot", async () =>
        {
            var owner = new GrantAuthority(16, 16, 0);
            var lease = await owner.OpenAsync(1);
            await owner.DisposeAsync();
            for (var i = 0; i < 2; i++)
            {
                try { await owner.WindowUpdateAsync(lease, 1); throw new Exception("Expected closed queue."); }
                catch (System.Threading.Channels.ChannelClosedException) { }
                Require(!lease.State.UpdateCommand.IsBusy, "enqueue failure must release the consumed slot");
            }
        });
        await Case("old completion token cannot release a live reusable slot", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0);
            var lease = await owner.OpenAsync(1);
            var old = owner.AcquireAsync(lease, 16);
            owner.Commit(await old);
            var live = owner.AcquireAsync(lease, 1);
            // Defensive rejection of invalid double-consumption, not supported ValueTask usage.
            await Reject(() => { _ = old.GetAwaiter().GetResult(); return Task.CompletedTask; });
            await Reject(async () => await owner.AcquireAsync(lease, 1));
            await owner.WindowUpdateAsync(lease, 16);
            owner.Commit(await live);
            _ = await owner.SnapshotAsync();
        });
    }

    private sealed class OwnerPause : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Task<bool> _pause;

        internal OwnerPause(GrantAuthority owner)
        {
            _pause = owner.OnOwnerAsync(() =>
            {
                _entered.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Owner pause not released.");
                return true;
            });
            if (!_entered.Wait(TimeSpan.FromSeconds(10)))
            {
                _release.Set();
                throw new TimeoutException("Owner pause not entered.");
            }
        }

        internal void Release() => _release.Set();
        public void Dispose()
        {
            Release();
            _pause.GetAwaiter().GetResult();
            _entered.Dispose();
            _release.Dispose();
        }
    }
}
