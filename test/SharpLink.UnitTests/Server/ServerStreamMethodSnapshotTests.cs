using System.Collections.Frozen;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    public async Task SuspendedStreamErrorShouldUseOriginalMethodFactsAfterRegistryReplacement()
    {
        var mapper = new SnapshotExceptionMapper();
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseExceptionMapper(mapper)
            .UseTransport(new IdleListener())
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "stream-method-snapshot", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var calls = CreateCallCancellations();
        var original = new SnapshotStreamStub(TimeSpan.FromSeconds(17), idempotent: true);
        var replacement = new SnapshotStreamStub(TimeSpan.FromSeconds(99), idempotent: false);
        await using var originalRegistration = ServiceRegistration.CreateSingleton(
            typeof(object), original, new object(), ownsService: false);
        await using var replacementRegistration = ServiceRegistration.CreateSingleton(
            typeof(object), replacement, new object(), ownsService: false);
        var registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer).GetField(
            "_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        registry.PublishServices(new Dictionary<long, ServiceRegistration>
        {
            [original.InterfaceHash] = originalRegistration
        }.ToFrozenDictionary());
        var resolved = new ResolvedMethodCall(originalRegistration, SnapshotStreamStub.MethodId,
            original.ResolveMethodShape(SnapshotStreamStub.MethodId));
        using var state = RentSnapshotState(8701);
        state.AttachResolvedMethod(in resolved);
        calls.Set(state.RequestId, state);
        var stream = new SuspendedFailingSnapshotStream();
        var bridge = new ServerGeneratedBridge(server, session, calls);
        // No observer/interceptor context exists before the error. The error mapper must project
        // the original captured shape, rather than resolve the currently registered stub.
        using var ambient = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, null));
        var pumping = bridge.PumpOutboundStreamAsync(8701, 0, stream,
            RpcSessionTestFixture.RuntimeContext.Codecs.GetCodec<int>(), false,
            original.InterfaceHash, SnapshotStreamStub.MethodId, CancellationToken.None).AsTask();
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registry.PublishServices(new Dictionary<long, ServiceRegistration>
        {
            [replacement.InterfaceHash] = replacementRegistration
        }.ToFrozenDictionary());
        Ensure(calls.TryRemove(8701, state), "original call state must leave the map while the pump is suspended");
        stream.Fail();
        await pumping.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(original.ResolveCount).IsEqualTo(1);
        await Assert.That(original.DescribeCount).IsEqualTo(1);
        await Assert.That(replacement.ResolveCount).IsEqualTo(0);
        await Assert.That(replacement.DescribeCount).IsEqualTo(0);
        var observed = mapper.Context ?? throw new InvalidOperationException("stream exception mapper was not invoked");
        await Assert.That(observed.Method.MethodTimeout).IsEqualTo(TimeSpan.FromSeconds(17));
        await Assert.That(observed.Method.IsIdempotent).IsTrue();
        await Assert.That(observed.Method.MethodId).IsEqualTo(SnapshotStreamStub.MethodId);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task OutboundStreamWithoutOriginalSnapshotShouldFailBeforeEnumeration(bool publishEmptyState)
        => AssertStreamSnapshotBoundaryAsync(publishEmptyState ? 1 : 0);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task OutboundStreamWithMismatchedIdentityShouldFailBeforeEnumeration(bool mismatchContract)
        => AssertStreamSnapshotBoundaryAsync(mismatchContract ? 2 : 3);

    private static async Task AssertStreamSnapshotBoundaryAsync(int mode)
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "stream-snapshot-boundary", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var calls = CreateCallCancellations();
        using var state = RentSnapshotState(8706);
        var stub = new SnapshotStreamStub(TimeSpan.FromSeconds(17), true);
        await using var registration = ServiceRegistration.CreateSingleton(
            typeof(object), stub, new object(), ownsService: false);
        if (mode >= 2)
        {
            var resolved = new ResolvedMethodCall(registration, SnapshotStreamStub.MethodId,
                stub.ResolveMethodShape(SnapshotStreamStub.MethodId));
            state.AttachResolvedMethod(in resolved);
        }
        if (mode != 0)
            calls.Set(8706, state);
        var stream = new SuspendedFailingSnapshotStream();
        var bridge = new ServerGeneratedBridge(server, session, calls);
        await Assert.That(async () => await bridge.PumpOutboundStreamAsync(8706, 0, stream,
            RpcSessionTestFixture.RuntimeContext.Codecs.GetCodec<int>(), false,
            mode == 2 ? stub.InterfaceHash + 1 : stub.InterfaceHash,
            mode == 3 ? SnapshotStreamStub.MethodId + 1 : SnapshotStreamStub.MethodId,
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<InvalidOperationException>();
        await Assert.That(stream.EnumeratorCalls).IsEqualTo(0);
        await Assert.That(stream.Entered.Task.IsCompleted).IsFalse();
        calls.TryRemove(8706, state);
    }

    [Test]
    public async Task CallStateShouldRejectReplacingItsOriginalResolvedMethod()
    {
        var stub = new SnapshotStreamStub(TimeSpan.FromSeconds(17), true);
        await using var registration = ServiceRegistration.CreateSingleton(
            typeof(object), stub, new object(), ownsService: false);
        var resolved = new ResolvedMethodCall(registration, SnapshotStreamStub.MethodId,
            stub.ResolveMethodShape(SnapshotStreamStub.MethodId));
        using var state = RentSnapshotState(8705);
        state.AttachResolvedMethod(in resolved);
        await Assert.That(() => state.AttachResolvedMethod(in resolved)).Throws<InvalidOperationException>();
        await Assert.That(ReferenceEquals(state.ResolvedMethod!.Value.Registration, registration)).IsTrue();
    }

    [Test]
    public async Task PooledCallStateShouldReleaseOriginalRegistrationAfterItsFinalUseLease()
    {
        var stubReference = AttachAndReturnSnapshotState();
        for (var attempt = 0; attempt < 20 && stubReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        await Assert.That(stubReference.IsAlive).IsFalse();
        using var next = RentSnapshotState(8703);
        await Assert.That(next.ResolvedMethod.HasValue).IsFalse();
    }

    [Test]
    public async Task StreamObserverSnapshotShouldNotRootTheResolvedStub()
    {
        var (observer, stubReference) = CreateScalarStreamObserver();
        for (var attempt = 0; attempt < 20 && stubReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        await Assert.That(stubReference.IsAlive).IsFalse();
        await Assert.That(observer.Method.Kind).IsEqualTo(RpcMethodKind.ServerStreaming);
        await Assert.That(observer.Method.MethodTimeout).IsEqualTo(TimeSpan.FromSeconds(17));
        GC.KeepAlive(observer);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndReturnSnapshotState()
    {
        var stub = new SnapshotStreamStub(TimeSpan.FromSeconds(17), true);
        var registration = ServiceRegistration.CreateSingleton(typeof(object), stub, new object(), ownsService: false);
        var resolved = new ResolvedMethodCall(registration, SnapshotStreamStub.MethodId,
            stub.ResolveMethodShape(SnapshotStreamStub.MethodId));
        var state = RentSnapshotState(8702);
        state.AttachResolvedMethod(in resolved);
        var lease = state.CaptureLease(8702);
        Ensure(lease.TryAcquire(), "active state must provide a generation-checked use lease");
        state.Dispose();
        Ensure(ReferenceEquals(state.ResolvedMethod!.Value.Registration, registration),
            "the original registration must survive until the final use lease releases it");
        lease.ReleaseUse();
        // Keep no registration or stub alive outside this non-inlined frame. The pool itself is
        // strongly rooted, so collection proves ReturnCore cleared the old generation's facts.
        return new WeakReference(stub);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SharpLinkServerInvocationContext Observer, WeakReference Stub) CreateScalarStreamObserver()
    {
        var stub = new SnapshotStreamStub(TimeSpan.FromSeconds(17), true);
        var shape = stub.ResolveMethodShape(SnapshotStreamStub.MethodId);
        var observer = new SharpLinkServerInvocationContext(stub, SnapshotStreamStub.MethodId, shape,
            8704, "stream-observer", null, null, null, default, TimeProvider.System, null, CancellationToken.None);
        Ensure(stub.ResolveCount == 1 && stub.DescribeCount == 1, "observer must project already-resolved facts once");
        return (observer, new WeakReference(stub));
    }

    private static ServerCallCancellationState RentSnapshotState(long id)
        => ServerCallCancellationState.Rent(id, default, TimeProvider.System,
            CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: true);

    private sealed class SnapshotExceptionMapper : IRpcExceptionMapper
    {
        internal SharpLinkServerInvocationContext? Context { get; private set; }
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            Context = context;
            return new SharpLinkException(SharpLinkErrorCode.Internal, "mapped stream failure", exception);
        }
    }

    private sealed class SuspendedFailingSnapshotStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int EnumeratorCalls { get; private set; }
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            EnumeratorCalls++;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            Entered.TrySetResult();
            return new ValueTask<bool>(_next.Task);
        }
        internal void Fail() => _next.TrySetException(new InvalidOperationException("original stream failed"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SnapshotStreamStub(TimeSpan timeout, bool idempotent) : IRpcStub
    {
        internal const long MethodId = 870;
        internal int ResolveCount { get; private set; }
        internal int DescribeCount { get; private set; }
        public long InterfaceHash => 87;
        public RpcMethodShape ResolveMethodShape(long methodHash)
        {
            ResolveCount++;
            return methodHash == MethodId
                ? new RpcMethodShape(RpcMethodKind.ServerStreaming, 0, true, true,
                    hasMethodTimeout: true, isIdempotent: idempotent, hasMethodTimeoutValue: true)
                : RpcMethodShape.UnknownMethod;
        }
        public void DescribeMethod(long methodHash, RpcMethodShape shape, out RpcMethodDescriptor descriptor)
        {
            DescribeCount++;
            descriptor = RpcMethodDescriptor.FromShape(InterfaceHash, methodHash, shape, timeout);
        }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => throw new NotSupportedException();
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => throw new NotSupportedException();
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
