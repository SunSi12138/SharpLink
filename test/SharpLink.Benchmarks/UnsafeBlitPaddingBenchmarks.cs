using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    private readonly record struct PaddingRange(int Start, int Length);

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
    private struct NestedPadding
    {
        public ByteInt32 Inner;
        public byte Tail;
    }
}
