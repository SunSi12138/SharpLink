

namespace SharpLink.UnitTests.Protocol;

public class PacketHelperTests
{
    [Test]
    public void DecodeSimplePacket()
    {
        var writer = BufferWriterPool.Get();
        try
        {
            writer.WritePacket(PacketType.Heartbeat, PacketFlags.None, 123);
            ReadOnlySequence<byte> seq = new(writer.WrittenMemory);

            var ok = PacketHelper.TryReadMessage(ref seq, out var header, out var payload);

            Ensure(ok, "decode ok");
            Ensure(header.Type == PacketType.Heartbeat, "type");
            Ensure(header.RequestId == 123, "request id");
            Ensure(payload.Length == 0, "payload");
        }
        finally
        {
            BufferWriterPool.Return(writer);
        }
    }

    [Test]
    public void DecodeCancellableFlag()
    {
        var writer = BufferWriterPool.Get();
        try
        {
            writer.WritePacket(PacketType.RpcCall, PacketFlags.IsCancellable, 99);
            ReadOnlySequence<byte> seq = new(writer.WrittenMemory);

            var ok = PacketHelper.TryReadMessage(ref seq, out var header, out _);

            Ensure(ok, "decode cancellable packet");
            Ensure((header.Flags&PacketFlags.IsCancellable)!=0, "cancellable flag");
        }
        finally
        {
            BufferWriterPool.Return(writer);
        }
    }

    [Test]
    public void ReturnFalseOnHalfPacket()
    {
        var writer = BufferWriterPool.Get();
        try
        {
            writer.WritePacket(PacketType.Heartbeat, PacketFlags.None, 1);
            var half = writer.WrittenMemory[..(ProtocolConstants.HeaderBytes - 1)];
            ReadOnlySequence<byte> seq = new(half);

            var ok = PacketHelper.TryReadMessage(ref seq, out _, out _);

            Ensure(!ok, "half packet");
        }
        finally
        {
            BufferWriterPool.Return(writer);
        }
    }

    [Test]
    public void ThrowOnInvalidMagic()
    {
        var bytes = new byte[ProtocolConstants.HeaderBytes];
        bytes[0] = 0x77;
        ReadOnlySequence<byte> seq = new(bytes);

        try
        {
            _ = PacketHelper.TryReadMessage(ref seq, out _, out _);
            throw new Exception("expected InvalidDataException");
        }
        catch (InvalidDataException)
        {
        }
    }

    [Test]
    public void DecodeTwoPacketsInSequence()
    {
        var writer = BufferWriterPool.Get();
        try
        {
            writer.WritePacket(PacketType.Heartbeat, PacketFlags.None, 1);
            writer.WritePacket(PacketType.RpcResponse, PacketFlags.IsError, 2);
            ReadOnlySequence<byte> seq = new(writer.WrittenMemory);

            var ok1 = PacketHelper.TryReadMessage(ref seq, out var h1, out _);
            var ok2 = PacketHelper.TryReadMessage(ref seq, out var h2, out _);

            Ensure(ok1, "first decode");
            Ensure(ok2, "second decode");
            Ensure(h1 is { RequestId: 1, Type: PacketType.Heartbeat }, "first packet");
            Ensure(h2 is { RequestId: 2, Type: PacketType.RpcResponse }, "second packet");
            Ensure(seq.Length == 0, "buffer fully consumed");
        }
        finally
        {
            BufferWriterPool.Return(writer);
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }
}
