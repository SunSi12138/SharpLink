using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
// Negative compile oracle: the previous optional resolver and MethodId-only invocation ABI cannot satisfy API6.
public sealed class OldStub : IRpcStub
{
    public long InterfaceHash => 9;
    public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;
    public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => ValueTask.CompletedTask;
    public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
