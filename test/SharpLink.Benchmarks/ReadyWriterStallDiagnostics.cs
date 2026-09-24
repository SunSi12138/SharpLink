#if SHARPLINK_READY_WRITER_DIAGNOSTIC
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks
{

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
                DumpStandaloneCredit();
                _grants?.DumpForStall();
                if (_transport == "tcp") DescribeTcpWindow(_receiver.LocalEndPoint);
            }
            catch (Exception error) { Console.Error.WriteLine($"STALL-SNAPSHOT-ERROR {error}"); }
        }

        private void DumpStandaloneCredit()
        {
            if (_phaseA is null) return;
            var gate = (Lock)DiagnosticField(_phaseA, "_gate")!;
            if (!gate.TryEnter())
            {
                Console.Error.WriteLine("STANDALONE-A gate-busy; snapshot skipped, no blocking or retry");
                return;
            }
            try
            {
                var waiters = DiagnosticField(_phaseA, "_waiters");
                Console.Error.WriteLine($"STANDALONE-A connectionCredit={DiagnosticField(_phaseA, "_sendConnectionCredit")}; waiters={waiters?.GetType().GetProperty("Count")?.GetValue(waiters)}");
            }
            finally { gate.Exit(); }
        }

        private static void DescribeTcpWindow(EndPoint? endpoint)
        {
            if (!OperatingSystem.IsLinux() || endpoint is not IPEndPoint ip || !IPAddress.IsLoopback(ip.Address))
                return;
            // Read kernel state for this test pair only. Never alter socket options or
            // issue writes/reads on the measured connection. This exists only in the
            // separately compiled diagnostic binary; its reports cannot pass timing gates.
            using var probe = new Process
            {
                StartInfo = new ProcessStartInfo("ss")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            probe.StartInfo.ArgumentList.Add("-tinmH");
            probe.StartInfo.ArgumentList.Add($"( sport = :{ip.Port} or dport = :{ip.Port} )");
            probe.Start();
            if (!probe.WaitForExit(2000))
            {
                probe.Kill(entireProcessTree: true);
                Console.Error.WriteLine("TCP-SNAPSHOT unavailable: ss exceeded observation limit");
                return;
            }
            var output = probe.StandardOutput.ReadToEnd();
            var error = probe.StandardError.ReadToEnd();
            Console.Error.WriteLine($"TCP-SNAPSHOT t={Stopwatch.GetTimestamp()} port={ip.Port} exit={probe.ExitCode}\n{output}{error}");
        }

        internal static async Task<int> RunDiagnosticCaptureChecksAsync()
        {
            var count = 0;
            foreach (var transport in new[] { "pipe", "tcp" })
            foreach (var mode in new[] { "A-ready", "B3-ready", "A", "B1", "B2", "B2-adaptive" })
            {
                var pair = await PhaseBTransportPair.CreateAsync(transport);
                await using var test = new PhaseBTransportCase(mode, transport, pair.Client, pair.Server,
                    2, 4, 16, 8192, 4, mode.EndsWith("-ready", StringComparison.Ordinal) ? 16384 : 1, 1, 8192);
                test.DumpForStall();
                var result = await test.MeasureAsync(0);
                test.DumpForStall();
                if (result.ItemsReceived != 8 || result.BytesReturned != 128 || result.ReadyWriterMetrics is not null && result.ReadyWriterMetrics["RemainingQueuedBytes"] != 0)
                    throw new InvalidOperationException("A diagnostic snapshot changed the measured contract.");
                Console.WriteLine($"PASS {mode}/{transport} cold and settled diagnostic capture");
                count++;
            }
            return count;
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
}

namespace SharpLink.FlowStatePhaseB
{
    internal sealed partial class GrantAuthority
    {
        internal void DumpForStall()
        {
            // Fixed-lifecycle case only: read state, never submit an owner command or
            // consume the channel. Concurrent snapshots are advisory, not conservation proofs.
            Console.Error.WriteLine($"GRANT-OWNER task={_owner.Status}; free={Volatile.Read(ref _free)}; pressure={Volatile.Read(ref _pressure)}; waiters={_waiters.Count}; queued={(_commands.Reader.CanCount ? _commands.Reader.Count : -1)}; submitted={Volatile.Read(ref QueueSubmissions)}; acquired={Volatile.Read(ref AcquireSubmissions)}");
            foreach (var state in _states.Values)
            {
                if (!state.Gate.TryEnter())
                {
                    Console.Error.WriteLine($"GRANT-STREAM key={state.Key}/{state.StreamId}; gate-busy; skipped");
                    continue;
                }
                try
                {
                    if (state.Outstanding == 0 && state.Pending == 0 && !state.AcquirePending && !state.PublicationActive) continue;
                    Console.Error.WriteLine($"GRANT-STREAM key={state.Key}/{state.StreamId}; generation={state.Generation}; credit={state.Credit}; grant={state.Grant}; pending={state.Pending}; outstanding={state.Outstanding}; acquirePending={state.AcquirePending}; acquireBusy={state.AcquireCommand.IsBusy}; publication={state.PublicationActive}; publicationUncredited={state.PublicationUncredited}; attached={state.Attached}; closed={state.Closed}");
                }
                finally { state.Gate.Exit(); }
            }
        }
    }
}
#endif
