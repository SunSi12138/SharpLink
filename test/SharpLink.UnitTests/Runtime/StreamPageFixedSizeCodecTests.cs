using System.Buffers.Binary;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpLink.UnitTests.Runtime;

public class StreamPageFixedSizeCodecTests
{
    [Test]
    public void EveryTrustedCodecShouldRetainItsExactSerializedBytesAndWidth()
    {
        var covered = new HashSet<Type>();
        AssertWire(BoolCodec.Instance, false, [0], covered);
        AssertWire(BoolCodec.Instance, true, [1], covered);
        AssertWire<bool?>(NullableBoolCodec.Instance, null, [0xFF], covered);
        AssertWire<bool?>(NullableBoolCodec.Instance, false, [0], covered);
        AssertWire<bool?>(NullableBoolCodec.Instance, true, [1], covered);

        AssertPair<byte>(ByteCodec.Instance, NullableByteCodec.Instance, 1, [0, 1, byte.MaxValue], covered);
        AssertPair<sbyte>(SByteCodec.Instance, NullableSByteCodec.Instance, 1, [sbyte.MinValue, 0, sbyte.MaxValue], covered);
        AssertPair(CharCodec.Instance, NullableCharCodec.Instance, 2, ['\0', '汉', char.MaxValue], covered);
        AssertPair<short>(Int16Codec.Instance, NullableInt16Codec.Instance, 2, [short.MinValue, 0, short.MaxValue], covered);
        AssertPair<ushort>(UInt16Codec.Instance, NullableUInt16Codec.Instance, 2, [0, 1, ushort.MaxValue], covered);
        AssertPair(Int32Codec.Instance, NullableInt32Codec.Instance, 4, [int.MinValue, 0, int.MaxValue], covered);
        AssertPair<uint>(UInt32Codec.Instance, NullableUInt32Codec.Instance, 4, [0, 1, uint.MaxValue], covered);
        AssertPair(Int64Codec.Instance, NullableInt64Codec.Instance, 8, [long.MinValue, 0, long.MaxValue], covered);
        AssertPair<ulong>(UInt64Codec.Instance, NullableUInt64Codec.Instance, 8, [0, 1, ulong.MaxValue], covered);
        AssertPair(Int128Codec.Instance, NullableInt128Codec.Instance, 16, [Int128.MinValue, 0, Int128.MaxValue], covered);
        AssertPair<UInt128>(UInt128Codec.Instance, NullableUInt128Codec.Instance, 16, [0, 1, UInt128.MaxValue], covered);
        AssertPair(FloatCodec.Instance, NullableFloatCodec.Instance, 4,
            [0f, -0f, float.NegativeInfinity, float.PositiveInfinity, BitConverter.Int32BitsToSingle(0x7FC01234)], covered);
        AssertPair(DoubleCodec.Instance, NullableDoubleCodec.Instance, 8,
            [0d, -0d, double.NegativeInfinity, double.PositiveInfinity, BitConverter.Int64BitsToDouble(0x7FF8000000001234)], covered);
        AssertPair(HalfCodec.Instance, NullableHalfCodec.Instance, 2,
            [(Half)0, BitConverter.UInt16BitsToHalf(0x8000), Half.NegativeInfinity, Half.PositiveInfinity,
                BitConverter.UInt16BitsToHalf(0x7E12)], covered);
        AssertPair(DecimalCodec.Instance, NullableDecimalCodec.Instance, 16,
            [decimal.MinValue, 0m, decimal.MaxValue, 1.2300m, new decimal(0, 0, 0, true, 3)], covered);
        AssertPair(GuidCodec.Instance, NullableGuidCodec.Instance, 16,
            [Guid.Empty, Guid.Parse("12345678-9abc-def0-1234-56789abcdef0")], covered);
        AssertPair(DateOnlyCodec.Instance, NullableDateOnlyCodec.Instance, 4,
            [DateOnly.MinValue, new DateOnly(2026, 10, 29), DateOnly.MaxValue], covered,
            static value => RawBytes(value.DayNumber));
        AssertPair(DateTimeCodec.Instance, NullableDateTimeCodec.Instance, 8,
            [DateTime.MinValue, DateTime.MaxValue, new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Local)], covered);
        AssertPair(DateTimeOffsetCodec.Instance, NullableDateTimeOffsetCodec.Instance, 10,
            [DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
                new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.FromHours(-5))], covered,
            DateTimeOffsetBytes);
        AssertPair(TimeOnlyCodec.Instance, NullableTimeOnlyCodec.Instance, 8,
            [TimeOnly.MinValue, new TimeOnly(12, 34, 56), TimeOnly.MaxValue], covered);
        AssertPair(TimeSpanCodec.Instance, NullableTimeSpanCodec.Instance, 8,
            [TimeSpan.MinValue, TimeSpan.Zero, TimeSpan.MaxValue], covered);
        AssertPair(IndexCodec.Instance, NullableIndexCodec.Instance, 4,
            [Index.Start, Index.End, new Index(123), new Index(456, fromEnd: true)], covered);
        AssertPair(RangeCodec.Instance, NullableRangeCodec.Instance, 8,
            [Range.All, new Range(new Index(1), new Index(2, fromEnd: true))], covered);
        AssertPair(RuneCodec.Instance, NullableRuneCodec.Instance, 4,
            [new Rune(0), new Rune('界'), new Rune(0x10FFFF)], covered);

        var capabilityTypes = typeof(IStreamPageFixedSizeCodec).Assembly.GetTypes()
            .Where(type => type.IsClass && typeof(IStreamPageFixedSizeCodec).IsAssignableFrom(type))
            .ToArray();
        Ensure(covered.Count == 50, "all 25 primitive and nullable codec pairs must be covered");
        Ensure(covered.SetEquals(capabilityTypes), "every capability implementation must have an exact-wire fixture");
        Ensure(capabilityTypes.All(type => type.IsSealed && !type.IsGenericType && !type.IsPublic),
            "the stream capability must remain restricted to sealed internal non-generic runtime codecs");
    }

    [Test]
    public void SelectedCustomCodecAndUnreviewedCodecsShouldNotAdvertiseTheCapability()
    {
        var customCodec = new CustomValueCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec<CustomValue>(customCodec)
            .Build(includeGeneratedAssemblyCatalog: false);
        var selected = context.Codecs.GetCodec<CustomValue>();
        Ensure(ReferenceEquals(selected, customCodec), "the selected custom codec must not be replaced");
        Ensure(selected is not IStreamPageFixedSizeCodec, "a custom codec must remain on the ordinary stream path");

        object[] unreviewed =
        [
            StringCodec.Instance,
            EnumCodec<DayOfWeek>.Instance,
            UnsafeBlitCodec<int>.Instance,
            BlitArrayCodec<int>.Instance,
            RpcEmptyRequestCodec.Instance,
            new WrappedInt32Codec()
        ];
        Ensure(unreviewed.All(codec => codec is not IStreamPageFixedSizeCodec),
            "variable, enum, unsafe-blit, RPC and custom wrappers must not inherit the capability");
    }

    private static void AssertPair<T>(IRpcCodec<T> codec, IRpcCodec<T?> nullableCodec, int size,
        T[] samples, HashSet<Type> covered, Func<T, byte[]>? expectedBytes = null) where T : unmanaged
    {
        Ensure(ReferenceEquals(RpcSessionTestFixture.RuntimeContext.Codecs.GetCodec<T>(), codec),
            $"{typeof(T)} must retain its selected built-in codec");
        Ensure(ReferenceEquals(RpcSessionTestFixture.RuntimeContext.Codecs.GetCodec<T?>(), nullableCodec),
            $"{typeof(T?)} must retain its selected built-in codec");
        AssertWire<T?>(nullableCodec, null, new byte[size + 1], covered);
        foreach (var value in samples)
        {
            // These are the existing primitive wire representations, not a new page serializer.
            var expected = expectedBytes is null ? RawBytes(value) : expectedBytes(value);
            Ensure(expected.Length == size, $"{typeof(T)} fixture width");
            AssertWire(codec, value, expected, covered);
            var nullableExpected = new byte[size + 1];
            nullableExpected[0] = 1;
            expected.CopyTo(nullableExpected, 1);
            AssertWire<T?>(nullableCodec, value, nullableExpected, covered);
        }
    }

    private static void AssertWire<T>(IRpcCodec<T> codec, T value, byte[] expected, HashSet<Type> covered)
    {
        Ensure(codec is IStreamPageFixedSizeCodec, $"{codec.GetType().Name} capability");
        var capability = (IStreamPageFixedSizeCodec)codec;
        Ensure(capability.EncodedBytes == expected.Length, $"{codec.GetType().Name} advertised width");
        covered.Add(codec.GetType());

        var ordinary = new ArrayBufferWriter<byte>();
        codec.Serialize(value, ordinary);
        Ensure(ordinary.WrittenSpan.SequenceEqual(expected), $"{codec.GetType().Name} ordinary Serialize wire");

        // Poison a precisely sized destination so null bodies must still be fully initialized.
        var exact = new ExactWriter(capability.EncodedBytes);
        codec.Serialize(value, exact);
        Ensure(exact.WrittenCount == expected.Length, $"{codec.GetType().Name} exact destination length");
        Ensure(exact.Bytes.AsSpan().SequenceEqual(expected), $"{codec.GetType().Name} exact destination wire");
        var decoded = codec.Deserialize(new ReadOnlySequence<byte>(expected));
        var reencoded = new ArrayBufferWriter<byte>();
        codec.Serialize(decoded!, reencoded);
        Ensure(reencoded.WrittenSpan.SequenceEqual(expected), $"{codec.GetType().Name} bitwise round trip");
    }

    private static byte[] RawBytes<T>(T value) where T : unmanaged
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)).ToArray();

    private static byte[] DateTimeOffsetBytes(DateTimeOffset value)
    {
        var bytes = new byte[10];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value.Ticks);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), checked((short)value.Offset.TotalMinutes));
        return bytes;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ExactWriter(int size) : IBufferWriter<byte>
    {
        public byte[] Bytes { get; } = Enumerable.Repeat((byte)0xA5, size).ToArray();
        public int WrittenCount { get; private set; }

        public void Advance(int count)
        {
            Ensure(count >= 0 && count <= Bytes.Length - WrittenCount, "exact writer advance bounds");
            WrittenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint >= 0 && Math.Max(1, sizeHint) <= Bytes.Length - WrittenCount,
                "the codec must not request more than its advertised width");
            return Bytes.AsMemory(WrittenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    private readonly record struct CustomValue(int Value);

    private sealed class CustomValueCodec : IRpcCodec<CustomValue>
    {
        public void Serialize(in CustomValue value, IBufferWriter<byte> writer)
            => Int32Codec.Instance.Serialize(value.Value, writer);

        public CustomValue Deserialize(in ReadOnlySequence<byte> buffer)
            => new(Int32Codec.Instance.Deserialize(buffer));
    }

    private sealed class WrappedInt32Codec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> writer)
            => Int32Codec.Instance.Serialize(value, writer);

        public int Deserialize(in ReadOnlySequence<byte> buffer) => Int32Codec.Instance.Deserialize(buffer);
    }
}
