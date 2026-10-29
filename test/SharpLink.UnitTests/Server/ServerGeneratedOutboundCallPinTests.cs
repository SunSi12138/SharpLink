using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerGeneratedOutboundCallPinTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);
    private const long RequestId = 73;

    [Test]
    [Arguments((int)ServerCallCancellationReason.RemoteCancel)]
    [Arguments((int)ServerCallCancellationReason.ModuleDraining)]
    [Arguments((int)ServerCallCancellationReason.ServerStopping)]
    [Arguments((int)ServerCallCancellationReason.ConnectionClosed)]
    public async Task PinnedTerminalPreventsEnumeratorFactoryEntry(int reasonValue)
    {
        var reason = (ServerCallCancellationReason)reasonValue;
        await using var fixture = new Fixture();
        var state = fixture.AddState();
        Ensure(state.TryCancel(reason), "terminal precedes the pinned enumerator factory");
        var stream = new CallbackStream();

        await fixture.Pump(stream).WaitAsync(Watchdog);

        Ensure(stream.FactoryCalls == 0 && stream.MoveCalls == 0 && stream.DisposeCalls == 0,
            "the acquired pin must not let a selected terminal enter or dispose an uncreated enumerator");
        Ensure(state.Reason == reason && !fixture.Mapper.Observed.Task.IsCompleted,
            "strong terminal reason and its application-mapper suppression are preserved");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    // Pool identity is the assertion: no unrelated test may rent the returned state first.
    [NotInParallel]
    public async Task RemovedCallCannotRecycleUntilPhysicalMoveAndDisposalBothFinish(bool cancel)
    {
        await using var fixture = new Fixture();
        var state = fixture.AddState();
        var generation = state.CaptureLease(RequestId).Generation;
        using var cancellation = new CancellationTokenSource();
        var stream = new PendingStream();
        var pump = fixture.Pump(stream, cancellation.Token, withAmbientDeadline: true);
        ServerCallCancellationState? duringMove = null;
        ServerCallCancellationState? duringDispose = null;
        ServerCallCancellationState? returned = null;
        try
        {
            await stream.MoveEntered.Task.WaitAsync(Watchdog);
            fixture.RemoveAndDisposeState();
            if (cancel)
            {
                cancellation.Cancel();
                await fixture.Mapper.Observed.Task.WaitAsync(Watchdog);
            }
            Ensure(!pump.IsCompleted && !stream.DisposeEntered.Task.IsCompleted,
                "a pending or logically canceled MoveNext stays physically owned, without concurrent disposal");
            Ensure(state.RequestId == RequestId && state.CaptureLease(RequestId).Generation == generation,
                "unlink/dispose cannot reset the generation while the outbound pump owns it");
            duringMove = fixture.RentState();
            Ensure(!ReferenceEquals(state, duringMove), "pending physical MoveNext must prevent pool reuse");

            stream.MoveRelease.TrySetResult(false);
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(!pump.IsCompleted && state.RequestId == RequestId,
                "finishing MoveNext cannot release the pin before asynchronous DisposeAsync finishes");
            duringDispose = fixture.RentState();
            Ensure(!ReferenceEquals(state, duringDispose), "pending disposal must independently prevent pool reuse");

            stream.DisposeRelease.TrySetResult();
            await pump.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == 1, "abandoned producer is disposed exactly once");
            returned = fixture.RentState();
            Ensure(ReferenceEquals(state, returned), "the final pump release returns its removed state to the pool");
            Ensure(returned.CaptureLease(RequestId).Generation != generation,
                "reuse starts a different generation only after all physical work completes");
        }
        finally
        {
            stream.MoveRelease.TrySetResult(false);
            stream.DisposeRelease.TrySetResult();
            await pump.WaitAsync(Watchdog);
            duringMove?.Dispose();
            duringDispose?.Dispose();
            returned?.Dispose();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PinnedCallRejectsNextMoveAfterUnlinkAtTerminalOrExactDeadline(bool deadline)
    {
        await using var fixture = new Fixture();
        var state = fixture.AddState(deadline ? TimeSpan.FromSeconds(1) : null);
        if (deadline)
            fixture.Clock.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1).Subtract(TimeSpan.FromTicks(1)));
        var stream = new CallbackStream(onCurrent: () =>
        {
            if (deadline)
                fixture.Clock.AdvanceWithoutRunningTimers(TimeSpan.FromTicks(1));
            else
                Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel), "remote terminal wins");
            fixture.UnlinkState();
        });

        // No ambient deadline or cooperative token: only the pinned state can reject the next
        // user entry, and the timer callback cannot rescue an omitted exact-boundary check.
        await fixture.Pump(stream).WaitAsync(Watchdog);
        Ensure(stream.FactoryCalls == 1 && stream.MoveCalls == 1 && stream.DisposeCalls == 1,
            "a removed call's terminal must reject the next MoveNext, not enter user code again");
        Ensure(state.Reason == (deadline ? ServerCallCancellationReason.DeadlineExceeded :
                ServerCallCancellationReason.RemoteCancel),
            "the exact-boundary progress claim must publish the deadline or preserve the earlier terminal");
        Ensure(fixture.Mapper.Observed.Task.IsCompleted,
            "the rejected entry must take the existing exception/publication path after map removal");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnpinnedPumpStillFindsStateCreatedAfterEnumeratorEntry(bool rejectedAcquisition)
    {
        await using var fixture = new Fixture();
        var heldLease = default(ServerCallCancellationLease);
        if (rejectedAcquisition)
        {
            var disposed = fixture.AddState();
            heldLease = disposed.CaptureLease(RequestId);
            Ensure(heldLease.TryAcquire(), "test owner keeps the disposed map entry from recycling");
            disposed.Dispose();
        }

        var stream = new CallbackStream(onFactory: () =>
        {
            if (rejectedAcquisition)
                fixture.RemoveAndDisposeState();
            var lateState = fixture.AddState();
            Ensure(lateState.TryCancel(ServerCallCancellationReason.RemoteCancel), "late state is terminal");
        });
        try
        {
            await fixture.Pump(stream).WaitAsync(Watchdog);
            Ensure(stream.FactoryCalls == 1 && stream.MoveCalls == 0 && stream.DisposeCalls == 1,
                "missing/failed initial pin must retain per-entry lookup and reject a later-created terminal");
            Ensure(!fixture.Mapper.Observed.Task.IsCompleted,
                "a mapped strong terminal must keep the existing no-application-mapper behavior");
        }
        finally
        {
            if (rejectedAcquisition)
                heldLease.ReleaseUse();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    // The post-release reset assertion must not race an unrelated rent from the global pool.
    [NotInParallel]
    public async Task MappingAndPublicationFaultsRetainPinThroughPhysicalWorkAndReleaseIt(bool publicationFault)
    {
        await using var fixture = new Fixture();
        var state = fixture.AddState();
        var child = NewSignal();
        var original = new InvalidOperationException("application failure");
        var carrier = new SharpLinkException(SharpLinkErrorCode.Internal, "physical carrier", original);
        var owner = SharpLinkPhysicalWork.CreateCarrierOwner(carrier, original);
        owner.Track(child.Task);
        var injected = new InvalidOperationException(publicationFault ? "publication failure" : "mapper failure");
        var pinWasRetainedDuringMapping = false;
        fixture.Mapper.Callback = exception =>
        {
            fixture.RemoveAndDisposeState();
            pinWasRetainedDuringMapping = state.RequestId == RequestId;
            if (publicationFault)
                return new PublicationFailure(injected);
            throw injected;
        };
        var pump = fixture.Pump(new FactoryFailureStream(carrier));
        try
        {
            Ensure(ReferenceEquals(await fixture.Mapper.Observed.Task.WaitAsync(Watchdog), original),
                "the existing mapping path must preserve the physical carrier's logical exception");
            Ensure(pinWasRetainedDuringMapping && state.RequestId == RequestId && !pump.IsCompleted,
                "mapper/publication failure cannot release a removed pin while its physical child is pending");
            child.TrySetResult();
            var failure = await CaptureFailureAsync(pump);
            Ensure(publicationFault ? ReferenceEquals(failure, injected) : failure is null,
                "publication failures propagate, while application mapper failures retain Internal-error fallback");
            Ensure(state.RequestId == 0,
                "both catch exit paths release the final pin after physical work, rather than leaking a generation");
        }
        finally
        {
            child.TrySetResult();
            _ = await CaptureFailureAsync(pump);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly SharpLinkServer _server;
        private readonly RpcSession _session;
        private readonly ServerGeneratedBridge _bridge;
        private readonly StripedLongMap<ServerCallCancellationState> _calls = new();
        private ServerCallCancellationState? _state;
        internal ManualTimeProvider Clock { get; } = new();
        internal CallbackMapper Mapper { get; } = new();

        internal Fixture()
        {
            _server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration().UseExceptionMapper(Mapper)
                .UseTransport(new IdleListener()).Build();
            var runtime = (SharpLinkRuntimeContext)typeof(SharpLinkServer).GetField(
                "_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_server)!;
            _session = RpcSessionTestFixture.CreateSessionOverTestTransport("outbound-call-pin",
                _input.Reader, _output.Writer, RpcSessionTestFixture.ServerOptions(runtime));
            _bridge = new ServerGeneratedBridge(_server, _session, _calls);
        }

        internal ServerCallCancellationState RentState(TimeSpan? timeout = null)
            => ServerCallCancellationState.Rent(RequestId,
                timeout is { } duration ? RpcDeadline.Create(duration, Clock) : default,
                Clock, CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: false);

        internal ServerCallCancellationState AddState(TimeSpan? timeout = null)
        {
            Ensure(_state is null, "fixture has only one call-map owner");
            _state = RentState(timeout);
            _calls.Set(RequestId, _state);
            return _state;
        }

        internal void UnlinkState()
            => Ensure(_state is not null && _calls.TryRemove(RequestId, _state), "remove original call mapping");

        internal void RemoveAndDisposeState()
        {
            if (_state is not { } state)
                return;
            _ = _calls.TryRemove(RequestId, state);
            _state = null;
            state.Dispose();
        }

        internal Task Pump(IAsyncEnumerable<int> stream, CancellationToken token = default,
            bool withAmbientDeadline = false)
        {
            var method = new RpcMethodDescriptor(101, 202, RpcMethodKind.ServerStreaming, true, false, true,
                TimeSpan.FromMinutes(5));
            using var scope = SharpLinkCallContext.Push(new SharpLinkServerInvocationContext(method, RequestId,
                _session.Id, null, null, null,
                withAmbientDeadline ? RpcDeadline.Create(TimeSpan.FromMinutes(5), Clock) : default,
                Clock, null, token));
            return _bridge.PumpOutboundStreamAsync(RequestId, 0, stream,
                _session.RuntimeContext.Codecs.GetCodec<int>(), false, 101, 202, token).AsTask();
        }

        public async ValueTask DisposeAsync()
        {
            RemoveAndDisposeState();
            await _session.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
            await _server.DisposeAsync();
        }
    }

    private sealed class CallbackMapper : IRpcExceptionMapper
    {
        internal Func<Exception, SharpLinkException>? Callback { get; set; }
        internal TaskCompletionSource<Exception> Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            try
            {
                return Callback?.Invoke(exception) ?? new SharpLinkException(SharpLinkErrorCode.Internal, "mapped");
            }
            finally { Observed.TrySetResult(exception); }
        }
    }

    private sealed class PendingStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource MoveEntered { get; } = NewSignal();
        internal TaskCompletionSource<bool> MoveRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeEntered { get; } = NewSignal();
        internal TaskCompletionSource DisposeRelease { get; } = NewSignal();
        internal int DisposeCalls;
        public int Current => 7;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            MoveEntered.TrySetResult();
            return new ValueTask<bool>(MoveRelease.Task);
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            DisposeEntered.TrySetResult();
            return new ValueTask(DisposeRelease.Task);
        }
    }

    private sealed class CallbackStream(Action? onFactory = null, Action? onCurrent = null)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal int FactoryCalls;
        internal int MoveCalls;
        internal int DisposeCalls;
        public int Current { get { onCurrent?.Invoke(); return 7; } }
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            FactoryCalls++;
            onFactory?.Invoke();
            return this;
        }
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++MoveCalls == 1);
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }

    private sealed class FactoryFailureStream(Exception failure) : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class PublicationFailure(Exception failure)
        : SharpLinkException(SharpLinkErrorCode.Internal, "unreadable wire message")
    {
        public override string Message => throw failure;
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try { await task.WaitAsync(Watchdog); return null; }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
