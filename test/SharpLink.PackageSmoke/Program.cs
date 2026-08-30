using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpPack;

[assembly: SharpLinkClusterContractAssembly(
    "runtime",
    typeof(SharpLink.PackageSmoke.IPackageSmokeService))]

namespace SharpLink.PackageSmoke;

[RpcContract]
public interface IPackageSmokeService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);

    [NonCancellable]
    ValueTask<PackageSmokeEnvelope> EchoAsync(PackageSmokeEnvelope value);
}

[SharpPackable]
public sealed partial class PackageSmokeAddress
{
    public string City { get; set; } = string.Empty;
    public int PostalCode { get; set; }
}

[SharpPackable]
public sealed partial class PackageSmokeEnvelope
{
    public string Name { get; set; } = string.Empty;
    public PackageSmokeAddress Address { get; set; } = new();
    public List<int> Values { get; set; } = [];
}

[RpcService]
public sealed class PackageSmokeService : IPackageSmokeService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public ValueTask<PackageSmokeEnvelope> EchoAsync(PackageSmokeEnvelope value) =>
        ValueTask.FromResult(value);
}

public static class Program
{
    private static readonly string[] RuntimeRawDispatcherTypeNames =
    [
        "SharpLink.Runtime.IStreamDispatcher",
        "SharpLink.Runtime.IStreamConsumptionAwareDispatcher",
        "SharpLink.Runtime.IStreamDispatchLease",
        "SharpLink.Runtime.IStreamDispatchState",
        "SharpLink.Runtime.InboundStreamChildDispatchState",
        "SharpLink.Runtime.PooledAsyncStreamDispatcher`1",
        "SharpLink.Runtime.PreAdmissionStreamDispatcher",
        "SharpLink.Runtime.DiscardingStreamDispatcher"
    ];

    public static async Task Main()
    {
        AssertEnginePublicApiBoundary();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await RunTransportSmokeAsync(useSharedMemory: false, timeout.Token);
        await RunTransportSmokeAsync(useSharedMemory: true, timeout.Token);
        await RunStaticEndpointSmokeAsync(timeout.Token);
        await RunReferencedAssemblyPackageSmokeAsync(timeout.Token);
    }

    private static async Task RunTransportSmokeAsync(
        bool useSharedMemory,
        CancellationToken cancellationToken)
    {
        var sharedMemoryName = $"sharplink-package-smoke-{Guid.NewGuid():N}";
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(options => options.Global.UseConcurrency(64));
        if (useSharedMemory)
            serverBuilder.UseSharedMemory(sharedMemoryName);
        else
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());

        var localEndPoint = serverBuilder.Transport!.LocalEndPoint as IPEndPoint;
        if (!useSharedMemory && localEndPoint is null)
            throw new InvalidOperationException("Package smoke server did not expose its TCP endpoint.");
        var server = serverBuilder.Build();
        var serverTask = RunServerAsync(server, cancellationToken);

