using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Stress coverage for force-flush waiter completion (issue 156). Each scenario
/// races waiter completion against cancellation, transport fault, or session
/// stop and asserts that every waiter terminates cleanly without hangs, double
/// completion, stale reuse, or pool accounting corruption, regardless of the
/// waiter implementation (TaskCompletionSource or pooled IValueTaskSource).
/// </summary>
public class FlushOperationStressTests
{
    [Test]
    public async Task CancellationRacingFlushCompletionShouldNeverHangOrCorruptPool()
    {
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "flush-cancel-stress",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        var consume = ConsumeLoopAsync(output.Reader);
        try
        {
            var random = new Random(156_156);
            var totalCancelled = 0;
            for (var round = 0; round < 150; round++)
            {
                var pending = new List<Task<bool>>(8);
                var sources = new List<CancellationTokenSource>(8);
                for (var index = 0; index < 8; index++)
                {
                    // Every fourth token is pre-cancelled so the cancellation path is
                    // deterministically exercised regardless of scheduling.
                    var cancelAfter = index % 4 == 0
                        ? TimeSpan.Zero
                        : TimeSpan.FromTicks(random.Next(0, 40));
                    var cancellation = new CancellationTokenSource(cancelAfter);
                    sources.Add(cancellation);
                    if ((round + index) % 3 == 0)
                    {
                        pending.Add(FlushSendQueueAsync(session, cancellation.Token));
                    }
                    else
                    {
                        var frame = CreateFrame(session, 32, (ulong)(round * 8 + index + 1));
                        pending.Add(SendAndFlushAsync(session, frame, cancellation.Token));
                    }
                }

                var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
                foreach (var source in sources)
                    source.Dispose();
                totalCancelled += CountCancelled(results);
            }

            Ensure(totalCancelled > 0,
                "the cancellation stress must actually exercise cancellation completions");
            var finalFrame = CreateFrame(session, 32, 1_000_000);
            await session.SendPacketAndFlushAsync(finalFrame).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(session.IsConnected, "cancellation stress must not fault or close the session");
            Ensure(session.QueuedSendBytes == 0, "every in-flight waiter must be released");
        }
        finally
        {
            await session.DisposeAsync();
            await consume;
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task CancellationWithStalledTransportShouldStillReleaseEveryWaiterAtStop()
    {
        var input = new Pipe();
        var output = CreateBackpressuredPipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "flush-cancel-stalled-stress",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            var random = new Random(156_156 + 1);
            var pending = new List<Task<bool>>(100);
            var sources = new List<CancellationTokenSource>(100);
            for (var index = 0; index < 100; index++)
            {
                var cancellation = new CancellationTokenSource(TimeSpan.FromTicks(random.Next(0, 30)));
                sources.Add(cancellation);
                // Real payload frames (not the empty flush marker) so the writer's
                // pause threshold is crossed and the pump's flush genuinely stalls.
                var frame = CreateFrame(session, 32, (ulong)(index + 1));
                pending.Add(SendAndFlushAsync(session, frame, cancellation.Token));
            }

            var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var source in sources)
                source.Dispose();
            Ensure(CountCancelled(results) == pending.Count,
                "every stalled flush waiter must be released by caller cancellation");
            Ensure(session.IsConnected,
                "caller cancellation while the transport stalls must not fault the session");
        }
        finally
        {
            // Stopping the session must drain the queued frames: their waiters are
            // still pump-owned until the drain completes, so dispose must not hang.
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task TransportFaultShouldCompleteEveryForceFlushWaiter()
    {
        var input = new Pipe();
        var output = new Pipe();
        var controlled = new ControlledOutputPipeWriter(output.Writer);
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "flush-fault-stress",
            input.Reader,
            controlled,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            var pending = new List<Task>(300);
            for (var index = 0; index < 300; index++)
            {
                var frame = CreateFrame(session, 32, (ulong)(index + 1));
                pending.Add(SendAndFlushCoreAsync(session, frame, CancellationToken.None));
            }

            // The pump is now suspended in its first controlled flush. Fault it:
            // every accepted frame, in-flight and queued, must terminate through
            // the drain path instead of hanging.
            await Task.Delay(50);
            controlled.FailFlush(new InvalidOperationException("simulated transport failure"));

            await AssertEveryWaiterFailedAsync(pending, TimeSpan.FromSeconds(15),
                "a transport fault must fail every flush waiter");
            Ensure(!session.IsConnected, "a transport fault must mark the session terminal");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task SessionStopShouldCompleteEveryForceFlushWaiter()
    {
        var input = new Pipe();
        var output = CreateBackpressuredPipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.PerformanceProfile = SharpLinkPerformanceProfile.Balanced)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "flush-stop-stress",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            var pending = new List<Task>(300);
            for (var index = 0; index < 300; index++)
            {
                var frame = CreateFrame(session, 32, (ulong)(index + 1));
                pending.Add(index % 3 == 0
                    ? SendAndFlushCoreAsync(session, frame, CancellationToken.None)
                    : TryEnqueueAndFlushAsync(session, frame));
            }

            await Task.Delay(50);
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            await AssertEveryWaiterFailedAsync(pending, TimeSpan.FromSeconds(10),
                "session stop must fail every in-flight flush waiter");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    private static async Task<bool> SendAndFlushAsync(
        RpcSession session,
        IRpcByteBufferWriter frame,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.SendPacketAndFlushAsync(frame, cancellationToken);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Expected: caller cancellation may win the race with the pump flush.
            return true;
        }
    }

    private static async Task<bool> FlushSendQueueAsync(RpcSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.FlushSendQueueAsync(cancellationToken);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Expected: caller cancellation may win the race with the pump flush.
            return true;
        }
    }

    private static async Task SendAndFlushCoreAsync(
        RpcSession session,
        IRpcByteBufferWriter frame,
        CancellationToken cancellationToken)
    {
        await session.SendPacketAndFlushAsync(frame, cancellationToken);
    }

    private static async Task TryEnqueueAndFlushAsync(RpcSession session, IRpcByteBufferWriter frame)
    {
        await session.SendPacketAsync(frame, waitForCapacity: false, forceFlush: true);
    }

    private static int CountCancelled(IEnumerable<bool> results)
    {
        var count = 0;
        foreach (var cancelled in results)
        {
            if (cancelled)
                count++;
        }
        return count;
    }

    private static async Task AssertEveryWaiterFailedAsync(
        List<Task> pending,
        TimeSpan timeout,
        string message)
    {
        var all = Task.WhenAll(pending);
        try
        {
            await all.WaitAsync(timeout);
        }
        catch
        {
            // Expected: faulted waiters surface through WhenAll; the per-task
            // assertions below are the authoritative check.
        }

        for (var index = 0; index < pending.Count; index++)
        {
            var task = pending[index];
            if (!task.IsCompleted)
                throw new Exception($"{message}: waiter {index} did not complete within {timeout.TotalSeconds}s");
            Ensure(task.Status == TaskStatus.Faulted,
                $"{message}: waiter {index} ended as {task.Status}");
            Ensure(task.Exception?.InnerException is SharpLinkException,
                $"{message}: waiter {index} surfaced " +
                $"{task.Exception?.InnerException?.GetType().Name ?? "no"} exception");
        }
    }

    private static async Task ConsumeLoopAsync(PipeReader reader)
    {
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync();
                if (read.IsCanceled || (read.IsCompleted && read.Buffer.IsEmpty))
                    return;
                reader.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted)
                    return;
            }
        }
        catch
        {
        }
    }

    private static Pipe CreateBackpressuredPipe()
        => new(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 0));

    private static IRpcByteBufferWriter CreateFrame(RpcSession session, int payloadBytes, ulong requestId)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId))
        {
            writer.Write(new byte[payloadBytes]);
        }
        return writer;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    /// <summary>
    /// A pipe writer whose flush completion is test-controlled: FlushAsync stays
    /// pending until <see cref="FailFlush"/> delivers a transport fault. This
    /// models an async transport whose I/O fault wakes the send pump.
    /// </summary>
    private sealed class ControlledOutputPipeWriter : PipeWriter, IValueTaskSource<FlushResult>
    {
        private readonly PipeWriter _inner;
        // Not readonly: C# copies mutable structs on method calls through readonly
        // fields, which would silently discard every core state transition.
        private ManualResetValueTaskSourceCore<FlushResult> _core;
        private Exception? _flushFault;
        private readonly Lock _gate = new();

        public ControlledOutputPipeWriter(PipeWriter inner)
        {
            _inner = inner;
            _core.RunContinuationsAsynchronously = true;
        }

        public void FailFlush(Exception exception)
        {
            // The gate serializes fault publication against the Reset in FlushAsync:
            // either the fault is visible before the core is reset (FlushAsync takes
            // the FromException path) or SetException completes the pending core;
            // the completion can never be discarded by a racing Reset.
            lock (_gate)
            {
                _flushFault = exception;
                _core.SetException(exception);
            }
        }

        public override void Advance(int bytes) => _inner.Advance(bytes);

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => _inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_flushFault is { } fault)
                    return ValueTask.FromException<FlushResult>(fault);

                _core.Reset();
                return new ValueTask<FlushResult>(this, _core.Version);
            }
        }

        public FlushResult GetResult(short token) => _core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}
