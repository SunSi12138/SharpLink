using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks.Sources;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// The RpcEmptyRequest pool is process-wide. Isolate the version/queue assertions from
// unrelated calls that could rent and reset the captured operation after this invocation.
[NotInParallel]
public sealed class SharpLinkClientOneWayLeaseTests
{
    [Test]
    [Arguments("cancel")]
    [Arguments("deadline")]
    [Arguments("close")]
    public async Task TerminalDuringRegistrationShouldConsumeTheLeaseExactlyOnce(string terminal)
    {
        var clock = new RegistrationTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder => builder.UseTimeProvider(clock));
        await client.ConnectAsync();
        var connection = GetConnection(client);
        await connection.Session.FlushSendQueueAsync();
        using var cancellation = new CancellationTokenSource();
        var producer = new ProducerProbe();
        OperationProbe? operation = null;
        var close = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "registration connection closed");

        // PendingDeadlineScheduler arms its timer after MarkRegistered, but before
        // RegisterOneWayClientStream returns. No sleeps or competing thread are needed.
        clock.OnPendingTimerChange = () =>
        {
            operation = OperationProbe.Capture(connection);
            ApplyTerminal(terminal, connection, clock, cancellation, close);
            // Losing causes become true before the caller resumes. Only the pending winner
            // may decide the result, including the original caller cancellation token.
            cancellation.Cancel();
            clock.Manual.Advance(TimeSpan.FromSeconds(5));
            Ensure(!connection.PendingCalls.Contains(operation.Id), "the terminal must remove the entry before registration returns");
        };
        var failure = await CaptureFailure(Invoke(client, producer, cancellation.Token));

        Ensure(operation is not null, "the registration boundary must have been exercised");
        AssertTerminal(terminal, failure, cancellation.Token, close);
        Ensure(producer.Calls == 0, "an already terminal registration must not start a producer");
        operation!.AssertReturnedOnce();
        AssertAdmissionReleased(connection);
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("deadline")]
    [Arguments("close")]
    public async Task TerminalDuringSuspendedEmissionShouldConsumeTheLeaseWithoutStartingProducer(string terminal)
    {
        var clock = new RegistrationTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder => builder.UseTimeProvider(clock));
        await client.ConnectAsync();
        var connection = GetConnection(client);
        await connection.Session.FlushSendQueueAsync();
        using var cancellation = new CancellationTokenSource();
        using var releaseEmission = new ManualResetEventSlim();
        var emissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new ProducerProbe();
        var close = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "emission connection closed");
        transport.Connection.RunOnNextOutputBufferRequest(() =>
        {
            emissionEntered.TrySetResult();
            Ensure(releaseEmission.Wait(TimeSpan.FromSeconds(30)), "test did not release emission");
        });
        try
        {
            var invocation = Invoke(client, producer, cancellation.Token);
            await emissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!invocation.IsCompleted, "emission must still be suspended at the terminal boundary");
            var operation = OperationProbe.Capture(connection);
            ApplyTerminal(terminal, connection, clock, cancellation, close);
            var failure = await CaptureFailure(invocation);

            AssertTerminal(terminal, failure, cancellation.Token, close);
            Ensure(producer.Calls == 0, "a terminal during emission must not start the producer");
            Ensure(operation.ProducerToken.IsCancellationRequested, "terminal completion must cancel the owned producer token");
            operation.AssertReturnedOnce();
            AssertAdmissionReleased(connection);
        }
        finally
        {
            releaseEmission.Set();
        }
    }

    [Test]
    [Arguments("success")]
    [Arguments("fault")]
    [Arguments("cancel")]
    [Arguments("deadline")]
    [Arguments("close")]
    public async Task ProducerTerminalShouldRetainItsWinnerAndReturnTheLeaseExactlyOnce(string terminal)
    {
        var clock = new RegistrationTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder => builder.UseTimeProvider(clock));
        await client.ConnectAsync();
        var connection = GetConnection(client);
        using var cancellation = new CancellationTokenSource();
        var producer = new ProducerProbe();
        var close = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "producer connection closed");
        var invocation = Invoke(client, producer, cancellation.Token);
        await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = OperationProbe.Capture(connection);
        if (terminal == "success")
            producer.Release.TrySetResult();
        else if (terminal == "fault")
            producer.Release.TrySetException(producer.Failure);
        else
            ApplyTerminal(terminal, connection, clock, cancellation, close);
        var failure = await CaptureFailure(invocation);
        await producer.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (terminal == "success")
            Ensure(failure is null, "successful producer must complete the caller");
        else if (terminal == "fault")
            Ensure(ReferenceEquals(failure, producer.Failure), "producer failure must reach the caller unchanged");
        else
            AssertTerminal(terminal, failure, cancellation.Token, close);
        Ensure(producer.Calls == 1, "a live call must run exactly one producer");
        operation.AssertReturnedOnce();
        AssertAdmissionReleased(connection);

        // Late losing terminals must neither complete the old request again nor touch the
        // recycled operation. A later pending owner must also remain independent.
        var next = connection.PendingCalls.RegisterOneWayClientStream(default, default);
        var nextResult = next.Operation.AsValueTask();
        Ensure(!connection.PendingCalls.TryComplete(operation.Id, PendingCallCompletionReason.ConnectionClosed, close),
            "a stale terminal must not claim a later pending owner");
        cancellation.Cancel();
        Ensure(!nextResult.IsCompleted, "late cancellation must not complete the next rental");
        operation.AssertResetOnce();
        Ensure(connection.PendingCalls.TryComplete(next.Id, PendingCallCompletionReason.LocalStreamComplete),
            "the next rental must retain its own terminal transition");
        _ = await nextResult;
        AssertAdmissionReleased(connection);
    }

    private static Task Invoke(SharpLinkClient client, ProducerProbe producer, CancellationToken token)
    {
        var request = default(RpcEmptyRequest);
        var streams = new GatedProducer(producer);
        var method = new RpcMethodDescriptor(
            ContractId: 1, MethodId: 297, Kind: RpcMethodKind.OneWay,
            HasResponsePayload: false, HasClientStreams: true,
            HasMethodTimeout: true, MethodTimeout: TimeSpan.FromSeconds(5), ClientStreamCount: 1);
        return ((IRpcChannel)client).InvokeOneWayAsync(
            method, in request, RpcEmptyRequestCodec.Instance, in streams, metadata: null, token).AsTask();
    }

    private static void ApplyTerminal(
        string terminal, ClientConnection connection, RegistrationTimeProvider clock,
        CancellationTokenSource cancellation, Exception close)
    {
        switch (terminal)
        {
            case "cancel":
                cancellation.Cancel();
                break;
            case "deadline":
                clock.Manual.Advance(TimeSpan.FromSeconds(5));
                break;
            case "close":
                connection.PendingCalls.FailAllPendingRequests(close);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(terminal));
        }
    }

    private static void AssertTerminal(string terminal, Exception? failure, CancellationToken token, Exception close)
    {
        Ensure(terminal switch
        {
            "cancel" => failure is OperationCanceledException cancelled && cancelled.CancellationToken == token,
            "deadline" => failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "close" => ReferenceEquals(failure, close),
            _ => false
        }, $"the pending owner's {terminal} terminal must reach the caller unchanged; got {failure}");
    }

    private static void AssertAdmissionReleased(ClientConnection connection)
    {
        Ensure(connection.PendingCalls.Count == 0 && connection.PendingCalls.ActiveCount == 0,
            "terminal completion must release pending capacity");
        Ensure(connection.ActiveCallCount == 0 && connection.CallAdmissionReservationCount == 0,
            "terminal completion must release active-call and admission ownership");
        Ensure(connection.TryReserveCallAdmission(out _), "the next call must be admissible");
        connection.ReleaseCallAdmissionReservation();
    }

    private static ClientConnection GetConnection(SharpLinkClient client)
        => ((ClientConnection[])typeof(SharpLinkClient).GetField(
            "_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!).Single();

    private static async Task<Exception?> CaptureFailure(Task invocation)
    {
        try
        {
            await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class OperationProbe(RpcRequestOperation<RpcEmptyRequest> operation, CancellationToken producerToken)
    {
        private static readonly FieldInfo Core = typeof(RpcRequestOperation<RpcEmptyRequest>).GetField(
            "_core", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly short _version = ReadVersion(operation);
        internal long Id { get; } = operation.Id;
        internal CancellationToken ProducerToken { get; } = producerToken;

        internal static OperationProbe Capture(ClientConnection connection)
        {
            var slots = (Array)typeof(PendingRequestTable).GetField(
                "_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection.PendingCalls)!;
            var call = slots.Cast<object?>().Single(static call => call is not null)!;
            var operation = (RpcRequestOperation<RpcEmptyRequest>)call.GetType().GetProperty("Operation")!.GetValue(call)!;
            return new OperationProbe(operation, connection.PendingCalls.GetProducerCancellationToken(operation.Id));
        }

        internal void AssertResetOnce()
            => Ensure(ReadVersion(operation) == unchecked((short)(_version + 1)),
                "the pooled operation must reset exactly once when the invocation consumes its terminal result");

        internal void AssertReturnedOnce()
        {
            AssertResetOnce();
            var pool = typeof(PendingRequestTable).GetNestedType("RpcOperationPool`1", BindingFlags.NonPublic)!
                .MakeGenericType(typeof(RpcEmptyRequest));
            var queue = (ConcurrentQueue<RpcRequestOperation<RpcEmptyRequest>>)pool.GetField(
                "Queue", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Ensure(queue.Count(candidate => ReferenceEquals(candidate, operation)) == 1,
                "the consumed operation must be retained exactly once in its pool");
        }

        private static short ReadVersion(RpcRequestOperation<RpcEmptyRequest> operation)
            => ((ManualResetValueTaskSourceCore<RpcEmptyRequest>)Core.GetValue(operation)!).Version;
    }

    private sealed class RegistrationTimeProvider : TimeProvider
    {
        internal ManualTimeProvider Manual { get; } = new();
        internal Action? OnPendingTimerChange;
        public override long TimestampFrequency => Manual.TimestampFrequency;
        public override long GetTimestamp() => Manual.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => Manual.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Manual.CreateTimer(callback, state, dueTime, period);
            // Select only the pending scheduler's timer, not heartbeat or lifecycle timers.
            return state is PendingDeadlineScheduler ? new RegistrationTimer(this, timer) : timer;
        }

        private sealed class RegistrationTimer(RegistrationTimeProvider owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = inner.Change(dueTime, period);
                if (dueTime != Timeout.InfiniteTimeSpan)
                    Interlocked.Exchange(ref owner.OnPendingTimerChange, null)?.Invoke();
                return changed;
            }
            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class ProducerProbe
    {
        internal int Calls;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception Failure { get; } = new InvalidOperationException("oneway producer failed");
    }

    private readonly struct GatedProducer(ProducerProbe probe) : IRpcClientStreamWriter
    {
        public async ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
        {
            probe.Calls++;
            probe.Started.TrySetResult();
            try
            {
                await probe.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                probe.Stopped.TrySetResult();
            }
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
