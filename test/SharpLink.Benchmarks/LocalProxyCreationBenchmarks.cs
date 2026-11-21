using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>Local handle creation only. Neither case connects or prepares a contract.</summary>
[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 10)]
public class LocalProxyCreationBenchmarks
{
    private ISharpLinkClient? _staticClient;
    private ISharpLinkClient? _dynamicClient;
    private AssemblyLoadContext? _loadContext;
    private readonly Func<ISharpLinkClient, object> _createStaticProxy = CreateLocalProxy<IBenchmarkRpc>;
    private Func<ISharpLinkClient, object> _createDynamicProxy = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var loadContext = new AssemblyLoadContext("LocalProxyCreationBenchmarks-Dynamic", isCollectible: true);
        _loadContext = loadContext;
        try
        {
            _staticClient = CreateLocalClient().Build();
            var contractAssembly = loadContext.LoadFromAssemblyPath(
                Path.Combine(AppContext.BaseDirectory, "SharpLink.DynamicPlugin.Contracts.dll"));
            var contractType = contractAssembly.GetType("SharpLink.DynamicPlugin.IDynamicPluginService", throwOnError: true)!;
            _dynamicClient = CreateLocalClient()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .Build();
            var registration = _dynamicClient.RegisterAssembly(contractAssembly);
            if (!registration.Succeeded)
                throw new InvalidOperationException($"Dynamic proxy benchmark registration failed: {registration.Error}");

            _createDynamicProxy = typeof(LocalProxyCreationBenchmarks)
                .GetMethod(nameof(CreateLocalProxy), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(contractType)
                .CreateDelegate<Func<ISharpLinkClient, object>>();
        }
        catch
        {
            await Cleanup().ConfigureAwait(false);
            throw;
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _createDynamicProxy = null!;
        try
        {
            if (_dynamicClient is not null)
                await _dynamicClient.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _dynamicClient = null;
            _loadContext?.Unload();
            _loadContext = null;
            if (_staticClient is not null)
                await _staticClient.DisposeAsync().ConfigureAwait(false);
            _staticClient = null;
        }
    }

    [Benchmark]
    public object CreateLocalProxy_Static() => _createStaticProxy(_staticClient!);

    [Benchmark]
    public object CreateLocalProxy_DynamicCollectible() => _createDynamicProxy(_dynamicClient!);

    private static SharpClientBuilder CreateLocalClient() => SharpClientBuilder.Create()
        .UseTcp(IPAddress.Loopback.ToString(), 1)
        .DisableRequestTimeout();

    private static object CreateLocalProxy<TContract>(ISharpLinkClient client) where TContract : IService
        => client.CreateProxy<TContract>();
}
