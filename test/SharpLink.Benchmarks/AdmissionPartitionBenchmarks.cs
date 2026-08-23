using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class AdmissionPartitionReleaseBenchmarks
{
    private readonly FrozenTimeProvider _time = new();
    private AdmissionPartitionPool _pool = null!;
    private SharpLinkAdmissionContext _context = null!;
    private string _key = string.Empty;

    [Params(1, 128, 1024)]
    public int Partitions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var options = new SharpLinkPartitionAdmissionOptions
        {
            MaxPartitions = Partitions,
            IdleTimeout = TimeSpan.FromMinutes(5)
        };
        options.UseConcurrency(1);
        _context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "partition-benchmark", null, null, null);
        _pool = new AdmissionPartitionPool(_ => _key, options, queueLimit: 0, _time);

        for (var index = 0; index < Partitions; index++)
        {
            _key = $"partition-{index}";
            _pool.TryAcquire(_context)!.Dispose();
        }
        _key = "partition-0";
    }

    [GlobalCleanup]
    public void Cleanup() => _pool.Dispose();

    [Benchmark]
    public void AcquireReleaseRecentlyIdle()
    {
        var lease = _pool.TryAcquire(_context)!;
        lease.Dispose();
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
    }
}
