using System.Buffers;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class UnsafeBlitPaddingEvidenceRunner
{
    public static void Run(string[] args)
    {
        if (args.Length > 1)
            throw new ArgumentException("Expected at most one output path.", nameof(args));

        var output = args.Length == 1
            ? args[0]
            : Environment.GetEnvironmentVariable("SHARPLINK_PADDING_EVIDENCE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                "Padding evidence output path was not supplied. Pass it as the first argument or set SHARPLINK_PADDING_EVIDENCE_OUTPUT.");
        }

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
            SchemaVersion: 2,
            CheckedOutCommit: Environment.GetEnvironmentVariable("SHARPLINK_COMMIT") ?? "unknown",
            SourceHeadCommit: Environment.GetEnvironmentVariable("SHARPLINK_HEAD_COMMIT") ?? "unknown",
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
            throw new InvalidOperationException($"{name}: ordinary managed-construction control carried non-zero padding.");

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
            OrdinaryManagedConstructionPaddingIsZero: safePaddingIsZero,
            CanonicalizedPoisonedWiresEqual: canonicalEqual);
    }

    private static byte[] SerializeToArray<T>(in T value)
        where T : unmanaged
    {
        var writer = new ArrayBufferWriter<byte>(Unsafe.SizeOf<T>());
        UnsafeBlitCodec<T>.Instance.Serialize(value, writer);
        return writer.WrittenSpan.ToArray();
    }

    private sealed record PaddingEvidenceReport(
        int SchemaVersion,
        string CheckedOutCommit,
        string SourceHeadCommit,
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
        bool OrdinaryManagedConstructionPaddingIsZero,
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
