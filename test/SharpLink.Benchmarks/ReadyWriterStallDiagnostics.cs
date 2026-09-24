#if SHARPLINK_READY_WRITER_DIAGNOSTIC
using System.Collections;
using System.Linq;
using System.Reflection;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

// Opt-in diagnostic binary only. No timer, reflection or sampling exists in measured
// JIT/NativeAOT builds. Snapshots are advisory concurrent observations, not invariants.
internal sealed partial class PhaseBTransportCase
{
    private static object? DiagnosticField(object? value, string name)
        => value?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);

    private static string DescribePump(RpcSession session)
    {
        var pump = DiagnosticField(session, "_pump");
        var task = DiagnosticField(pump, "_pumpTask") as Task;
        var machine = DiagnosticField(task, "StateMachine");
        var pendingField = machine?.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(f => f.Name.StartsWith("<pending>", StringComparison.Ordinal));
        var pending = pendingField?.GetValue(machine) as ICollection;
        var state = DiagnosticField(machine, "<>1__state");
        return $"queuedBytes={DiagnosticField(pump, "_queuedBytes")}, wakeState={DiagnosticField(DiagnosticField(pump, "_wakeup"), "_state")}, task={task?.Status}, asyncState={state}, pendingFrames={pending?.Count}, stopped={DiagnosticField(pump, "_stopped")}";
    }

    private void DumpForStall()
    {
        try
        {
            Console.Error.WriteLine($"STALL-SNAPSHOT mode={_mode}; received={Volatile.Read(ref _received)}; returned={Volatile.Read(ref _returned)}; updates={Volatile.Read(ref _updates)}; sender=[{DescribePump(_sender)}]; receiver=[{DescribePump(_receiver)}]");
            Console.Error.WriteLine($"TASKS {string.Join(",", _ownedWork.Select((task, index) => $"{index}:{task.Status}"))}");
            _readyWriter?.DumpForStall();
        }
        catch (Exception error) { Console.Error.WriteLine($"STALL-SNAPSHOT-ERROR {error}"); }
    }

    internal static async Task RunDiagnosticCaptureChecksAsync()
    {
        foreach (var mode in new[] { "A-ready", "B3-ready" })
        {
            var pair = await PhaseBTransportPair.CreateAsync("pipe");
            await using var test = new PhaseBTransportCase(mode, "pipe", pair.Client, pair.Server,
                2, 4, 16, 8192, 4, 16384, 1, 8192);
            test.DumpForStall();
            var result = await test.MeasureAsync(0);
            test.DumpForStall();
            if (result.ItemsReceived != 8 || result.BytesReturned != 128 || result.ReadyWriterMetrics!["RemainingQueuedBytes"] != 0)
                throw new InvalidOperationException("A diagnostic snapshot changed the measured contract.");
            Console.WriteLine($"PASS {mode} cold and settled diagnostic capture");
        }
    }
}

internal sealed partial class ReadyWriterCoordinator
{
    private void DumpReferenceCredit()
    {
        if (_reference is null) return;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string name) => typeof(StreamFlowController).GetField(name, flags)?.GetValue(_reference);
        var gate = (Lock)Field("_gate")!;
        lock (gate)
        {
            var waiters = Field("_waiters");
            var waiterCount = waiters?.GetType().GetProperty("Count")?.GetValue(waiters);
            Console.Error.WriteLine($"REFERENCE connectionCredit={Field("_sendConnectionCredit")}; waiters={waiterCount}");
        }
    }

    internal void DumpForStall()
    {
        Console.Error.WriteLine($"READY notifications={Volatile.Read(ref _notificationsPending)}; updates={Volatile.Read(ref _updatesPending)}; blocked={_blocked}; ready={_ready.Count}; hasWork={HasWork}; releases={Volatile.Read(ref _releases)}; creditReturned={Volatile.Read(ref _returned)}; B3credit={Volatile.Read(ref _connectionCredit)}");
        foreach (var stream in _streams)
            lock (stream.Gate)
            {
                if (stream.Taken == _items && stream.Released == _items) continue;
                Console.Error.WriteLine($"STREAM {stream.Index}: queued={stream.Frames.Count}; queuedBytes={stream.QueuedBytes}; scheduled={stream.Scheduled}; node={stream.Node.List is not null}; producer={Volatile.Read(ref stream.ProducerBusy)}; taken={stream.Taken}; released={stream.Released}; credit={stream.Credit}; outstanding={stream.Outstanding}");
            }
        DumpReferenceCredit();
    }
}
#endif
