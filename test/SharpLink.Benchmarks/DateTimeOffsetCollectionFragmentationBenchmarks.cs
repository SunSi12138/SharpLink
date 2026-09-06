using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
[BenchmarkCategory("Codec", "DateTimeOffset", "Fragmentation")]
public class DateTimeOffsetCollectionFragmentationBenchmarks
{
    private ReadOnlySequence<byte> _payload;

    [Params(16, 128, 512)]
    public int Count { get; set; }

    // 0 = contiguous. Small positive values deliberately force elements to span segments.
    [Params(0, 1, 8, 16, 64)]
    public int SegmentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var values = new DateTimeOffset[Count];
        for (var index = 0; index < values.Length; index++)
        {
            var offsetMinutes = ((index % 29) - 14) * 30;
            values[index] = new DateTimeOffset(
                2024,
                1 + index % 12,
                1 + index % 28,
                index % 24,
                index % 60,
                index % 60,
                TimeSpan.FromMinutes(offsetMinutes));
        }

        var writer = new ArrayBufferWriter<byte>();
        DateTimeOffsetArrayCodec.Instance.Serialize(in values, writer);
        var bytes = writer.WrittenMemory.ToArray();
        _payload = SegmentSize == 0
            ? new ReadOnlySequence<byte>(bytes)
            : CreateSegmented(bytes, SegmentSize);
    }

    [Benchmark(Baseline = true)]
    public DateTimeOffset[]? DecodeArray()
        => DateTimeOffsetArrayCodec.Instance.Deserialize(_payload);

    [Benchmark]
    public List<DateTimeOffset>? DecodeList()
        => DateTimeOffsetListCodec.Instance.Deserialize(_payload);

    [Benchmark]
    public Memory<DateTimeOffset> DecodeMemory()
        => DateTimeOffsetMemoryCodec.Instance.Deserialize(_payload);

    [Benchmark]
    public ReadOnlyMemory<DateTimeOffset> DecodeReadOnlyMemory()
        => DateTimeOffsetReadOnlyMemoryCodec.Instance.Deserialize(_payload);

    [Benchmark]
    public ImmutableArray<DateTimeOffset> DecodeImmutableArray()
        => DateTimeOffsetImmutableArrayCodec.Instance.Deserialize(_payload);

    private static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentSize)
    {
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += segmentSize)
        {
            var length = Math.Min(segmentSize, bytes.Length - offset);
            var segment = new Segment(bytes.AsMemory(offset, length));
            if (first is null)
                first = segment;
            else
                last!.SetNext(segment);
            last = segment;
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment SetNext(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
            return next;
        }
    }
}
