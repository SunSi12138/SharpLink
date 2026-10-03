namespace SharpLink.FlowStatePhaseB;

internal static partial class Checks
{
    private static async Task RunPublicationAsync()
    {
        await Case("writer-owned credit survives close and cannot admit another stream", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            var receipt = await owner.AcquireAsync(a, 16);
            var publication = owner.BeginPublication(receipt);
            await owner.CloseAsync(a);
            await owner.ReturnUnsentAsync(receipt); // old receipt must not reclaim writer-owned bytes
            var waiting = owner.AcquireAsync(b, 1).AsTask();
            Require((await owner.SnapshotAsync()) is { Free: 0, Outstanding: 16, Publications: 1, Waiters: 1 }, "close must not release writer credit");
            await owner.FinishPublicationAsync(publication, accepted: true);
            Require(!waiting.IsCompleted, "publication success is not a credit return");
            await owner.WindowUpdateAsync(a, 16);
            owner.Commit(await waiting);
            _ = await owner.SnapshotAsync();
        });
        await Case("writer rejects an unpublished item after close and refunds exactly once", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16);
            var a = await owner.OpenAsync(1);
            var b = await owner.OpenAsync(2);
            var publication = owner.BeginPublication(await owner.AcquireAsync(a, 16));
            await owner.CloseAsync(a);
            var waiting = owner.AcquireAsync(b, 16).AsTask();
            Require((await owner.SnapshotAsync()).Waiters == 1, "wait before failed publication");
            await owner.FinishPublicationAsync(publication, accepted: false);
            owner.Commit(await waiting);
            await Reject(async () => await owner.FinishPublicationAsync(publication, accepted: false));
            Require((await owner.SnapshotAsync()) is { Free: 0, Outstanding: 16, Publications: 0 }, "refund exactly once");
        });
        await Case("closed writer rejection promptly returns credit while earlier bytes remain outstanding", async () =>
        {
            await using var owner = new GrantAuthority(32, 32, 0);
            var lease = await owner.OpenAsync(1);
            owner.Commit(await owner.AcquireAsync(lease, 16));
            var publication = owner.BeginPublication(await owner.AcquireAsync(lease, 16));
            await owner.CloseAsync(lease);
            await owner.FinishPublicationAsync(publication, accepted: false);
            Require((await owner.SnapshotAsync()) is { Free: 16, Unspent: 0, Outstanding: 16, Publications: 0 }, "closed stream must not hoard the unsent refund");
            await owner.WindowUpdateAsync(lease, 16);
        });
        await Case("early peer credit cannot recycle a still writer-owned tombstone", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16, maxStreams: 1);
            var old = await owner.OpenAsync(1);
            var publication = owner.BeginPublication(await owner.AcquireAsync(old, 16));
            await owner.CloseAsync(old);
            await owner.WindowUpdateAsync(old, 16);
            Require((await owner.SnapshotAsync()) is { Free: 16, Outstanding: 0, Retained: 1, Publications: 1 }, "retain publication pin after full credit");
            await Reject(async () => await owner.OpenAsync(1));
            await owner.FinishPublicationAsync(publication, accepted: true);
            var fresh = await owner.OpenAsync(1);
            Require(ReferenceEquals(old.State, fresh.State), "exercise actual pool reuse");
            var next = owner.BeginPublication(await owner.AcquireAsync(fresh, 16));
            await Reject(async () => await owner.FinishPublicationAsync(publication, accepted: true));
            Require((await owner.SnapshotAsync()) is { Free: 0, Outstanding: 16, Publications: 1 }, "old writer cannot release replacement pin");
            await owner.FinishPublicationAsync(next, accepted: true);
        });
        await Case("observed publication cannot also be refunded as unsent", async () =>
        {
            await using var owner = new GrantAuthority(32, 32, 0);
            var a = await owner.OpenAsync(1);
            var publication = owner.BeginPublication(await owner.AcquireAsync(a, 32));
            await owner.WindowUpdateAsync(a, 16);
            await Reject(async () => await owner.FinishPublicationAsync(publication, accepted: false));
            Require((await owner.SnapshotAsync()) is { Free: 16, Outstanding: 16, Publications: 1 }, "rejection must leave the pin and ledger intact");
            await owner.CloseAsync(a);
            await owner.FinishPublicationAsync(publication, accepted: true);
            await owner.WindowUpdateAsync(a, 16);
            Require((await owner.SnapshotAsync()) is { Free: 32, Retained: 0, Publications: 0 }, "remaining peer credit closes tombstone");
        });
        await Case("ordered update for an earlier item does not consume the active publication", async () =>
        {
            await using var owner = new GrantAuthority(64, 64, 0);
            var a = await owner.OpenAsync(1);
            owner.Commit(await owner.AcquireAsync(a, 16));
            var publication = owner.BeginPublication(await owner.AcquireAsync(a, 16));
            await owner.WindowUpdateAsync(a, 16);
            await owner.FinishPublicationAsync(publication, accepted: false);
            Require((await owner.SnapshotAsync()) is { Free: 48, Unspent: 16, Outstanding: 0, Publications: 0 }, "earlier credit plus one local refund");
            await owner.CloseAsync(a);
            Require((await owner.SnapshotAsync()).Free == 64, "return unused grant on close");
        });
        await Case("close before writer ownership rejects the old receipt", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16, maxStreams: 1);
            var a = await owner.OpenAsync(1);
            var receipt = await owner.AcquireAsync(a, 16);
            await owner.CloseAsync(a);
            var fresh = await owner.OpenAsync(1);
            await Reject(() => { owner.BeginPublication(receipt); return Task.CompletedTask; });
            Require((await owner.SnapshotAsync()) is { Free: 16, Outstanding: 0, Publications: 0 }, "late begin does not touch new lease");
            await owner.CloseAsync(fresh);
        });
        await Case("successful publication uses local state without extra owner commands", async () =>
        {
            await using var owner = new GrantAuthority(1024, 1024, 256);
            var lease = await owner.OpenAsync(1);
            var submissions = owner.QueueSubmissions;
            var allocations = owner.ReusableCommandAllocations;
            for (var i = 0; i < 16; i++)
            {
                var publication = owner.BeginPublication(await owner.AcquireAsync(lease, 16));
                await Reject(async () => await owner.AcquireAsync(lease, 1));
                var completion = owner.FinishPublicationAsync(publication, accepted: true);
                Require(completion.IsCompletedSuccessfully, "ordinary publication has no owner await");
                await completion;
            }
            Require(owner.QueueSubmissions - submissions == 1, "only the initial 256-byte refill visits the owner");
            Require(owner.ReusableCommandAllocations == allocations, "no per-publication command allocation");
            Require((await owner.SnapshotAsync()) is { Outstanding: 256, Publications: 0 }, "all writes accounted");
        });
        await Case("duplicate concurrent publication settlement has one winner", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0);
            var lease = await owner.OpenAsync(1);
            var publication = owner.BeginPublication(await owner.AcquireAsync(lease, 16));
            async Task<bool> Finish()
            {
                try { await owner.FinishPublicationAsync(publication, accepted: true); return true; }
                catch (InvalidOperationException) { return false; }
            }
            var results = await Task.WhenAll(Task.Run(Finish), Task.Run(Finish));
            Require(results.Count(x => x) == 1, "only one settlement may release ownership");
            Require((await owner.SnapshotAsync()) is { Outstanding: 16, Publications: 0 }, "no double debit or refund");
        });
        await Case("foreign and unresolved publications cannot change credit", async () =>
        {
            await using var a = new GrantAuthority(16, 16, 0);
            await using var b = new GrantAuthority(16, 16, 0);
            var lease = await a.OpenAsync(1);
            var receipt = await a.AcquireAsync(lease, 16);
            await Reject(() => { b.BeginPublication(receipt); return Task.CompletedTask; });
            var publication = a.BeginPublication(receipt);
            await Reject(async () => await b.FinishPublicationAsync(publication, accepted: true));
            await Reject(async () => await a.FinishPublicationAsync(default, accepted: false));
            await a.FinishPublicationAsync(publication, accepted: true);
            Require((await b.SnapshotAsync()).Free == 16, "foreign connection unchanged");
        });
        await Case("changed receipt size cannot settle an issued publication", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 0);
            var lease = await owner.OpenAsync(1);
            var publication = owner.BeginPublication(await owner.AcquireAsync(lease, 16));
            var altered = new GrantAuthority.Publication(publication.Receipt with { Bytes = 1 });
            await Reject(async () => await owner.FinishPublicationAsync(altered, accepted: true));
            Require((await owner.SnapshotAsync()).Publications == 1, "only the issued receipt may release this pin");
            await owner.FinishPublicationAsync(publication, accepted: true);
        });
        await Case("last writer releases its pin after connection termination", async () =>
        {
            var owner = new GrantAuthority(16, 16, 16);
            var lease = await owner.OpenAsync(1);
            var publication = owner.BeginPublication(await owner.AcquireAsync(lease, 16));
            await owner.DisposeAsync();
            await owner.FinishPublicationAsync(publication, accepted: true);
            await Reject(async () => await owner.AcquireAsync(lease, 1));
            await Reject(async () => await owner.FinishPublicationAsync(publication, accepted: true));
            Require(!lease.State.PublicationActive, "terminal owner need not be restarted for final settlement");
        });
        await Case("100000 writer-pinned generations never accept an old settlement", async () =>
        {
            await using var owner = new GrantAuthority(16, 16, 16, maxStreams: 1);
            GrantAuthority.Publication old = default;
            for (var i = 0; i < 100000; i++)
            {
                var lease = await owner.OpenAsync(1);
                var publication = owner.BeginPublication(await owner.AcquireAsync(lease, 1));
                if (i == 0) old = publication;
                await owner.CloseAsync(lease);
                await owner.WindowUpdateAsync(lease, 1);
                await owner.FinishPublicationAsync(publication, accepted: true);
            }
            var fresh = await owner.OpenAsync(1);
            var active = owner.BeginPublication(await owner.AcquireAsync(fresh, 1));
            await Reject(async () => await owner.FinishPublicationAsync(old, accepted: false));
            await owner.FinishPublicationAsync(active, accepted: false);
            await owner.CloseAsync(fresh);
            Require((await owner.SnapshotAsync()) is { Free: 16, Retained: 0, Publications: 0 }, "pool identity never substitutes for generation");
        });
    }
}
