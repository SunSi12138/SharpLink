using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

[assembly: SharpLinkClusterContractAssembly(
    "multicluster-bench",
    typeof(SharpLink.Benchmarks.IBenchmarkRpc))]

namespace SharpLink.Benchmarks;

internal static class MultiClusterRouterEvaluationEvidenceRunner
{
    private const string Cluster = "multicluster-bench";
    private const int AcquisitionOperations = 524_288;
    private const int AcquisitionAllocationOperations = 100_000;
    private const int PublicationOperations = 10_000;
    private const int RpcOperations = 8_192;
    private const int RpcWarmupOperations = 256;
    private static readonly int[] ConcurrencyLevels = [1, 8, 32, 128];

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: --multicluster-router-evidence <output-json>");

        var outputPath = Path.GetFullPath(args[0]);
        var acquisition = await MeasureAcquisitionAsync().ConfigureAwait(false);
        var rpc = await MeasureRpcAsync().ConfigureAwait(false);
        var publication = MeasurePublication();

        var document = new MultiClusterRouterEvaluationDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Framework = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            AcquisitionOperationsPerScenario = AcquisitionOperations,
            RpcOperationsPerScenario = RpcOperations,
            Acquisition = acquisition,
            Rpc = rpc,
            Publication = publication,
            Notes =
            [
                "The prototype router is intentionally only Assembly -> ISharpLinkClient plus child.Get<T>(); it owns no lifecycle state.",
                "Current MultiCluster and the prototype route only at proxy acquisition. Cached RPC measurements invoke the already-bound generated proxy and perform zero coordinator/router lookups.",
                "The prototype-router cached RPC proxy is ReferenceEquals to the direct child proxy by construction, so any measured delta between those two rows is harness/runtime noise, not an extra RPC path.",
                "Acquisition managed B/op is measured on one thread with GC.GetAllocatedBytesForCurrentThread after warmup. If that value is zero, managed objects/op is exactly zero; otherwise an exact object count is not exposed by the runtime API.",
                "End-to-end async RPC managed B/op uses GC.GetTotalAllocatedBytes around an already-created worker set. Exact cross-thread object counts are not available in-process, so the report records bytes/op and leaves objects/op null.",
                "Snapshot publication measures the isolated immutable route-map preparation/publication cost only. Child build/connect/drain/migration are deliberately excluded and are evaluated as lifecycle semantics rather than routing-map cost."
            ]
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine(json);
    }

    private static async Task<IReadOnlyList<AcquisitionEvidence>> MeasureAcquisitionAsync()
    {
        await using var direct = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), 1)
            .Build();
        await using var multiCluster = SharpLinkMultiClusterClientBuilder.Create()
            .DisableRequestTimeout()
            .AddCluster(
                Cluster,
                child => child.UseTcp(IPAddress.Loopback.ToString(), 1))
            .Build();

        var directProxy = direct.Get<IBenchmarkRpc>();
        var multiClusterProxy = multiCluster.Get<IBenchmarkRpc>();
        var prototype = new AssemblyClientRouter(
            typeof(IBenchmarkRpc).Assembly,
            direct);
        var prototypeProxy = prototype.Get<IBenchmarkRpc>();
        if (!ReferenceEquals(directProxy, prototypeProxy))
            throw new InvalidOperationException("Prototype router must return the direct child's cached proxy instance.");

        var scenarios = new AcquisitionScenario[]
        {
            new("direct-client", () => direct.Get<IBenchmarkRpc>(), directProxy, 0),
            new("current-multicluster", () => multiCluster.Get<IBenchmarkRpc>(), multiClusterProxy, 1),
            new("prototype-assembly-router", () => prototype.Get<IBenchmarkRpc>(), prototypeProxy, 1)
        };
        var results = new List<AcquisitionEvidence>(scenarios.Length * ConcurrencyLevels.Length);
        foreach (var scenario in scenarios)
        {
            var allocation = MeasureAcquisitionAllocation(scenario);
            foreach (var concurrency in ConcurrencyLevels)
            {
                results.Add(await MeasureAcquisitionThroughputAsync(
                    scenario,
                    concurrency,
                    allocation.BytesPerOperation,
                    allocation.ObjectsPerOperation).ConfigureAwait(false));
            }
        }
        return results;
    }

    private static (double BytesPerOperation, double? ObjectsPerOperation) MeasureAcquisitionAllocation(
        AcquisitionScenario scenario)
    {
        for (var index = 0; index < 10_000; index++)
            ValidateProxy(scenario.Get(), scenario.ExpectedProxy, scenario.Variant);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < AcquisitionAllocationOperations; index++)
            ValidateProxy(scenario.Get(), scenario.ExpectedProxy, scenario.Variant);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var bytesPerOperation = allocated / (double)AcquisitionAllocationOperations;
        return (bytesPerOperation, allocated == 0 ? 0d : null);
    }

    private static async Task<AcquisitionEvidence> MeasureAcquisitionThroughputAsync(
        AcquisitionScenario scenario,
        int concurrency,
        double managedBytesPerOperation,
        double? managedObjectsPerOperation)
    {
        var operationsPerWorker = AcquisitionOperations / concurrency;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task[concurrency];
        for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            workers[workerIndex] = Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                for (var operation = 0; operation < operationsPerWorker; operation++)
                    ValidateProxy(scenario.Get(), scenario.ExpectedProxy, scenario.Variant);
            });
        }

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        gate.SetResult();
        await Task.WhenAll(workers).ConfigureAwait(false);
        var elapsedTicks = Stopwatch.GetTimestamp() - started;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var seconds = elapsedTicks / (double)Stopwatch.Frequency;
        return new AcquisitionEvidence
        {
            Variant = scenario.Variant,
            Concurrency = concurrency,
            RoutingLookupsPerGet = scenario.RoutingLookupsPerGet,
            RoutingLookupsPerCachedRpc = 0,
            MeanNanosecondsPerGet = seconds * 1_000_000_000d / AcquisitionOperations,
            GetsPerSecond = AcquisitionOperations / seconds,
            ManagedBytesPerOperation = managedBytesPerOperation,
            ManagedObjectsPerOperation = managedObjectsPerOperation,
            ConcurrentHarnessBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)AcquisitionOperations
        };
    }

    private static async Task<IReadOnlyList<RpcEvidence>> MeasureRpcAsync()
    {
        var results = new List<RpcEvidence>();
        foreach (var transport in new[] { "tcp", "shared-memory" })
        {
            await using (var directHarness = await RpcHarness.CreateAsync(
                             transport,
                             multiCluster: false).ConfigureAwait(false))
            {
                var prototype = new AssemblyClientRouter(
                    typeof(IBenchmarkRpc).Assembly,
                    directHarness.DirectClient!);
                var prototypeProxy = prototype.Get<IBenchmarkRpc>();
                if (!ReferenceEquals(directHarness.Proxy, prototypeProxy))
                {
                    throw new InvalidOperationException(
                        "Prototype router must preserve the direct child's cached proxy for RPC evidence.");
                }

                foreach (var concurrency in ConcurrencyLevels)
                {
                    results.Add(await MeasureCachedRpcAsync(
                        transport,
                        "direct-client",
                        directHarness.Proxy,
                        concurrency,
                        proxyReferenceEqualsDirect: true).ConfigureAwait(false));
                    results.Add(await MeasureCachedRpcAsync(
                        transport,
                        "prototype-assembly-router",
                        prototypeProxy,
                        concurrency,
                        proxyReferenceEqualsDirect: true).ConfigureAwait(false));
                }
            }

            await using (var multiClusterHarness = await RpcHarness.CreateAsync(
                             transport,
                             multiCluster: true).ConfigureAwait(false))
            {
                foreach (var concurrency in ConcurrencyLevels)
                {
                    results.Add(await MeasureCachedRpcAsync(
                        transport,
                        "current-multicluster",
                        multiClusterHarness.Proxy,
                        concurrency,
                        proxyReferenceEqualsDirect: null).ConfigureAwait(false));
                }
            }
        }
        return results;
    }

    private static async Task<RpcEvidence> MeasureCachedRpcAsync(
        string transport,
        string variant,
        IBenchmarkRpc proxy,
        int concurrency,
        bool? proxyReferenceEqualsDirect)
    {
        for (var index = 0; index < RpcWarmupOperations; index++)
            ValidateRpcResult(await proxy.AddAsync(10, 20).ConfigureAwait(false));

        var operationsPerWorker = RpcOperations / concurrency;
        var latencies = new long[RpcOperations];
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task[concurrency];
        for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            var worker = workerIndex;
            workers[workerIndex] = Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                var startIndex = worker * operationsPerWorker;
                var endIndex = startIndex + operationsPerWorker;
                for (var operation = startIndex; operation < endIndex; operation++)
                {
                    var started = Stopwatch.GetTimestamp();
                    ValidateRpcResult(await proxy.AddAsync(10, 20).ConfigureAwait(false));
                    latencies[operation] = Stopwatch.GetTimestamp() - started;
                }
            });
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var startedAll = Stopwatch.GetTimestamp();
        gate.SetResult();
        await Task.WhenAll(workers).ConfigureAwait(false);
        var elapsedTicks = Stopwatch.GetTimestamp() - startedAll;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);

        Array.Sort(latencies);
        var seconds = elapsedTicks / (double)Stopwatch.Frequency;
        return new RpcEvidence
        {
            Transport = transport,
            Variant = variant,
            Concurrency = concurrency,
            RoutingLookupsPerRpc = 0,
            ProxyReferenceEqualsDirect = proxyReferenceEqualsDirect,
            RequestsPerSecond = RpcOperations / seconds,
            P50Microseconds = ToMicroseconds(Percentile(latencies, 0.50)),
            P99Microseconds = ToMicroseconds(Percentile(latencies, 0.99)),
            ManagedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)RpcOperations,
            ManagedObjectsPerOperation = null
        };
    }

    private static IReadOnlyList<PublicationEvidence> MeasurePublication()
    {
        using var direct = new SynchronousClientScope(
            SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), 1)
                .Build());
        return
        [
            MeasurePublicationMap(
                "current-type-route-map",
                typeof(IBenchmarkRpc),
                direct.Client),
            MeasurePublicationMap(
                "prototype-assembly-route-map",
                typeof(IBenchmarkRpc).Assembly,
                direct.Client)
        ];
    }

    private static PublicationEvidence MeasurePublicationMap<TKey>(
        string variant,
        TKey key,
        ISharpLinkClient client)
        where TKey : notnull
    {
        FrozenDictionary<TKey, ISharpLinkClient> published = FrozenDictionary<TKey, ISharpLinkClient>.Empty;
        for (var index = 0; index < 100; index++)
        {
            Volatile.Write(
                ref published,
                new Dictionary<TKey, ISharpLinkClient> { [key] = client }.ToFrozenDictionary());
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < PublicationOperations; index++)
        {
            Volatile.Write(
                ref published,
                new Dictionary<TKey, ISharpLinkClient> { [key] = client }.ToFrozenDictionary());
        }
        var elapsedTicks = Stopwatch.GetTimestamp() - started;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        GC.KeepAlive(published);
        return new PublicationEvidence
        {
            Variant = variant,
            MeanNanosecondsPerPublication =
                elapsedTicks * 1_000_000_000d / Stopwatch.Frequency / PublicationOperations,
            ManagedBytesPerPublication =
                (allocatedAfter - allocatedBefore) / (double)PublicationOperations
        };
    }

    private static long Percentile(long[] sortedValues, double percentile)
    {
        var index = (int)Math.Ceiling(sortedValues.Length * percentile) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    private static double ToMicroseconds(long stopwatchTicks)
        => stopwatchTicks * 1_000_000d / Stopwatch.Frequency;

    private static void ValidateProxy(
        IBenchmarkRpc actual,
        IBenchmarkRpc expected,
        string variant)
    {
        if (!ReferenceEquals(actual, expected))
        {
            throw new InvalidOperationException(
                $"{variant} returned a different proxy inside one published generation.");
        }
    }

    private static void ValidateRpcResult(int result)
    {
        if (result != 30)
            throw new InvalidOperationException($"Benchmark RPC returned {result} instead of 30.");
    }

    private sealed record AcquisitionScenario(
        string Variant,
        Func<IBenchmarkRpc> Get,
        IBenchmarkRpc ExpectedProxy,
        int RoutingLookupsPerGet);

    private sealed class AssemblyClientRouter
    {
        private FrozenDictionary<Assembly, ISharpLinkClient> _routes;

        internal AssemblyClientRouter(Assembly assembly, ISharpLinkClient client)
        {
            _routes = new Dictionary<Assembly, ISharpLinkClient>
            {
                [assembly] = client
            }.ToFrozenDictionary();
        }

        internal TContract Get<TContract>() where TContract : IService
        {
            var routes = Volatile.Read(ref _routes);
            if (routes.TryGetValue(typeof(TContract).Assembly, out var client))
                return client.Get<TContract>();
            throw new InvalidOperationException(
                $"No prototype assembly route exists for '{typeof(TContract).Assembly.FullName}'.");
        }
    }

    private sealed class RpcHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private readonly ISharpLinkClient? _directClient;
        private readonly ISharpLinkMultiClusterClient? _multiClusterClient;

        private RpcHarness(
            IBenchmarkRpc proxy,
            ISharpLinkClient? directClient,
            ISharpLinkMultiClusterClient? multiClusterClient,
            ISharpLinkServer server,
            CancellationTokenSource shutdown,
            Task serverTask)
        {
            Proxy = proxy;
            _directClient = directClient;
            _multiClusterClient = multiClusterClient;
            _server = server;
            _shutdown = shutdown;
            _serverTask = serverTask;
        }

        internal IBenchmarkRpc Proxy { get; }

        internal ISharpLinkClient? DirectClient => _directClient;

        internal static async Task<RpcHarness> CreateAsync(
            string transport,
            bool multiCluster)
        {
            var serverBuilder = SharpLinkServerBuilder.Create();
            string? sharedMemoryName = null;
            var port = 0;
            if (transport == "tcp")
            {
                serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
                port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            }
            else if (transport == "shared-memory")
            {
                sharedMemoryName = $"sharplink-405-{Guid.NewGuid():N}";
                serverBuilder.UseSharedMemory(sharedMemoryName);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown transport.");
            }

            var server = serverBuilder.Build();
            var shutdown = new CancellationTokenSource();
            var serverTask = server.RunAsync(shutdown.Token).AsTask();
            try
            {
                await Task.Yield();
                if (multiCluster)
                {
                    var client = SharpLinkMultiClusterClientBuilder.Create()
                        .DisableRequestTimeout()
                        .AddCluster(
                            Cluster,
                            child => ConfigureTransport(child, transport, port, sharedMemoryName))
                        .Build();
                    try
                    {
                        await client.ConnectAsync(shutdown.Token).ConfigureAwait(false);
                        var proxy = client.Get<IBenchmarkRpc>();
                        ValidateRpcResult(await proxy.AddAsync(10, 20).ConfigureAwait(false));
                        return new RpcHarness(proxy, null, client, server, shutdown, serverTask);
                    }
                    catch
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
                else
                {
                    var builder = SharpClientBuilder.Create().DisableRequestTimeout();
                    ConfigureTransport(builder, transport, port, sharedMemoryName);
                    var client = builder.Build();
                    try
                    {
                        await client.ConnectAsync(shutdown.Token).ConfigureAwait(false);
                        var proxy = client.Get<IBenchmarkRpc>();
                        ValidateRpcResult(await proxy.AddAsync(10, 20).ConfigureAwait(false));
                        return new RpcHarness(proxy, client, null, server, shutdown, serverTask);
                    }
                    catch
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
            }
            catch
            {
                shutdown.Cancel();
                await server.DisposeAsync().ConfigureAwait(false);
                shutdown.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_directClient is not null)
                    await _directClient.StopAsync().ConfigureAwait(false);
                if (_multiClusterClient is not null)
                    await _multiClusterClient.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await _server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
                }
                finally
                {
                    _shutdown.Cancel();
                    try
                    {
                        await _serverTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                    }
                    catch (TimeoutException)
                    {
                    }

                    if (_directClient is not null)
                        await _directClient.DisposeAsync().ConfigureAwait(false);
                    if (_multiClusterClient is not null)
                        await _multiClusterClient.DisposeAsync().ConfigureAwait(false);
                    await _server.DisposeAsync().ConfigureAwait(false);
                    _shutdown.Dispose();
                }
            }
        }

        private static void ConfigureTransport(
            SharpClientBuilder builder,
            string transport,
            int port,
            string? sharedMemoryName)
        {
            if (transport == "tcp")
            {
                builder.UseTcp(IPAddress.Loopback.ToString(), port);
                return;
            }
            builder.UseSharedMemory(sharedMemoryName!);
        }
    }

    private sealed class SynchronousClientScope : IDisposable
    {
        internal SynchronousClientScope(ISharpLinkClient client)
        {
            Client = client;
        }

        internal ISharpLinkClient Client { get; }

        public void Dispose()
            => Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

internal sealed class MultiClusterRouterEvaluationDocument
{
    public string Commit { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public int AcquisitionOperationsPerScenario { get; init; }
    public int RpcOperationsPerScenario { get; init; }
    public IReadOnlyList<AcquisitionEvidence> Acquisition { get; init; } = [];
    public IReadOnlyList<RpcEvidence> Rpc { get; init; } = [];
    public IReadOnlyList<PublicationEvidence> Publication { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

internal sealed class AcquisitionEvidence
{
    public string Variant { get; init; } = string.Empty;
    public int Concurrency { get; init; }
    public int RoutingLookupsPerGet { get; init; }
    public int RoutingLookupsPerCachedRpc { get; init; }
    public double MeanNanosecondsPerGet { get; init; }
    public double GetsPerSecond { get; init; }
    public double ManagedBytesPerOperation { get; init; }
    public double? ManagedObjectsPerOperation { get; init; }
    public double ConcurrentHarnessBytesPerOperation { get; init; }
}

internal sealed class RpcEvidence
{
    public string Transport { get; init; } = string.Empty;
    public string Variant { get; init; } = string.Empty;
    public int Concurrency { get; init; }
    public int RoutingLookupsPerRpc { get; init; }
    public bool? ProxyReferenceEqualsDirect { get; init; }
    public double RequestsPerSecond { get; init; }
    public double P50Microseconds { get; init; }
    public double P99Microseconds { get; init; }
    public double ManagedBytesPerOperation { get; init; }
    public double? ManagedObjectsPerOperation { get; init; }
}

internal sealed class PublicationEvidence
{
    public string Variant { get; init; } = string.Empty;
    public double MeanNanosecondsPerPublication { get; init; }
    public double ManagedBytesPerPublication { get; init; }
}
