using System;
using System.Buffers;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Measures realistic generated-codec string-field serialization shapes that can
/// trigger <see cref="PooledByteBufferWriter"/> growth.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class BufferWriterGrowthBenchmarks
{
    private SharpLinkBufferWriterPool _pool = null!;
    private StringFieldPayload _payload = null!;
    private StringFieldPayloadCodec _codec = null!;

    [Params(65_536, 1_048_576)]
    public int PayloadBytes { get; set; }

    [Params(1, 64)]
    public int FieldCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _pool = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions
        {
            InitialCapacity = 1024,
            MaxPooledWriters = 1,
            MaxRetainedCapacityBytes = 64 * 1024
        });
        _payload = StringFieldPayload.Create(PayloadBytes, FieldCount);
        _codec = new StringFieldPayloadCodec();
    }

    [GlobalCleanup]
    public void Cleanup() => _pool.Dispose();

    [Benchmark]
    public int SerializeGeneratedStringFields()
    {
        var writer = _pool.Rent();
        try
        {
            _codec.Serialize(_payload, writer);
            return writer.WrittenCount;
        }
        finally
        {
            _pool.Return(writer);
        }
    }

    internal sealed class StringFieldPayload(string[] fields)
    {
        public string[] Fields { get; } = fields;

        public static StringFieldPayload Create(int payloadBytes, int fieldCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldCount);

            var fields = new string[fieldCount];
            var baseLength = payloadBytes / fieldCount;
            var remainder = payloadBytes % fieldCount;
            for (var index = 0; index < fields.Length; index++)
                fields[index] = new string('x', baseLength + (index < remainder ? 1 : 0));
            return new StringFieldPayload(fields);
        }
    }

    internal sealed class StringFieldPayloadCodec : IRpcCodec<StringFieldPayload>
    {
        public void Serialize(in StringFieldPayload value, IBufferWriter<byte> writer)
        {
            foreach (var field in value.Fields)
                RpcGeneratedCodecWire.WriteString(writer, field);
        }

        public StringFieldPayload Deserialize(in ReadOnlySequence<byte> buffer)
            => throw new NotSupportedException("The benchmark exercises serialization only.");
    }
}

/// <summary>Prints non-timed growth evidence for the same generated-codec field shapes.</summary>
public static class BufferWriterGrowthEvidenceRunner
{
    private static readonly (int PayloadBytes, int FieldCount)[] s_cases =
    [
        (65_536, 1),
        (65_536, 64),
        (1_048_576, 1),
        (1_048_576, 64)
    ];

    public static void Run()
    {
        foreach (var (payloadBytes, fieldCount) in s_cases)
        {
            var payload = BufferWriterGrowthBenchmarks.StringFieldPayload.Create(payloadBytes, fieldCount);
            using var writer = new PooledByteBufferWriter(1024);
            var growthCount = 0;
            long copiedBytes = 0;
            foreach (var field in payload.Fields)
            {
                var capacity = writer.Capacity;
                var written = writer.WrittenCount;
                RpcGeneratedCodecWire.WriteString(writer, field);
                if (writer.Capacity == capacity)
                    continue;

                growthCount++;
                copiedBytes += written;
            }

            Console.WriteLine(
                $"[BufferWriterGrowth] payload={payloadBytes} fields={fieldCount} " +
                $"written={writer.WrittenCount} finalCapacity={writer.Capacity} " +
                $"growths={growthCount} copied={copiedBytes} " +
                $"copyRatio={(double)copiedBytes / writer.WrittenCount:F4}");
        }
    }
}
