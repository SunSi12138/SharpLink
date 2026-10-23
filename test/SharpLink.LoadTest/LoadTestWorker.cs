using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest;

internal static class LoadTestWorker
{
    internal static async Task<WorkerStageOutcome> RunAsync(
        ILoadTestService rpc,
        string operation,
        string echoPayload,
        int workerIndex,
        MeasurementStageLifecycle lifecycle,
        WorkerLatencyRecorder? workerRecorder,
        StageLatencyRecorder? formalRecorder,
        LatencyHistogram? diagnosticHistogram,
        RealtimeLatencyState? realtime,
        FailureRecorder failures,
        bool retryOneWaySendQueueBackpressure)
    {
        long success = 0;
        long failure = 0;
        long sendQueueBackpressureRetries = 0;
        long operationsStarted = 0;
        await lifecycle.ReadyAndWaitForStartAsync(workerIndex).ConfigureAwait(false);

        while (lifecycle.TryBeginOperationStart(workerIndex, out var admission))
        {
            operationsStarted++;
            var start = workerRecorder is not null || diagnosticHistogram is not null
                ? Stopwatch.GetTimestamp()
                : 0;
            while (true)
            {
                try
                {
                    PendingLoadOperation pendingOperation;
                    // Admission belongs to this logical operation's first attempt.
                    // Release its start scope even when the factory throws synchronously;
                    // later attempts complete that same operation during bounded drain.
                    var startAdmission = admission;
                    admission = default;
                    using (startAdmission)
                        pendingOperation = StartLoadOperation(rpc, operation, echoPayload);
                    switch (pendingOperation.Kind)
                    {
                        case PendingLoadOperationKind.Void:
                            await pendingOperation.VoidCompletion.ConfigureAwait(false);
                            break;
                        case PendingLoadOperationKind.Int32:
                            _ = await pendingOperation.Int32Completion.ConfigureAwait(false);
                            break;
                        case PendingLoadOperationKind.String:
                            _ = await pendingOperation.StringCompletion.ConfigureAwait(false);
                            break;
                        default:
                            throw new InvalidOperationException("Unknown pending load operation kind.");
                    }

                    if (workerRecorder is not null)
                    {
                        var elapsedTicks = Stopwatch.GetTimestamp() - start;
                        workerRecorder.RecordTicks(workerIndex, elapsedTicks);
                        if (diagnosticHistogram is not null)
                            diagnosticHistogram.Record(formalRecorder!.TicksToMicroseconds(elapsedTicks));
                    }
                    else if (diagnosticHistogram is not null)
                    {
                        var elapsedUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                        diagnosticHistogram.Record(elapsedUs);
                        Volatile.Read(ref realtime!.Histogram).Record(elapsedUs);
                    }

                    success++;
                    if (realtime is not null)
                        Interlocked.Increment(ref realtime.Success);
                    break;
                }
                catch (LatencySampleCapacityExceededException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (Program.ShouldRetryOneWaySendQueueBackpressure(
                            retryOneWaySendQueueBackpressure,
                            operation,
                            ex))
                    {
                        sendQueueBackpressureRetries++;
                        await Task.Yield();
                        continue;
                    }

                    failures.Record(ex);
                    failure++;
                    if (Program.ShouldYieldAfterBackpressure(operation, ex))
                        await Task.Yield();
                    break;
                }
            }
        }

        return new WorkerStageOutcome(
            success,
            failure,
            sendQueueBackpressureRetries,
            operationsStarted);
    }

    private static PendingLoadOperation StartLoadOperation(
        ILoadTestService rpc,
        string operation,
        string echoPayload)
        => operation switch
        {
            "echo" => PendingLoadOperation.From(rpc.EchoAsync(echoPayload)),
            "empty" => PendingLoadOperation.From(rpc.PingAsync()),
            "yield" => PendingLoadOperation.From(rpc.YieldAsync(7, 9)),
            "delay" => PendingLoadOperation.From(rpc.DelayAsync(7, 9)),
            "oneway" => PendingLoadOperation.From(rpc.NotifyAsync(7, 9)),
            _ => PendingLoadOperation.From(rpc.AddAsync(7, 9))
        };

}

internal sealed class RealtimeLatencyState
{
    internal LatencyHistogram Histogram = new(200_000);
    internal long Success;
}
