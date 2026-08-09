using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[AllStatisticsColumn]
public class ServerFeatureMatrixBenchmarks
{
    private FeatureBenchmarkCase _case = null!;

    [ParamsSource(nameof(Scenarios))]
    public ServerFeatureScenario Scenario { get; set; }

    public IEnumerable<ServerFeatureScenario> Scenarios =>
        Enum.GetValues<ServerFeatureScenario>();

    [GlobalSetup]
    public async Task Setup() => _case = await FeatureBenchmarkCase.CreateAsync(Scenario);

    [GlobalCleanup]
    public async ValueTask Cleanup() => await _case.DisposeAsync();

    [Benchmark]
    public ValueTask<int> Unary() => _case.InvokeAsync();
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[AllStatisticsColumn]
public class ClientFeatureMatrixBenchmarks
{
    private FeatureBenchmarkCase _case = null!;

    [ParamsSource(nameof(Scenarios))]
    public ClientFeatureScenario Scenario { get; set; }

    public IEnumerable<ClientFeatureScenario> Scenarios =>
        Enum.GetValues<ClientFeatureScenario>();

    [GlobalSetup]
    public async Task Setup() => _case = await FeatureBenchmarkCase.CreateAsync(Scenario);

    [GlobalCleanup]
    public async ValueTask Cleanup() => await _case.DisposeAsync();

    [Benchmark]
    public ValueTask<int> Unary() => _case.InvokeAsync();
}