        var clientBuilder = SharpClientBuilder.Create()
            .UseRuntime(ConfigureCompression);
        if (useSharedMemory)
            clientBuilder.UseSharedMemory(sharedMemoryName);
        else
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), localEndPoint!.Port);
        var client = clientBuilder.Build();

        try
        {
            await client.ConnectAsync(cancellationToken);
            var fixedReadiness = await client.WaitForReadinessAsync(1, cancellationToken);
            VerifyReadinessSnapshot(
                fixedReadiness,
                expectedActiveEndpoints: 1,
                expectedTargetReadyEndpoints: 1);
            VerifyReadinessSnapshot(
                client.GetReadinessSnapshot(),
                expectedActiveEndpoints: 1,
                expectedTargetReadyEndpoints: 1);

            var proxy = client.Get<IPackageSmokeService>();
            var result = await proxy.AddAsync(20, 22);
            if (result != 42)
                throw new InvalidOperationException($"Package smoke returned {result} instead of 42.");

            var expected = new PackageSmokeEnvelope
            {
                Name = new string('p', 4096),
                Address = new PackageSmokeAddress { City = "Shanghai", PostalCode = 200000 },
                Values = [1, 2, 3]
            };
            var actual = await proxy.EchoAsync(expected);
            if (actual.Name != expected.Name ||
                actual.Address.City != expected.Address.City ||
                actual.Address.PostalCode != expected.Address.PostalCode ||
                !actual.Values.SequenceEqual(expected.Values))
                throw new InvalidOperationException("Package smoke generated DTO codec round-trip failed.");

            if (!useSharedMemory)
                await RunRuntimeMultiClusterSmokeAsync(localEndPoint!.Port, cancellationToken);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
    }

    private static async Task RunRuntimeMultiClusterSmokeAsync(
        int port,
        CancellationToken cancellationToken)
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster(
                "bootstrap",
                child => child.UseTcp(IPAddress.Loopback.ToString(), port),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync(cancellationToken);

        await client.AddClusterAsync(
            "runtime",
            child => child.UseTcp(IPAddress.Loopback.ToString(), port),
            cancellationToken: cancellationToken);
        if (await client.Get<IPackageSmokeService>().AddAsync(20, 22) != 42)
            throw new InvalidOperationException("Runtime multi-cluster Add package smoke failed.");

        await client.ReplaceClusterAsync(
            "runtime",
            child => child.UseTcp(IPAddress.Loopback.ToString(), port),
            TimeSpan.FromSeconds(2),
            cancellationToken);
        if (await client.Get<IPackageSmokeService>().AddAsync(19, 23) != 42)
            throw new InvalidOperationException("Runtime multi-cluster Replace package smoke failed.");

        var removal = await client.RemoveClusterAsync(
            "runtime",
            TimeSpan.FromSeconds(2),
            cancellationToken);
        if (!removal.Succeeded || !removal.ReferencesReleased || removal.ForcedStop)
            throw new InvalidOperationException("Runtime multi-cluster Remove package smoke failed.");
    }

    private static async Task RunStaticEndpointSmokeAsync(CancellationToken cancellationToken)
    {
        var firstBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(options => options.Global.UseConcurrency(64))
            .UseTcp(0, IPAddress.Loopback.ToString());
        var secondBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(options => options.Global.UseConcurrency(64))
            .UseTcp(0, IPAddress.Loopback.ToString());
        var firstPort = ((IPEndPoint)firstBuilder.Transport!.LocalEndPoint!).Port;
        var secondPort = ((IPEndPoint)secondBuilder.Transport!.LocalEndPoint!).Port;
        var firstServer = firstBuilder.Build();
        var secondServer = secondBuilder.Build();
        var firstTask = RunServerAsync(firstServer, cancellationToken);
        var secondTask = RunServerAsync(secondServer, cancellationToken);
        var endpoints = new[]
        {
            new SharpLinkEndpoint
            {
                Id = "first",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), firstPort),
                Attributes = new Dictionary<string, string> { ["zone"] = "a" }
            },
            new SharpLinkEndpoint
            {
                Id = "second",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), secondPort),
                Attributes = new Dictionary<string, string> { ["zone"] = "b" }
            }
        };
        var client = SharpClientBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseEndpoints(
                endpoints,
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
            .Build();

        try
        {
            await client.ConnectAsync(cancellationToken);
            var staticReadiness = await client.WaitForReadinessAsync(2, cancellationToken);
            VerifyReadinessSnapshot(
                staticReadiness,
                expectedActiveEndpoints: 2,
                expectedTargetReadyEndpoints: 2);
            VerifyReadinessSnapshot(
                client.GetReadinessSnapshot(),
                expectedActiveEndpoints: 2,
                expectedTargetReadyEndpoints: 2);
            if (await client.Get<IPackageSmokeService>().AddAsync(20, 22) != 42)
                throw new InvalidOperationException("Static endpoint package smoke returned an unexpected result.");

            await using var dynamicClient = SharpClientBuilder.Create()
                .UseRuntime(ConfigureCompression)
                .UseEndpointResolver(
                    new DelegateSharpLinkEndpointResolver(
                        _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1, endpoints))),
                    SharpLinkTransportFactories.Sockets())
                .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
                .Build();
            await dynamicClient.ConnectAsync(cancellationToken);
            var dynamicReadiness = await dynamicClient.WaitForReadinessAsync(2, cancellationToken);
            VerifyReadinessSnapshot(
                dynamicReadiness,
                expectedActiveEndpoints: 2,
                expectedTargetReadyEndpoints: 2);
            VerifyReadinessSnapshot(
                dynamicClient.GetReadinessSnapshot(),
                expectedActiveEndpoints: 2,
                expectedTargetReadyEndpoints: 2);
            if (await dynamicClient.Get<IPackageSmokeService>().AddAsync(20, 22) != 42)
                throw new InvalidOperationException("Dynamic endpoint package smoke returned an unexpected result.");
        }
        finally
        {
            await client.DisposeAsync();
            await firstServer.DisposeAsync();
            await secondServer.DisposeAsync();
            await Task.WhenAny(firstTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
    }

    private static void VerifyReadinessSnapshot(
        SharpLinkClientReadinessSnapshot snapshot,
        int expectedActiveEndpoints,
        int expectedTargetReadyEndpoints)
    {
        if (!snapshot.MeetsTarget ||
            snapshot.State != SharpLinkConnectionState.Ready ||
            snapshot.ActiveEndpoints != expectedActiveEndpoints ||
            snapshot.ReadyEndpoints != expectedActiveEndpoints ||
            snapshot.ReadyConnections < snapshot.ReadyEndpoints ||
            snapshot.TargetReadyEndpoints != expectedTargetReadyEndpoints)
        {
            throw new InvalidOperationException($"Unexpected Client readiness snapshot: {snapshot}.");
        }
    }

    private static async Task RunServerAsync(ISharpLinkServer server, CancellationToken cancellationToken)
    {
        try
        {
            await server.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task RunReferencedAssemblyPackageSmokeAsync(CancellationToken cancellationToken)
    {
        var sharedMemoryName = $"sharplink-package-reference-rooting-{Guid.NewGuid():N}";
        var serverAssembly = FindReferenceRootingAssembly(
            "SharpLink.ReferenceRooting.PackageServer",
            "SharpLink.ReferenceRooting.PackageServer.dll");
        var clientAssembly = FindReferenceRootingAssembly(
            "SharpLink.ReferenceRooting.PackageClient",
            "SharpLink.ReferenceRooting.PackageClient.dll");
        using var server = StartReferenceRootingProcess(serverAssembly, sharedMemoryName);
        try
        {
            var ready = await server.StandardOutput.ReadLineAsync(cancellationToken);
            if (!string.Equals(ready, "PACKAGE_REFERENCE_ROOTING_SERVER_READY", StringComparison.Ordinal))
                throw new InvalidOperationException($"Referenced package server did not become ready: '{ready}'.");

            using var client = StartReferenceRootingProcess(clientAssembly, sharedMemoryName);
            var clientOutput = await client.StandardOutput.ReadToEndAsync(cancellationToken);
            var clientError = await client.StandardError.ReadToEndAsync(cancellationToken);
            await client.WaitForExitAsync(cancellationToken);
            if (client.ExitCode != 0 ||
                !clientOutput.Contains("PACKAGE_REFERENCE_ROOTING_PASS", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Referenced package client failed ({client.ExitCode}): {clientOutput} {clientError}");
            }
            Console.WriteLine("PACKAGE_REFERENCE_ROOTING_PASS");
        }
        finally
        {
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static Process StartReferenceRootingProcess(string assemblyPath, string sharedMemoryName)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add(sharedMemoryName);
        return Process.Start(start) ?? throw new InvalidOperationException(
            $"Could not start reference-rooting package process '{assemblyPath}'.");
    }

    private static string FindReferenceRootingAssembly(string projectName, string assemblyName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate the SharpLink repository root.");

        var path = Path.Combine(
            directory.FullName,
            "test",
            projectName,
            "bin",
            "Release",
            "net10.0",
            assemblyName);
        if (!File.Exists(path))
            throw new FileNotFoundException("The reference-rooting package smoke assembly was not built.", path);
        return path;
    }

    private static void ConfigureCompression(SharpLinkRuntimeOptions options)
        => options.Compression.Providers.Add(SharpLinkCompressionProviders.CreateBrotli());

    private static void AssertEnginePublicApiBoundary()
    {
        var abstractions = typeof(IRpcGeneratedServerBridge).Assembly;
        foreach (var name in new[]
                 {
                     "SharpLink.Abstractions.IRpcSession",
                     "SharpLink.Abstractions.IStreamManager",
                     "SharpLink.Abstractions.IStreamDispatcher",
                     "SharpLink.Abstractions.IStreamConsumptionAwareDispatcher"
                 })
        {
            if (abstractions.GetType(name, throwOnError: false) is not null)
                throw new InvalidOperationException($"Removed Runtime engine API is still exported: {name}.");
        }

        var runtime = typeof(SharpLinkRuntimeContext).Assembly;
        foreach (var name in new[]
                 {
                     "SharpLink.Runtime.RpcSession",
                     "SharpLink.Runtime.StreamManager",
                     "SharpLink.Runtime.RpcSessionExtensions"
                 })
        {
            var engineType = runtime.GetType(name, throwOnError: false);
            if (engineType is null || engineType.IsPublic)
                throw new InvalidOperationException($"Runtime engine API is still public: {name}.");
        }

        var rawDispatcherTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var name in RuntimeRawDispatcherTypeNames)
        {
            var rawDispatcherType = runtime.GetType(name, throwOnError: false);
            if (rawDispatcherType is null)
                throw new InvalidOperationException($"Runtime raw stream dispatcher type is missing: {name}.");
            if (rawDispatcherType.IsPublic || rawDispatcherType.IsNestedPublic || rawDispatcherType.IsVisible)
            {
                throw new InvalidOperationException(
                    $"Runtime raw stream dispatcher type is externally visible: {name}.");
            }
            rawDispatcherTypes.Add(name, rawDispatcherType);
        }

        var streamDispatcher = rawDispatcherTypes["SharpLink.Runtime.IStreamDispatcher"];
        var dispatchLease = rawDispatcherTypes["SharpLink.Runtime.IStreamDispatchLease"];
        var dispatchState = rawDispatcherTypes["SharpLink.Runtime.IStreamDispatchState"];
        var expectedRawDispatcherTypeNames = RuntimeRawDispatcherTypeNames.ToHashSet(StringComparer.Ordinal);
        var discoveredRawDispatcherTypeNames = runtime.GetTypes()
            .Where(type =>
                !type.IsNested &&
                (streamDispatcher.IsAssignableFrom(type) ||
                 dispatchLease.IsAssignableFrom(type) ||
                 dispatchState.IsAssignableFrom(type)))
            .Select(static type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedRawDispatcherTypeNames.SetEquals(discoveredRawDispatcherTypeNames))
        {
            var missing = expectedRawDispatcherTypeNames
                .Except(discoveredRawDispatcherTypeNames)
                .OrderBy(static name => name, StringComparer.Ordinal);
            var unexpected = discoveredRawDispatcherTypeNames
                .Except(expectedRawDispatcherTypeNames)
                .OrderBy(static name => name, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Runtime raw stream dispatcher inventory changed. Missing: {string.Join(", ", missing)}; " +
                $"unexpected: {string.Join(", ", unexpected)}.");
        }

        var explicitlyDeniedExports = runtime.GetExportedTypes()
            .Select(static type => type.FullName)
            .Where(name => name is not null && expectedRawDispatcherTypeNames.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (explicitlyDeniedExports.Length != 0)
        {
            throw new InvalidOperationException(
                $"Explicitly denied Runtime raw stream dispatcher types are exported: " +
                $"{string.Join(", ", explicitlyDeniedExports)}.");
        }

        var exportedRawDispatchers = new[] { abstractions, runtime }
            .SelectMany(static assembly => assembly.GetExportedTypes())
            .Where(static type =>
                type.Name.Contains("Dispatcher", StringComparison.Ordinal) ||
                type.Name is "IStreamDispatchLease" or "IStreamDispatchState")
            .Select(static type => type.FullName ?? type.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (exportedRawDispatchers.Length != 0)
        {
            throw new InvalidOperationException(
                $"Raw stream dispatcher types are still exported: {string.Join(", ", exportedRawDispatchers)}.");
        }

        AssertPublicType<IRpcGeneratedServerBridge>();

        var connection = new PackageTransport();
        var codec = new PackageCodec();
        var clientTransport = new PackageClientTransportFactory();
        var serverListener = new PackageServerTransportListener();
        var clientAuthenticator = new PackageClientAuthenticator();
        var serverAuthenticator = new PackageServerAuthenticator();
        var endpointResolver = new PackageEndpointResolver();
        var endpointSelector = new PackageEndpointSelector();
        var retryPolicy = new PackageRetryPolicy();
        var admissionPolicy = new PackageEndpointAdmissionPolicy();
        var clientInterceptor = new PackageClientInterceptor();
        var serverInterceptor = new PackageInterceptor();

        AssertPublicSpi<ITransportConnection, PackageTransport>(connection);
        AssertPublicSpi<IRpcCodec<int>, PackageCodec>(codec);
        AssertPublicSpi<IClientTransportFactory, PackageClientTransportFactory>(clientTransport);
        AssertPublicSpi<IServerTransportListener, PackageServerTransportListener>(serverListener);
        AssertPublicSpi<ISharpLinkClientAuthenticator, PackageClientAuthenticator>(clientAuthenticator);
        AssertPublicSpi<ISharpLinkServerAuthenticator, PackageServerAuthenticator>(serverAuthenticator);
        AssertPublicSpi<ISharpLinkEndpointResolver, PackageEndpointResolver>(endpointResolver);
        AssertPublicSpi<ISharpLinkEndpointSelector, PackageEndpointSelector>(endpointSelector);
        AssertPublicSpi<ISharpLinkRetryPolicy, PackageRetryPolicy>(retryPolicy);
        AssertPublicSpi<ISharpLinkEndpointAdmissionPolicy, PackageEndpointAdmissionPolicy>(admissionPolicy);
        AssertPublicSpi<ISharpLinkClientInterceptor, PackageClientInterceptor>(clientInterceptor);
        AssertPublicSpi<ISharpLinkServerInterceptor, PackageInterceptor>(serverInterceptor);

        var directClientBuilder = SharpClientBuilder.Create();
        AssertBuilderReturnsSelf(
            directClientBuilder,
            directClientBuilder
                .UseTransport(clientTransport)
                .UseAuthenticator(clientAuthenticator)
                .AddInterceptor(clientInterceptor)
                .UseEndpointSelector(endpointSelector)
                .UseRetry(retryPolicy)
                .UseEndpointAdmission(admissionPolicy),
            "SharpClientBuilder direct transport and policy SPI configuration");

        SharpLinkEndpointTransportFactory endpointTransportFactory =
            static _ => new PackageClientTransportFactory();
        AssertPublicType<SharpLinkEndpointTransportFactory>();
        var resolverClientBuilder = SharpClientBuilder.Create();
        AssertBuilderReturnsSelf(
            resolverClientBuilder,
            resolverClientBuilder.UseEndpointResolver(endpointResolver, endpointTransportFactory),
            "SharpClientBuilder endpoint resolver SPI configuration");

        var serverBuilder = SharpLinkServerBuilder.Create();
        AssertBuilderReturnsSelf(
            serverBuilder,
            serverBuilder
                .UseTransport(serverListener)
                .UseAuthenticator(serverAuthenticator)
                .AddInterceptor(serverInterceptor),
            "SharpLinkServerBuilder transport and policy SPI configuration");
    }

    private static void AssertPublicType<T>()
    {
        if (!typeof(T).IsPublic)
            throw new InvalidOperationException($"Supported SharpLink API is not public: {typeof(T).FullName}.");
    }

    private static void AssertPublicSpi<TContract, TImplementation>(TContract instance)
        where TImplementation : TContract
    {
        AssertPublicType<TContract>();
        if (!typeof(TContract).IsAssignableFrom(typeof(TImplementation)))
        {
            throw new InvalidOperationException(
                $"Package consumer type {typeof(TImplementation).FullName} does not implement {typeof(TContract).FullName}.");
        }
        if (!typeof(TImplementation).IsInstanceOfType(instance))
        {
            throw new InvalidOperationException(
                $"Package consumer SPI instance has the wrong runtime type for {typeof(TContract).FullName}.");
        }
    }

    private static void AssertBuilderReturnsSelf<TBuilder>(TBuilder expected, TBuilder actual, string operation)
        where TBuilder : class
    {
        if (!ReferenceEquals(expected, actual))
            throw new InvalidOperationException($"{operation} did not preserve the configured builder instance.");
    }

    private sealed class PackageTransport : ITransportConnection
    {
        public string Id => "package-smoke-transport";

        public PipeReader Input => PipeReader.Create(Stream.Null);

        public PipeWriter Output => PipeWriter.Create(Stream.Null);

        public EndPoint? LocalEndPoint => null;

        public EndPoint? RemoteEndPoint => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PackageClientTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new NotSupportedException("Package SPI compile probe does not connect."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PackageServerTransportListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new NotSupportedException("Package SPI compile probe does not accept connections."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PackageClientAuthenticator : ISharpLinkClientAuthenticator
    {
        public ValueTask<ReadOnlyMemory<byte>> CreatePayloadAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    }

    private sealed class PackageServerAuthenticator : ISharpLinkServerAuthenticator
    {
        public ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
            SharpLinkAuthenticationRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(SharpLinkAuthenticationResult.Success);
    }

    private sealed class PackageEndpointResolver : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new SharpLinkEndpointSnapshot(0, []));

        public IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(CancellationToken cancellationToken)
            => EmptySnapshots();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<SharpLinkEndpointSnapshot> EmptySnapshots()
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class PackageEndpointSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
            => context.Count == 0 ? -1 : 0;
    }

    private sealed class PackageRetryPolicy : ISharpLinkRetryPolicy
    {
        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
            => new(false, TimeSpan.Zero);
    }

    private sealed class PackageEndpointAdmissionPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
            => new(true, Token: 0, RetryAfter: null);

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }

    private sealed class PackageClientInterceptor : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => next(context);
    }

    private sealed class PackageCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BitConverter.TryWriteBytes(span, value);
            buffer.Advance(sizeof(int));
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BitConverter.ToInt32(buffer.ToArray());
    }

    private sealed class PackageInterceptor : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
            => next(context);
    }
}
