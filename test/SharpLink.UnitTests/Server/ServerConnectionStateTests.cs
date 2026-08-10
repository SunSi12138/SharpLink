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
        Ensure(ReferenceEquals(authentication, state.AuthenticationContext), "authentication must belong to the connection");
        var callContext = state.DefaultCallContext ??
            throw new Exception("ready connection must publish a default call context");
        Ensure(callContext.SessionId == state.Session.Id, "call context session ID");
        Ensure(ReferenceEquals(authentication, callContext.Authentication), "call context authentication");
        Ensure(callContext.Deadline is null, "default call context deadline");
        Ensure(callContext.Metadata is null, "default call context metadata");
        Ensure(ReferenceEquals(callContext, state.GetCallContextSnapshot(null, null)),
            "plain calls must reuse the default call context");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var deadlineContext = state.GetCallContextSnapshot(deadline, null);
        Ensure(!ReferenceEquals(callContext, deadlineContext), "deadline calls must not reuse the default context");
        Ensure(deadlineContext.Deadline == deadline, "deadline call context");

        var metadata = new SharpLinkMetadata();
        var metadataContext = state.GetCallContextSnapshot(null, metadata);
        Ensure(!ReferenceEquals(callContext, metadataContext), "metadata calls must not reuse the default context");
        Ensure(ReferenceEquals(metadata, metadataContext.Metadata), "metadata call context");
        Ensure(state.TryRecordAcceptedRequest(42), "ready connection should accept request IDs");
        Ensure(state.LastAcceptedRequestId == 42, "last accepted request ID");

        state.MarkDraining();
        Ensure(!state.TryRecordAcceptedRequest(43), "draining connection must reject new request IDs");

        await Task.WhenAll(state.CloseAsync().AsTask(), state.CloseAsync().AsTask());
        Ensure(state.LifecycleState == ServerConnectionLifecycleState.Closed, "closed state");
        Ensure(state.SessionTask.IsCompletedSuccessfully, "session completion should be published");
        Ensure(state.AuthenticationContext is null, "closed connection must release authentication context");
        Ensure(state.DefaultCallContext is null, "closed connection must release default call context");
        Ensure(disconnectCount == 1, "session should disconnect exactly once");
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
            CancellationToken.None);
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
        await close.WaitAsync(TimeSpan.FromSeconds(2));

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
        return new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            CreateCallCancellations(),
            serverToken);
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
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
            => ValueTask.CompletedTask;
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
