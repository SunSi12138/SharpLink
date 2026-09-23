namespace SharpLink.FlowStatePhaseB;

internal static class GrantProbe
{
    internal static async Task RunAsync(int items, int repetitions, List<Result> rows, int? streamFilter = null, int? itemBytesFilter = null)
    {
        foreach (var streams in new[] { 1, 8, 32, 128 }.Where(x => streamFilter is null || x == streamFilter))
        foreach (var bytes in new[] { 16, 4096 }.Where(x => itemBytesFilter is null || x == itemBytesFilter))
        for (var repetition = 0; repetition < repetitions; repetition++)
        foreach (var grant in repetition % 2 == 0 ? new[] { 0, 256, 1024, 4096 } : new[] { 4096, 1024, 256, 0 })
        {
            // Identical peer credit schedule for B1 and B2. No receive implementation
            // or transport is included. Small windows/starvation have separate checks.
            await using var owner = new GrantAuthority(1024 * 1024, checked(streams * 1024 * 1024), grant);
            var leases = new GrantAuthority.Lease[streams];
            for (var stream = 0; stream < streams; stream++) leases[stream] = await owner.OpenAsync(stream + 1);
            foreach (var lease in leases)
            {
                for (var i = 0; i < 64; i++) owner.Commit(await owner.AcquireAsync(lease, bytes));
                await owner.WindowUpdateAsync(lease, checked(64 * bytes));
            }
            var beforeQueue = owner.QueueSubmissions;
            var beforeAcquires = owner.AcquireSubmissions;
#if REUSABLE_OWNER_COMMANDS
            var beforeCommands = owner.ReusableCommandAllocations;
            var beforeBackpressure = owner.QueueBackpressureWaits;
#endif
            var beforeAlloc = GC.GetTotalAllocatedBytes(precise: true);
            var began = Stopwatch.GetTimestamp();
            var tasks = new Task[streams];
            for (var stream = 0; stream < streams; stream++)
            {
                var lease = leases[stream];
                tasks[stream] = Task.Run(async () =>
                {
                    for (var item = 0; item < items; item++)
                    {
                        owner.Commit(await owner.AcquireAsync(lease, bytes));
                        if ((item + 1) % 64 == 0) await owner.WindowUpdateAsync(lease, checked(bytes * 64));
                    }
                    if (items % 64 != 0) await owner.WindowUpdateAsync(lease, checked(bytes * (items % 64)));
                });
            }
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(120));
            var elapsed = Stopwatch.GetElapsedTime(began).TotalNanoseconds;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeAlloc;
            var queue = owner.QueueSubmissions - beforeQueue;
            var acquisitions = owner.AcquireSubmissions - beforeAcquires;
            var count = checked((long)streams * items);
            var ledger = await owner.SnapshotAsync();
            if (ledger.Pending != 0 || ledger.Outstanding != 0 || ledger.Waiters != 0)
                throw new InvalidOperationException("Workload must settle every credit.");
            long? commands = null, backpressure = null;
#if REUSABLE_OWNER_COMMANDS
            commands = owner.ReusableCommandAllocations - beforeCommands;
            backpressure = owner.QueueBackpressureWaits - beforeBackpressure;
#endif
            var row = new Result("send-owner-model", grant == 0 ? "B1-item-queue" : $"B2-grant-{grant}",
                "periodic-update-64", streams, 0, bytes, items, repetition, elapsed / count,
                allocated / (double)count, 0, queue / (double)count, 2.0 * queue / count,
                (queue + acquisitions + (commands ?? 0) + (backpressure ?? 0)) / (double)count, null, 0, 0, false, checked(count * bytes),
                commands, backpressure);
            rows.Add(row);
            Console.WriteLine($"{row.Variant} c{streams} {bytes}B: {row.NsPerItem:F2} ns/item, {row.OwnerHandoffsPerItem:F6} handoffs/item, {row.AllocatedBytesPerItem:F2} B/item");
        }
    }
}
