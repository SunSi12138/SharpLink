using System.Diagnostics;
using System.Text.Json;
using SharpLink.Client;
using SharpLink.Abstractions;

namespace SharpLink.OneWayLocalEvidence;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var scenario = args.ElementAtOrDefault(0) ?? "sync0";
        var iterations = int.Parse(args.ElementAtOrDefault(1) ?? "100000");
        var batch = int.Parse(args.ElementAtOrDefault(2) ?? "256");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);
        var forcedAsync = scenario.StartsWith("async", StringComparison.Ordinal);
        var streams = scenario[^1] - '0';
        if (streams is < 0 or > 2 || (!scenario.StartsWith("sync", StringComparison.Ordinal) && !forcedAsync))
            throw new ArgumentException("Scenario must be sync0, sync1, sync2, async0, async1, or async2.");
        var transport = new LocalTransport();
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTransport(transport).UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
            .UseRuntime(options => options.FlowControl.MaxSendQueueBytes = 32 * 1024 * 1024).Build();
        await client.ConnectAsync();
        var proxy = client.Get<ILocalOneWay>();
        var first = new ControlledEmptyStream(forcedAsync);
        var second = new ControlledEmptyStream(forcedAsync);
        var warmup = Math.Max(16384, Math.Min(iterations, 65536));
        await Run(warmup);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var result = await Run(iterations);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            scenario, iterations, batch, result.ElapsedNanoseconds,
            ns_per_op = result.ElapsedNanoseconds / iterations,
            bytes_per_op = (double)allocated / iterations,
            caller_thread_bytes_per_op = forcedAsync && streams == 0 ? (double?)null : (double)result.CallerAllocatedBytes / iterations,
            completed_synchronously = result.CompletedSynchronously,
            requests = transport.Writer.Requests,
            stream_completions = transport.Writer.Streams,
            wire_checksum = transport.Writer.Checksum,
            first_enumerations = first.Enumerations, second_enumerations = second.Enumerations,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processor_count = Environment.ProcessorCount,
            tiered_compilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
            tiered_pgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default",
            ready_to_run = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "default",
            warmup
        }));

        async Task<(double ElapsedNanoseconds, long CompletedSynchronously, long CallerAllocatedBytes)> Run(int count)
        {
            long ticks = 0, sync = 0, callerBytes = 0;
            var requestsBefore = transport.Writer.Requests;
            var streamsBefore = transport.Writer.Streams;
            for (var start = 0; start < count; start += batch)
            {
                var take = Math.Min(batch, count - start);
                var allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
                var startingThread = Environment.CurrentManagedThreadId;
                var begin = Stopwatch.GetTimestamp();
                for (var i = 0; i < take; i++)
                {
                    if (forcedAsync && streams == 0) transport.Writer.HoldNextFlush();
                    var call = streams switch
                    {
                        0 => forcedAsync ? proxy.PlainDeadline(42) : proxy.Plain(42),
                        1 => proxy.One(42, first),
                        _ => proxy.Two(42, first, second)
                    };
                    var completed = call.IsCompleted;
                    if (completed) sync++;
                    if (completed == forcedAsync)
                        throw new InvalidOperationException($"{scenario} unexpectedly completed={completed}.");
                    if (forcedAsync && streams == 0)
                    {
                        while (!transport.Writer.HeldFlushEntered) await Task.Yield();
                        transport.Writer.ReleaseFlush();
                    }
                    first.Release(); second.Release();
                    if (streams != 0 && !call.IsCompleted)
                        throw new InvalidOperationException("Controlled producer did not resume synchronously after release.");
                    await call.ConfigureAwait(false);
                }
                ticks += Stopwatch.GetTimestamp() - begin;
                if (!(forcedAsync && streams == 0))
                {
                    if (Environment.CurrentManagedThreadId != startingThread) throw new InvalidOperationException("Caller thread changed inside timed batch.");
                    callerBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
                }
                // Drain and correctness checks stay outside timed intervals. Real serialization,
                // admission, send queueing and async completion remain in the timed runtime path.
                var expected = requestsBefore + start + take;
                var expectedStreams = streamsBefore + (long)(start + take) * streams;
                var timeout = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
                while (transport.Writer.Requests < expected || transport.Writer.Streams < expectedStreams)
                {
                    if (Stopwatch.GetTimestamp() > timeout) throw new TimeoutException("Send pump did not drain.");
                    await Task.Yield();
                }
            }
            if (transport.Writer.Requests != requestsBefore + count || transport.Writer.Streams != streamsBefore + (long)count * streams)
                throw new InvalidOperationException("Observable frame counts differ from invoked work.");
            if (first.Disposals != first.Enumerations || second.Disposals != second.Enumerations)
                throw new InvalidOperationException("Stream enumeration was not disposed exactly once.");
            return (ticks * (1e9 / Stopwatch.Frequency), sync, callerBytes);
        }
    }
}
