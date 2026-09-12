using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class AdmissionFreshRateRpcBenchmarks
{
    private BenchmarkEnvironment _tokenBucket = null!;
    private BenchmarkEnvironment _fixedWindow = null!;
    private BenchmarkEnvironment _slidingWindow = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _tokenBucket = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server => server.EnableAdmissionControl(options =>
                options.Global.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = 1_000_000_000;
                    rate.TokensPerPeriod = 10_000;
                    rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
                })));
        _fixedWindow = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server => server.EnableAdmissionControl(options =>
                options.Global.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = 1_000_000_000;
                    rate.Window = TimeSpan.FromHours(1);
                })));
        _slidingWindow = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server => server.EnableAdmissionControl(options =>
                options.Global.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = 1_000_000_000;
                    rate.Window = TimeSpan.FromHours(1);
                    rate.SegmentsPerWindow = 4;
                })));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _tokenBucket.DisposeAsync();
        await _fixedWindow.DisposeAsync();
        await _slidingWindow.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> TokenBucketImmediatePermit()
        => _tokenBucket.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> FixedWindowImmediatePermit()
        => _fixedWindow.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> SlidingWindowImmediatePermit()
        => _slidingWindow.Rpc.AddAsync(10, 20);
}
