using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class AdmissionPartitionControllerBenchmarks
{
    private SharpLinkAdmissionController _controller = null!;
    private SharpLinkAdmissionContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "issue-305", null, null, null);
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(
            _ => "hot",
            partition =>
            {
                partition.MaxPartitions = 1;
                partition.UseConcurrency(1024);
            });
        _controller = SharpLinkAdmissionController.Create(options, []);
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _controller.DisposeAsync();

    [Benchmark]
    public void PartitionConcurrencyImmediate()
    {
        var decision = _controller.AcquireAsync(
            _context, 1, false, CancellationToken.None).Result;
        decision.Lease!.Dispose();
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class AdmissionPartitionRpcBenchmarks
{
    private BenchmarkEnvironment _disabled = null!;
    private BenchmarkEnvironment _partition = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _disabled = await BenchmarkEnvironment.CreateAsync();
        _partition = await BenchmarkEnvironment.CreateAsync(
            configureServer: builder => builder.UseAdmissionControl(
                options => options.UsePartition(
                    _ => "hot",
                    partition =>
                    {
                        partition.MaxPartitions = 1;
                        partition.UseConcurrency(1024);
                    })));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _disabled.DisposeAsync();
        await _partition.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Disabled() => _disabled.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> PartitionConcurrencyImmediate()
        => _partition.Rpc.AddAsync(10, 20);
}
