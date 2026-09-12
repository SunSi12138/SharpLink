using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpLink.Runtime;

namespace SharpLink.CodecCompatibility;

internal interface IFixture
{
    string Id { get; }
    string Category { get; }
    string TypeName { get; }
    bool NativeWidth { get; }
    int Size { get; }
    Dictionary<string, int> FieldOffsets { get; }
    string ExpectedLogicalValue { get; }
    byte[] Serialize();
    VerificationEntry Verify(byte[] producerBytes, CaseManifest producerCase, RuntimeManifest producer, RuntimeManifest consumer);
}

internal sealed class Fixture<T> : IFixture where T : struct
{
    private static readonly JsonSerializerOptions DescribeOptions = new() { IncludeFields = true };
    private readonly T _value;
    private readonly Func<T, T, bool> _logicalEquals;

    internal Fixture(string id, string category, T value, bool nativeWidth = false, params string[] fieldNames)
        : this(id, category, value, EqualityComparer<T>.Default.Equals, nativeWidth, fieldNames)
    {
    }

    internal Fixture(
        string id,
        string category,
        T value,
        Func<T, T, bool> logicalEquals,
        bool nativeWidth = false,
        params string[] fieldNames)
    {
        Id = id;
        Category = category;
        _value = value;
        _logicalEquals = logicalEquals;
        NativeWidth = nativeWidth;
        FieldOffsets = fieldNames.ToDictionary(static name => name, static name => Marshal.OffsetOf<T>(name).ToInt32(), StringComparer.Ordinal);
        ExpectedLogicalValue = Describe(value);
    }

    public string Id { get; }
    public string Category { get; }
    public string TypeName => typeof(T).FullName ?? typeof(T).Name;
    public bool NativeWidth { get; }
    public int Size => Unsafe.SizeOf<T>();
    public Dictionary<string, int> FieldOffsets { get; }
    public string ExpectedLogicalValue { get; }

    public byte[] Serialize()
    {
        var writer = new ArrayBufferWriter<byte>(Size);
        var value = _value;
        UnsafeBlitCodec<T>.Instance.Serialize(in value, writer);
        return writer.WrittenSpan.ToArray();
    }

    public VerificationEntry Verify(
        byte[] producerBytes,
        CaseManifest producerCase,
        RuntimeManifest producer,
        RuntimeManifest consumer)
    {
        var localBytes = Serialize();
        var entry = new VerificationEntry
        {
            Producer = producer.PlatformTag,
            Consumer = consumer.PlatformTag,
            Fixture = Id,
            Category = Category,
            CodecPath = "UnsafeBlitDirect",
            ProducerSize = producerCase.Size,
            ConsumerSize = Size,
            ProducerPointerSize = producer.PointerSize,
            ConsumerPointerSize = consumer.PointerSize,
            ProducerFieldOffsets = producerCase.FieldOffsets,
            ConsumerFieldOffsets = FieldOffsets,
            ProducerWireHash = producerCase.WireSha256,
            ConsumerLocalWireHash = Hash(localBytes),
            ExpectedLogicalValue = ExpectedLogicalValue,
            ByteForByteEquality = producerBytes.AsSpan().SequenceEqual(localBytes),
            FirstDifferingByteOffset = FindFirstDifference(producerBytes, localBytes)
        };

        if (producerBytes.Length != Size || producerCase.Size != Size)
        {
            entry.Classification = NativeWidth && producer.PointerSize != consumer.PointerSize
                ? "EXPECTED_ARCH_DEPENDENT"
                : "SIZE_OR_LAYOUT_MISMATCH";
            entry.Blocking = entry.Classification != "EXPECTED_ARCH_DEPENDENT";
            return entry;
        }

        try
        {
            var sequence = new ReadOnlySequence<byte>(new ReadOnlyMemory<byte>(producerBytes));
            var actual = UnsafeBlitCodec<T>.Instance.Deserialize(in sequence);
            entry.CrossDeserializeResult = true;
            entry.LogicalEquality = _logicalEquals(_value, actual);
            entry.ActualLogicalValue = Describe(actual);
        }
        catch (Exception exception)
        {
            entry.CrossDeserializeResult = false;
            entry.ExceptionType = exception.GetType().FullName;
            entry.ExceptionMessage = exception.Message;
            entry.Classification = "DESERIALIZE_REJECTED";
            entry.Blocking = true;
            return entry;
        }

        if (entry.LogicalEquality != true)
        {
            entry.Classification = "DESERIALIZED_VALUE_MISMATCH";
            entry.Blocking = true;
            return entry;
        }

        if (producerBytes.Length > 1)
        {
            try
            {
                var segmentedSequence = CreateSegmentedSequence(producerBytes);
                var segmentedActual = UnsafeBlitCodec<T>.Instance.Deserialize(in segmentedSequence);
                entry.SegmentedCrossDeserializeResult = true;
                entry.SegmentedLogicalEquality = _logicalEquals(_value, segmentedActual);
            }
            catch (Exception exception)
            {
                entry.SegmentedCrossDeserializeResult = false;
                entry.ExceptionType = exception.GetType().FullName;
                entry.ExceptionMessage = exception.Message;
                entry.Classification = "SEGMENTED_DESERIALIZE_REJECTED";
                entry.Blocking = true;
                return entry;
            }

            if (entry.SegmentedLogicalEquality != true)
            {
                entry.Classification = "SEGMENTED_DESERIALIZED_VALUE_MISMATCH";
                entry.Blocking = true;
                return entry;
            }
        }

        entry.Classification = entry.ByteForByteEquality
            ? "IDENTICAL_BYTES_AND_COMPATIBLE"
            : "DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE";
        entry.Blocking = false;
        return entry;
    }

