namespace SharpLink.FlowStatePhaseB;

internal static class DirectionalProbe
{
    private sealed record Rig(Action<int, int, int, bool> Loop, ProbeGate Send, ProbeGate Receive);

    internal static void Run(int items, int repetitions, bool diagnose, List<Result> rows)
    {
        foreach (var streams in new[] { 1, 8, 32, 128 })
        foreach (var bytes in new[] { 16, 4096 })
        foreach (var shape in new[] { "send", "receive", "duplex" })
        for (var repetition = 0; repetition < repetitions; repetition++)
        foreach (var split in repetition % 2 == 0 ? new[] { false, true } : new[] { true, false })
        {
            var rig = split ? CreateSplitGate(streams, bytes) : CreatePhaseA(streams, bytes);
            var directions = shape == "duplex" ? 2 : 1;
            var perDirection = Math.Min(streams, shape == "duplex" ? 2 : 4);
            var workers = directions * perDirection;
            using var ready = new CountdownEvent(workers);
            using var start = new ManualResetEventSlim();
            var threads = new Thread[workers];
            var allocations = new long[workers];
            var errors = new Exception?[workers];
            for (var worker = 0; worker < workers; worker++)
            {
                var index = worker;
                threads[index] = new Thread(() =>
                {
                    var send = shape == "send" || shape == "duplex" && index < perDirection;
                    try
                    {
                        rig.Loop(index % perDirection, perDirection, 2000, send);
                    }
                    catch (Exception error) { errors[index] = error; }
                    finally { ready.Signal(); }
                    start.Wait();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    try { rig.Loop(index % perDirection, perDirection, items, send); }
                    catch (Exception error) { errors[index] = error; }
                    allocations[index] = GC.GetAllocatedBytesForCurrentThread() - before;
                });
                threads[index].Start();
            }
            if (!ready.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Worker warmup failed.");
            rig.Send.Entries = rig.Send.WaitTicks = rig.Send.HoldTicks = 0;
            rig.Receive.Entries = rig.Receive.WaitTicks = rig.Receive.HoldTicks = 0;
            rig.Send.Diagnose = rig.Receive.Diagnose = diagnose;
            var began = Stopwatch.GetTimestamp();
            start.Set();
            foreach (var thread in threads)
                if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("Worker did not finish.");
            var elapsed = Stopwatch.GetElapsedTime(began).TotalNanoseconds;
            if (errors.Any(e => e is not null)) throw new AggregateException(errors.OfType<Exception>());
            var count = checked((long)items * streams * directions);
            var gates = ReferenceEquals(rig.Send, rig.Receive) ? new[] { rig.Send } : new[] { rig.Send, rig.Receive };
            var factor = 1e9 / Stopwatch.Frequency / count;
            var row = new Result("full-controller", split ? "B0-split-gate" : "A-resolved-gate", shape,
                streams, workers, bytes, items, repetition, elapsed / count,
                allocations.Sum() / (double)count, diagnose ? gates.Sum(g => g.Entries) / (double)count : 2,
                0, 0, 0, null, gates.Sum(g => g.WaitTicks) * factor,
                gates.Sum(g => g.HoldTicks) * factor, diagnose, checked(count * bytes));
            rows.Add(row);
            Console.WriteLine($"{row.Variant}/{shape} c{streams} {bytes}B: {row.NsPerItem:F2} ns/item, {row.ConnectionGateEntriesPerItem:F3} gates/item");
        }
    }

    private static Rig CreatePhaseA(int streams, int bytes)
    {
        var controller = new PhaseA.StreamFlowController(bytes * 2, checked(bytes * 2 * streams), 4 * 1024 * 1024);
        var sends = new PhaseA.StreamFlowController.ResolvedSendCreditLease[streams];
        var receives = new PhaseA.StreamFlowController.ResolvedReceiveCreditLease[streams];
        for (var stream = 0; stream < streams; stream++)
        {
            if (!controller.TryAcquireSendCreditLease(stream + 1, 1, bytes, out sends[stream]))
                throw new InvalidOperationException("send setup");
            controller.ReturnUnsentCredit(in sends[stream], bytes);
            receives[stream] = controller.ResolveReceiveCreditLease(stream + 1, 1);
        }
        return new Rig((offset, stride, items, send) =>
        {
            for (var stream = offset; stream < streams; stream += stride)
            for (var item = 0; item < items; item++)
            {
                if (send)
                {
                    if (!controller.TryAcquireSendCredit(in sends[stream], bytes)) throw new InvalidOperationException("send blocked");
                    controller.ReturnUnsentCredit(in sends[stream], bytes);
                }
                else
                {
                    controller.AcceptReceived(in receives[stream], bytes);
                    if (controller.RecordConsumed(in receives[stream], bytes) != bytes) throw new InvalidOperationException("credit mismatch");
                }
            }
        }, controller.SendGate, controller.ReceiveGate);
    }
    private static Rig CreateSplitGate(int streams, int bytes)
    {
        var controller = new SplitGate.StreamFlowController(bytes * 2, checked(bytes * 2 * streams), 4 * 1024 * 1024);
        var sends = new SplitGate.StreamFlowController.ResolvedSendCreditLease[streams];
        var receives = new SplitGate.StreamFlowController.ResolvedReceiveCreditLease[streams];
        for (var stream = 0; stream < streams; stream++)
        {
            if (!controller.TryAcquireSendCreditLease(stream + 1, 1, bytes, out sends[stream]))
                throw new InvalidOperationException("send setup");
            controller.ReturnUnsentCredit(in sends[stream], bytes);
            receives[stream] = controller.ResolveReceiveCreditLease(stream + 1, 1);
        }
        return new Rig((offset, stride, items, send) =>
        {
            for (var stream = offset; stream < streams; stream += stride)
            for (var item = 0; item < items; item++)
            {
                if (send)
                {
                    if (!controller.TryAcquireSendCredit(in sends[stream], bytes)) throw new InvalidOperationException("send blocked");
                    controller.ReturnUnsentCredit(in sends[stream], bytes);
                }
                else
                {
                    controller.AcceptReceived(in receives[stream], bytes);
                    if (controller.RecordConsumed(in receives[stream], bytes) != bytes) throw new InvalidOperationException("credit mismatch");
                }
            }
        }, controller.SendGate, controller.ReceiveGate);
    }
}
