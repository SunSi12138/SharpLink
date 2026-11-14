using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcContract]
public interface IOneWayDrainSingleton : IService
{
    [Oneway] ValueTask NotifyStreamAsync(IAsyncEnumerable<DrainItem> values, CancellationToken cancellationToken);
    ValueTask ReceiveStreamAsync(IAsyncEnumerable<DrainItem> values, CancellationToken cancellationToken);
    [Oneway] ValueTask NotifyAsync(CancellationToken cancellationToken);
}

[RpcContract]
public interface IOneWayDrainCall : IService
{
    [Oneway] ValueTask NotifyStreamAsync(IAsyncEnumerable<DrainItem> values, CancellationToken cancellationToken);
    ValueTask ReceiveStreamAsync(IAsyncEnumerable<DrainItem> values, CancellationToken cancellationToken);
    [Oneway] ValueTask NotifyAsync(CancellationToken cancellationToken);
}

[RpcCodec(typeof(DrainItemCodec))]
public readonly record struct DrainItem(int Value);

[RpcCodecSemanticIdentity(0x70ebf451385b4721UL, 0x8b17405e911fac36UL)]
public sealed class DrainItemCodec : IRpcCodec<DrainItem>
{
    private static TaskCompletionSource _decodeEntered = NewSignal();
    private static TaskCompletionSource _decodeRelease = NewSignal();
    private static int _activeDecodes;
    public static Task DecodeEntered => _decodeEntered.Task;
    public static int ActiveDecodes => Volatile.Read(ref _activeDecodes);
    public static void Reset()
    {
        _decodeEntered = NewSignal();
        _decodeRelease = NewSignal();
        Volatile.Write(ref _activeDecodes, 0);
    }
    public static void ReleaseDecode() => _decodeRelease.TrySetResult();
    public static async IAsyncEnumerable<DrainItem> Items()
    {
        yield return new DrainItem(7);
        await Task.CompletedTask;
    }
    public void Serialize(in DrainItem value, IBufferWriter<byte> buffer)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(sizeof(int)), value.Value);
        buffer.Advance(sizeof(int));
    }
    public DrainItem Deserialize(in ReadOnlySequence<byte> buffer)
    {
        Interlocked.Increment(ref _activeDecodes);
        _decodeEntered.TrySetResult();
        try
        {
            _decodeRelease.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            buffer.CopyTo(bytes);
            return new DrainItem(BinaryPrimitives.ReadInt32LittleEndian(bytes));
        }
        finally { Interlocked.Decrement(ref _activeDecodes); }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