    private static ReadOnlySequence<byte> CreateSegmentedSequence(byte[] bytes)
    {
        var split = Math.Clamp(bytes.Length / 2, 1, bytes.Length - 1);
        var first = new SequenceSegment(bytes.AsMemory(0, split));
        var last = first.Append(bytes.AsMemory(split));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static string Describe(T value)
    {
        try
        {
            return JsonSerializer.Serialize(value, DescribeOptions);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? typeof(T).Name;
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int? FindFirstDifference(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var common = Math.Min(left.Length, right.Length);
        for (var index = 0; index < common; index++)
        {
            if (left[index] != right[index])
                return index;
        }

        return left.Length == right.Length ? null : common;
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        internal SequenceSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        internal SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SequenceSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = next;
            return next;
        }
    }
}

internal static class FixtureRegistry
{
    private delegate void RefMutator<T>(ref T value) where T : struct;

    internal static IReadOnlyList<IFixture> All { get; } = CreateFixtures();

    internal static IReadOnlyDictionary<string, IFixture> ById { get; } = All.ToDictionary(static fixture => fixture.Id, StringComparer.Ordinal);

    internal static List<PaddingPoisonResult> RunPaddingPoison()
    {
        return
        [
            Poison("ByteInt32", [nameof(ByteInt32.A), nameof(ByteInt32.B)], static (ref ByteInt32 value) =>
            {
                value.A = 0x12;
                value.B = 0x34567890;
            }),
            Poison("Int64Byte", [nameof(Int64Byte.A), nameof(Int64Byte.B)], static (ref Int64Byte value) =>
            {
                value.A = 0x0102030405060708;
                value.B = 0x5A;
            })
        ];
    }

    private static IReadOnlyList<IFixture> CreateFixtures()
    {
        var block64 = CreateBlock64(0x1000);
        var block256 = new Block256 { A = block64, B = CreateBlock64(0x2000), C = CreateBlock64(0x3000), D = CreateBlock64(0x4000) };
        var block1024 = new Block1024
        {
            A = block256,
            B = Offset(block256, 0x10000),
            C = Offset(block256, 0x20000),
            D = Offset(block256, 0x30000)
        };
        var block2048 = new Block2048
        {
            A = block1024,
            B = Offset(block1024, 0x40000)
        };

        return
        [
            new Fixture<byte>("Byte", "no-padding", 0xA5),
            new Fixture<short>("Int16", "no-padding", -12345),
            new Fixture<int>("Int32", "no-padding", 0x12345678),
            new Fixture<long>("Int64", "no-padding", 0x0102030405060708),
            new Fixture<float>("Single", "no-padding", 123.5f),
            new Fixture<double>("Double", "no-padding", -9876.125d),
            new Fixture<Half>("Half", "no-padding", (Half)3.5f),
            new Fixture<Int128>("Int128", "no-padding", (Int128)0x1122334455667788L),
            new Fixture<UInt128>("UInt128", "no-padding", (UInt128)0xFEDCBA9876543210UL),
            new Fixture<Guid>("Guid", "no-padding", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
            new Fixture<Int32Pair>("Int32Pair", "no-padding", new Int32Pair { A = 0x11223344, B = 0x55667788 }, false, nameof(Int32Pair.A), nameof(Int32Pair.B)),

            new Fixture<ByteInt32>("ByteInt32", "internal-padding", new ByteInt32 { A = 0x12, B = 0x34567890 }, false, nameof(ByteInt32.A), nameof(ByteInt32.B)),
            new Fixture<ByteInt64>("ByteInt64", "internal-padding", new ByteInt64 { A = 0x21, B = 0x1020304050607080 }, false, nameof(ByteInt64.A), nameof(ByteInt64.B)),
            new Fixture<Int64Byte>("Int64Byte", "tail-padding", new Int64Byte { A = 0x0102030405060708, B = 0x5A }, false, nameof(Int64Byte.A), nameof(Int64Byte.B)),
            new Fixture<ByteShortIntLong>("ByteShortIntLong", "alignment", new ByteShortIntLong { A = 1, B = 0x2233, C = 0x44556677, D = 0x0102030405060708 }, false, nameof(ByteShortIntLong.A), nameof(ByteShortIntLong.B), nameof(ByteShortIntLong.C), nameof(ByteShortIntLong.D)),
            new Fixture<ByteDouble>("ByteDouble", "alignment", new ByteDouble { A = 0x4A, B = 12345.25d }, false, nameof(ByteDouble.A), nameof(ByteDouble.B)),
            new Fixture<ShortLongByte>("ShortLongByte", "alignment", new ShortLongByte { A = 0x1234, B = 0x0102030405060708, C = 0x7F }, false, nameof(ShortLongByte.A), nameof(ShortLongByte.B), nameof(ShortLongByte.C)),
            new Fixture<NestedPadded>("NestedPadded", "nested", new NestedPadded { Inner = new ByteInt32 { A = 0x33, B = 0x55667788 }, Tail = 0x44, Count = 0x0102030405060708 }, false, nameof(NestedPadded.Inner), nameof(NestedPadded.Tail), nameof(NestedPadded.Count)),

            new Fixture<SequentialControl>("SequentialDefault", "explicit-layout-control", new SequentialControl { A = 0x12, B = 0x34567890, C = 0x0102030405060708 }, false, nameof(SequentialControl.A), nameof(SequentialControl.B), nameof(SequentialControl.C)),
            new Fixture<Packed1Control>("Pack1", "packed-control", new Packed1Control { A = 0x12, B = 0x34567890, C = 0x0102030405060708 }, static (left, right) => left.A == right.A && left.B == right.B && left.C == right.C, false, nameof(Packed1Control.A), nameof(Packed1Control.B), nameof(Packed1Control.C)),
            new Fixture<Packed2Control>("Pack2", "packed-control", new Packed2Control { A = 0x12, B = 0x34567890, C = 0x0102030405060708 }, static (left, right) => left.A == right.A && left.B == right.B && left.C == right.C, false, nameof(Packed2Control.A), nameof(Packed2Control.B), nameof(Packed2Control.C)),
            new Fixture<Packed4Control>("Pack4", "packed-control", new Packed4Control { A = 0x12, B = 0x34567890, C = 0x0102030405060708 }, static (left, right) => left.A == right.A && left.B == right.B && left.C == right.C, false, nameof(Packed4Control.A), nameof(Packed4Control.B), nameof(Packed4Control.C)),
            new Fixture<Packed8Control>("Pack8", "packed-control", new Packed8Control { A = 0x12, B = 0x34567890, C = 0x0102030405060708 }, static (left, right) => left.A == right.A && left.B == right.B && left.C == right.C, false, nameof(Packed8Control.A), nameof(Packed8Control.B), nameof(Packed8Control.C)),
            new Fixture<ExplicitControl>("ExplicitLayout", "explicit-layout-control", new ExplicitControl { A = 0x12345678, B = 0x0102030405060708 }, static (left, right) => left.A == right.A && left.B == right.B, false, nameof(ExplicitControl.A), nameof(ExplicitControl.B)),

            new Fixture<nint>("NativeInt", "native-width", (nint)0x12345678, true),
            new Fixture<nuint>("NativeUInt", "native-width", (nuint)0x87654321u, true),
            new Fixture<NativePair>("NativePair", "native-width", new NativePair { A = (nint)0x12345678, B = (nuint)0x87654321u }, true, nameof(NativePair.A), nameof(NativePair.B)),

            new Fixture<ByteEnum>("ByteEnum", "enum", ByteEnum.Beta),
            new Fixture<ShortEnum>("ShortEnum", "enum", ShortEnum.Beta),
            new Fixture<IntEnum>("IntEnum", "enum", IntEnum.Beta),
            new Fixture<LongEnum>("LongEnum", "enum", LongEnum.Beta),
            new Fixture<EnumContainer>("EnumContainer", "enum", new EnumContainer { A = ByteEnum.Beta, B = LongEnum.Beta, C = 0x12345678 }, false, nameof(EnumContainer.A), nameof(EnumContainer.B), nameof(EnumContainer.C)),

            new Fixture<Block64>("Large64", "large", block64, false, nameof(Block64.A), nameof(Block64.B), nameof(Block64.C), nameof(Block64.D), nameof(Block64.E), nameof(Block64.F), nameof(Block64.G), nameof(Block64.H)),
            new Fixture<Block256>("Large256", "large", block256, false, nameof(Block256.A), nameof(Block256.B), nameof(Block256.C), nameof(Block256.D)),
            new Fixture<Block1024>("Large1024", "large", block1024, false, nameof(Block1024.A), nameof(Block1024.B), nameof(Block1024.C), nameof(Block1024.D)),
            new Fixture<Block2048>("Large2048", "large", block2048, false, nameof(Block2048.A), nameof(Block2048.B)),

            new Fixture<Vector3Value>("Vector3Value", "user-like", new Vector3Value { X = 1.25, Y = -2.5, Z = 100.125 }, false, nameof(Vector3Value.X), nameof(Vector3Value.Y), nameof(Vector3Value.Z)),
            new Fixture<TimestampFlags>("TimestampFlags", "user-like", new TimestampFlags { UnixNanoseconds = 1_787_224_683_123_456_789, Flags = 0xA5A55A5A, Sequence = 42 }, false, nameof(TimestampFlags.UnixNanoseconds), nameof(TimestampFlags.Flags), nameof(TimestampFlags.Sequence)),
            new Fixture<IdentityCounter>("IdentityCounter", "user-like", new IdentityCounter { High = 0x1122334455667788, Low = 0x99AABBCCDDEEFF00, Count = 123456789 }, false, nameof(IdentityCounter.High), nameof(IdentityCounter.Low), nameof(IdentityCounter.Count)),
            new Fixture<GeometryValue>("GeometryValue", "user-like", new GeometryValue { Position = new Vector3Value { X = 10, Y = 20, Z = 30 }, Velocity = new Vector3Value { X = -1, Y = 0.5, Z = 3 }, Timestamp = 1_787_224_683_000_000_000 }, false, nameof(GeometryValue.Position), nameof(GeometryValue.Velocity), nameof(GeometryValue.Timestamp)),

            .. AutoLayoutEvidenceFixtures.Create(),

            new Fixture<DateOnly>("DateOnlyRaw", "builtin-semantic-raw", new DateOnly(2026, 8, 20)),
            new Fixture<DateTime>("DateTimeRaw", "builtin-semantic-raw", new DateTime(2026, 8, 20, 12, 34, 56, DateTimeKind.Utc), static (left, right) => left.Ticks == right.Ticks && left.Kind == right.Kind),
            new Fixture<DateTimeOffset>("DateTimeOffsetRaw", "builtin-semantic-raw", new DateTimeOffset(2026, 8, 20, 12, 34, 56, TimeSpan.FromHours(8)), static (left, right) => left.Ticks == right.Ticks && left.UtcTicks == right.UtcTicks && left.Offset == right.Offset),
            new Fixture<TimeOnly>("TimeOnlyRaw", "builtin-semantic-raw", new TimeOnly(12, 34, 56, 789)),
            new Fixture<TimeSpan>("TimeSpanRaw", "builtin-semantic-raw", TimeSpan.FromTicks(1234567890123)),
            new Fixture<Index>("IndexRaw", "builtin-semantic-raw", new Index(7, fromEnd: true)),
            new Fixture<Range>("RangeRaw", "builtin-semantic-raw", new Range(new Index(2), new Index(3, fromEnd: true))),
            new Fixture<Rune>("RuneRaw", "builtin-semantic-raw", new Rune('λ')),
            new Fixture<decimal>("DecimalRaw", "builtin-semantic-raw", 1234567890.123456789m, static (left, right) => decimal.GetBits(left).AsSpan().SequenceEqual(decimal.GetBits(right)))
        ];
    }

    private static Block64 CreateBlock64(long seed) => new()
    {
        A = seed + 1,
        B = seed + 2,
        C = seed + 3,
        D = seed + 4,
        E = seed + 5,
        F = seed + 6,
        G = seed + 7,
        H = seed + 8
    };

    private static Block1024 Offset(Block1024 value, long offset) => new()
    {
        A = Offset(value.A, offset),
        B = Offset(value.B, offset),
        C = Offset(value.C, offset),
        D = Offset(value.D, offset)
    };

    private static Block256 Offset(Block256 value, long offset) => new()
    {
        A = Offset(value.A, offset),
        B = Offset(value.B, offset),
        C = Offset(value.C, offset),
        D = Offset(value.D, offset)
    };

    private static Block64 Offset(Block64 value, long offset) => new()
    {
        A = value.A + offset,
        B = value.B + offset,
        C = value.C + offset,
        D = value.D + offset,
        E = value.E + offset,
        F = value.F + offset,
        G = value.G + offset,
        H = value.H + offset
    };

    private static PaddingPoisonResult Poison<T>(string fixture, string[] fieldNames, RefMutator<T> mutate) where T : struct
    {
        var size = Unsafe.SizeOf<T>();
        var sourceA = new byte[size];
        var sourceB = new byte[size];
        sourceA.AsSpan().Fill(0xAA);
        sourceB.AsSpan().Fill(0x55);

        ref var valueA = ref MemoryMarshal.AsRef<T>(sourceA.AsSpan());
        ref var valueB = ref MemoryMarshal.AsRef<T>(sourceB.AsSpan());
        mutate(ref valueA);
        mutate(ref valueB);

        var logicalEqual = EqualityComparer<T>.Default.Equals(valueA, valueB);
        var wireA = SerializeValue(in valueA);
        var wireB = SerializeValue(in valueB);
        var differing = Enumerable.Range(0, Math.Min(wireA.Length, wireB.Length)).Where(index => wireA[index] != wireB[index]).ToList();
        if (wireA.Length != wireB.Length)
            differing.Add(Math.Min(wireA.Length, wireB.Length));

        var padding = FindPaddingOffsets<T>(fieldNames);
        return new PaddingPoisonResult
        {
            Fixture = fixture,
            Size = size,
            LogicalValuesEqual = logicalEqual,
            WireBytesEqual = wireA.AsSpan().SequenceEqual(wireB),
            DifferingByteOffsets = differing,
            PaddingByteOffsets = padding,
            DifferencesOnlyInPadding = differing.All(padding.Contains),
            SourceAHash = Hash(sourceA),
            SourceBHash = Hash(sourceB),
            WireAHash = Hash(wireA),
            WireBHash = Hash(wireB)
        };
    }

    private static byte[] SerializeValue<T>(in T value) where T : struct
    {
        var writer = new ArrayBufferWriter<byte>(Unsafe.SizeOf<T>());
        UnsafeBlitCodec<T>.Instance.Serialize(in value, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static List<int> FindPaddingOffsets<T>(string[] fieldNames) where T : struct
    {
        var occupied = new bool[Unsafe.SizeOf<T>()];
        foreach (var fieldName in fieldNames)
        {
            var field = typeof(T).GetField(fieldName) ?? throw new InvalidOperationException($"Missing field {typeof(T).Name}.{fieldName}.");
            var offset = Marshal.OffsetOf<T>(fieldName).ToInt32();
            var fieldSize = Marshal.SizeOf(field.FieldType);
            for (var index = offset; index < Math.Min(offset + fieldSize, occupied.Length); index++)
                occupied[index] = true;
        }

        return Enumerable.Range(0, occupied.Length).Where(index => !occupied[index]).ToList();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

[StructLayout(LayoutKind.Sequential)]
internal struct Int32Pair { public int A; public int B; }

[StructLayout(LayoutKind.Sequential)]
internal struct ByteInt32 { public byte A; public int B; }

[StructLayout(LayoutKind.Sequential)]
internal struct ByteInt64 { public byte A; public long B; }

[StructLayout(LayoutKind.Sequential)]
internal struct Int64Byte { public long A; public byte B; }

[StructLayout(LayoutKind.Sequential)]
internal struct ByteShortIntLong { public byte A; public short B; public int C; public long D; }

[StructLayout(LayoutKind.Sequential)]
internal struct ByteDouble { public byte A; public double B; }

[StructLayout(LayoutKind.Sequential)]
internal struct ShortLongByte { public short A; public long B; public byte C; }

[StructLayout(LayoutKind.Sequential)]
internal struct NestedPadded { public ByteInt32 Inner; public byte Tail; public long Count; }

[StructLayout(LayoutKind.Sequential)]
internal struct SequentialControl { public byte A; public int B; public long C; }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Packed1Control { public byte A; public int B; public long C; }

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct Packed2Control { public byte A; public int B; public long C; }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct Packed4Control { public byte A; public int B; public long C; }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct Packed8Control { public byte A; public int B; public long C; }

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct ExplicitControl
{
    [FieldOffset(0)] public int A;
    [FieldOffset(8)] public long B;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePair { public nint A; public nuint B; }

internal enum ByteEnum : byte { Alpha = 1, Beta = 0xA5 }
internal enum ShortEnum : short { Alpha = 1, Beta = 0x1234 }
internal enum IntEnum : int { Alpha = 1, Beta = 0x12345678 }
internal enum LongEnum : long { Alpha = 1, Beta = 0x0102030405060708 }

[StructLayout(LayoutKind.Sequential)]
internal struct EnumContainer { public ByteEnum A; public LongEnum B; public int C; }

[StructLayout(LayoutKind.Sequential)]
internal struct Block64
{
    public long A; public long B; public long C; public long D;
    public long E; public long F; public long G; public long H;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Block256 { public Block64 A; public Block64 B; public Block64 C; public Block64 D; }

[StructLayout(LayoutKind.Sequential)]
internal struct Block1024 { public Block256 A; public Block256 B; public Block256 C; public Block256 D; }

[StructLayout(LayoutKind.Sequential)]
internal struct Block2048 { public Block1024 A; public Block1024 B; }

[StructLayout(LayoutKind.Sequential)]
internal struct Vector3Value { public double X; public double Y; public double Z; }

[StructLayout(LayoutKind.Sequential)]
internal struct TimestampFlags { public long UnixNanoseconds; public uint Flags; public int Sequence; }

[StructLayout(LayoutKind.Sequential)]
internal struct IdentityCounter { public ulong High; public ulong Low; public long Count; }

[StructLayout(LayoutKind.Sequential)]
internal struct GeometryValue { public Vector3Value Position; public Vector3Value Velocity; public long Timestamp; }
