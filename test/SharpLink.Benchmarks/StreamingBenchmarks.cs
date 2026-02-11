using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput)]
public class StreamingBenchmarks
{
    private BenchmarkEnvironment _env = null!;
    private IReadOnlyList<int> _leftNumbers = [];
    private IReadOnlyList<int> _rightNumbers = [];
    private IReadOnlyList<string> _messages = [];

    [Params(32, 256)]
    public int StreamSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _env = await BenchmarkEnvironment.CreateAsync();
        _leftNumbers = Enumerable.Range(0, StreamSize).ToArray();
        _rightNumbers = Enumerable.Range(StreamSize, StreamSize).ToArray();
        _messages = Enumerable.Range(0, StreamSize).Select(i => $"msg-{i}").ToArray();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _env.DisposeAsync();
    }

    [Benchmark]
    public ValueTask<int> Rpc_ClientStreamUpload()
        => _env.Rpc.UploadNumbersAsync(BenchmarkEnvironment.ToStream(_leftNumbers));

    [Benchmark]
    public async Task<int> Rpc_ServerStreamDownload()
    {
        var sum = 0;
        await foreach (var value in _env.Rpc.DownloadNumbersAsync(StreamSize))
        {
            sum += value;
        }

        return sum;
    }

    [Benchmark]
    public async Task<int> Rpc_DuplexStream()
    {
        var count = 0;
        await foreach (var value in _env.Rpc.DuplexAsync(BenchmarkEnvironment.ToStream(_messages)))
        {
            count += value.Length;
        }

        return count;
    }

    [Benchmark]
    public ValueTask<int> Rpc_ClientMultiStreamMerge()
        => _env.Rpc.MergeStreamsAsync(
            BenchmarkEnvironment.ToStream(_leftNumbers),
            BenchmarkEnvironment.ToStream(_rightNumbers));
}
