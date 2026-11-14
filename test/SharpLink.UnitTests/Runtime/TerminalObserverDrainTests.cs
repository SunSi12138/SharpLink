using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public sealed class TerminalObserverDrainTests
{
    [Test]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [NotInParallel]
    public async Task TerminalDrainShouldJoinRealPendingObserverAndEveryPhysicalOwner(bool faultClock, bool queuedDisconnectCleanup)
    {
        var clock = new ThrowingClock();
        using var context = new SharpLinkRuntimeContextBuilder().UseTimeProvider(clock).Configure(options =>
        {
            options.FlowControl.MaxSendQueueBytes = 1024;
            options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput;
        }).Build();
        var facade = SharpClientBuilder.Create().UseTransport(new NoConnect()).UseTimeProvider(clock)
            .UseRequestTimeout(TimeSpan.FromHours(1)).Build();
        var input = new Pipe();
        var output = new GatedOutput();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport("terminal-observer", input.Reader, output,
            RpcSessionTestFixture.ClientOptions(context), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, ProtocolV2Capabilities.FlowControl);
        var flow = ((RpcSessionProtocolState)typeof(RpcSession).GetField("_protocolState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!).FlowController!;
        using var cancellation = new CancellationTokenSource();
        var connection = new ClientConnection((SharpLinkClient)facade, session, cancellation, 16, context);
        var table = connection.PendingCalls;
        // Component boundary: this is the actual framework scheduler, not a synchronous fake FailAll.
        var queueCleanup = typeof(SharpLinkClient).GetMethod("QueueConnectionCleanup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<ClientConnection, string, Exception?>>((SharpLinkClient)facade);
        var disconnected = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnDisconnected += error =>
        {
            if (queuedDisconnectCleanup) queueCleanup(connection, "TerminalObserverComponentCleanup", error);
            disconnected.TrySetResult(error!);
        };
        var owners = new List<IRpcByteBufferWriter>();
        var operations = new List<Task<int>>();
        var observe = 0;
        var metricClaimed = 0;
        Exception? gateFailure = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.send.queue.bytes") owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, bytes, _, _) =>
        {
            if (bytes >= 0 || Volatile.Read(ref observe) == 0 || Interlocked.Exchange(ref metricClaimed, 1) != 0) return;
            if (faultClock && queuedDisconnectCleanup)
            {
                if (!clock.FailAllAttempted.Wait(TimeSpan.FromSeconds(5))) gateFailure = new Exception("queued FailAll did not reach the real clock");
            }
            else if (!faultClock)
            {
                if (!SpinWait.SpinUntil(() => operations.All(task => task.IsCompleted), TimeSpan.FromSeconds(5)))
                    gateFailure = new Exception("healthy FailAll did not complete before observer");
                clock.RemainingFailures = int.MaxValue;
            }
        });
        listener.Start();
        IRpcByteBufferWriter Frame(ProtocolV2FrameType type, long id, int length = 32)
        {
            var writer = session.RentFrameWriter(); owners.Add(writer);
            using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, unchecked((ulong)id)))
            { writer.GetSpan(length)[..length].Clear(); writer.Advance(length); }
            Ensure(writer is PooledByteBufferWriter, "real default pooled frame owner required");
            return writer;
        }
        IRpcByteBufferWriter Request()
        {
            var operation = table.Rent(context.Codecs.GetCodec<int>(), PendingCallKind.Unary,
                RpcDeadline.Create(TimeSpan.FromHours(1), clock), default, out var id);
            operations.Add(operation.AsValueTask().AsTask());
            var writer = Frame(ProtocolV2FrameType.Request, id);
            var frame = session.PrepareOutboundFrame(writer, default);
            Ensure(table.TryPublishRequest(id, session, frame, false, default, table, out _, out var rejection) && rejection is null,
                "actual pending publication and actual table emission observer required");
            return writer;
        }
        Task? marker = null;
        Task? waiter = null;
        try
        {
            var seed = session.SendPacketAndFlushAsync(Frame(ProtocolV2FrameType.Response, 100)).AsTask();
            await output.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = Request(); var later = Request();
            output.ReleaseFirst();
            await seed.WaitAsync(TimeSpan.FromSeconds(5));
            await output.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var progress = Frame(ProtocolV2FrameType.Ping, 0, 8); session.SendPacket(progress);
            var normal = Request();
            marker = session.FlushSendQueueAsync().AsTask();
            var waiting = Frame(ProtocolV2FrameType.Request, 900, 1100);
            waiter = session.SendPacketWithBackpressureAsync(waiting).AsTask();
            var waitingDrain = session.CaptureBackpressuredSendDrain();
            Ensure(!waiter.IsCompleted && !waitingDrain.IsCompleted, "capacity waiter must be physically pending");
            await session.AcquireStreamSendCreditAsync(990, 1, context.FlowControl.StreamReceiveWindowBytes, default);
            var creditWaiter = session.AcquireStreamSendCreditAsync(990, 1, 1, default).AsTask();
            Ensure(!creditWaiter.IsCompleted && flow.ActiveSendStreamCount == 1, "real flow-credit waiter must be pending");
            Volatile.Write(ref observe, 1);
            clock.RemainingFailures = faultClock ? int.MaxValue : 0;
            output.ReleaseSecond();
            var terminal = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var sessionFailure = await Capture(session.DisposeAsync().AsTask());
            await waitingDrain.WaitAsync(TimeSpan.FromSeconds(5));
            var waiterFailure = await Capture(waiter);
            var creditFailure = await Capture(creditWaiter);
            var snapshot = new
            {
                faultClock,
                queuedDisconnectCleanup,
                first = Active(first),
                later = Active(later),
                progress = Active(progress),
                normal = Active(normal),
                waiting = Active(waiting),
                marker = marker.IsCompleted,
                pending = table.Count,
                active = table.ActiveCount,
                completed = operations.Count(task => task.IsCompleted),
                bytes = session.QueuedSendBytes,
                clockThrows = clock.Throws,
                creditStreams = flow.ActiveSendStreamCount,
                retainedCreditStreams = flow.RetainedSendStreamCount,
                sessionFailure = sessionFailure?.ToString(),
                gateFailure = gateFailure?.ToString()
            };
            Console.WriteLine("TERMINAL_OBSERVER_COMPONENT_V1 " + JsonSerializer.Serialize(snapshot));
            Ensure(gateFailure is null, "component ordering prerequisite");
            Ensure(ReferenceEquals(terminal.InnerException, output.Failure), "original transport failure must remain terminal");
            Ensure(!Active(waiting) && waiterFailure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed }, "capacity waiter convergence");
            Ensure(ReferenceEquals(creditFailure, terminal) && flow.ActiveSendStreamCount == 0 && flow.RetainedSendStreamCount == 0,
                "flow-credit waiter and stream accounting must converge with the original transport cause");
            Ensure(!Active(first) && !Active(later) && !Active(progress) && !Active(normal) && marker.IsCompleted && session.QueuedSendBytes == 0,
                "all batch, progress, normal and flush owners must converge before salvage");
            Ensure(table.Count == 0 && table.ActiveCount == 0 && operations.All(task => task.IsCompleted), "all pending slots and operations must converge");
            foreach (var operation in operations)
                Ensure(ReferenceEquals(await Capture(operation), terminal), "transport cause must reach every operation without invented deadline expiry");
            Ensure(ReferenceEquals(await Capture(marker), terminal), "flush marker must retain transport cause");
            var joinedFailure = await Capture(facade.StopAsync().AsTask());
            if (faultClock) Ensure(Contains(joinedFailure, clock.Failure) || Contains(sessionFailure, clock.Failure), "clock failure must remain joined");
            else Ensure(sessionFailure is null && joinedFailure is null && clock.Throws == 0, "already-cleared FailAll prevents later observer clock access");
        }
        finally
        {
            Volatile.Write(ref observe, 0); clock.RemainingFailures = 0; output.ReleaseFirst(); output.ReleaseSecond();
            // Post-observation cleanup permits the baseline-failing component to exit; it is not recovery evidence.
            try { table.FailAllPendingRequests(new IOException("test salvage")); } catch { }
            _ = await Capture(session.DisposeAsync().AsTask());
            foreach (var owner in owners) if (Active(owner)) context.Buffers.Return(owner);
            await input.Writer.CompleteAsync();
            _ = await Capture(facade.DisposeAsync().AsTask());
            foreach (var operation in operations.Where(task => task.IsCompleted)) _ = operation.Exception;
            if (marker?.IsCompleted == true) _ = marker.Exception;
            if (waiter?.IsCompleted == true) _ = waiter.Exception;
        }
    }

    [Test]
    public async Task DirectConnectionDisposeShouldJoinSessionAfterPendingCleanupFailure()
    {
        var clock = new ThrowingClock();
        using var context = new SharpLinkRuntimeContextBuilder().UseTimeProvider(clock).Build();
        await using var facade = SharpClientBuilder.Create().UseTransport(new NoConnect()).DisableRequestTimeout().Build();
        var input = new Pipe(); var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport("direct-terminal-cleanup", input.Reader, output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions(context));
        RpcSessionTestFixture.CompleteHandshake(session);
        using var cancellation = new CancellationTokenSource();
        var connection = new ClientConnection((SharpLinkClient)facade, session, cancellation, 4, context);
        var operation = connection.PendingCalls.Rent(Int32Codec.Instance, PendingCallKind.Unary,
            RpcDeadline.Create(TimeSpan.FromHours(1), clock), default, out _).AsValueTask();
        clock.RemainingFailures = int.MaxValue;
        var failure = await Capture(connection.DisposeAsync().AsTask());
        clock.RemainingFailures = 0;
        Ensure(Contains(failure, clock.Failure) && operation.IsCompleted && connection.PendingCalls.Count == 0 && connection.PendingCalls.ActiveCount == 0,
            "direct disposal must complete pending work and preserve clock cleanup failure");
        Ensure(transport.DisposeCount == 1 && !session.IsConnected, "pending cleanup failure must not bypass joined Session/transport disposal");
        Ensure(await Capture(operation.AsTask()) is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed }, "direct disposal must not invent a timeout");
        await input.Writer.CompleteAsync(); await output.Reader.CompleteAsync();
    }

    [Test]
    public async Task KnownExpiredDeadlineShouldStillWinTransportCompletion()
    {
        var clock = new ThrowingClock();
        using var table = PendingRequestTableTestFixture.Create(1, timeProvider: clock);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary,
            RpcDeadline.Create(TimeSpan.FromHours(1), clock), default, out _).AsValueTask();
        clock.Offset = 2 * 3600L * Stopwatch.Frequency;
        table.FailAllPendingRequests(new IOException("later transport failure"));
        Ensure(await Capture(operation.AsTask()) is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "a successful authoritative expired deadline sample must retain timeout priority");
        Ensure(table.Count == 0 && table.ActiveCount == 0, "expired completion must release its slot exactly once");
    }

    [Test]
    [Arguments(1)]
    [Arguments(int.MaxValue)]
    public async Task FailAllShouldRetireEveryCallWhenDeadlineClockThrows(int failures)
    {
        var clock = new ThrowingClock();
        using var table = PendingRequestTableTestFixture.Create(4, timeProvider: clock);
        var operations = Enumerable.Range(0, 3).Select(index => table.Rent(Int32Codec.Instance, PendingCallKind.Unary,
            RpcDeadline.Create(TimeSpan.FromHours(1), clock), default, out _).AsValueTask()).ToArray();
        var transport = new IOException("authoritative transport failure");
        clock.RemainingFailures = failures;
        Exception? cleanup = null;
        try { table.FailAllPendingRequests(transport); } catch (Exception error) { cleanup = error; }
        var count = table.Count; var active = table.ActiveCount; var completed = operations.Count(task => task.IsCompleted);
        clock.RemainingFailures = 0;
        if (count != 0) table.FailAllPendingRequests(new IOException("post-observation salvage"));
        Console.WriteLine($"TERMINAL_PENDING_CLOCK_V1 failures={failures} pending={count} active={active} completed={completed}");
        Ensure(Contains(cleanup, clock.Failure) && count == 0 && active == 0 && completed == 3, "clock failure must be joined only after every pending terminal");
        foreach (var operation in operations) Ensure(ReferenceEquals(await Capture(operation.AsTask()), transport), "unknown expiry must preserve transport cause");
    }

    [Test]
    public async Task FailAllShouldFinishOperationAfterRealAttemptOutcomeClockFailure()
    {
        var clock = new ThrowingClock(); var policy = new AdmissionPolicy();
        await using var client = SharpClientBuilder.Create().UseTransport(new NoConnect()).UseTimeProvider(clock)
            .UseEndpointAdmission(policy).DisableRequestTimeout().Build();
        var type = typeof(SharpLinkClient).GetNestedType("AttemptOutcomeState", BindingFlags.NonPublic)!;
        var observer = (IPendingCallCompletionObserver)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [client, default(RpcMethodDescriptor)], null)!;
        Ensure((bool)type.GetMethod("TryAcquire")!.Invoke(observer, [default(SharpLinkEndpointCandidate)])!, "real admission lease");
        using var table = PendingRequestTableTestFixture.Create(1, timeProvider: clock);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, default, out _, observer).AsValueTask();
        var transport = new IOException("attempt transport failure");
        clock.RemainingFailures = int.MaxValue;
        Exception? cleanup = null;
        try { table.FailAllPendingRequests(transport); } catch (Exception error) { cleanup = error; }
        clock.RemainingFailures = 0;
        Console.WriteLine($"TERMINAL_ATTEMPT_CLOCK_V1 completed={operation.IsCompleted} pending={table.Count} active={table.ActiveCount} reports={policy.Reports}");
        Ensure(Contains(cleanup, clock.Failure) && operation.IsCompleted && table.Count == 0 && table.ActiveCount == 0,
            "a removed slot must not leave its operation orphaned when real elapsed-time reporting fails");
        Ensure(ReferenceEquals(await Capture(operation.AsTask()), transport), "attempt cleanup cannot replace transport cause");
        Ensure(policy.Reports == 0, "a failed timestamp cannot fabricate an elapsed admission outcome");
    }

    private static bool Contains(Exception? error, Exception target) => ReferenceEquals(error, target) ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Any(child => Contains(child, target)) ||
        error?.InnerException is { } inner && Contains(inner, target);
    private static bool Active(IRpcByteBufferWriter writer) { try { _ = writer.WrittenCount; return true; } catch (ObjectDisposedException) { return false; } }
    private static async Task<Exception?> Capture(Task task)
    {
        await task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(5));
        try { await task; return null; } catch (Exception error) { return error; }
    }
    private static void Ensure(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class NoConnect : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default) => throw new Exception("component must never perform RPC");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class AdmissionPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        public int Reports;
        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method) => new(true, 1, null);
        public void Report(in SharpLinkEndpointOutcome outcome, long token) => Reports++;
    }
    private sealed class ThrowingClock : TimeProvider
    {
        public int RemainingFailures;
        public int Throws;
        public long Offset;
        public Exception Failure { get; } = new InvalidOperationException("terminal cleanup timestamp failure");
        public ManualResetEventSlim FailAllAttempted { get; } = new();
        public override long GetTimestamp()
        {
            if (Volatile.Read(ref RemainingFailures) > 0 && Interlocked.Decrement(ref RemainingFailures) >= 0)
            {
                if (new StackTrace().ToString().Contains("FailAllPendingRequests", StringComparison.Ordinal)) FailAllAttempted.Set();
                Interlocked.Increment(ref Throws); throw Failure;
            }
            return Stopwatch.GetTimestamp() + Offset;
        }
    }
    private sealed class GatedOutput : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(); private int _flush;
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously), _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception Failure { get; } = new IOException("original component output failure");
        public void ReleaseFirst() => _first.TrySetResult(); public void ReleaseSecond() => _second.TrySetResult();
        public override void Advance(int count) => _buffer.Advance(count);
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        public override void CancelPendingFlush() { ReleaseFirst(); ReleaseSecond(); }
        public override void Complete(Exception? exception = null) { ReleaseFirst(); ReleaseSecond(); }
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _flush);
            if (count == 1) { FirstEntered.TrySetResult(); await _first.Task; }
            if (count == 2) { SecondEntered.TrySetResult(); await _second.Task; throw Failure; }
            return new FlushResult(false, false);
        }
    }
}
