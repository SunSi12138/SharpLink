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
    private static readonly int[] s_payloadSizes = [1024, 4096, 65_536, 1_048_576];

    internal static async Task RunAsync(string[] args)
    {
        var outputPath = GetOption(args, "--output") ??
            Path.Combine("artifacts", "performance", "v0.7.4", "compression-provider.json");
        var results = new List<CompressionEvidenceResult>(120);
        foreach (var level in s_levels)
        {
            foreach (var payloadSize in s_payloadSizes)
            {
                foreach (var compressible in new[] { true, false })
                {
                    var provider = CompressionProviderBenchmarks.CreateProvider(level);
                    var payload = CreatePayload(payloadSize, compressible);
                    var compressed = Compress(provider, payload);
                    _ = Decompress(provider, compressed, payloadSize);
                    var iterations = Math.Clamp((16 * 1024 * 1024) / payloadSize, 4, 4096);

                    for (var round = 1; round <= 5; round++)
                    {
                        WarmUp(provider, payload, compressed);
                        var compression = Measure(
                            iterations,
                            payloadSize,
                            () => Compress(provider, payload).Length);
                        var decompression = Measure(
                            iterations,
                            payloadSize,
                            () => Decompress(provider, compressed, payloadSize));
                        results.Add(new CompressionEvidenceResult(
                            "brotli",
                            level,
                            payloadSize,
                            compressible,
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
        byte[] compressed,
        int originalLength)
    {
        var output = new ArrayBufferWriter<byte>(originalLength);
        var result = provider.Decompress(
            new ReadOnlySequence<byte>(compressed),
            output,
            originalLength);
        if (result.ConsumedBytes != compressed.Length || result.WrittenBytes != originalLength)
            throw new InvalidOperationException("Compression provider returned inconsistent decompression counts.");
        return result.WrittenBytes;
    }

    private static void WarmUp(
        ISharpLinkCompressionProvider provider,
        byte[] payload,
        byte[] compressed)
    {
        for (var iteration = 0; iteration < 3; iteration++)
        {
            _ = Compress(provider, payload);
            _ = Decompress(provider, compressed, payload.Length);
        }
    }

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
}

internal sealed record CompressionEvidenceResult(
    string Algorithm,
    string CompressionLevel,
    int PayloadSize,
    bool Compressible,
    int Round,
    int CompressedBytes,
    double CompressionRatio,
    double CompressionMegabytesPerSecond,
    double DecompressionMegabytesPerSecond,
    double CompressionAllocatedBytesPerOperation,
    double DecompressionAllocatedBytesPerOperation);
