using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class CompressionEvidenceRunner
{
    private static readonly string[] s_levels = ["fastest", "optimal", "smallest"];
    private static readonly int[] s_payloadSizes = [4096, 65_536, 262_144, 1_048_576];
    private static readonly CompressedInputShape[] s_inputShapes =
    [
        CompressedInputShape.SingleSegment,
        CompressedInputShape.TwoSegments,
        CompressedInputShape.EightSegments,
        CompressedInputShape.RealisticSegments
    ];

    internal static async Task RunAsync(string[] args)
    {
        var outputPath = GetOption(args, "--output") ??
            Path.Combine("artifacts", "performance", "current", "compression-provider.json");
        var inputShapes = GetInputShapes(args);
        var levels = GetLevels(args);
        var payloadSizes = GetPayloadSizes(args);
        var results = new List<CompressionEvidenceResult>(
            levels.Count * payloadSizes.Count * 2 * inputShapes.Count * 5);
        foreach (var level in levels)
        {
            foreach (var payloadSize in payloadSizes)
            {
                foreach (var compressible in new[] { true, false })
                {
                    var provider = CompressionProviderBenchmarks.CreateProvider(level);
                    var payload = CreatePayload(payloadSize, compressible);
                    var compressed = Compress(provider, payload);
                    // Segment nodes are test setup; every measured operation reuses these sequences.
                    var compressedInputs = new List<CompressionInput>(inputShapes.Count);
                    foreach (var inputShape in inputShapes)
                    {
                        var input = CreateCompressedInput(compressed, inputShape);
                        _ = Decompress(provider, input, payloadSize);
                        compressedInputs.Add(new CompressionInput(inputShape, input, CountSegments(input)));
                    }
                    var iterations = Math.Clamp((16 * 1024 * 1024) / payloadSize, 4, 4096);

                    for (var round = 1; round <= 5; round++)
                    {
                        WarmUpCompression(provider, payload);
                        var compression = Measure(
                            iterations,
                            payloadSize,
                            () => Compress(provider, payload).Length);
                        for (var index = 0; index < compressedInputs.Count; index++)
                        {
                            var inputIndex = round % 2 == 0
                                ? compressedInputs.Count - index - 1
                                : index;
                            var compressedInput = compressedInputs[inputIndex];
                            WarmUpDecompression(provider, compressedInput.Sequence, payloadSize);
                            var decompression = MeasureDecompression(
                                iterations,
                                provider,
                                compressedInput.Sequence,
                                payloadSize);
                            results.Add(new CompressionEvidenceResult(
                                "brotli",
                                level,
                                payloadSize,
                                compressible,
                                GetInputShapeName(compressedInput.Shape),
                                compressedInput.SegmentCount,
                                round,
                                compressed.Length,
                                compressed.Length / (double)payloadSize,
                                compression.ThroughputMegabytesPerSecond,
                                decompression.ThroughputMegabytesPerSecond,
                                compression.AllocatedBytesPerOperation,
                                decompression.AllocatedBytesPerOperation));
                        }
                    }
                }
            }
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        Console.WriteLine($"Compression provider evidence: {fullPath}");
    }

    private static byte[] CreatePayload(int size, bool compressible)
    {
        var payload = new byte[size];
        if (compressible)
            Array.Fill(payload, (byte)0x2a);
        else
            new Random(42).NextBytes(payload);
        return payload;
    }

    private static byte[] Compress(ISharpLinkCompressionProvider provider, byte[] payload)
    {
        var output = new ArrayBufferWriter<byte>(payload.Length * 2 + 1024);
        var result = provider.Compress(
            new ReadOnlySequence<byte>(payload),
            output,
            payload.Length * 2 + 1024);
        if (result.ConsumedBytes != payload.Length || result.WrittenBytes != output.WrittenCount)
            throw new InvalidOperationException("Compression provider returned inconsistent evidence counts.");
        return output.WrittenSpan.ToArray();
    }

    private static int Decompress(
        ISharpLinkCompressionProvider provider,
        ReadOnlySequence<byte> compressed,
        int originalLength)
    {
        var output = new ArrayBufferWriter<byte>(originalLength);
        return Decompress(provider, compressed, output, originalLength);
    }

    private static int Decompress(
        ISharpLinkCompressionProvider provider,
        ReadOnlySequence<byte> compressed,
        ArrayBufferWriter<byte> output,
        int originalLength)
    {
        output.Clear();
        var result = provider.Decompress(
            compressed,
            output,
            originalLength);
        if (result.ConsumedBytes != compressed.Length || result.WrittenBytes != originalLength)
            throw new InvalidOperationException("Compression provider returned inconsistent decompression counts.");
        return result.WrittenBytes;
    }

    private static void WarmUpCompression(
        ISharpLinkCompressionProvider provider,
        byte[] payload)
    {
        for (var iteration = 0; iteration < 3; iteration++)
            _ = Compress(provider, payload);
    }

    private static void WarmUpDecompression(
        ISharpLinkCompressionProvider provider,
        ReadOnlySequence<byte> compressed,
        int originalLength)
    {
        for (var iteration = 0; iteration < 3; iteration++)
            _ = Decompress(provider, compressed, originalLength);
    }

    private static IReadOnlyList<CompressedInputShape> GetInputShapes(string[] args)
    {
        var option = GetOption(args, "--input-shapes");
        if (string.IsNullOrWhiteSpace(option))
            return s_inputShapes;

        var inputShapes = new List<CompressedInputShape>();
        foreach (var value in option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var inputShape = value.ToLowerInvariant() switch
            {
                "single" => CompressedInputShape.SingleSegment,
                "2" => CompressedInputShape.TwoSegments,
                "8" => CompressedInputShape.EightSegments,
                "realistic" => CompressedInputShape.RealisticSegments,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(args),
                    "Input shapes must be single, 2, 8, or realistic.")
            };
            if (!inputShapes.Contains(inputShape))
                inputShapes.Add(inputShape);
        }
        if (inputShapes.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(args), "At least one input shape is required.");
        return inputShapes;
    }

    private static IReadOnlyList<string> GetLevels(string[] args)
    {
        var option = GetOption(args, "--levels");
        if (string.IsNullOrWhiteSpace(option))
            return s_levels;

        var levels = new List<string>();
        foreach (var value in option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var level = value.ToLowerInvariant();
            if (Array.IndexOf(s_levels, level) < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(args),
                    "Levels must be fastest, optimal, or smallest.");
            }
            if (!levels.Contains(level))
                levels.Add(level);
        }
        if (levels.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(args), "At least one compression level is required.");
        return levels;
    }

    private static IReadOnlyList<int> GetPayloadSizes(string[] args)
    {
        var option = GetOption(args, "--payload-sizes");
        if (string.IsNullOrWhiteSpace(option))
            return s_payloadSizes;

        var payloadSizes = new List<int>();
        foreach (var value in option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(value, out var payloadSize) || Array.IndexOf(s_payloadSizes, payloadSize) < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(args),
                    "Payload sizes must be 4096, 65536, 262144, or 1048576.");
            }
            if (!payloadSizes.Contains(payloadSize))
                payloadSizes.Add(payloadSize);
        }
        if (payloadSizes.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(args), "At least one payload size is required.");
        return payloadSizes;
    }

    private static ReadOnlySequence<byte> CreateCompressedInput(
        byte[] compressed,
        CompressedInputShape inputShape)
        => inputShape switch
        {
            CompressedInputShape.SingleSegment => new ReadOnlySequence<byte>(compressed),
            CompressedInputShape.TwoSegments => CreateEvenlySegmented(compressed, 2),
            CompressedInputShape.EightSegments => CreateEvenlySegmented(compressed, 8),
            CompressedInputShape.RealisticSegments => CreateRealisticSegments(compressed),
            _ => throw new ArgumentOutOfRangeException(nameof(inputShape))
        };

    private static ReadOnlySequence<byte> CreateEvenlySegmented(byte[] bytes, int segmentCount)
    {
        if (bytes.Length < segmentCount)
            throw new ArgumentOutOfRangeException(nameof(segmentCount));

        BufferSegment? first = null;
        BufferSegment? last = null;
        var offset = 0;
        for (var segment = 1; segment <= segmentCount; segment++)
        {
            var nextOffset = checked((int)((long)bytes.Length * segment / segmentCount));
            AppendSegment(ref first, ref last, bytes.AsMemory(offset, nextOffset - offset));
            offset = nextOffset;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static ReadOnlySequence<byte> CreateRealisticSegments(byte[] bytes)
    {
        var random = new Random(89_2026);
        BufferSegment? first = null;
        BufferSegment? last = null;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var length = Math.Min(random.Next(4 * 1024, 16 * 1024 + 1), bytes.Length - offset);
            AppendSegment(ref first, ref last, bytes.AsMemory(offset, length));
            offset += length;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static void AppendSegment(
        ref BufferSegment? first,
        ref BufferSegment? last,
        ReadOnlyMemory<byte> memory)
    {
        var segment = new BufferSegment(memory);
        if (first is null)
            first = segment;
        else
            last!.SetNext(segment);
        last = segment;
    }

    private static int CountSegments(ReadOnlySequence<byte> input)
    {
        var count = 0;
        foreach (var _ in input)
            count++;
        return count;
    }

    private static string GetInputShapeName(CompressedInputShape inputShape)
        => inputShape switch
        {
            CompressedInputShape.SingleSegment => "SingleSegment",
            CompressedInputShape.TwoSegments => "2Segments",
            CompressedInputShape.EightSegments => "8Segments",
            CompressedInputShape.RealisticSegments => "RealisticSegments",
            _ => throw new ArgumentOutOfRangeException(nameof(inputShape))
        };

    private static CompressionMeasurement Measure(
        int iterations,
        int payloadSize,
        Func<int> operation)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var checksum = 0L;
        for (var iteration = 0; iteration < iterations; iteration++)
            checksum += operation();
        var elapsed = Stopwatch.GetElapsedTime(started);
        GC.KeepAlive(checksum);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var megabytes = (long)payloadSize * iterations / (1024d * 1024d);
        return new CompressionMeasurement(
            megabytes / elapsed.TotalSeconds,
            allocated / (double)iterations);
    }

    private static CompressionMeasurement MeasureDecompression(
        int iterations,
        ISharpLinkCompressionProvider provider,
        ReadOnlySequence<byte> compressed,
        int originalLength)
    {
        var output = new ArrayBufferWriter<byte>(originalLength);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var checksum = 0L;
        for (var iteration = 0; iteration < iterations; iteration++)
            checksum += Decompress(provider, compressed, output, originalLength);
        var elapsed = Stopwatch.GetElapsedTime(started);
        GC.KeepAlive(checksum);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var megabytes = (long)originalLength * iterations / (1024d * 1024d);
        return new CompressionMeasurement(
            megabytes / elapsed.TotalSeconds,
            allocated / (double)iterations);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }
        return null;
    }

    private readonly record struct CompressionMeasurement(
        double ThroughputMegabytesPerSecond,
        double AllocatedBytesPerOperation);

    private readonly record struct CompressionInput(
        CompressedInputShape Shape,
        ReadOnlySequence<byte> Sequence,
        int SegmentCount);

    private enum CompressedInputShape
    {
        SingleSegment,
        TwoSegments,
        EightSegments,
        RealisticSegments
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public void SetNext(BufferSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}

internal sealed record CompressionEvidenceResult(
    string Algorithm,
    string CompressionLevel,
    int PayloadSize,
    bool Compressible,
    string CompressedInputShape,
    int CompressedInputSegments,
    int Round,
    int CompressedBytes,
    double CompressionRatio,
    double CompressionMegabytesPerSecond,
    double DecompressionMegabytesPerSecond,
    double CompressionAllocatedBytesPerOperation,
    double DecompressionAllocatedBytesPerOperation);
