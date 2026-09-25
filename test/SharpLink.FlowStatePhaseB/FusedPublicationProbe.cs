namespace SharpLink.FlowStatePhaseB;

// Identical source is overlaid on the frozen writer-owned reference. Only the
// admission call differs; both branches settle an explicit publication pin.
// Warmup is outside timing: the one-item case is a short tail, not cold lifecycle.
internal static class FusedPublicationProbe
{
    internal static async Task RunAsync(int items, int repetitions, List<Result> rows)
    {
        foreach (var streams in new[] { 1, 8, 32, 128 })
        foreach (var bytes in new[] { 16, 4096 })
        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            await using var owner = new GrantAuthority(1024 * 1024, checked(streams * 1024 * 1024), 4096);
            var leases = new GrantAuthority.Lease[streams];
            for (var i = 0; i < streams; i++) leases[i] = await owner.OpenAsync(i + 1);
            foreach (var lease in leases) await Produce(owner, lease, 64, bytes);
            var beforeQueue = owner.QueueSubmissions;
            var beforeAcquires = owner.AcquireSubmissions;
            var beforeCommands = owner.ReusableCommandAllocations;
            var beforeBackpressure = owner.QueueBackpressureWaits;
            var beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
            var began = Stopwatch.GetTimestamp();
            var tasks = new Task[streams];
            for (var i = 0; i < streams; i++)
            {
                var lease = leases[i];
                tasks[i] = Task.Run(() => Produce(owner, lease, items, bytes));
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
                throw new InvalidOperationException("Every writer pin and peer credit must settle.");
#if DIRECT_PUBLICATION_ADMISSION
            const string shape = "direct-writer-admission";
#else
            const string shape = "split-writer-admission";
#endif
            var row = new Result("send-fused-admission-model", "B2-grant-4096", shape,
                streams, streams, bytes, items, repetition, elapsed / count,
                allocated / (double)count, 0, queue / (double)count, 2.0 * queue / count,
                (queue + acquires + commands + backpressure) / (double)count,
                null, 0, 0, false, checked(count * bytes), commands, backpressure);
            rows.Add(row);
            Console.WriteLine($"{shape} c{streams} {bytes}B: {row.NsPerItem:F2} ns/item, {row.OwnerHandoffsPerItem:F8} owner/item, {row.AllocatedBytesPerItem:F5} B/item");
        }
    }

    private static async Task Produce(GrantAuthority owner, GrantAuthority.Lease lease, int items, int bytes)
    {
        for (var i = 0; i < items; i++)
        {
#if DIRECT_PUBLICATION_ADMISSION
            var publication = await owner.AcquirePublicationAsync(lease, bytes);
#else
            var publication = owner.BeginPublication(await owner.AcquireAsync(lease, bytes));
#endif
            await owner.FinishPublicationAsync(publication, accepted: true);
            if ((i + 1) % 64 == 0) await owner.WindowUpdateAsync(lease, checked(64 * bytes));
        }
        if (items % 64 != 0) await owner.WindowUpdateAsync(lease, checked(items % 64 * bytes));
    }
}
