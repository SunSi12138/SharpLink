using System.Threading.RateLimiting;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class DynamicFixedWindowPrototypeBenchmarks
{
    private AdmissionRateState _currentPermit = null!;
    private AdmissionRateState _currentReject = null!;
    private DynamicFixedWindowRateLimiter _dynamicPermit = null!;
    private DynamicFixedWindowRateLimiter _dynamicReject = null!;

    [GlobalSetup]
    public void Setup()
    {
        _currentPermit = CreateCurrent(1_000_000_000, TimeSpan.FromHours(1));
        _currentReject = CreateCurrent(1, TimeSpan.FromHours(1));
        _dynamicPermit = new DynamicFixedWindowRateLimiter(1_000_000_000, TimeSpan.FromHours(1));
        _dynamicReject = new DynamicFixedWindowRateLimiter(1, TimeSpan.FromHours(1));
        using var currentConsumed = _currentReject.AttemptAcquire(1);
        using var dynamicConsumed = _dynamicReject.AttemptAcquire(1);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _currentPermit.Dispose();
        _currentReject.Dispose();
        _dynamicPermit.Dispose();
        _dynamicReject.Dispose();
    }

    [Benchmark(Baseline = true)]
    public bool CurrentFixedWindowImmediatePermit()
    {
        using var lease = _currentPermit.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    [Benchmark]
    public bool DynamicFixedWindowImmediatePermit()
    {
        using var lease = _dynamicPermit.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    [Benchmark]
    public bool CurrentFixedWindowImmediateReject()
    {
        using var lease = _currentReject.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    [Benchmark]
    public bool DynamicFixedWindowImmediateReject()
    {
        using var lease = _dynamicReject.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    private static AdmissionRateState CreateCurrent(int permitLimit, TimeSpan window)
    {
        var options = new SharpLinkAdmissionRuleOptions();
        options.UseFixedWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = window;
        });
        return AdmissionRateState.Create(options, TimeProvider.System);
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class DynamicFixedWindowUpdatePrototypeBenchmarks
{
    private AdmissionRateState _current = null!;
    private DynamicFixedWindowRateLimiter _immediate = null!;
    private DynamicFixedWindowRateLimiter _nextBoundary = null!;
    private bool _toggle;

    [GlobalSetup]
    public void Setup()
    {
        _current = CreateCurrent(1_000_000_000, TimeSpan.FromHours(1));
        _immediate = new DynamicFixedWindowRateLimiter(1_000_000_000, TimeSpan.FromHours(1));
        _nextBoundary = new DynamicFixedWindowRateLimiter(1_000_000_000, TimeSpan.FromHours(1));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _current.Dispose();
        _immediate.Dispose();
        _nextBoundary.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void CurrentFixedWindowSuccessorTransition()
    {
        _toggle = !_toggle;
        var target = CreateCurrent(
            _toggle ? 999_000_000 : 1_000_000_000,
            _toggle ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1),
            _current);
        var previous = _current;
        previous.CommitTransitionTo(target);
        _current = target;
        previous.Dispose();
    }

    [Benchmark]
    public void DynamicFixedWindowImmediateUpdate()
    {
        _toggle = !_toggle;
        _immediate.Update(
            _toggle ? 999_000_000 : 1_000_000_000,
            _toggle ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1),
            DynamicFixedWindowActivationMode.Immediate);
    }

    [Benchmark]
    public void DynamicFixedWindowNextBoundaryUpdate()
    {
        _toggle = !_toggle;
        _nextBoundary.Update(
            _toggle ? 999_000_000 : 1_000_000_000,
            _toggle ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1),
            DynamicFixedWindowActivationMode.NextWindowBoundary);
    }

    private static AdmissionRateState CreateCurrent(
        int permitLimit,
        TimeSpan window,
        AdmissionRateState? transitionSource = null)
    {
        var options = new SharpLinkAdmissionRuleOptions();
        options.UseFixedWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = window;
        });
        return AdmissionRateState.Create(options, TimeProvider.System, transitionSource);
    }
}
