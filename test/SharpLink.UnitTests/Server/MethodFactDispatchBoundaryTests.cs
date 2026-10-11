using System.IO.Pipelines;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    public Task UnknownMethodFactsShouldReturnUnimplementedWithoutInvoking()
        => VerifyRejectedMethodFactsAsync(RpcMethodShape.UnknownMethod, SharpLinkErrorCode.Unimplemented);

    [Test]
    public Task InvalidMethodFactsShouldReturnInternalWithoutInvoking()
        => VerifyRejectedMethodFactsAsync(default, SharpLinkErrorCode.Internal);

    private static async Task VerifyRejectedMethodFactsAsync(RpcMethodShape shape, SharpLinkErrorCode error)
    {
        var output = new Pipe();
        var stub = new RejectedMethodFactStub(shape);
        await using var harness = new ServerDispatchHarness(stub, output.Writer, maxSendQueueBytes: 4096);
        await harness.Dispatch(71, ProtocolV2FrameFlags.HasReturn).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        EnsureResponseFrame(read.Buffer, harness.Session.RuntimeContext.Protocol, 71, error, null);
        output.Reader.AdvanceTo(read.Buffer.End);
        Ensure(stub.ResolveCount == 1 && stub.InvokeCount == 0,
            "rejected method facts must be resolved once and never reach user code");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "rejected method facts must not retain admission capacity");
        await output.Reader.CompleteAsync();
    }

    private sealed class RejectedMethodFactStub(RpcMethodShape shape) : IRpcStub
    {
        public long InterfaceHash => 8;
        internal int ResolveCount { get; private set; }
        internal int InvokeCount { get; private set; }
        public RpcMethodShape ResolveMethodShape(long methodHash)
        {
            ResolveCount++;
            return shape;
        }
        private ValueTask Invoked()
        {
            InvokeCount++;
            throw new InvalidOperationException("Rejected facts reached invocation.");
        }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args) => Invoked();
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Invoked();
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => Invoked();
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken) => Invoked();
    }
}
