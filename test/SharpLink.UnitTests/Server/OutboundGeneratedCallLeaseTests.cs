using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

[NotInParallel]
public sealed class OutboundGeneratedCallLeaseTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(100, false)]
    [Arguments(1, true)]
    public async Task NormalItemsAndMissingStateFallbackPreserveCompletion(int items, bool missingState)
    {
        await using var fixture = new Fixture();
        if (missingState) fixture.RemoveLookup();
        var stream = new CountingStream(items);
        await fixture.Pump(stream).WaitAsync(Watchdog);
        Ensure(stream.MoveCalls == items + 1 && stream.DisposeCalls == 1,
            "the tracked and fallback paths preserve every item, EOF and exactly-once disposal");
        Ensure(fixture.State.Reason == ServerCallCancellationReason.None &&
            !fixture.State.InvocationToken.IsCancellationRequested,
            "the pump cannot select the outer invocation terminal or cancel its lifetime on normal completion");
        fixture.ReleaseOwner();
        Ensure(fixture.State.RequestId == 0, "normal completion leaves no acquired call lease behind");
    }

    [Test]
    public async Task AlreadySelectedTerminalPreventsEnumeratorCreation()
    {
        await using var fixture = new Fixture();
        var stream = new CountingStream(1);
        fixture.State.TryCancel(ServerCallCancellationReason.RemoteCancel);
        await fixture.Pump(stream).WaitAsync(Watchdog);
        Ensure(stream.GetCalls == 0 && stream.MoveCalls == 0 && stream.DisposeCalls == 0,
            "a terminal selected before the pump cannot enter user enumeration code");
        fixture.ReleaseOwner();
        Ensure(fixture.State.RequestId == 0, "refused enumeration does not retain its acquired lease");
    }

    [Test]
    public async Task TrackedApplicationFailurePreservesMapperAndSingleWireTerminal()
    {
        await using var fixture = new Fixture();
        var failure = new InvalidOperationException("tracked source failure");
        var stream = new GatedStream();
        stream.MoveRelease.SetException(failure);
        var pump = fixture.Pump(stream);
        try
        {
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(fixture.Mapper.Calls == 0 && !pump.IsCompleted,
                "ordinary tracked source failure still disposes before invoking the exception mapper");
            stream.DisposeRelease.SetResult();
            await pump.WaitAsync(Watchdog);
            Ensure(fixture.Mapper.Calls == 1 && ReferenceEquals(await fixture.Mapper.Observed.Task, failure),
                "the tracked call preserves mapper count and exact original exception identity");
            await fixture.AssertSingleErrorAsync(ProtocolV2FrameType.StreamComplete,
                SharpLinkErrorCode.Internal, "mapped lease test failure");
            fixture.ReleaseOwner();
            Ensure(fixture.State.RequestId == 0, "mapped error completion releases the pump lease");
        }
        finally { stream.DisposeRelease.TrySetResult(); await pump.WaitAsync(Watchdog); }
    }

    [Test]
    public async Task TrackedModuleDrainPublishesOnceBeforePhysicalCleanupAndSkipsMapper()
    {
        await using var fixture = new Fixture(withDeadline: true);
        var stream = new GatedStream();
        var pump = fixture.Pump(stream);
        try
        {
            fixture.State.TryCancel(ServerCallCancellationReason.ModuleDraining);
            // ReadAsync waits for the actual publication, not a scheduler delay or task status.
            await fixture.AssertSingleErrorAsync(ProtocolV2FrameType.Response,
                SharpLinkErrorCode.Unavailable, "RPC module is draining");
            Ensure(!pump.IsCompleted && stream.DisposeCalls == 0 && fixture.Mapper.Calls == 0,
                "module drain publishes its call terminal while physical MoveNext stays owned, without user mapping");
            Ensure(!fixture.State.TryClaimModuleDrainResponse(),
                "the outer dispatcher must not be able to claim a duplicate module-drain response");
            stream.MoveRelease.SetResult(false);
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(!pump.IsCompleted, "selected module drain still owns asynchronous disposal");
            stream.DisposeRelease.SetResult();
            await pump.WaitAsync(Watchdog);
            Ensure(fixture.Mapper.Calls == 0 && stream.DisposeCalls == 1,
                "late physical completion neither maps a second terminal nor repeats disposal");
            await fixture.AssertNoFurtherFramesAsync();
            fixture.ReleaseOwner();
            Ensure(fixture.State.RequestId == 0, "module drain cleanup releases the call generation");
        }
        finally
        {
            stream.MoveRelease.TrySetResult(false);
            stream.DisposeRelease.TrySetResult();
            await pump.WaitAsync(Watchdog);
        }
    }

    [Test]
    public async Task RejectedLeaseAcquisitionPreservesAmbientDeadlineFallback()
    {
        await using var fixture = new Fixture(withDeadline: true);
        var externalLease = fixture.State.CaptureLease(Fixture.RequestId);
        Ensure(externalLease.TryAcquire(), "the fixture must retain a generation while its owner is disposed");
        try
        {
            fixture.DisposeOwnerKeepingLookup();
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            var stream = new CountingStream(1);
            await fixture.Pump(stream).WaitAsync(Watchdog);
            Ensure(stream.GetCalls == 0 && fixture.Mapper.Calls == 0,
                "failed call-lease acquisition still rejects expired ambient deadlines before user entry");
            Ensure(fixture.State.RequestId == Fixture.RequestId &&
                fixture.State.Reason == ServerCallCancellationReason.None,
                "the fallback neither releases another owner's lease nor mutates a disposed call generation");
            Ensure(fixture.State.InvocationToken.CanBeCanceled,
                "the independently held generation remains alive after fallback pump completion");
        }
        finally { fixture.RemoveLookup(); externalLease.ReleaseUse(); }
        Ensure(fixture.State.RequestId == 0, "the rejected pump did not retain an extra lease");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task TerminalRetainsGenerationThroughHeldMoveAndDispose(bool cancel, bool lateFault)
    {
        await using var fixture = new Fixture(withDeadline: true);
        var stream = new GatedStream();
        var lease = fixture.State.CaptureLease(Fixture.RequestId);
        var pump = fixture.Pump(stream);
        try
        {
            if (cancel)
                fixture.State.TryCancel(ServerCallCancellationReason.RemoteCancel);
            else
                fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            fixture.ReleaseOwner();
            Ensure(fixture.State.RequestId == Fixture.RequestId,
                "removing the call owner must not recycle a generation used by a pending producer");
            Ensure(!pump.IsCompleted && stream.DisposeCalls == 0,
                "terminal selection must neither finish the pump nor overlap MoveNext with disposal");
            using (var other = fixture.RentOther())
                Ensure(!ReferenceEquals(other, fixture.State), "the retained call cannot be rented to another request");

            if (lateFault) stream.MoveRelease.TrySetException(new InvalidOperationException("late source failure"));
            else stream.MoveRelease.TrySetResult(false);
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(!pump.IsCompleted && fixture.State.RequestId == Fixture.RequestId,
                "physical ownership includes held DisposeAsync, not only the outstanding MoveNext");
            Ensure(stream.DisposeCalls == 1, "physical disposal runs exactly once");
            stream.DisposeRelease.TrySetResult();
            await pump.WaitAsync(Watchdog);
            Ensure(fixture.State.RequestId == 0, "the final pump release returns a disposed call to its pool");
            using var reused = fixture.RentOther();
            Ensure(ReferenceEquals(reused, fixture.State), "the completed call can now be reused");
            Ensure(!lease.TryAcquire(), "a stale lease cannot acquire the reused generation");
        }
        finally
        {
            stream.MoveRelease.TrySetResult(false);
            stream.DisposeRelease.TrySetResult();
            await pump.WaitAsync(Watchdog);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NormalCompletionRetainsGenerationUntilDisposalFinishes(bool disposalFault)
    {
        await using var fixture = new Fixture();
        var stream = new GatedStream();
        stream.MoveRelease.SetResult(false);
        var pump = fixture.Pump(stream);
        try
        {
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            fixture.ReleaseOwner();
            Ensure(fixture.State.RequestId == Fixture.RequestId && !pump.IsCompleted,
                "normal EOF does not release the generation while disposal is pending");
            if (disposalFault) stream.DisposeRelease.SetException(new InvalidOperationException("disposal fault"));
            else stream.DisposeRelease.SetResult();
            await pump.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == 1 && fixture.State.RequestId == 0,
                "successful and failing disposal both release the lease exactly once");
            Ensure(fixture.Mapper.Calls == (disposalFault ? 1 : 0),
                "ordinary disposal exception mapping is unchanged");
        }
        finally { stream.DisposeRelease.TrySetResult(); await pump.WaitAsync(Watchdog); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryItemChecksPinnedStateEvenAfterLookupRemoval(bool deadline)
    {
        await using var fixture = new Fixture(withDeadline: deadline);
        var stream = new OneItemStream(() =>
        {
            fixture.RemoveLookup();
            if (deadline) fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            else fixture.State.TryCancel(ServerCallCancellationReason.RemoteCancel);
        });
        // Isolate the entry guard from the protocol pump's token/deadline checks. The call
        // state's lifetime/terminal still exists, but no subsequent map lookup can find it.
        await fixture.Pump(stream, ambientDeadline: false, passInvocationToken: false).WaitAsync(Watchdog);
        Ensure(stream.MoveCalls == 1, "the next user MoveNext must be refused by the retained call state");
        Ensure(stream.DisposeCalls == 1, "a refused progress boundary still disposes its enumerator");
        Ensure(fixture.State.Reason == (deadline
                ? ServerCallCancellationReason.DeadlineExceeded : ServerCallCancellationReason.RemoteCancel),
            "per-item deadline promotion and selected terminal identity are preserved");
        fixture.ReleaseOwner();
        Ensure(fixture.State.RequestId == 0, "terminal progress refusal does not leak the lease");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EnumeratorCreationFailureRetainsNestedPhysicalWorkAndReleasesLease(bool nestedWork)
    {
        await using var fixture = new Fixture();
        var child = NewSignal();
        var original = new InvalidOperationException("enumerator creation fault");
        Exception failure = original;
        if (nestedWork)
        {
            failure = new SharpLinkException(SharpLinkErrorCode.Internal, "carrier", original);
            var owner = SharpLinkPhysicalWork.CreateCarrierOwner(failure, original);
            owner.Track(child.Task);
        }
        var stream = new ThrowingEnumerable(failure, fixture.ReleaseOwner);
        var pump = fixture.Pump(stream);
        try
        {
            await fixture.Mapper.Observed.Task.WaitAsync(Watchdog);
            Ensure(ReferenceEquals(fixture.Mapper.Observed.Task.Result, original),
                "enumerator creation preserves original exception mapping identity");
            if (nestedWork)
                Ensure(!pump.IsCompleted && fixture.State.RequestId == Fixture.RequestId,
                    "even GetAsyncEnumerator failure must retain its descendant before releasing the lease");
            child.TrySetResult();
            await pump.WaitAsync(Watchdog);
            Ensure(fixture.State.RequestId == 0, "enumerator creation failure releases the final lease");
        }
        finally { child.TrySetResult(); await pump.WaitAsync(Watchdog); }
    }

    private sealed class CountingStream(int items) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal int GetCalls;
        internal int MoveCalls;
        internal int DisposeCalls;
        public int Current => MoveCalls;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return this;
        }
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++MoveCalls <= items);
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }

    private sealed class GatedStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource<bool> MoveRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeEntered { get; } = NewSignal();
        internal TaskCompletionSource DisposeRelease { get; } = NewSignal();
        internal int DisposeCalls;
        public int Current => 7;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(MoveRelease.Task);
        public ValueTask DisposeAsync()
        {
            Ensure(MoveRelease.Task.IsCompleted, "DisposeAsync cannot overlap pending MoveNextAsync");
            DisposeCalls++;
            DisposeEntered.TrySetResult();
            return new ValueTask(DisposeRelease.Task);
        }
    }

    private sealed class OneItemStream(Action afterMove) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal int MoveCalls;
        internal int DisposeCalls;
        public int Current { get { afterMove(); return 7; } }
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++MoveCalls == 1);
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }

    private sealed class ThrowingEnumerable(Exception failure, Action entering) : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            entering();
            throw failure;
        }
    }

    private sealed class RecordingMapper : IRpcExceptionMapper
    {
        internal TaskCompletionSource<Exception> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            Calls++;
            Observed.TrySetResult(exception);
            return new SharpLinkException(SharpLinkErrorCode.Internal, "mapped lease test failure");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const long RequestId = 73;
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly SharpLinkServer _server;
        private readonly RpcSession _session;
        private readonly ServerGeneratedBridge _bridge;
        private readonly StripedLongMap<ServerCallCancellationState> _calls = new();
        private bool _ownerReleased;
        internal ManualTimeProvider Clock { get; } = new();
        internal RecordingMapper Mapper { get; } = new();
        internal ServerCallCancellationState State { get; }

        internal Fixture(bool withDeadline = false)
        {
            _server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration().UseExceptionMapper(Mapper)
                .UseTransport(new IdleListener()).Build();
            var runtime = (SharpLinkRuntimeContext)typeof(SharpLinkServer).GetField(
                "_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_server)!;
            _session = RpcSessionTestFixture.CreateSessionOverTestTransport("outbound-call-lease",
                _input.Reader, _output.Writer, RpcSessionTestFixture.ServerOptions(runtime));
            State = ServerCallCancellationState.Rent(RequestId,
                withDeadline ? RpcDeadline.Create(TimeSpan.FromMinutes(1), Clock) : default,
                Clock, CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: true);
            _calls.Set(RequestId, State);
            _bridge = new ServerGeneratedBridge(_server, _session, _calls);
        }

        internal Task Pump(IAsyncEnumerable<int> stream, bool ambientDeadline = true, bool passInvocationToken = true)
        {
            var method = new RpcMethodDescriptor(101, 202, RpcMethodKind.ServerStreaming, true, false, true,
                TimeSpan.FromMinutes(1));
            var token = passInvocationToken ? State.InvocationToken : CancellationToken.None;
            using var scope = SharpLinkCallContext.Push(new SharpLinkServerInvocationContext(method, RequestId, _session.Id,
                null, null, null, ambientDeadline ? State.Deadline : default, Clock, null, token));
            return _bridge.PumpOutboundStreamAsync(RequestId, 0, stream, _session.RuntimeContext.Codecs.GetCodec<int>(),
                false, 101, 202, token).AsTask();
        }

        internal ServerCallCancellationState RentOther()
            => ServerCallCancellationState.Rent(74, default, Clock,
                CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: true);

        internal void RemoveLookup() => _calls.TryRemove(RequestId, State);

        internal void ReleaseOwner()
        {
            if (_ownerReleased) return;
            _ownerReleased = true;
            RemoveLookup();
            State.Dispose();
        }

        internal void DisposeOwnerKeepingLookup()
        {
            _ownerReleased = true;
            State.Dispose();
        }

        internal async Task AssertSingleErrorAsync(ProtocolV2FrameType expectedType, SharpLinkErrorCode code, string message)
        {
            await _session.FlushSendQueueAsync().AsTask().WaitAsync(Watchdog);
            var read = await _output.Reader.ReadAsync().AsTask().WaitAsync(Watchdog);
            var remaining = read.Buffer;
            var frames = 0;
            while (ProtocolV2FrameParser.TryReadFrame(ref remaining, _session.RuntimeContext.Protocol, out var header, out var payload))
            {
                Ensure(header.Type == expectedType && header.RequestId == RequestId &&
                    (header.Flags & ProtocolV2FrameFlags.Error) != 0,
                    "the only published frame must have the original call identity and expected terminal shape");
                var errorPayload = expectedType == ProtocolV2FrameType.StreamComplete ? payload.Slice(sizeof(ushort)) : payload;
                var error = ProtocolV2PayloadCodec.ReadError(errorPayload, header.Flags,
                    _session.RuntimeContext.Protocol.MaxErrorMessageBytes);
                Ensure(error.Code == code && error.Message == message, "wire terminal code and message must be preserved");
                frames++;
            }
            _output.Reader.AdvanceTo(read.Buffer.End);
            Ensure(frames == 1 && remaining.IsEmpty, "exactly one complete terminal frame must be published");
        }

        internal async Task AssertNoFurtherFramesAsync()
        {
            await _session.FlushSendQueueAsync().AsTask().WaitAsync(Watchdog);
            if (!_output.Reader.TryRead(out var read)) return;
            var empty = read.Buffer.IsEmpty;
            _output.Reader.AdvanceTo(read.Buffer.End);
            Ensure(empty, "physical cleanup must not publish a duplicate terminal");
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseOwner();
            await _session.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
            await _server.DisposeAsync();
        }
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
