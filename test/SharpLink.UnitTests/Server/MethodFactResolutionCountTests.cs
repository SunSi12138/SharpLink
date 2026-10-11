using System.Linq;
using System.Buffers;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    private const long CountingMethodHash = 1;

    [Test]
    public async Task ServerShouldResolveMethodShapeExactlyOnceForATwoWayRpc()
    {
        var stub = new CountingMethodFactStub(RpcMethodKind.Unary);
        var output = new Pipe().Writer;
        await using var harness = new ServerDispatchHarness(stub, output, maxSendQueueBytes: 4096);

        await harness.Dispatch(1, ProtocolV2FrameFlags.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(stub.ResolveCount).IsEqualTo(1);
    }

    [Test]
    public async Task ServerShouldResolveMethodShapeExactlyOnceForAOneWayRpc()
    {
        var stub = new CountingMethodFactStub(RpcMethodKind.OneWay);
        var output = new Pipe().Writer;
        await using var harness = new ServerDispatchHarness(stub, output, maxSendQueueBytes: 4096);

        await harness.Dispatch(1, ProtocolV2FrameFlags.OneWay)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(stub.ResolveCount).IsEqualTo(1);
    }

    [Test]
    public async Task ServerShouldNotProjectADescriptorWhenNothingConsumesIt()
    {
        var stub = new CountingMethodFactStub(RpcMethodKind.Unary);
        var output = new Pipe().Writer;
        await using var harness = new ServerDispatchHarness(stub, output, maxSendQueueBytes: 4096);

        await harness.Dispatch(1, ProtocolV2FrameFlags.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        // No server interceptor is registered, so no observer reads
        // SharpLinkServerInvocationContext.Method and no descriptor is materialized.
        await Assert.That(stub.DescribeCount).IsEqualTo(0);
    }

    [Test]
    public async Task FailedCallShouldProjectOnceForExceptionMapperWithoutResolvingAgain()
    {
        var stub = new CountingMethodFactStub(RpcMethodKind.Unary, throwOnInvoke: true);
        var output = new Pipe().Writer;
        await using var harness = new ServerDispatchHarness(stub, output, maxSendQueueBytes: 4096);
        await harness.Dispatch(1, ProtocolV2FrameFlags.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(stub.ResolveCount).IsEqualTo(1);
        await Assert.That(stub.DescribeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ServerShouldNotAskForAStreamShapeOnATwoWayRpcWithoutClientStreams()
    {
        var stub = new CountingMethodFactStub(RpcMethodKind.Unary);
        var output = new Pipe().Writer;
        await using var harness = new ServerDispatchHarness(stub, output, maxSendQueueBytes: 4096);

        await harness.Dispatch(1, ProtocolV2FrameFlags.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(stub.RequestedHashes).IsEquivalentTo(new[] { CountingMethodHash });
    }

    [Test]
    public async Task ObserverContextShouldProjectOnceAndRetainOnlyScalarMethodFacts()
    {
        var (context, stubReference) = CreateObservedMethodContext();
        for (var attempt = 0; attempt < 20 && stubReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        await Assert.That(stubReference.IsAlive).IsFalse();
        await Assert.That(context.Method.ContractId).IsEqualTo(8L);
        await Assert.That(context.Method.MethodId).IsEqualTo(CountingMethodHash);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            if (context.Method.ContractId != 8L || context.Method.MethodId != CountingMethodHash)
                throw new InvalidOperationException("immutable observer metadata changed");
        })));
        GC.KeepAlive(context);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (SharpLinkServerInvocationContext Context, WeakReference Stub) CreateObservedMethodContext()
    {
        var stub = new CountingMethodFactStub(RpcMethodKind.Unary);
        var shape = stub.ResolveMethodShape(CountingMethodHash);
        var context = new SharpLinkServerInvocationContext(
            stub, CountingMethodHash, shape, 1, "observer", null, null, null,
            default, TimeProvider.System, null, CancellationToken.None);
        if (stub.ResolveCount != 1 || stub.DescribeCount != 1)
            throw new InvalidOperationException("observer context must reuse the resolved shape and project once");
        return (context, new WeakReference(stub));
    }

    /// <summary>Counts every generated method-fact resolution a single dispatch performs.</summary>
    private sealed class CountingMethodFactStub(RpcMethodKind kind, bool throwOnInvoke = false) : IRpcStub
    {
        private int _resolveCount;
        private int _describeCount;
        private readonly List<long> _requestedHashes = [];

        internal int ResolveCount => Volatile.Read(ref _resolveCount);

        internal int DescribeCount => Volatile.Read(ref _describeCount);

        internal IReadOnlyList<long> RequestedHashes
        {
            get
            {
                lock (_requestedHashes)
                    return _requestedHashes.ToArray();
            }
        }

        public long InterfaceHash => 8;

        public RpcMethodShape ResolveMethodShape(long methodHash)
        {
            Interlocked.Increment(ref _resolveCount);
            lock (_requestedHashes)
                _requestedHashes.Add(methodHash);
            return methodHash == CountingMethodHash
                ? new RpcMethodShape(kind, 0, supportsCancellation: true, hasResponsePayload: true)
                : RpcMethodShape.UnknownMethod;
        }

        public void DescribeMethod(long methodHash, RpcMethodShape shape, out RpcMethodDescriptor descriptor)
        {
            Interlocked.Increment(ref _describeCount);
            descriptor = RpcMethodDescriptor.FromShape(InterfaceHash, methodHash, shape);
        }

        public ValueTask InvokeNoReturnAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args)
            => throwOnInvoke
                ? ValueTask.FromException(new InvalidOperationException("counting handler failure"))
                : ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            CancellationToken cancellationToken)
            => throwOnInvoke
                ? ValueTask.FromException(new InvalidOperationException("counting handler failure"))
                : ValueTask.CompletedTask;

        public ValueTask InvokeAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output)
            => throwOnInvoke
                ? ValueTask.FromException(new InvalidOperationException("counting handler failure"))
                : ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output,
            CancellationToken cancellationToken)
            => throwOnInvoke
                ? ValueTask.FromException(new InvalidOperationException("counting handler failure"))
                : ValueTask.CompletedTask;
    }
}
