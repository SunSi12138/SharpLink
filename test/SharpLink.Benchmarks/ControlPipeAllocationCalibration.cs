#if SHARPLINK_ALLOCATION_PATH_OBSERVATION
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

// Public-API timing calibration, not an RPC workload or production repair.
internal static class ControlPipeAllocationCalibration
{
    private const int Operations = 4_000;
    private const int Warmup = 512;

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Expected output JSON path.");
        var samples = new List<Sample>();
        var passed = false;
        string? failure = null;
        object? lifecycle = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var name = $"control-allocation-{Guid.NewGuid():N}";
            await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var accept = server.WaitForConnectionAsync(deadline.Token);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await accept.ConfigureAwait(false);
            // The measured reads keep CancellationToken.None, matching production.
            // A setup-only deadline registration bounds them without per-read tokens.
            using var deadlineRegistration = deadline.Token.UnsafeRegister(static state => ((PipeStream)state!).Dispose(), server);
            var buffer = new byte[1];
            var send = new byte[1];
            foreach (var arrayApi in new[] { false, true })
            {
                // Warm both branches before any retained sample, then retain every
                // predetermined mix. No retry after an unexpected completion status.
                await ExecuteAsync(server, client, buffer, send, arrayApi, Warmup, 50, deadline.Token).ConfigureAwait(false);
                foreach (var percent in new[] { 0, 25, 50, 75, 100 })
                {
                    var before = AllocationPathObservation.Capture();
                    var processBefore = GC.GetTotalAllocatedBytes(precise: true);
                    var result = await ExecuteAsync(server, client, buffer, send, arrayApi, Operations, percent, deadline.Token).ConfigureAwait(false);
                    var processBytes = GC.GetTotalAllocatedBytes(precise: true) - processBefore;
                    var after = AllocationPathObservation.Capture();
                    var a = before.Paths.Single(row => row.Name == "ControlPipeRead");
                    var b = after.Paths.Single(row => row.Name == "ControlPipeRead");
                    var observed = b.RootBytes - a.RootBytes;
                    var sample = new Sample(arrayApi ? "array-task" : "memory-valuetask", percent, Operations,
                        result.Incomplete, result.RawBytes, observed, processBytes, processBytes - observed,
                        before.Stable && after.Stable, after.FirstThreadTouches - before.FirstThreadTouches, before, after);
                    samples.Add(sample);
                    Ensure(sample.StableSnapshots && b.Calls - a.Calls == Operations &&
                        b.RootCalls - a.RootCalls == Operations && b.Incomplete - a.Incomplete == result.Incomplete &&
                        b.Completed - a.Completed == Operations - result.Incomplete && b.Throws == a.Throws &&
                        observed == result.RawBytes, "Independent call-boundary accounting mismatch.");
                    Ensure(result.Incomplete == Operations * percent / 100, "Predetermined completion-count mismatch.");
                }
            }
            lifecycle = new[]
            {
                await CheckLifecycleAsync(false, deadline.Token).ConfigureAwait(false),
                await CheckLifecycleAsync(true, deadline.Token).ConfigureAwait(false)
            };
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
            File.WriteAllText(args[0], JsonSerializer.Serialize(new
            {
                diagnosticOnly = true, rpcGateReproduction = false, passed, failure,
                runtime = Environment.Version.ToString(), operations = Operations, warmup = Warmup,
                samples, lifecycle,
                limitations = "Synchronous same-thread call setup only. Process residual includes orchestration and later/other-thread work. These fixed mixtures are a PipeStream mechanism calibration, not RPC budget acceptance or a historical failure reproduction."
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        }
        if (!passed) throw new InvalidOperationException("Control-pipe calibration failed; partial evidence retained.");
    }

    private static async Task<Counts> ExecuteAsync(PipeStream reader, PipeStream writer, byte[] buffer,
        byte[] send, bool arrayApi, int operations, int pendingPercent, CancellationToken token)
    {
        long bytes = 0;
        var incomplete = 0;
        for (var i = 0; i < operations; i++)
        {
            token.ThrowIfCancellationRequested();
            var pending = i % 100 < pendingPercent;
            send[0] = (byte)(1 + i % 251);
            if (!pending) await writer.WriteAsync(send, token).ConfigureAwait(false);
            // Prime primitive thread-local observer state before the raw boundary.
            // First touches remain visible in the sample and process residual.
            var prime = AllocationPathObservation.Enter(AllocationPathObservation.Path.CalibrationOuter);
            prime.Observe(true);
            prime.Dispose();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var read = ReadObserved(reader, buffer, arrayApi);
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            if (!read.IsCompleted) incomplete++;
            Ensure(pending ? !read.IsCompleted : read.IsCompletedSuccessfully, "Predetermined read path was not realized.");
            if (pending) await writer.WriteAsync(send, token).ConfigureAwait(false);
            Ensure(await read.ConfigureAwait(false) == 1 && buffer[0] == send[0], "Read payload mismatch.");
        }
        return new Counts(incomplete, bytes);
    }

    private static ValueTask<int> ReadObserved(PipeStream reader, byte[] buffer, bool arrayApi)
    {
        var scope = AllocationPathObservation.Enter(AllocationPathObservation.Path.ControlPipeRead);
        try
        {
            var read = arrayApi ? new ValueTask<int>(reader.ReadAsync(buffer, 0, 1)) : reader.ReadAsync(buffer.AsMemory());
            scope.Observe(read.IsCompleted);
            return read;
        }
        finally { scope.Dispose(); }
    }

    private static async Task<object> CheckLifecycleAsync(bool arrayApi, CancellationToken deadline)
    {
        var name = $"control-lifecycle-{Guid.NewGuid():N}";
        await using var reader = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var writer = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accept = reader.WaitForConnectionAsync(deadline);
        await writer.ConnectAsync(deadline).ConfigureAwait(false);
        await accept.ConfigureAwait(false);
        var buffer = new byte[1];
        var send = new byte[1];
        using var cancellation = new CancellationTokenSource();
        var canceledRead = ReadLifecycle(reader, buffer, arrayApi, cancellation.Token);
        Ensure(!canceledRead.IsCompleted, "Cancellation control must start pending.");
        cancellation.Cancel();
        try
        {
            _ = await canceledRead.AsTask().WaitAsync(deadline).ConfigureAwait(false);
            throw new InvalidOperationException("Canceled read succeeded.");
        }
        catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { }
        send[0] = 123;
        await writer.WriteAsync(send, deadline).ConfigureAwait(false);
        Ensure(await ReadLifecycle(reader, buffer, arrayApi, deadline).ConfigureAwait(false) == 1 && buffer[0] == 123,
            "Canceled read broke subsequent pipe use.");
        var disposedRead = ReadLifecycle(reader, buffer, arrayApi, default);
        Ensure(!disposedRead.IsCompleted, "Dispose control must start pending.");
        await reader.DisposeAsync().ConfigureAwait(false);
        string terminal;
        try
        {
            var count = await disposedRead.AsTask().WaitAsync(deadline).ConfigureAwait(false);
            Ensure(count == 0, "Disposed empty pipe unexpectedly returned data.");
            terminal = "end-of-stream";
        }
        catch (Exception error) when ((error is ObjectDisposedException or IOException or OperationCanceledException) &&
                                     !deadline.IsCancellationRequested)
        {
            terminal = error.GetType().Name;
        }
        return new { api = arrayApi ? "array-task" : "memory-valuetask", cancellationPreserved = true, reuseAfterCancellation = true, disposalJoinedPendingRead = true, disposalOutcome = terminal };
    }

    private static ValueTask<int> ReadLifecycle(PipeStream reader, byte[] buffer, bool arrayApi, CancellationToken token)
        => arrayApi ? new ValueTask<int>(reader.ReadAsync(buffer, 0, 1, token)) : reader.ReadAsync(buffer.AsMemory(), token);

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private readonly record struct Counts(int Incomplete, long RawBytes);
    private sealed record Sample(string Api, int PendingPercent, int Operations, int Incomplete,
        long RawCallBytes, long ObservedCallBytes, long ProcessBytes, long UnattributedResidualBytes,
        bool StableSnapshots, long NewObserverThreadTouches, AllocationPathObservation.Snapshot Before,
        AllocationPathObservation.Snapshot After);
}
#endif
