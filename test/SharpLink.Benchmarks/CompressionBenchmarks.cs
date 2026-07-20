using System;
using System.Buffers;
using System.IO.Compression;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class CompressionProviderBenchmarks
{
    private ISharpLinkCompressionProvider _provider = null!;
    private byte[] _payload = [];
    private byte[] _compressed = [];

    [Params("gzip", "deflate", "brotli")]
    public string Algorithm { get; set; } = "gzip";

    [Params("fastest", "optimal", "smallest")]
    public string CompressionLevelName { get; set; } = "fastest";

    [Params(1024, 4096, 65_536, 1_048_576)]
    public int PayloadSize { get; set; }

    [Params(true, false)]
    public bool Compressible { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _provider = CreateProvider(Algorithm, CompressionLevelName);
        _payload = new byte[PayloadSize];
        if (Compressible)
            Array.Fill(_payload, (byte)0x2a);
        else
            new Random(42).NextBytes(_payload);
        var output = new ArrayBufferWriter<byte>(PayloadSize * 2 + 1024);
        _provider.CompressAsync(
            new ReadOnlySequence<byte>(_payload),
            output,
            PayloadSize * 2 + 1024).GetAwaiter().GetResult();
        _compressed = output.WrittenSpan.ToArray();
    }

    [Benchmark]
    public int Compress()
    {
        var output = new ArrayBufferWriter<byte>(PayloadSize * 2 + 1024);
        return _provider.CompressAsync(
            new ReadOnlySequence<byte>(_payload),
            output,
            PayloadSize * 2 + 1024).Result.WrittenBytes;
    }

    [Benchmark]
    public int Decompress()
    {
        var output = new ArrayBufferWriter<byte>(PayloadSize);
        return _provider.DecompressAsync(
            new ReadOnlySequence<byte>(_compressed),
            output,
            PayloadSize).Result.WrittenBytes;
    }

    internal static ISharpLinkCompressionProvider CreateProvider(
        string algorithm,
        string levelName = "fastest")
    {
        var level = levelName switch
        {
            "fastest" => CompressionLevel.Fastest,
            "optimal" => CompressionLevel.Optimal,
            "smallest" => CompressionLevel.SmallestSize,
            "nocompression" => CompressionLevel.NoCompression,
            _ => throw new ArgumentOutOfRangeException(nameof(levelName))
        };
        return algorithm switch
        {
            "gzip" => SharpLinkCompressionProviders.CreateGzip(level),
            "deflate" => SharpLinkCompressionProviders.CreateDeflate(level),
            "brotli" => SharpLinkCompressionProviders.CreateBrotli(level),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
    }
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class CompressionRpcBenchmarks
{
    private BenchmarkEnvironment _raw = null!;
    private BenchmarkEnvironment _compressed = null!;
    private string _payload = string.Empty;

    [Params("gzip", "deflate", "brotli")]
    public string Algorithm { get; set; } = "gzip";

    [Params("fastest", "optimal", "smallest")]
    public string CompressionLevelName { get; set; } = "fastest";

    [Params(1024, 4096, 65_536, 1_048_576)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _payload = new string('x', PayloadSize);
        _raw = await BenchmarkEnvironment.CreateAsync();
        _compressed = await BenchmarkEnvironment.CreateAsync(
            configureServerRuntime: ConfigureCompression,
            configureClientRuntime: ConfigureCompression);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _raw.DisposeAsync();
        await _compressed.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<string> Raw() => _raw.Rpc.EchoAsync(_payload);

    [Benchmark]
    public ValueTask<string> Compressed() => _compressed.Rpc.EchoAsync(_payload);

    private void ConfigureCompression(SharpLinkRuntimeOptions options)
    {
        options.Protocol.MaxFramePayloadBytes = Math.Max(
            options.Protocol.MaxFramePayloadBytes,
            PayloadSize * 2 + 1024);
        options.Compression.Providers.Add(
            CompressionProviderBenchmarks.CreateProvider(Algorithm, CompressionLevelName));
    }
}
