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

/// <summary>
/// Separates API6 cold local factories from retained Service access. These replace the
/// retired Get-cache rows; dynamic factory rows include reflection and are labelled so.
/// No RPC or remote Prepare is part of these local-construction measurements.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 10)]
public class ProxyCacheBenchmarks
{
    private BenchmarkEnvironment _staticEnvironment = null!;
    private RpcProxy<IBenchmarkRpc> _staticHandle = null!;
    private DynamicProxyCacheTarget _dynamicTarget = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _staticEnvironment = await BenchmarkEnvironment.CreateAsync();
        _staticHandle = _staticEnvironment.Client.CreateProxy<IBenchmarkRpc>();
        _dynamicTarget = DynamicProxyCacheTarget.Create();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _staticEnvironment.DisposeAsync();
        _dynamicTarget.Dispose();
    }

    [Benchmark]
    public RpcProxy<IBenchmarkRpc> CreateProxy_Static() => _staticEnvironment.Client.CreateProxy<IBenchmarkRpc>();

    [Benchmark]
    public object CreateProxy_DynamicViaReflection() => _dynamicTarget.CreateProxy();

    [Benchmark]
    public IBenchmarkRpc RetainedService_Static() => _staticHandle.Service;

    [Benchmark]
    public object RetainedService_Dynamic() => _dynamicTarget.Service;

    private sealed class DynamicProxyCacheTarget : IDisposable
    {
        private AssemblyLoadContext _loadContext;
        private ISharpLinkClient _client;
        private MethodInfo _factoryMethod;
        private object _service;
        private readonly object?[] _factoryArguments = [null];

        private DynamicProxyCacheTarget(
            AssemblyLoadContext loadContext,
            ISharpLinkClient client,
            Type contractType)
        {
            _loadContext = loadContext;
            _client = client;
            _factoryMethod = typeof(ISharpLinkClient)
                .GetMethod(nameof(ISharpLinkClient.CreateProxy))!
                .MakeGenericMethod(contractType);
            var handle = CreateProxy();
            _service = handle.GetType().GetProperty("Service")!.GetValue(handle)
                ?? throw new InvalidOperationException("Dynamic handle has no retained service.");
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

        public object CreateProxy() => _factoryMethod.Invoke(_client, _factoryArguments)
            ?? throw new InvalidOperationException("Dynamic proxy factory returned null.");

        public object Service => _service;

        public void Dispose()
        {
            _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var context = _loadContext;
            _client = null!;
            _factoryMethod = null!;
            _service = null!;
            _loadContext = null!;
            context.Unload();
        }
    }
}
