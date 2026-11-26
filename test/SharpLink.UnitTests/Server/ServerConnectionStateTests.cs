using System.IO.Pipelines;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerConnectionStateTests
{
    [Test]
    public async Task LifecycleShouldPublishAuthenticationAndCloseOnce()
    {
        var disconnectCount = 0;
        var state = CreateState(() => Interlocked.Increment(ref disconnectCount));
        var authentication = new SharpLinkAuthenticationContext(subject: "alice");

        Ensure(state.LifecycleState == ServerConnectionLifecycleState.Handshaking, "initial state");
        Ensure(!state.TryAcquireCall(1),
            "a handshaking connection must not admit a business invocation");
        Ensure(!state.TryRecordAcceptedRequest(1),
            "a handshaking connection must not publish a business request ID");
        Ensure(state.DefaultCallContext is null, "handshaking connection must not publish a call context");
        Ensure(state.MarkReady(authentication), "handshake should mark the connection ready");
        state.Session.AssertStateInvariant();
        state.AssertStateInvariant();
        Ensure(ReferenceEquals(authentication, state.AuthenticationContext), "authentication must belong to the connection");
        var callContext = state.DefaultCallContext ??
            throw new Exception("ready connection must publish a default call context");
        Ensure(callContext.SessionId == state.Session.Id, "call context session ID");
        Ensure(ReferenceEquals(authentication, callContext.Authentication), "call context authentication");
        Ensure(!callContext.LocalRpcDeadline.HasValue, "default call context deadline");
        Ensure(callContext.Metadata is null, "default call context metadata");
        Ensure(ReferenceEquals(callContext, state.GetCallContextSnapshot(default, null)),
            "plain calls must reuse the default call context");

        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(30), TimeProvider.System);
        var deadlineContext = state.GetCallContextSnapshot(deadline, null);
        Ensure(!ReferenceEquals(callContext, deadlineContext), "deadline calls must not reuse the default context");
        Ensure(deadlineContext.LocalRpcDeadline.HasValue &&
               deadlineContext.LocalRpcDeadline.Timestamp == deadline.Timestamp,
            "deadline call context");

        var metadata = new SharpLinkMetadata();
        var metadataContext = state.GetCallContextSnapshot(default, metadata);
        Ensure(!ReferenceEquals(callContext, metadataContext), "metadata calls must not reuse the default context");
        Ensure(ReferenceEquals(metadata, metadataContext.Metadata), "metadata call context");
        Ensure(state.TryRecordAcceptedRequest(42), "ready connection should accept request IDs");
        Ensure(state.LastAcceptedRequestId == 42, "last accepted request ID");

        state.MarkDraining();
        state.Session.AssertStateInvariant();
        state.AssertStateInvariant();
        Ensure(!state.TryRecordAcceptedRequest(43), "draining connection must reject new request IDs");
        Ensure(state.Session.ProtocolPhase == RpcSessionProtocolPhase.Draining,
            "server connection draining must update the shared Session protocol phase");

        await Task.WhenAll(state.CloseAsync().AsTask(), state.CloseAsync().AsTask());
        state.Session.AssertStateInvariant();
        state.AssertStateInvariant();
        Ensure(state.LifecycleState == ServerConnectionLifecycleState.Closed, "closed state");
        Ensure(state.SessionTask.IsCompletedSuccessfully, "session completion should be published");
        Ensure(state.AuthenticationContext is null, "closed connection must release authentication context");
        Ensure(state.DefaultCallContext is null, "closed connection must release default call context");
        Ensure(disconnectCount == 1, "session should disconnect exactly once");
    }

    [Test]
    public async Task CloseShouldReleaseIdleGrantsAndKeepEachBorrowReturnable()
    {
        var state = CreateState(static () => { });
        Ensure(state.MarkReady(null), "connection ready");
        var bindings = state.NStudyBindings;
        await using var active = CreateConnectionRegistration(new ThrowingService("never activated"));
        await using var idle = CreateConnectionRegistration(new ThrowingService("never activated"));
        await using var invalidating = CreateConnectionRegistration(new ThrowingService("never activated"));
        Ensure(bindings.TryInstall(active, out var activeId), "install the active grant");
        Ensure(bindings.TryInstall(idle, out var idleId), "install another independent idle grant");
        Ensure(bindings.TryInstall(invalidating, out _), "install a different registration");
        Ensure(bindings.Seal(invalidating.NStudyTarget), "an invalidation is pending before closure");
        Ensure(bindings.TryBorrow(activeId, out _, out var use), "borrow the original slot");
        using (use)
        {
            Ensure(use.CanPrepareDispatch() && bindings.Snapshot() == (2, 0, 1), "one exact borrowed grant is active");
            await state.CloseAsync();
            Ensure(bindings.Snapshot() == (0, 1, 1) && !use.CanPrepareDispatch() &&
                   !bindings.TryResolve(idleId, out _) && !bindings.TryInstall(idle, out _) &&
                   bindings.TakeInvalidated(new long[NStudyWire.BindingLimit]) == 0,
                "close drops idle grants and pending notices without forgetting the exact physical borrower");
        }
        Ensure(bindings.Snapshot() == (0, 0, 0), "the last original borrow can return normally");
    }

    [Test]
    public async Task BindingGroupCutClosesOldStartsAndKeepsTheirSlotsUntilReturn()
    {
        var table = new NStudyServerBindings();
        await using var group = CreateConnectionRegistration(new ThrowingService("never activated"));
        await using var sibling = CreateConnectionRegistration(new ThrowingService("never activated"));
        Ensure(table.TryInstall(group, out var first), "install the first group member");
        Ensure(table.TryInstall(group, out var second), "install the second group member");
        Ensure(table.TryInstall(sibling, out var other), "install the unrelated member");
        Ensure(table.TryBorrow(first, out _, out var firstUse), "capture the first exact slot");
        using (firstUse)
        {
            Ensure(table.TryBorrow(second, out _, out var secondUse), "capture the second exact slot");
            using (secondUse)
            {
                Ensure(firstUse.CanPrepareDispatch() && secondUse.CanPrepareDispatch(),
                    "both original routes pass preparation before the cut");
                Ensure(table.Seal(group.NStudyTarget), "the group has pending invalidations");
                Ensure(!firstUse.CanPrepareDispatch() && !secondUse.CanPrepareDispatch() && table.Snapshot() == (1, 2, 2),
                    "the cut denies both new starts but keeps both physical owners until they return");
                Ensure(table.TryResolve(other, out var current) && ReferenceEquals(current, sibling),
                    "the cut does not close the unrelated binding");
            }
        }

        Ensure(table.Snapshot() == (1, 0, 0), "both old slots are reclaimable only after return");
        Ensure(table.TryInstall(sibling, out var replacement) && NStudyWire.SlotIndex(replacement) == NStudyWire.SlotIndex(first) &&
               replacement != first && !table.TryResolve(first, out _), "slot reuse does not reopen the previous incarnation");
        Ensure(table.TryBorrow(replacement, out _, out var nextUse), "capture the new incarnation");
        using (nextUse)
        {
            Ensure(nextUse.TryEnterExecution(isStream: false, out var selected, out var lease) &&
                   ReferenceEquals(selected, sibling) && !lease.IsAcquired,
                "the new static incarnation can start without a dynamic module lease");
            table.Release(replacement);
            Ensure(!nextUse.TryEnterExecution(isStream: false, out _, out _),
                "single-binding release prevents a later execution claim");
        }
    }

    [Test]
    public async Task BindingTableGrowthPreservesRetiringBorrowAndPendingNotice()
    {
        var table = new NStudyServerBindings();
        await using var retiring = CreateConnectionRegistration(new ThrowingService("never activated"));
        await using var live = CreateConnectionRegistration(new ThrowingService("never activated"));
        Ensure(table.TryInstall(retiring, out var original), "install the initial grant");
        Ensure(table.TryBorrow(original, out _, out var borrow), "hold the old grant through table growth");
        using (borrow)
        {
            Ensure(table.Seal(retiring.NStudyTarget), "retirement records a notice while the borrower is active");
            for (var index = 1; index < NStudyWire.BindingLimit; index++)
            {
                Ensure(table.TryInstall(live, out var binding) && NStudyWire.SlotIndex(binding) == index &&
                       table.TryResolve(binding, out var current) && ReferenceEquals(current, live),
                    "growing the table must retain published indices and exact service selection");
            }

            Ensure(!borrow.CanPrepareDispatch() && table.Snapshot() == (NStudyWire.BindingLimit - 1, 1, 1) &&
                   !table.TryInstall(live, out _), "an old physical borrower still occupies one of the 64 slots");
            var notices = new long[NStudyWire.BindingLimit];
            Ensure(table.TakeInvalidated(notices) == 1 && notices[0] == original && table.TakeInvalidated(notices) == 0,
                "growth preserves the queued notice for the exact original token");
        }

        Ensure(table.TryInstall(live, out var replacement) &&
               NStudyWire.SlotIndex(replacement) == NStudyWire.SlotIndex(original) && replacement != original &&
               !table.TryResolve(original, out _) && table.IsRetired(original),
            "only physical return enables reuse at the next incarnation");
        table.Close();
        Ensure(table.Snapshot() == (0, 0, 0), "close reclaims all grown slots");
    }

    [Test]
    public async Task CloseShouldWaitForSessionLoopToReleaseItsReadBuffer()
    {
        var input = new Pipe();
        var reader = new CompletionTrackingPipeReader(input.Reader);
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            Guid.NewGuid().ToString("N"),
            reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ServerOptions());
        var state = new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            CreateCallCancellations(),
            CancellationToken.None,
            RpcSessionTestFixture.RuntimeContext.TimeProvider);
        var stream = new ShutdownJoiningDispatcher();
        session.StreamManager.Register(7, 1, stream);
        var streamDispatch = session.StreamManager.DispatchChunkAsync(
            7,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await stream.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        state.MarkSessionLoopStarted();

        var close = state.CloseAsync().AsTask();

        Ensure(state.ConnectionToken.IsCancellationRequested,
            "close must cancel the session loop before waiting for its read buffer");
        Ensure(!close.IsCompleted,
            "close must not complete the PipeReader while the session loop still owns a ReadResult");
        Ensure(!session.IsConnected,
            "close must signal stream and send-pump shutdown before joining the read loop");
        await streamDispatch.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(stream.CompleteCount == 1,
            "the pre-disposal shutdown phase must release a read loop blocked in stream dispatch");
        Ensure(reader.CompleteCount == 0 && transport.DisposeCount == 0,
            "PipeReader and transport completion must wait until the read buffer has been released");

        state.MarkSessionLoopCompleted();
        // The ownership invariant ends at the loop-completed publication. Session disposal is a
        // cleanup tail and must not be turned into a two-second read-buffer ownership deadline.
        await close;

        Ensure(reader.CompleteCount == 1 && transport.DisposeCount == 1,
            "PipeReader and transport completion should resume after the loop releases its buffer");
        Ensure(state.LifecycleState == ServerConnectionLifecycleState.Closed, "closed state");
        await input.Writer.CompleteAsync();
        await output.Reader.CompleteAsync();
    }

    [Test]
    public async Task DefaultCallContextShouldBeIsolatedPerConnectionAndSafeForConcurrentReads()
    {
        var first = CreateState(static () => { });
        var second = CreateState(static () => { });
        var firstAuthentication = new SharpLinkAuthenticationContext(subject: "alice");
        var secondAuthentication = new SharpLinkAuthenticationContext(subject: "bob");

        Ensure(first.MarkReady(firstAuthentication), "first ready");
        Ensure(second.MarkReady(secondAuthentication), "second ready");

        var firstContext = first.DefaultCallContext ?? throw new Exception("first context");
        var secondContext = second.DefaultCallContext ?? throw new Exception("second context");
        Ensure(!ReferenceEquals(firstContext, secondContext), "connections must not share call contexts");
        Ensure(ReferenceEquals(firstAuthentication, firstContext.Authentication), "first authentication");
        Ensure(ReferenceEquals(secondAuthentication, secondContext.Authentication), "second authentication");

        Parallel.For(0, 1024, _ =>
            Ensure(ReferenceEquals(firstContext, first.DefaultCallContext), "concurrent context read"));

        await first.CloseAsync();
        await second.CloseAsync();
    }

    [Test]
    public async Task ServerCancellationShouldCancelOnlyLinkedConnectionState()
    {
        using var serverCancellation = new CancellationTokenSource();
        var state = CreateState(static () => { }, serverCancellation.Token);
        var unrelated = CreateState(static () => { });

        serverCancellation.Cancel();

        Ensure(state.ConnectionToken.IsCancellationRequested, "linked connection token should be canceled");
        Ensure(!unrelated.ConnectionToken.IsCancellationRequested, "unrelated connection token should remain active");
        await state.CloseAsync();
        await unrelated.CloseAsync();
    }

    [Test]
    public async Task CallAdmissionShouldBePerConnectionAndRecoverCapacity()
    {
        var first = CreateState(static () => { });
        var second = CreateState(static () => { });
        Ensure(first.MarkReady(null), "first ready");
        Ensure(second.MarkReady(null), "second ready");
        first.AssertStateInvariant();
        second.AssertStateInvariant();

        Ensure(first.TryAcquireCall(1), "first call should acquire capacity");
        Ensure(!first.TryAcquireCall(1), "same connection should enforce its limit");
        Ensure(second.TryAcquireCall(1), "another connection should have independent capacity");
        first.ReleaseCall();
        Ensure(first.TryAcquireCall(1), "released capacity should be reusable");

        first.ReleaseCall();
        second.ReleaseCall();
        await first.CloseAsync();
        await second.CloseAsync();
    }

    [Test]
    public async Task ConnectionServiceCleanupShouldSurfaceEveryFailure()
    {
        var state = CreateState(static () => { });
        Ensure(state.MarkReady(null), "connection ready");
        var firstService = new ThrowingService("first connection service cleanup failed");
        var secondService = new ThrowingService("second connection service cleanup failed");
        var first = CreateConnectionRegistration(firstService);
        var second = CreateConnectionRegistration(secondService);
        _ = await state.AcquireServiceAsync(first, default);
        _ = await state.AcquireServiceAsync(second, default);

        await state.CloseAsync();
        var failure = await CaptureFailureAsync(state.ServiceCleanupTask);

        Ensure(ContainsMessage(failure, "first connection service cleanup failed"),
            "connection cleanup must retain the first service failure");
        Ensure(ContainsMessage(failure, "second connection service cleanup failed"),
            "connection cleanup must retain the second service failure");
        Ensure(firstService.DisposeCount == 1 && secondService.DisposeCount == 1,
            "one service failure must not skip later connection services");
    }

    [Test]
    public async Task CloseShouldPreserveCancellationAndSessionCleanupFailures()
    {
        var state = CreateState(static () => throw new InvalidOperationException("session cleanup failed"));
        using var registration = state.ConnectionToken.Register(
            static () => throw new InvalidOperationException("connection cancellation failed"));

        Exception failure;
        try
        {
            await state.CloseAsync();
            throw new Exception("expected connection close failure");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(ContainsMessage(failure, "connection cancellation failed"),
            "connection close must retain cancellation callback failure");
        Ensure(ContainsMessage(failure, "session cleanup failed"),
            "connection close must retain Session cleanup failure");
    }

    private static ServerConnectionState CreateState(
        Action disconnect,
        CancellationToken serverToken = default)
    {
        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            Guid.NewGuid().ToString("N"),
            input.Reader,
            output.Writer,
            () =>
            {
                disconnect();
                return ValueTask.CompletedTask;
            });
        var session = new RpcSession(transport, RpcSessionTestFixture.ServerOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        return new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            CreateCallCancellations(),
            serverToken,
            RpcSessionTestFixture.RuntimeContext.TimeProvider);
    }

    private static StripedLongMap<ServerCallCancellationState> CreateCallCancellations()
        => new(RpcSessionTestFixture.RuntimeContext.Concurrency);

    private static ServiceRegistration CreateConnectionRegistration(ThrowingService service)
        => ServiceRegistration.CreateConnection(
            typeof(object),
            new StubMarker(),
            new ScopeFactory(),
            _ => service,
            disposeService: true);

    private static async Task<Exception> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation;
            throw new Exception("expected connection service cleanup failure");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (ContainsMessage(inner, message))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private sealed class ScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope();
    }

    private sealed class Scope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new EmptyServiceProvider();
        public void Dispose() { }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class CompletionTrackingPipeReader(PipeReader inner) : PipeReader
    {
        private int _completeCount;

        internal int CompleteCount => Volatile.Read(ref _completeCount);

        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            => inner.AdvanceTo(consumed, examined);

        public override void CancelPendingRead() => inner.CancelPendingRead();

        public override void Complete(Exception? exception = null)
        {
            Interlocked.Increment(ref _completeCount);
            inner.Complete(exception);
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            Interlocked.Increment(ref _completeCount);
            await inner.CompleteAsync(exception);
        }

        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            => inner.ReadAsync(cancellationToken);
    }

    private sealed class ShutdownJoiningDispatcher : IStreamDispatcher
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completeCount;

        internal Task Entered => _entered.Task;
        internal int CompleteCount => Volatile.Read(ref _completeCount);

        public async ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }

        public void Complete(bool isError, string? errorMessage)
            => Complete(isError ? new Exception(errorMessage) : null);

        public void Complete(Exception? exception)
        {
            _ = exception;
            Interlocked.Increment(ref _completeCount);
            _release.TrySetResult();
        }
    }

    private sealed class ThrowingService(string message) : IAsyncDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.FromException(new InvalidOperationException(message));
        }
    }

    private sealed class StubMarker : IRpcStub
    {
        public long InterfaceHash => 1;
        public bool TryPrepareMethod(long methodId, out RpcGeneratedPreparedMethod method)
        {
            method = default;
            return false;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
