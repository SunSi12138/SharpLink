using System.Buffers;
using SharpLink.Abstractions;
// Exact negative consumer: the previous narrow invocation ABI must not compile in the new profile.
public sealed class OldStub : IRpcStub
{
    public long InterfaceHash => 1;
    public bool TryGetMethodDescriptor(long id, out RpcMethodDescriptor method) { method = default; return false; }
    public bool SupportsCancellation(long id) => true;
    public ValueTask InvokeNoReturnAsync(object s, IRpcGeneratedServerBridge b, long m, long id, ReadOnlySequence<byte> a) => default;
    public ValueTask InvokeNoReturnCancellableAsync(object s, IRpcGeneratedServerBridge b, long m, long id, ReadOnlySequence<byte> a, CancellationToken ct) => default;
    public ValueTask InvokeAsync(object s, IRpcGeneratedServerBridge b, long m, long id, ReadOnlySequence<byte> a, IBufferWriter<byte> o) => default;
    public ValueTask InvokeCancellableAsync(object s, IRpcGeneratedServerBridge b, long m, long id, ReadOnlySequence<byte> a, IBufferWriter<byte> o, CancellationToken ct) => default;
}
