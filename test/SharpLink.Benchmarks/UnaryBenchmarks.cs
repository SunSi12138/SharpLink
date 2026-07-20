using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    invocationCount: 4096,
    iterationCount: 10)]
public class UnaryBenchmarks
{
    private BenchmarkEnvironment _env = null!;
    private string _echoText = string.Empty;
    private int[] _array = [];
    private List<int> _list = [];
    private Memory<byte> _memory;
    private BenchmarkPayload _payload = null!;

    [Params(16, 256)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _env = await BenchmarkEnvironment.CreateAsync();
        _echoText = new string('x', PayloadSize);
        _array = Enumerable.Range(1, PayloadSize).ToArray();
        _list = _array.ToList();
        _memory = new Memory<byte>(_array.Select(v => (byte)(v % byte.MaxValue)).ToArray());
        _payload = new BenchmarkPayload
        {
            Name = $"payload-{PayloadSize}",
            Values = _array,
            Tags = Enumerable.Range(0, Math.Max(1, PayloadSize / 8)).Select(i => $"t{i}").ToList(),
            Buffer = _memory
        };
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _env.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Local_Add() => _env.LocalService.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> Rpc_Add() => _env.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<string> Rpc_EchoString() => _env.Rpc.EchoAsync(_echoText);

    [Benchmark]
    public ValueTask<BenchmarkPayload> Rpc_EchoPayload() => _env.Rpc.EchoPayloadAsync(_payload);

    [Benchmark]
    public ValueTask<int> Rpc_SumArray() => _env.Rpc.SumArrayAsync(_array);

    [Benchmark]
    public ValueTask<int> Rpc_SumList() => _env.Rpc.SumListAsync(_list);

    [Benchmark]
    public ValueTask<int> Rpc_SumMemory() => _env.Rpc.SumMemoryAsync(_memory);

    [Benchmark]
    public ValueTask Rpc_OnewayPublish() => _env.Rpc.PublishEventAsync(7, Environment.TickCount64, "bench");
}
