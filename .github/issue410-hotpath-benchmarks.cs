using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

public enum Issue410DirectRateKind
{
    TokenBucket,
    FixedWindow,
    SlidingWindow
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 10)]
public class Issue410DirectRatePermitBenchmarks
{
    private AdmissionDynamicRateState _state = null!;

    [Params(
        Issue410DirectRateKind.TokenBucket,
        Issue410DirectRateKind.FixedWindow,
        Issue410DirectRateKind.SlidingWindow)]
    public Issue410DirectRateKind Kind { get; set; }

    [GlobalSetup]
    public void Setup()
        => _state = new AdmissionDynamicRateState(
            Issue410DirectRateDefinitions.Create(Kind, 1_000_000_000),
            TimeProvider.System);

    [GlobalCleanup]
    public void Cleanup() => _state.Dispose();

    [Benchmark]
    public bool AttemptAcquire()
        => _state.AttemptAcquire(1).IsAcquired;
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 10)]
public class Issue410DirectRateRejectBenchmarks
{
    private AdmissionDynamicRateState _state = null!;

    [Params(
        Issue410DirectRateKind.TokenBucket,
        Issue410DirectRateKind.FixedWindow,
        Issue410DirectRateKind.SlidingWindow)]
    public Issue410DirectRateKind Kind { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _state = new AdmissionDynamicRateState(
            Issue410DirectRateDefinitions.Create(Kind, 1),
            TimeProvider.System);
        if (!_state.AttemptAcquire(1).IsAcquired)
            throw new InvalidOperationException("Failed to consume the one setup permit.");
    }

    [GlobalCleanup]
    public void Cleanup() => _state.Dispose();

    [Benchmark]
    public bool AttemptAcquire()
        => _state.AttemptAcquire(1).IsAcquired;
}

internal static class Issue410DirectRateDefinitions
{
    private static readonly long OneHourTicks = TimeSpan.FromHours(1).Ticks;

    internal static AdmissionRateStateDefinition Create(Issue410DirectRateKind kind, int limit)
        => kind switch
        {
            Issue410DirectRateKind.TokenBucket => new AdmissionRateStateDefinition(
                AdmissionRateStateKind.TokenBucket,
                limit,
                limit,
                OneHourTicks,
                0),
            Issue410DirectRateKind.FixedWindow => new AdmissionRateStateDefinition(
                AdmissionRateStateKind.FixedWindow,
                limit,
                0,
                OneHourTicks,
                0),
            Issue410DirectRateKind.SlidingWindow => new AdmissionRateStateDefinition(
                AdmissionRateStateKind.SlidingWindow,
                limit,
                0,
                OneHourTicks,
                4),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}
