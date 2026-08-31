using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SharpLink.UnitTests;

public class BuiltinCollectionWireStrategyTests
{
    [Test]
    public async Task DateTimeCollectionShouldUseRawElementLayoutRatherThanScalarCodec()
    {
        using var provider = new RpcCodecProvider(null, new Dictionary<Type, IRpcCodec>());
        var scalarCodec = provider.GetCodec<DateTime>();
        var arrayCodec = provider.GetCodec<DateTime[]>();
        Ensure(scalarCodec.GetType().Name == "DateTimeCodec",
            "DateTime scalar must resolve its semantic scalar Codec");
        Ensure(arrayCodec.GetType().Name.StartsWith("BlitArrayCodec", StringComparison.Ordinal),
            "DateTime[] must resolve the builtin raw blit collection strategy");

        var value = new DateTime(2026, 8, 31, 13, 45, 12, DateTimeKind.Local);
        var scalarBytes = Serialize(scalarCodec, value);
        Ensure(scalarBytes.Length == sizeof(long), "DateTime scalar wire size");
        Ensure(BinaryPrimitives.ReadInt64LittleEndian(scalarBytes) == value.ToBinary(),
            "DateTime scalar wire must encode ToBinary semantics");

        var values = new[] { value };
        var arrayBytes = Serialize(arrayCodec, values);
        Ensure(BinaryPrimitives.ReadInt32LittleEndian(arrayBytes) == 1, "DateTime[] element count");
        var raw = MemoryMarshal.AsBytes(values.AsSpan());
        Ensure(arrayBytes.AsSpan(sizeof(int)).SequenceEqual(raw),
            "DateTime[] payload must contain raw DateTime element memory rather than scalar DateTimeCodec bytes");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DateTimeOffsetCollectionShouldUseNormalizedRaw16RatherThanScalarCodec()
    {
        using var provider = new RpcCodecProvider(null, new Dictionary<Type, IRpcCodec>());
        var scalarCodec = provider.GetCodec<DateTimeOffset>();
        var arrayCodec = provider.GetCodec<DateTimeOffset[]>();
        Ensure(scalarCodec.GetType().Name == "DateTimeOffsetCodec",
            "DateTimeOffset scalar must resolve its logical scalar Codec");
        Ensure(arrayCodec.GetType().Name == "DateTimeOffsetArrayCodec",
            "DateTimeOffset[] must resolve its dedicated normalized raw collection strategy");

        var value = new DateTimeOffset(2026, 8, 31, 13, 45, 12, TimeSpan.FromHours(5.5));
        var scalarBytes = Serialize(scalarCodec, value);
        Ensure(scalarBytes.Length == 10,
            "DateTimeOffset scalar wire must remain the 10-byte ticks+offset representation");

        var arrayBytes = Serialize(arrayCodec, new[] { value });
        Ensure(BinaryPrimitives.ReadInt32LittleEndian(arrayBytes) == 1, "DateTimeOffset[] element count");
        var payload = arrayBytes.AsSpan(sizeof(int));
        Ensure(payload.Length == 16, "DateTimeOffset[] must use a 16-byte element representation");
        Ensure(payload.Slice(sizeof(short), 6).IndexOfAnyExcept((byte)0) < 0,
            "DateTimeOffset[] must normalize bytes 2..7 to zero independently of scalar Codec semantics");
        Ensure(BinaryPrimitives.ReadInt16LittleEndian(payload) == (short)value.Offset.TotalMinutes,
            "DateTimeOffset[] raw element offset minutes");
        Ensure(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(sizeof(long))) == value.UtcTicks,
            "DateTimeOffset[] raw element UTC ticks");
        await Task.CompletedTask;
    }

    private static byte[] Serialize<T>(IRpcCodec<T> codec, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(in value, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
