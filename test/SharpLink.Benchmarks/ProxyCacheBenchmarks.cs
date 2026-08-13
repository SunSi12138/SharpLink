using System;
using System.Threading.Tasks;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

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
    private DynamicProxyCacheTarget _dynamicTarget = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _staticEnvironment = await BenchmarkEnvironment.CreateAsync();
        _dynamicTarget = DynamicProxyCacheTarget.Create();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _staticEnvironment.DisposeAsync();
        _dynamicTarget.Dispose();
    }

    [Benchmark]
    public IBenchmarkRpc RepeatedGet_Static() => _staticEnvironment.Get<IBenchmarkRpc>();

    [Benchmark]
    public object RepeatedGet_Dynamic() => _dynamicTarget.GetProxy();

    private sealed class DynamicProxyCacheTarget : IDisposable
    {
        private readonly AssemblyLoadContext _loadContext;
        private readonly ISharpLinkClient _client;
        private readonly MethodInfo _getMethod;

        private DynamicProxyCacheTarget(
            AssemblyLoadContext loadContext,
            ISharpLinkClient client,
            Type contractType)
        {
            _loadContext = loadContext;
            _client = client;
            _getMethod = typeof(ISharpLinkClient)
                .GetMethod(nameof(ISharpLinkClient.Get))!
                .MakeGenericMethod(contractType);
        }

        public static DynamicProxyCacheTarget Create()
        {
            var loadContext = new AssemblyLoadContext(
                "ProxyCacheBenchmarks-Dynamic",
                isCollectible: true);
            var contractPath = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "SharpLink.DynamicPlugin.Contracts.dll");
            var contractAssembly = loadContext.LoadFromAssemblyPath(contractPath);
            var contractType = contractAssembly.GetType("SharpLink.DynamicPlugin.IDynamicPluginService")
                ?? throw new InvalidOperationException("Dynamic proxy benchmark contract type was not found.");
            var client = (ISharpLinkClient)SharpClientBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseTcp(IPAddress.Loopback.ToString(), 1)
                .Build();

            try
            {
                var registration = client.RegisterAssembly(contractAssembly);
                if (!registration.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Dynamic proxy benchmark registration failed: {registration.Error}");
                }

                return new DynamicProxyCacheTarget(loadContext, client, contractType);
            }
            catch
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                loadContext.Unload();
                throw;
            }
        }

        public object GetProxy() => _getMethod.Invoke(_client, null)
            ?? throw new InvalidOperationException("Dynamic proxy factory returned null.");

        public void Dispose()
        {
            _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _loadContext.Unload();
        }
    }
}
