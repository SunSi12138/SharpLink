using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcContract]
public interface IPhysicalTailOwnershipService : IService
{
    IAsyncEnumerable<int> BlockPhysicalTailAsync(CancellationToken cancellationToken);

    // No method timeout: the control supplies one original inherited business deadline.
    IAsyncEnumerable<int> BlockDuplexPhysicalTailAsync(
        IAsyncEnumerable<PhysicalTailPayload> incoming, CancellationToken cancellationToken);
}

[RpcCodec(typeof(PhysicalTailPayloadCodec))]
public sealed class PhysicalTailPayload
{
    public int Value { get; set; }
}

// Four-byte little-endian payload; the fixture gate does not change wire semantics.
[RpcCodecSemanticIdentity(0x775D020031100001UL, 0x775D020031100002UL)]
public sealed class PhysicalTailPayloadCodec : IRpcCodec<PhysicalTailPayload>
{
    private static TaskCompletionSource _decodeEntered = NewGate(), _decodeRelease = NewGate();
    private static bool _holdDecode;
    private static int _decodes;
    public static Task DecodeEntered => _decodeEntered.Task;
    public static int Decodes => Volatile.Read(ref _decodes);
    public static void Reset(bool holdDecode)
    {
        _decodeEntered = NewGate(); _decodeRelease = NewGate();
        _holdDecode = holdDecode; _decodes = 0;
    }
    public static void ReleaseDecode() => _decodeRelease.TrySetResult();
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Serialize(in PhysicalTailPayload value, IBufferWriter<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(value);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(sizeof(int)), value.Value);
        buffer.Advance(sizeof(int));
    }

    public PhysicalTailPayload Deserialize(in ReadOnlySequence<byte> buffer)
    {
        _decodeEntered.TrySetResult();
        if (_holdDecode)
            _decodeRelease.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        if (buffer.Length != sizeof(int)) throw new InvalidDataException("Physical-tail payload must be four bytes.");
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        buffer.CopyTo(bytes);
        Interlocked.Increment(ref _decodes);
        return new PhysicalTailPayload { Value = BinaryPrimitives.ReadInt32LittleEndian(bytes) };
    }
}
