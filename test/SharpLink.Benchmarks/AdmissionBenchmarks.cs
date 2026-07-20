using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class AdmissionRpcBenchmarks
{
    private BenchmarkEnvironment _disabled = null!;
    private BenchmarkEnvironment _immediate = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _disabled = await BenchmarkEnvironment.CreateAsync();
        _immediate = await BenchmarkEnvironment.CreateAsync(
            configureServer: builder => builder.UseAdmissionControl(
                options => options.Global.UseConcurrency(1024)));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _disabled.DisposeAsync();
        await _immediate.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Disabled() => _disabled.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> ImmediatePermit() => _immediate.Rpc.AddAsync(10, 20);
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class AdmissionControllerBenchmarks
{
    private SharpLinkAdmissionController _immediate = null!;
    private SharpLinkAdmissionController _reject = null!;
    private SharpLinkAdmissionController _queue = null!;
    private AdmissionLease _rejectBlocker = null!;
    private SharpLinkAdmissionContext _context = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "benchmark", null, null, null);
        _immediate = CreateController(queue: false);
        _reject = CreateController(queue: false);
        _queue = CreateController(queue: true);
        _rejectBlocker = (await _reject.AcquireAsync(
            _context, 1, false, CancellationToken.None)).Lease!;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _rejectBlocker.Dispose();
        await _immediate.DisposeAsync();
        await _reject.DisposeAsync();
        await _queue.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public void ImmediatePermit()
    {
        var decision = _immediate.AcquireAsync(
            _context, 1, false, CancellationToken.None).Result;
        decision.Lease!.Dispose();
    }

    [Benchmark]
    public bool ImmediateRejection()
        => _reject.AcquireAsync(_context, 1, false, CancellationToken.None).Result.IsAcquired;

    [Benchmark]
    public async ValueTask QueueAndRelease()
    {
        var blocker = (await _queue.AcquireAsync(
            _context, 1, true, CancellationToken.None)).Lease!;
        var pending = _queue.AcquireAsync(_context, 1, true, CancellationToken.None);
        blocker.Dispose();
        var acquired = await pending.ConfigureAwait(false);
        acquired.Lease!.Dispose();
    }

    private static SharpLinkAdmissionController CreateController(bool queue)
    {
        var options = new SharpLinkAdmissionControlOptions();
        options.Global.UseConcurrency(1);
        if (queue)
        {
            options.MaxQueuedCalls = 1;
            options.MaxQueuedBytes = 1024;
            options.MaxQueueDelay = TimeSpan.FromSeconds(1);
        }
        return SharpLinkAdmissionController.Create(options, []);
    }
}
