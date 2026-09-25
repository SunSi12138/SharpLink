namespace SharpLink.FlowStatePhaseB;

// Compare the cost of explicit writer ownership with the same model's legacy
// immediate Commit. No serialization/transport runs here. Interleaving correctness
// comes from Checks.Publication, not from this uncontended per-stream loop.
internal static class PublicationProbe
{
    internal static async Task RunAsync(int items, int repetitions, List<Result> rows)
    {
        foreach (var streams in new[] { 1, 8, 32, 128 })
        foreach (var bytes in new[] { 16, 4096 })
        for (var repetition = 0; repetition < repetitions; repetition++)
        foreach (var pinned in repetition % 2 == 0 ? new[] { false, true } : new[] { true, false })
        {
            await using var owner = new GrantAuthority(1024 * 1024, checked(streams * 1024 * 1024), 4096);
            var leases = new GrantAuthority.Lease[streams];
            for (var i = 0; i < streams; i++) leases[i] = await owner.OpenAsync(i + 1);
            foreach (var lease in leases) await Produce(owner, lease, 64, bytes, pinned);
            var beforeQueue = owner.QueueSubmissions;
            var beforeAcquires = owner.AcquireSubmissions;
            var beforeCommands = owner.ReusableCommandAllocations;
            var beforeBackpressure = owner.QueueBackpressureWaits;
            var beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
            var began = Stopwatch.GetTimestamp();
            var tasks = new Task[streams];
            for (var stream = 0; stream < streams; stream++)
            {
                var lease = leases[stream];
                tasks[stream] = Task.Run(() => Produce(owner, lease, items, bytes, pinned));
            }
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(120));
            var elapsed = Stopwatch.GetElapsedTime(began).TotalNanoseconds;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeAllocated;
            var queue = owner.QueueSubmissions - beforeQueue;
            var acquires = owner.AcquireSubmissions - beforeAcquires;
            var commands = owner.ReusableCommandAllocations - beforeCommands;
            var backpressure = owner.QueueBackpressureWaits - beforeBackpressure;
            var count = checked((long)streams * items);
            var ledger = await owner.SnapshotAsync();
            if (ledger.Pending != 0 || ledger.Outstanding != 0 || ledger.Publications != 0 || ledger.Waiters != 0)
                throw new InvalidOperationException("Every publication and peer credit must settle.");
            var row = new Result("send-publication-model", "B2-grant-4096",
                pinned ? "writer-owned" : "legacy-commit", streams, streams, bytes, items,
                repetition, elapsed / count, allocated / (double)count, 0,
                queue / (double)count, 2.0 * queue / count,
                (queue + acquires + commands + backpressure) / (double)count,
                null, 0, 0, false, checked(count * bytes), commands, backpressure);
            rows.Add(row);
            Console.WriteLine($"publication {row.Shape} c{streams} {bytes}B: {row.NsPerItem:F2} ns/item, {row.OwnerHandoffsPerItem:F6} owner/item, {row.AllocatedBytesPerItem:F4} B/item");
        }
    }

    private static async Task Produce(GrantAuthority owner, GrantAuthority.Lease lease,
        int items, int bytes, bool pinned)
    {
        for (var i = 0; i < items; i++)
        {
            var receipt = await owner.AcquireAsync(lease, bytes);
            if (pinned)
            {
                var publication = owner.BeginPublication(receipt);
                await owner.FinishPublicationAsync(publication, accepted: true);
            }
            else owner.Commit(receipt);
            if ((i + 1) % 64 == 0) await owner.WindowUpdateAsync(lease, checked(64 * bytes));
        }
        if (items % 64 != 0) await owner.WindowUpdateAsync(lease, checked(items % 64 * bytes));
    }
}
