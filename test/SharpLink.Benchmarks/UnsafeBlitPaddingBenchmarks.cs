using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class UnsafeBlitPaddingBenchmarks
{
    private static readonly PaddingRange[] ByteInt32Padding = [new(1, 3)];
    private static readonly PaddingRange[] ByteInt64Padding = [new(1, 7)];
    private static readonly PaddingRange[] Int64BytePadding = [new(9, 7)];
    private static readonly PaddingRange[] NestedPaddingRanges = [new(1, 3), new(9, 3)];

    private readonly ArrayBufferWriter<byte> _writer = new(64);
    private ByteInt32 _byteInt32;
    private ByteInt64 _byteInt64;
    private Int64Byte _int64Byte;
    private NestedPadding _nested;

    [GlobalSetup]
    public void Setup()
    {
        _byteInt32 = new ByteInt32 { A = 0x12, B = 0x11223344 };
        _byteInt64 = new ByteInt64 { A = 0x12, B = 0x1122334455667788 };
        _int64Byte = new Int64Byte { A = 0x1122334455667788, B = 0x12 };
        _nested = new NestedPadding
        {
            Inner = new ByteInt32 { A = 0x12, B = 0x11223344 },
            Tail = 0x34,
        };

        WriteEvidenceIfRequested();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ByteInt32")]
    public byte RawByteInt32()
        => SerializeRaw(_byteInt32);

    [Benchmark]
    [BenchmarkCategory("ByteInt32")]
    public byte CanonicalByteInt32()
        => SerializeCanonical(_byteInt32, ByteInt32Padding);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ByteInt64")]
    public byte RawByteInt64()
        => SerializeRaw(_byteInt64);

    [Benchmark]
    [BenchmarkCategory("ByteInt64")]
    public byte CanonicalByteInt64()
        => SerializeCanonical(_byteInt64, ByteInt64Padding);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Int64Byte")]
    public byte RawInt64Byte()
        => SerializeRaw(_int64Byte);

    [Benchmark]
    [BenchmarkCategory("Int64Byte")]
    public byte CanonicalInt64Byte()
        => SerializeCanonical(_int64Byte, Int64BytePadding);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("NestedPadding")]
    public byte RawNestedPadding()
        => SerializeRaw(_nested);

    [Benchmark]
    [BenchmarkCategory("NestedPadding")]
    public byte CanonicalNestedPadding()
        => SerializeCanonical(_nested, NestedPaddingRanges);

    private byte SerializeRaw<T>(in T value)
        where T : unmanaged
    {
        _writer.Clear();
        UnsafeBlitCodec<T>.Instance.Serialize(value, _writer);
        return Consume(_writer.WrittenSpan);
    }

    private byte SerializeCanonical<T>(in T value, ReadOnlySpan<PaddingRange> padding)
        where T : unmanaged
    {
        _writer.Clear();
        var size = Unsafe.SizeOf<T>();
        var destination = _writer.GetSpan(size)[..size];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
        foreach (var range in padding)
            destination.Slice(range.Start, range.Length).Clear();
        _writer.Advance(size);
        return Consume(_writer.WrittenSpan);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte Consume(ReadOnlySpan<byte> bytes)
        => (byte)(bytes[0] ^ bytes[^1]);

    private static void WriteEvidenceIfRequested()
    {
        var output = Environment.GetEnvironmentVariable("SHARPLINK_PADDING_EVIDENCE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            return;

        var cases = new[]
        {
            EvaluateByteInt32(),
            EvaluateByteInt64(),
            EvaluateInt64Byte(),
            EvaluateByteDouble(),
            EvaluateNestedPadding(),
            EvaluateExplicitGap(),
            EvaluatePackedControl(),
        };

        var report = new PaddingEvidenceReport(
            SchemaVersion: 1,
            SharpLinkCommit: Environment.GetEnvironmentVariable("SHARPLINK_COMMIT") ?? "unknown",
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            OsDescription: RuntimeInformation.OSDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            PointerSize: IntPtr.Size,
            Cases: cases);

        var directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static PaddingEvidenceCase EvaluateByteInt32()
    {
        Span<byte> leftStorage = stackalloc byte[Unsafe.SizeOf<ByteInt32>()];
        Span<byte> rightStorage = stackalloc byte[Unsafe.SizeOf<ByteInt32>()];
        leftStorage.Fill(0xAA);
        rightStorage.Fill(0x55);
        ref var left = ref Unsafe.As<byte, ByteInt32>(ref MemoryMarshal.GetReference(leftStorage));
        ref var right = ref Unsafe.As<byte, ByteInt32>(ref MemoryMarshal.GetReference(rightStorage));
        left.A = right.A = 0x12;
        left.B = right.B = 0x11223344;
        var safe = new ByteInt32 { A = 0x12, B = 0x11223344 };
        return Evaluate("ByteInt32", [1, 2, 3], in left, in right, in safe);
    }

    private static PaddingEvidenceCase EvaluateByteInt64()
    {
        Span<byte> leftStorage = stackalloc byte[Unsafe.SizeOf<ByteInt64>()];
        Span<byte> rightStorage = stackalloc byte[Unsafe.SizeOf<ByteInt64>()];
        leftStorage.Fill(0xAA);
        rightStorage.Fill(0x55);
        ref var left = ref Unsafe.As<byte, ByteInt64>(ref MemoryMarshal.GetReference(leftStorage));
        ref var right = ref Unsafe.As<byte, ByteInt64>(ref MemoryMarshal.GetReference(rightStorage));
        left.A = right.A = 0x12;
        left.B = right.B = 0x1122334455667788;
        var safe = new ByteInt64 { A = 0x12, B = 0x1122334455667788 };
        return Evaluate("ByteInt64", [1, 2, 3, 4, 5, 6, 7], in left, in right, in safe);
    }

    private static PaddingEvidenceCase EvaluateInt64Byte()
    {
        Span<byte> leftStorage = stackalloc byte[Unsafe.SizeOf<Int64Byte>()];
        Span<byte> rightStorage = stackalloc byte[Unsafe.SizeOf<Int64Byte>()];
        leftStorage.Fill(0xAA);
        rightStorage.Fill(0x55);
        ref var left = ref Unsafe.As<byte, Int64Byte>(ref MemoryMarshal.GetReference(leftStorage));
        ref var right = ref Unsafe.As<byte, Int64Byte>(ref MemoryMarshal.GetReference(rightStorage));
        left.A = right.A = 0x1122334455667788;
        left.B = right.B = 0x12;
        var safe = new Int64Byte { A = 0x1122334455667788, B = 0x12 };
        return Evaluate("Int64Byte", [9, 10, 11, 12, 13, 14, 15], in left, in right, in safe);
    }

    private static PaddingEvidenceCase EvaluateByteDouble()
    {
        Span<byte> leftStorage = stackalloc byte[Unsafe.SizeOf<ByteDouble>()];
        Span<byte> rightStorage = stackalloc byte[Unsafe.SizeOf<ByteDouble>()];
        leftStorage.Fill(0xAA);
        rightStorage.Fill(0x55);
        ref var left = ref Unsafe.As<byte, ByteDouble>(ref MemoryMarshal.GetReference(leftStorage));
        ref var right = ref Unsafe.As<byte, ByteDouble>(ref MemoryMarshal.GetReference(rightStorage));
        left.A = right.A = 0x12;
        left.B = right.B = 1234.5;
        var safe = new ByteDouble { A = 0x12, B = 1234.5 };
        return Evaluate("ByteDouble", [1, 2, 3, 4, 5, 6, 7], in left, in right, in safe);
    }

    private static PaddingEvidenceCase EvaluateNestedPadding()
    {
        Span<byte> leftStorage = stackalloc byte[Unsafe.SizeOf<NestedPadding>()];
        Span<byte> rightStorage = stackalloc byte[Unsafe.SizeOf<NestedPadding>()];
        leftStorage.Fill(0xAA);
        rightStorage.Fill(0x55);
        ref var left = ref Unsafe.As<byte, NestedPadding>(ref MemoryMarshal.GetReference(leftStorage));
        ref var right = ref Unsafe.As<byte, NestedPadding>(ref MemoryMarshal.GetReference(rightStorage));
        left.Inner.A = right.Inner.A = 0x12;
        left.Inner.B = right.Inner.B = 0x11223344;
        left.Tail = right.Tail = 0x34;
        var safe = new NestedPadding
        {
            Inner = new ByteInt32 { A = 0x12, B = 0x11223344 },
            Tail = 0x34,
        };
        return Evaluate("NestedPadding", [1, 2, 3, 9, 10, 11], in left, in right, in safe);
    }

    private static PaddingEvidenceCase EvaluateExplicitGap()
    {
        Span<byte> leftStorage = stackalloc byte[Unsafe.SizeOf<ExplicitGap>()];
        Span<byte> rightStorage = stackalloc byte[Unsafe.SizeOf<ExplicitGap>()];
        leftStorage.Fill(0xAA);
        rightStorage.Fill(0x55);
        ref var left = ref Unsafe.As<byte, ExplicitGap>(ref MemoryMarshal.GetReference(leftStorage));
        ref var right = ref Unsafe.As<byte, ExplicitGap>(ref MemoryMarshal.GetReference(rightStorage));
        left.A = right.A = 0x12;
        left.B = right.B = 0x11223344;
        var safe = new ExplicitGap { A = 0x12, B = 0x11223344 };
        return Evaluate("ExplicitGap", [1, 2, 3], in left, in right, in safe);
    }

    private static PaddingEvidenceCase EvaluatePackedControl()
    {
        Span<byte> leftStorage = stackalloc byte[Unsafe.SizeOf<PackedByteInt32>()];
        Span<byte> rightStorage = stackalloc byte[Unsafe.SizeOf<PackedByteInt32>()];
        leftStorage.Fill(0xAA);
        rightStorage.Fill(0x55);
        ref var left = ref Unsafe.As<byte, PackedByteInt32>(ref MemoryMarshal.GetReference(leftStorage));
        ref var right = ref Unsafe.As<byte, PackedByteInt32>(ref MemoryMarshal.GetReference(rightStorage));
        left.A = right.A = 0x12;
        left.B = right.B = 0x11223344;
        var safe = new PackedByteInt32 { A = 0x12, B = 0x11223344 };
        return Evaluate("PackedByteInt32", [], in left, in right, in safe);
    }

    private static PaddingEvidenceCase Evaluate<T>(
        string name,
        int[] expectedPaddingOffsets,
        in T poisonedA,
        in T poisonedB,
        in T safe)
        where T : unmanaged
    {
        var wireA = SerializeToArray(poisonedA);
        var wireB = SerializeToArray(poisonedB);
        var safeWire = SerializeToArray(safe);
        var differingOffsets = Enumerable.Range(0, wireA.Length)
            .Where(index => wireA[index] != wireB[index])
            .ToArray();

        if (!differingOffsets.SequenceEqual(expectedPaddingOffsets))
        {
            throw new InvalidOperationException(
                $"{name}: expected padding-only differences [{string.Join(',', expectedPaddingOffsets)}], observed [{string.Join(',', differingOffsets)}].");
        }

        var safePaddingIsZero = expectedPaddingOffsets.All(index => safeWire[index] == 0);
        if (!safePaddingIsZero)
            throw new InvalidOperationException($"{name}: default-initialized control carried non-zero padding.");

        var canonicalA = (byte[])wireA.Clone();
        var canonicalB = (byte[])wireB.Clone();
        foreach (var offset in expectedPaddingOffsets)
        {
            canonicalA[offset] = 0;
            canonicalB[offset] = 0;
        }
        var canonicalEqual = canonicalA.AsSpan().SequenceEqual(canonicalB);
        if (!canonicalEqual)
            throw new InvalidOperationException($"{name}: zeroing known padding did not canonicalize equal logical values.");

        return new PaddingEvidenceCase(
            Name: name,
            Size: wireA.Length,
            PaddingOffsets: expectedPaddingOffsets,
            PoisonedWireDifferenceOffsets: differingOffsets,
            DefaultInitializedPaddingIsZero: safePaddingIsZero,
            CanonicalizedPoisonedWiresEqual: canonicalEqual);
    }

    private static byte[] SerializeToArray<T>(in T value)
        where T : unmanaged
    {
        var writer = new ArrayBufferWriter<byte>(Unsafe.SizeOf<T>());
        UnsafeBlitCodec<T>.Instance.Serialize(value, writer);
        return writer.WrittenSpan.ToArray();
    }

    private readonly record struct PaddingRange(int Start, int Length);

    private sealed record PaddingEvidenceReport(
        int SchemaVersion,
        string SharpLinkCommit,
        string FrameworkDescription,
        string OsDescription,
        string ProcessArchitecture,
        int PointerSize,
        PaddingEvidenceCase[] Cases);

    private sealed record PaddingEvidenceCase(
        string Name,
        int Size,
        int[] PaddingOffsets,
        int[] PoisonedWireDifferenceOffsets,
        bool DefaultInitializedPaddingIsZero,
        bool CanonicalizedPoisonedWiresEqual);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteInt32
    {
        public byte A;
        public int B;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteInt64
    {
        public byte A;
        public long B;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Int64Byte
    {
        public long A;
        public byte B;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteDouble
    {
        public byte A;
        public double B;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NestedPadding
    {
        public ByteInt32 Inner;
        public byte Tail;
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct ExplicitGap
    {
        [FieldOffset(0)] public byte A;
        [FieldOffset(4)] public int B;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PackedByteInt32
    {
        public byte A;
        public int B;
    }
}
