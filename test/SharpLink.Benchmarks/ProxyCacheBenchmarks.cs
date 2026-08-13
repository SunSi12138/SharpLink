using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.DynamicPlugin;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    invocationCount: 1000,
    iterationCount: 10)]
public class ProxyCacheBenchmarks
{
    private BenchmarkEnvironment _staticEnvironment = null!;
    private FeatureBenchmarkCase _dynamicCase = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _staticEnvironment = await BenchmarkEnvironment.CreateAsync();
        _dynamicCase = await FeatureBenchmarkCase.CreateAsync(ServerFeatureScenario.DynamicServiceActual);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _staticEnvironment.DisposeAsync();
        await _dynamicCase.DisposeAsync();
    }

    [Benchmark]
    public IBenchmarkRpc RepeatedGet_Static() => _staticEnvironment.Get<IBenchmarkRpc>();

    [Benchmark]
    public IDynamicPluginService RepeatedGet_Dynamic() => _dynamicCase.Get<IDynamicPluginService>();
}
