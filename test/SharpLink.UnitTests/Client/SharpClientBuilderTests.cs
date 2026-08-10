using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class SharpClientBuilderTests
{
    [Test]
    public async Task StaticClientSnapshotShouldRejectIncompatibleManifestVersions()
    {
        Ensure(SharpLinkGeneratedManifestVersions.Api == 4,
            "the 2.0 Runtime must require generated manifest API 4");
        await EnsureThrows<InvalidOperationException>(() =>
        {
            SharpLinkClient.ValidateStaticManifestCompatibility(new IncompatibleManifest());
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldUseThirtySecondUnaryTimeoutByDefault()
    {
        var client = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .Build();

        Ensure(ReadRequestTimeout(client) == TimeSpan.FromSeconds(30), "default unary timeout");
        await client.DisposeAsync();
    }

    [Test]
    public async Task UseRequestTimeoutShouldRejectNonPositiveValues()
    {
        var builder = SharpClientBuilder.Create();
        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            builder.UseRequestTimeout(TimeSpan.Zero);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldCarryConfiguredRequestTimeout()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseRequestTimeout(TimeSpan.FromSeconds(2));

        var client = builder.Build();
        var timeout = ReadRequestTimeout(client);
        Ensure(timeout == TimeSpan.FromSeconds(2), "request timeout should be applied");
        await client.DisposeAsync();
    }

    [Test]
    public async Task BuildShouldForwardTheApplicationOwnedTimeProvider()
    {
        var timeProvider = new ManualTimeProvider();
        var client = SharpClientBuilder.Create()
            .UseTimeProvider(timeProvider)
            .UseTransport(new NoopTransport())
            .Build();

        var runtimeContext = (SharpLinkRuntimeContext)((IRpcChannel)client).RuntimeContext;
        Ensure(ReferenceEquals(runtimeContext.TimeProvider, timeProvider),
            "client builder must preserve the configured provider instance");
        await client.DisposeAsync();
        Ensure(timeProvider.ActiveTimerCount == 0,
            "disposing the client must not leave a timer on the application-owned provider");
    }

    [Test]
    public async Task BuildShouldClearRequestTimeoutAfterDisable()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseRequestTimeout(TimeSpan.FromSeconds(2))
            .DisableRequestTimeout();

        var client = builder.Build();
        var timeout = ReadRequestTimeout(client);
        Ensure(timeout is null, "request timeout should be disabled");
        await client.DisposeAsync();
    }

    [Test]
    public async Task UseRpcSessionFlushShouldRejectInvalidValues()
    {
        var builder = SharpClientBuilder.Create();
        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            builder.UseRpcSessionFlush(0, TimeSpan.FromMilliseconds(1));
            return Task.CompletedTask;
        });

        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            builder.UseRpcSessionFlush(1024, TimeSpan.Zero);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldCarryRpcSessionFlushWithoutMutatingTransport()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseRpcSessionFlush(8192, TimeSpan.FromMilliseconds(2));

        var client = builder.Build();
        var options = ReadRpcSessionFlushOptions(client);
        Ensure(options is { FlushSizeThreshold: 8192 }, "flush size should match");
        Ensure(options?.MaxLatency == TimeSpan.FromMilliseconds(2), "max latency should match");
        await client.DisposeAsync();
    }

    [Test]
    public async Task BuildShouldRejectInvalidProtocolLimits()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseProtocol(static options =>
                options.MaxFramePayloadBytes = SharpLinkProtocolOptions.MinMaxFramePayloadBytes - 1);

        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldRejectPendingRequestCapacityThatIsNotPowerOfTwo()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseProtocol(static options => options.MaxPendingRequestsPerConnection = 1000);

        await EnsureThrows<ArgumentException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldFreezeProtocolLimitSnapshot()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseProtocol(static options => options.MaxFramePayloadBytes = 2048);

        var client = builder.Build();

        Ensure(ReadMaxFramePayloadBytes(client) == 2048, "built client protocol snapshot");
        await EnsureConsumed(() =>
        {
            builder.UseProtocol(static options => options.MaxFramePayloadBytes = 4096);
            return Task.CompletedTask;
        });
        await EnsureConsumed(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
        await client.DisposeAsync();
    }

    [Test]
    public async Task BuildShouldAllowDefaultSessionFlush()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport());

        var client = builder.Build();
        Ensure(ReadRpcSessionFlushOptions(client) is null, "default flush should remain session default");
        await client.DisposeAsync();
    }

    [Test]
    public async Task ConnectionPoolShouldDefaultToOneAndFreezeExplicitBounds()
    {
        var defaultBuilder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport());
        var defaultClient = defaultBuilder.Build();
        Ensure(ReadConnectionPool(defaultClient) is { MinConnections: 1, MaxConnections: 1 },
            "balanced default pool");

        SharpLinkConnectionPoolOptions? configuredDraft = null;
        var configuredBuilder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseConnectionPool(options =>
        {
            options.MinConnections = 2;
            options.MaxConnections = 4;
            configuredDraft = options;
        });
        var configuredClient = configuredBuilder.Build();
        configuredDraft!.MaxConnections = 6;
        Ensure(ReadConnectionPool(configuredClient) is { MinConnections: 2, MaxConnections: 4 },
            "built client should own a frozen pool snapshot");
        await EnsureConsumed(() =>
        {
            configuredBuilder.UseConnectionPool(options => options.MaxConnections = 6);
            return Task.CompletedTask;
        });

        await defaultClient.DisposeAsync();
        await configuredClient.DisposeAsync();
    }

    [Test]
    public async Task ThroughputProfileShouldUseBoundedMultiConnectionDefault()
    {
        var client = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseRuntime(options => options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput)
            .Build();
        var pool = ReadConnectionPool(client);
        Ensure(pool.MinConnections == 1, "throughput minimum");
        Ensure(pool.MaxConnections == Math.Min(Environment.ProcessorCount, 4), "throughput maximum");
        await client.DisposeAsync();
    }

    [Test]
    public async Task BuildShouldRejectInvalidConnectionPoolBounds()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseConnectionPool(options =>
            {
                options.MinConnections = 2;
                options.MaxConnections = 1;
            });
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task DirectTransportShouldBeTransferredByOnlyOneBuild()
    {
        var transport = new TrackingTransport();
        var builder = SharpClientBuilder.Create().UseTransport(transport);
        var first = builder.Build();

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });

        await first.DisposeAsync();
        Ensure(transport.DisposeCount == 1, "one Client must own and dispose the direct transport");
    }

    [Test]
    public async Task EndpointResolverShouldBeTransferredByOnlyOneBuild()
    {
        var resolver = new TrackingResolver();
        var builder = SharpClientBuilder.Create()
            .UseEndpointResolver(resolver, static _ => new NoopTransport());
        var first = builder.Build();

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });

        await first.DisposeAsync();
        Ensure(resolver.DisposeCount == 1, "one Client must own and dispose the endpoint resolver");
    }

    private static TimeSpan? ReadRequestTimeout(ISharpLinkClient client)
    {
        var hasField = client.GetType().GetField("_hasRequestTimeout", BindingFlags.Instance | BindingFlags.NonPublic);
        var valueField = client.GetType().GetField("_requestTimeoutValue", BindingFlags.Instance | BindingFlags.NonPublic);
        if (hasField is null || valueField is null)
            throw new Exception("cannot find request-timeout fields");

        var hasTimeout = (bool)(hasField.GetValue(client) ?? false);
        if (!hasTimeout)
            return null;

        return (TimeSpan?)valueField.GetValue(client);
    }

    private static int ReadMaxFramePayloadBytes(ISharpLinkClient client)
    {
        var field = client.GetType().GetField("_protocolOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(client) is not SharpLinkProtocolOptions options)
            throw new Exception("cannot find protocol-options field");

        return options.MaxFramePayloadBytes;
    }

    private static RpcSessionFlushOptions? ReadRpcSessionFlushOptions(ISharpLinkClient client)
    {
        var field = client.GetType().GetField("_rpcSessionFlushOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(client) as RpcSessionFlushOptions?;
    }

    private static SharpLinkConnectionPoolOptions ReadConnectionPool(ISharpLinkClient client)
    {
        var field = client.GetType().GetField("_connectionPoolOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(client) as SharpLinkConnectionPoolOptions ??
               throw new Exception("cannot find connection-pool options");
    }

    private static async Task EnsureThrows<TException>(Func<Task> func) where TException : Exception
    {
        try
        {
            await func();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static async Task EnsureConsumed(Func<Task> action)
    {
        try
        {
            await action();
            throw new Exception("expected consumed builder failure");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message == "This SharpLink builder has already been consumed.",
                "consumed builders must have a stable error message");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class NoopTransport : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingTransport : IClientTransportFactory
    {
        public int DisposeCount { get; private set; }

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingResolver : ISharpLinkEndpointResolver
    {
        public int DisposeCount { get; private set; }

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<SharpLinkEndpointSnapshot>(new NotSupportedException());

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IncompatibleManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => 1;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "0.7.3-legacy-test";
        public Assembly OwnerAssembly => typeof(IncompatibleManifest).Assembly;
        public string CompileTimeDescriptor => "future-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
