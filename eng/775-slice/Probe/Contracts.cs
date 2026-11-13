using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.BindingSlice;

[RpcContract]
public interface ISliceRpc : IService
{
    [NonCancellable] ValueTask<int> PingAsync();
    [NonCancellable] ValueTask<int> EchoAsync(SlicePayload value);
    [Oneway, NonCancellable] ValueTask NotifyAsync(int value);
    [NonCancellable] ValueTask<string> SessionAsync();
    [NonCancellable] ValueTask<string> MetadataAsync();
    ValueTask<int> HoldAsync(CancellationToken cancellationToken);
}

[RpcCodec(typeof(SlicePayloadCodec))]
public readonly record struct SlicePayload(int Value);

[RpcCodecSemanticIdentity(0x77501UL, 0x77502UL)]
public sealed class SlicePayloadCodec : IRpcCodec<SlicePayload>
{
    internal static int Serialized;
    internal static int Deserialized;
    public void Serialize(in SlicePayload value, IBufferWriter<byte> writer)
    {
        Interlocked.Increment(ref Serialized);
        BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value.Value);
        writer.Advance(4);
    }
    public SlicePayload Deserialize(in ReadOnlySequence<byte> bytes)
    {
        Interlocked.Increment(ref Deserialized);
        var reader = new SequenceReader<byte>(bytes);
        if (!reader.TryReadLittleEndian(out int value)) throw new InvalidDataException("Truncated payload.");
        return new SlicePayload(value);
    }
}

[RpcService]
public sealed class SliceService : ISliceRpc
{
    internal readonly TaskCompletionSource<int> Notification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<int> QueuedNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Holding;
    internal int Activations;
    internal int Invocations;
    public ValueTask<int> PingAsync() { Interlocked.Increment(ref Invocations); return ValueTask.FromResult(42); }
    public ValueTask<int> EchoAsync(SlicePayload value)
    { Interlocked.Increment(ref Invocations); return ValueTask.FromResult(value.Value); }
    public ValueTask NotifyAsync(int value)
    { Interlocked.Increment(ref Invocations); if (value == 17) Notification.TrySetResult(value); else QueuedNotification.TrySetResult(value); return ValueTask.CompletedTask; }
    public ValueTask<string> SessionAsync()
        => ValueTask.FromResult(SharpLinkCallContext.Current!.SessionId);
    public ValueTask<string> MetadataAsync()
        => ValueTask.FromResult(SharpLinkCallContext.Current?.Metadata?.FirstOrDefault().Value ?? string.Empty);
    public async ValueTask<int> HoldAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref Holding, 1);
        Held.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        finally { Volatile.Write(ref Holding, 0); Exited.TrySetResult(); }
    }
}
