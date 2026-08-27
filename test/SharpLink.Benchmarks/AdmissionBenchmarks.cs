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
    private BenchmarkEnvironment _buildTimeImmediate = null!;
    private BenchmarkEnvironment _runtimeImmediate = null!;
    private BenchmarkEnvironment _afterConcurrencyResize = null!;
    private BenchmarkEnvironment _afterQueuePolicyUpdates = null!;
    private BenchmarkEnvironment _afterSameAlgorithmRateUpdates = null!;
    private BenchmarkEnvironment _afterAlgorithmReplacements = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _disabled = await BenchmarkEnvironment.CreateAsync();
        _buildTimeImmediate = await BenchmarkEnvironment.CreateAsync(
            configureServer: builder => builder.UseAdmissionControl(
                options => options.Global.UseConcurrency(1024)));
        _runtimeImmediate = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server => server.EnableAdmissionControl(
                options => options.Global.UseConcurrency(1024)));
        _afterConcurrencyResize = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                server.EnableAdmissionControl(options => options.Global.UseConcurrency(1024));
                for (var index = 0; index < 64; index++)
                {
                    var permitLimit = (index & 1) == 0 ? 2048 : 1024;
                    server.UpdateAdmissionControl(options =>
                        options.Global.UseConcurrency(permitLimit));
                }
            });
        _afterQueuePolicyUpdates = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                server.EnableAdmissionControl(options => ConfigureQueuePolicy(options, 128, 1024 * 1024, 1));
                for (var index = 0; index < 64; index++)
                {
                    var expanded = (index & 1) == 0;
                    server.UpdateAdmissionControl(options => ConfigureQueuePolicy(
                        options,
                        expanded ? 256 : 128,
                        expanded ? 2 * 1024 * 1024 : 1024 * 1024,
                        expanded ? 2 : 1));
                }
            });
        _afterSameAlgorithmRateUpdates = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                server.EnableAdmissionControl(options => ConfigureTokenBucket(
                    options,
                    tokenLimit: 1_000_000_000,
                    tokensPerPeriod: 10_000,
                    TimeSpan.FromHours(1)));
                for (var index = 0; index < 64; index++)
                {
                    var tokenLimit = (index & 1) == 0 ? 999_000_000 : 1_000_000_000;
                    var tokensPerPeriod = (index & 1) == 0 ? 9_000 : 10_000;
                    server.UpdateAdmissionControl(options => ConfigureTokenBucket(
                        options,
                        tokenLimit,
                        tokensPerPeriod,
                        TimeSpan.FromHours(1)));
                }
            });
        _afterAlgorithmReplacements = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                server.EnableAdmissionControl(options => ConfigureBenchmarkRate(options, BenchmarkRateKind.TokenBucket));
                for (var index = 0; index < 64; index++)
                {
                    var kind = (BenchmarkRateKind)((index + 1) % 3);
                    server.UpdateAdmissionControl(options => ConfigureBenchmarkRate(options, kind));
                }
            });
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _disabled.DisposeAsync();
        await _buildTimeImmediate.DisposeAsync();
        await _runtimeImmediate.DisposeAsync();
        await _afterConcurrencyResize.DisposeAsync();
        await _afterQueuePolicyUpdates.DisposeAsync();
        await _afterSameAlgorithmRateUpdates.DisposeAsync();
        await _afterAlgorithmReplacements.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Disabled() => _disabled.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> BuildTimeEnabledImmediatePermit()
        => _buildTimeImmediate.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> RuntimeEnabledImmediatePermit()
        => _runtimeImmediate.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> SteadyStateAfterRepeatedConcurrencyResize()
        => _afterConcurrencyResize.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> SteadyStateAfterRepeatedQueuePolicyUpdates()
        => _afterQueuePolicyUpdates.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> SteadyStateAfterRepeatedSameAlgorithmRateUpdates()
        => _afterSameAlgorithmRateUpdates.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> SteadyStateAfterRepeatedRateAlgorithmReplacements()
        => _afterAlgorithmReplacements.Rpc.AddAsync(10, 20);

    private static void ConfigureQueuePolicy(
        SharpLinkAdmissionControlOptions options,
        int maxQueuedCalls,
        long maxQueuedBytes,
        int maxQueueDelaySeconds)
    {
        options.Global.UseConcurrency(1024);
        options.MaxQueuedCalls = maxQueuedCalls;
        options.MaxQueuedBytes = maxQueuedBytes;
        options.MaxQueueDelay = TimeSpan.FromSeconds(maxQueueDelaySeconds);
        options.QueueOneWayCalls = (maxQueuedCalls & 1) == 0;
    }

    private static void ConfigureBenchmarkRate(
        SharpLinkAdmissionControlOptions options,
        BenchmarkRateKind kind)
    {
        switch (kind)
        {
            case BenchmarkRateKind.TokenBucket:
                ConfigureTokenBucket(
                    options,
                    tokenLimit: 1_000_000_000,
                    tokensPerPeriod: 10_000,
                    TimeSpan.FromHours(1));
                break;
            case BenchmarkRateKind.FixedWindow:
                options.Global.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = 1_000_000_000;
                    rate.Window = TimeSpan.FromHours(1);
                });
                break;
            case BenchmarkRateKind.SlidingWindow:
                options.Global.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = 1_000_000_000;
                    rate.Window = TimeSpan.FromHours(1);
                    rate.SegmentsPerWindow = 4;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void ConfigureTokenBucket(
        SharpLinkAdmissionControlOptions options,
        int tokenLimit,
        int tokensPerPeriod,
        TimeSpan period)
        => options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = tokenLimit;
            rate.TokensPerPeriod = tokensPerPeriod;
            rate.ReplenishmentPeriod = period;
        });

    private enum BenchmarkRateKind
    {
        TokenBucket,
        FixedWindow,
        SlidingWindow
    }
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
            1, 2, RpcMethodKind.Unary, "benchmark", null, null);
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

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class AdmissionRateControllerBenchmarks
{
    private SharpLinkAdmissionController _tokenPermit = null!;
    private SharpLinkAdmissionController _tokenReject = null!;
    private SharpLinkAdmissionController _fixedPermit = null!;
    private SharpLinkAdmissionController _fixedReject = null!;
    private SharpLinkAdmissionController _slidingPermit = null!;
    private SharpLinkAdmissionController _slidingReject = null!;
    private SharpLinkAdmissionContext _context = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "rate-benchmark", null, null);
        _tokenPermit = CreateRateController(RateKind.TokenBucket, 1_000_000_000);
        _tokenReject = CreateRateController(RateKind.TokenBucket, 1);
        _fixedPermit = CreateRateController(RateKind.FixedWindow, 1_000_000_000);
        _fixedReject = CreateRateController(RateKind.FixedWindow, 1);
        _slidingPermit = CreateRateController(RateKind.SlidingWindow, 1_000_000_000);
        _slidingReject = CreateRateController(RateKind.SlidingWindow, 1);

        await ConsumeAsync(_tokenReject);
        await ConsumeAsync(_fixedReject);
        await ConsumeAsync(_slidingReject);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _tokenPermit.DisposeAsync();
        await _tokenReject.DisposeAsync();
        await _fixedPermit.DisposeAsync();
        await _fixedReject.DisposeAsync();
        await _slidingPermit.DisposeAsync();
        await _slidingReject.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public void TokenBucketImmediatePermit()
        => AcquireAndDispose(_tokenPermit);

    [Benchmark]
    public bool TokenBucketImmediateReject()
        => _tokenReject.AcquireAsync(_context, 1, false, CancellationToken.None).Result.IsAcquired;

    [Benchmark]
    public void FixedWindowImmediatePermit()
        => AcquireAndDispose(_fixedPermit);

    [Benchmark]
    public bool FixedWindowImmediateReject()
        => _fixedReject.AcquireAsync(_context, 1, false, CancellationToken.None).Result.IsAcquired;

    [Benchmark]
    public void SlidingWindowImmediatePermit()
        => AcquireAndDispose(_slidingPermit);

    [Benchmark]
    public bool SlidingWindowImmediateReject()
        => _slidingReject.AcquireAsync(_context, 1, false, CancellationToken.None).Result.IsAcquired;

    private void AcquireAndDispose(SharpLinkAdmissionController controller)
    {
        var decision = controller.AcquireAsync(
            _context, 1, false, CancellationToken.None).Result;
        decision.Lease!.Dispose();
    }

    private async Task ConsumeAsync(SharpLinkAdmissionController controller)
    {
        var decision = await controller.AcquireAsync(
            _context, 1, false, CancellationToken.None);
        decision.Lease!.Dispose();
    }

    private static SharpLinkAdmissionController CreateRateController(RateKind kind, int permitLimit)
    {
        var options = new SharpLinkAdmissionControlOptions();
        switch (kind)
        {
            case RateKind.TokenBucket:
                options.Global.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = permitLimit;
                    rate.TokensPerPeriod = permitLimit;
                    rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
                });
                break;
            case RateKind.FixedWindow:
                options.Global.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = permitLimit;
                    rate.Window = TimeSpan.FromHours(1);
                });
                break;
            case RateKind.SlidingWindow:
                options.Global.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = permitLimit;
                    rate.Window = TimeSpan.FromHours(1);
                    rate.SegmentsPerWindow = 4;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return SharpLinkAdmissionController.Create(options, []);
    }

    private enum RateKind
    {
        TokenBucket,
        FixedWindow,
        SlidingWindow
    }
}
