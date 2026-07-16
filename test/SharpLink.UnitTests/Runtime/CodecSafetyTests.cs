using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpLink.UnitTests.Runtime;

public class CodecSafetyTests
{
    [Test]
    public void FixedLengthCodecsShouldRoundTripSingleAndMultiSegmentAndRejectTruncation()
    {
        AssertFixedRoundTrip(true);
        AssertFixedRoundTrip<bool?>(false);
        AssertFixedRoundTrip<byte>(123);
        AssertFixedRoundTrip<byte?>(234);
        AssertFixedRoundTrip<sbyte>(-12);
        AssertFixedRoundTrip<sbyte?>(34);
        AssertFixedRoundTrip<short>(-1234);
        AssertFixedRoundTrip<short?>(2345);
        AssertFixedRoundTrip<ushort>(4567);
        AssertFixedRoundTrip<ushort?>(5678);
        AssertFixedRoundTrip('汉');
        AssertFixedRoundTrip<char?>('A');
        AssertFixedRoundTrip<int>(-1234567);
        AssertFixedRoundTrip<int?>(2345678);
        AssertFixedRoundTrip<uint>(3456789);
        AssertFixedRoundTrip<uint?>(4567890);
        AssertFixedRoundTrip<long>(-1234567890123);
        AssertFixedRoundTrip<long?>(2345678901234);
        AssertFixedRoundTrip<ulong>(3456789012345);
        AssertFixedRoundTrip<ulong?>(4567890123456);
        AssertFixedRoundTrip<Int128>(Int128.Parse("123456789012345678901234"));
        AssertFixedRoundTrip<Int128?>(Int128.Parse("-234567890123456789012345"));
        AssertFixedRoundTrip<UInt128>(UInt128.Parse("345678901234567890123456"));
        AssertFixedRoundTrip<UInt128?>(UInt128.Parse("456789012345678901234567"));
        AssertFixedRoundTrip(12.5f);
        AssertFixedRoundTrip<float?>(-3.25f);
        AssertFixedRoundTrip(123.5d);
        AssertFixedRoundTrip<double?>(-456.25d);
        AssertFixedRoundTrip((Half)1.5f);
        AssertFixedRoundTrip<Half?>((Half)(-2.5f));
        AssertFixedRoundTrip(123456.789m);
        AssertFixedRoundTrip<decimal?>(-98765.4321m);
        AssertFixedRoundTrip(Guid.Parse("9f183a2e-0121-4c82-9fa6-7069cfab1447"));
        AssertFixedRoundTrip<Guid?>(Guid.Parse("11d35724-1157-42c2-b15e-901955d5fc30"));
        AssertFixedRoundTrip(new DateOnly(2026, 7, 16));
        AssertFixedRoundTrip<DateOnly?>(new DateOnly(2000, 1, 2));
        AssertFixedRoundTrip(new DateTime(2026, 7, 16, 12, 34, 56, DateTimeKind.Utc));
        AssertFixedRoundTrip<DateTime?>(new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Local));
        AssertFixedRoundTrip(new DateTimeOffset(2026, 7, 16, 12, 34, 56, TimeSpan.FromHours(8)));
        AssertFixedRoundTrip<DateTimeOffset?>(new DateTimeOffset(2002, 3, 4, 5, 6, 7, TimeSpan.FromHours(-5)));
        AssertFixedRoundTrip(new TimeOnly(12, 34, 56));
        AssertFixedRoundTrip<TimeOnly?>(new TimeOnly(1, 2, 3));
        AssertFixedRoundTrip(TimeSpan.FromHours(123.5));
        AssertFixedRoundTrip<TimeSpan?>(TimeSpan.FromMilliseconds(-4567));
        AssertFixedRoundTrip(new Index(123, fromEnd: false));
        AssertFixedRoundTrip<Index?>(new Index(9, fromEnd: true));
        AssertFixedRoundTrip(new Range(new Index(1), new Index(2, fromEnd: true)));
        AssertFixedRoundTrip<Range?>(new Range(new Index(3), new Index(4)));
        AssertFixedRoundTrip(new Rune('界'));
        AssertFixedRoundTrip<Rune?>(new Rune('Z'));
    }

    [Test]
    public void FixedLengthCodecShouldHandleEmptyLeadingSegment()
    {
        var sequence = CreateSegmentedSequence([1], includeLeadingEmptySegment: true);
        Ensure(RpcCodec.Deserialize<bool>(sequence), "bool with empty leading segment");
    }

    [Test]
    public void NullableFixedLengthCodecsShouldRoundTripNull()
    {
        AssertFixedRoundTrip<bool?>(null);
        AssertFixedRoundTrip<byte?>(null);
        AssertFixedRoundTrip<sbyte?>(null);
        AssertFixedRoundTrip<short?>(null);
        AssertFixedRoundTrip<ushort?>(null);
        AssertFixedRoundTrip<char?>(null);
        AssertFixedRoundTrip<int?>(null);
        AssertFixedRoundTrip<uint?>(null);
        AssertFixedRoundTrip<long?>(null);
        AssertFixedRoundTrip<ulong?>(null);
        AssertFixedRoundTrip<Int128?>(null);
        AssertFixedRoundTrip<UInt128?>(null);
        AssertFixedRoundTrip<float?>(null);
        AssertFixedRoundTrip<double?>(null);
        AssertFixedRoundTrip<Half?>(null);
        AssertFixedRoundTrip<decimal?>(null);
        AssertFixedRoundTrip<Guid?>(null);
        AssertFixedRoundTrip<DateOnly?>(null);
        AssertFixedRoundTrip<DateTime?>(null);
        AssertFixedRoundTrip<DateTimeOffset?>(null);
        AssertFixedRoundTrip<TimeOnly?>(null);
        AssertFixedRoundTrip<TimeSpan?>(null);
        AssertFixedRoundTrip<Index?>(null);
        AssertFixedRoundTrip<Range?>(null);
        AssertFixedRoundTrip<Rune?>(null);
    }

    [Test]
    public void StringCodecShouldValidateLengthsAndDecodeAcrossSegments()
    {
        AssertVariableRoundTrip<string?>(null, static (left, right) => left == right);
        AssertVariableRoundTrip<string?>(string.Empty, static (left, right) => left == right);
        AssertVariableRoundTrip<string?>("SharpLink-汉字", static (left, right) => left == right);

        ExpectDataLoss(() => RpcCodec.Deserialize<string>(new ReadOnlySequence<byte>(Array.Empty<byte>())));
        ExpectDataLoss(() => RpcCodec.Deserialize<string>(new ReadOnlySequence<byte>(CreateLengthPrefix(-2))));
        ExpectDataLoss(() => RpcCodec.Deserialize<string>(new ReadOnlySequence<byte>(CreateLengthPrefix(3))));
        ExpectDataLoss(() => RpcCodec.Deserialize<string>(new ReadOnlySequence<byte>(CreateLengthPrefixedPayload(4, [1, 2]))));
    }

    [Test]
    public void BlitCollectionsShouldValidateLengthBeforeAllocationAndRoundTrip()
    {
        AssertSequenceRoundTrip<int[]>([1, 2, 3], static value => value);
        AssertSequenceRoundTrip<int[]>([], static value => value);
        AssertSequenceRoundTrip<List<int>>([4, 5, 6], static value => value);
        AssertSequenceRoundTrip<List<int>>([], static value => value);
        AssertSequenceRoundTrip<Memory<int>>(new[] { 7, 8 }.AsMemory(), static value => value.ToArray());
        AssertSequenceRoundTrip<Memory<int>>(Memory<int>.Empty, static value => value.ToArray());
        AssertSequenceRoundTrip<ReadOnlyMemory<int>>(new ReadOnlyMemory<int>([9, 10]), static value => value.ToArray());
        AssertSequenceRoundTrip<ReadOnlyMemory<int>>(ReadOnlyMemory<int>.Empty, static value => value.ToArray());
        AssertSequenceRoundTrip<ImmutableArray<int>>(ImmutableArray.Create(11, 12), static value => value.AsEnumerable());
        AssertSequenceRoundTrip<ImmutableArray<int>>(ImmutableArray<int>.Empty, static value => value.AsEnumerable());

        Ensure(RpcCodec.Deserialize<int[]?>(new ReadOnlySequence<byte>(CreateLengthPrefix(-1))) is null, "null array");
        Ensure(RpcCodec.Deserialize<List<int>?>(new ReadOnlySequence<byte>(CreateLengthPrefix(-1))) is null, "null list");
        Ensure(RpcCodec.Deserialize<ImmutableArray<int>>(new ReadOnlySequence<byte>(CreateLengthPrefix(-1))).IsDefault, "default immutable array");
        Ensure(RpcCodec.Deserialize<int[]>(new ReadOnlySequence<byte>(CreateLengthPrefix(0))) is { Length: 0 }, "empty array");
        ExpectDataLoss(() => RpcCodec.Deserialize<int[]>(new ReadOnlySequence<byte>(CreateLengthPrefix(-2))));
        ExpectDataLoss(() => RpcCodec.Deserialize<int[]>(new ReadOnlySequence<byte>(CreateLengthPrefix(int.MaxValue))));
        ExpectDataLoss(() => RpcCodec.Deserialize<int[]>(new ReadOnlySequence<byte>(CreateLengthPrefixedPayload(2, [1, 2, 3, 4]))));
    }

    [Test]
    public void MemoryPackCodecShouldWrapMalformedPayloadAsDataLoss()
    {
        ExpectDataLoss(() => MemoryPackCodec<int>.Instance.Deserialize(new ReadOnlySequence<byte>(Array.Empty<byte>())));
    }

    [Test]
    public void SemanticFixedCodecsShouldMapInvalidValuesToDataLoss()
    {
        ExpectDataLoss(() => RpcCodec.Deserialize<DateOnly>(Int32Sequence(int.MaxValue)));
        ExpectDataLoss(() => RpcCodec.Deserialize<DateTime>(Int64Sequence(DateTime.MaxValue.Ticks + 1)));

        var dateTimeOffset = new byte[10];
        BinaryPrimitives.WriteInt64LittleEndian(dateTimeOffset, long.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(dateTimeOffset.AsSpan(sizeof(long)), 0);
        ExpectDataLoss(() => RpcCodec.Deserialize<DateTimeOffset>(new ReadOnlySequence<byte>(dateTimeOffset)));

        ExpectDataLoss(() => RpcCodec.Deserialize<TimeOnly>(Int64Sequence(long.MaxValue)));
        ExpectDataLoss(() => RpcCodec.Deserialize<Rune>(Int32Sequence(0x11_0000)));
        ExpectDataLoss(() => RpcCodec.Deserialize<decimal>(new ReadOnlySequence<byte>(Enumerable.Repeat((byte)0xFF, 16).ToArray())));
    }

    [Test]
    public void LargeUnsafeBlitCodecShouldUseBoundedCrossSegmentTemporaryStorage()
    {
        var bytes = new byte[2048];
        new Random(42).NextBytes(bytes);
        _ = RpcCodec.Deserialize<LargeBlittable>(CreateSegmentedSequence(bytes));
        ExpectDataLoss(() => RpcCodec.Deserialize<LargeBlittable>(new ReadOnlySequence<byte>(bytes.AsMemory(0, 2047))));
    }

    [Test]
    public void ParserAndCodecShouldRemainBoundedAcrossOneMillionRandomInputs()
    {
        const int iterations = 1_000_000;
        var random = new Random(0x5A17);
        var scratch = new byte[32];

        for (var index = 0; index < iterations; index++)
        {
            random.NextBytes(scratch);

            var codecInput = new ReadOnlySequence<byte>(scratch.AsMemory(0, sizeof(int)));
            _ = RpcCodec.Deserialize<int>(codecInput);

            var incompleteFrame = new ReadOnlySequence<byte>(
                scratch.AsMemory(0, index % ProtocolV2Constants.HeaderBytes));
            Ensure(!ProtocolV2FrameParser.TryReadFrame(
                ref incompleteFrame,
                new SharpLinkProtocolOptions(),
                out _,
                out _), "incomplete random frame");

            if ((index & 1023) != 0)
                continue;

            scratch[0] = ProtocolV2Constants.Magic;
            BinaryPrimitives.WriteInt32LittleEndian(
                scratch.AsSpan(1, sizeof(int)),
                -1);
            var invalidFrame = new ReadOnlySequence<byte>(scratch.AsMemory(0, ProtocolV2Constants.HeaderBytes));
            try
            {
                _ = ProtocolV2FrameParser.TryReadFrame(
                    ref invalidFrame,
                    new SharpLinkProtocolOptions(),
                    out _,
                    out _);
                throw new Exception("expected random protocol violation");
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ProtocolViolation)
            {
            }

            BinaryPrimitives.WriteInt32LittleEndian(scratch, -2);
            var invalidString = new ReadOnlySequence<byte>(scratch.AsMemory(0, sizeof(int)));
            try
            {
                _ = RpcCodec.Deserialize<string>(invalidString);
                throw new Exception("expected random data loss");
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.DataLoss)
            {
            }
        }
    }

    private static void AssertFixedRoundTrip<T>(T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        RpcCodec.Serialize(value, writer);
        var bytes = writer.WrittenSpan.ToArray();

        var single = RpcCodec.Deserialize<T>(new ReadOnlySequence<byte>(bytes));
        Ensure(EqualityComparer<T>.Default.Equals(value, single!), $"single segment {typeof(T)}");

        var segmented = RpcCodec.Deserialize<T>(CreateSegmentedSequence(bytes));
        Ensure(EqualityComparer<T>.Default.Equals(value, segmented!), $"multi segment {typeof(T)}");

        for (var length = 0; length < bytes.Length; length++)
        {
            var truncated = new ReadOnlySequence<byte>(bytes.AsMemory(0, length));
            ExpectDataLoss(() => RpcCodec.Deserialize<T>(truncated));
        }
    }

    private static void AssertVariableRoundTrip<T>(T value, Func<T?, T?, bool> equals)
    {
        var writer = new ArrayBufferWriter<byte>();
        RpcCodec.Serialize(value, writer);
        var bytes = writer.WrittenSpan.ToArray();
        var single = RpcCodec.Deserialize<T>(new ReadOnlySequence<byte>(bytes));
        var segmented = RpcCodec.Deserialize<T>(CreateSegmentedSequence(bytes));
        Ensure(equals(value, single), $"single segment {typeof(T)}");
        Ensure(equals(value, segmented), $"multi segment {typeof(T)}");
    }

    private static void AssertSequenceRoundTrip<T>(T value, Func<T, IEnumerable<int>> values)
    {
        var writer = new ArrayBufferWriter<byte>();
        RpcCodec.Serialize(value, writer);
        var expected = values(value).ToArray();
        var bytes = writer.WrittenSpan.ToArray();

        var single = RpcCodec.Deserialize<T>(new ReadOnlySequence<byte>(bytes));
        var segmented = RpcCodec.Deserialize<T>(CreateSegmentedSequence(bytes));
        Ensure(single is not null && values(single).SequenceEqual(expected), $"single segment {typeof(T)}");
        Ensure(segmented is not null && values(segmented).SequenceEqual(expected), $"multi segment {typeof(T)}");
    }

    private static byte[] CreateLengthPrefix(int length)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        return bytes;
    }

    private static ReadOnlySequence<byte> Int32Sequence(int value)
        => new(CreateLengthPrefix(value));

    private static ReadOnlySequence<byte> Int64Sequence(long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return new ReadOnlySequence<byte>(bytes);
    }

    private static byte[] CreateLengthPrefixedPayload(int length, byte[] payload)
    {
        var bytes = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        payload.CopyTo(bytes, sizeof(int));
        return bytes;
    }

    private static ReadOnlySequence<byte> CreateSegmentedSequence(
        byte[] bytes,
        bool includeLeadingEmptySegment = false)
    {
        TestSequenceSegment? first = null;
        TestSequenceSegment? last = null;

        if (includeLeadingEmptySegment)
        {
            first = last = new TestSequenceSegment(ReadOnlyMemory<byte>.Empty);
        }

        foreach (var value in bytes)
        {
            var segment = new TestSequenceSegment(new[] { value });
            if (first is null)
                first = segment;
            else
                last!.Append(segment);
            last = segment;
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static void ExpectDataLoss(Action action)
    {
        try
        {
            action();
            throw new Exception("expected DataLoss");
        }
        catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.DataLoss)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class TestSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSequenceSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public void Append(TestSequenceSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 2048)]
    private struct LargeBlittable
    {
    }
}
