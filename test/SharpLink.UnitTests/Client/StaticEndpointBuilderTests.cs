using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Reflection;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class StaticEndpointBuilderTests
{
    [Test]
    public async Task BuiltInEndpointFactoriesShouldFreezeConfigurationAtCreation()
    {
        var socketOptions = new SocketTransportOptions { NoDelay = true };
        var socketFactory = SharpLinkTransportFactories.Sockets(socketOptions);
        socketOptions.NoDelay = false;
        await using var socket = socketFactory(Endpoint("socket", 5001));
        Ensure(ReadPrivate<SocketTransportOptions>(socket, "_options").NoDelay,
            "socket options must be frozen before endpoint generations are created");

        var tlsOptions = new SslClientAuthenticationOptions { TargetHost = "original.example" };
        var tlsFactory = SharpLinkTransportFactories.Sockets(tlsOptions);
        tlsOptions.TargetHost = "changed.example";
        await using var tls = tlsFactory(Endpoint("tls", 5002));
        Ensure(ReadPrivate<SslClientAuthenticationOptions>(tls, "_tlsOptions").TargetHost == "original.example",
            "TLS options must be frozen before endpoint generations are created");

        SharedMemoryTransportOptions? leaked = null;
        var sharedMemoryFactory = SharpLinkTransportFactories.SharedMemory(options =>
        {
            options.SpinCount = 1;
            leaked = options;
        });
        leaked!.SpinCount = 2;
        await using var sharedMemory = sharedMemoryFactory(new SharpLinkEndpoint
        {
            Id = "memory",
            Address = new SharpLinkSharedMemoryAddress("factory-snapshot")
        });
        Ensure(ReadPrivate<SharedMemoryTransportOptions>(sharedMemory, "_options").SpinCount == 1,
            "shared-memory options must be frozen before endpoint generations are created");
    }

    [Test]
    public async Task AddressValidationAndAnonymousPipeRedactionShouldBeStable()
    {
        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            _ = new SharpLinkTcpAddress("localhost", 0);
            return Task.CompletedTask;
        });
        var address = new SharpLinkAnonymousPipeAddress("in-secret", "out-secret");
        Ensure(!address.ToString().Contains("secret", StringComparison.Ordinal), "anonymous handles must not be rendered");
    }

    [Test]
    public async Task SingleEndpointShouldFreezeAttributesAndDisposeItsFactoryOnce()
    {
        var attributes = new Dictionary<string, string> { ["zone"] = "a" };
        SharpLinkEndpoint? received = null;
        var factory = new TrackingFactory();
        var client = SharpClientBuilder.Create()
            .UseEndpoint(
                new SharpLinkEndpoint
                {
                    Id = "one",
                    Address = new SharpLinkTcpAddress("127.0.0.1", 5001),
                    Attributes = attributes
                },
                endpoint =>
                {
                    received = endpoint;
                    return factory;
                })
            .Build();

        attributes["zone"] = "changed";
        Ensure(received is not null && received.Attributes["zone"] == "a", "endpoint attributes must be frozen");
        var endpointField = client.GetType().GetField("_fixedEndpoint", BindingFlags.Instance | BindingFlags.NonPublic);
        Ensure((endpointField?.GetValue(client) as SharpLinkEndpoint)?.Id == "one", "fixed mode must retain endpoint identity");
        await client.DisposeAsync();
        Ensure(factory.DisposeCount == 1, "single endpoint factory disposal count");
    }

    [Test]
    public async Task SingleEndpointFactoryShouldBeReleasedWhenLaterBuildValidationFails()
    {
        var factory = new TrackingFactory();
        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoint(Endpoint("one", 5001), _ => factory)
                .UseConnectionPool(static options => options.MaxConnections = 0)
                .Build();
            return Task.CompletedTask;
        });
        Ensure(factory.DisposeCount == 1, "factory disposal after a fixed-client build failure");
    }

    [Test]
    public void SingleEndpointBuildRollbackShouldPreserveValidationAndCleanupFailures()
    {
        var factory = new TrackingFactory(throwOnDispose: true);

        var failure = CaptureFailure(() => SharpClientBuilder.Create()
            .UseEndpoint(Endpoint("one", 5001), _ => factory)
            .UseConnectionPool(static options => options.MaxConnections = 0)
            .Build());

        Ensure(ContainsException<ArgumentOutOfRangeException>(failure),
            "fixed-endpoint rollback must retain the build validation failure");
        Ensure(ContainsMessage(failure, "test disposal failure"),
            "fixed-endpoint rollback must retain the transport cleanup failure");
    }

    [Test]
    public async Task SingleEndpointAnonymousPipeFactoryShouldRejectExpandedConnectionPools()
    {
        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoint(
                    new SharpLinkEndpoint
                    {
                        Id = "pipe",
                        Address = new SharpLinkAnonymousPipeAddress("in-handle", "out-handle")
                    },
                    _ => new AnonymousPipeClientTransportFactory("in-handle", "out-handle"))
                .UseConnectionPool(options => options.MaxConnections = 2)
                .Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task StaticClusterShouldRejectAnonymousPipeFactories()
    {
        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints(
                    [Endpoint("first", 5001), Endpoint("second", 5002)],
                    _ => new AnonymousPipeClientTransportFactory("in-handle", "out-handle"))
                .Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task EndpointFactoryShouldBeDisposedWhenProfileBindingFails()
    {
        var factory = new ProfileBindingFailureFactory();
        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints([Endpoint("one", 5001), Endpoint("two", 5002)], _ => factory)
                .Build();
            return Task.CompletedTask;
        });
        Ensure(factory.DisposeCount == 1, "profile binding failure must release the newly created factory");
    }

    [Test]
    public void ProfileBindingRollbackShouldPreserveBindingAndCleanupFailures()
    {
        var factory = new ProfileBindingFailureFactory(throwOnDispose: true);

        var failure = CaptureFailure(() => SharpClientBuilder.Create()
            .UseEndpoints([Endpoint("one", 5001), Endpoint("two", 5002)], _ => factory)
            .Build());

        Ensure(ContainsMessage(failure, "test profile binding failure"),
            "profile rollback must retain the binding failure");
        Ensure(ContainsMessage(failure, "profile cleanup failure"),
            "profile rollback must retain the factory cleanup failure");
    }

    [Test]
    public void ClientBuildRollbackShouldPreserveBuildAndRuntimeContextCleanupFailures()
    {
        var failure = CaptureFailure(() => SharpClientBuilder.Create()
            .UseEndpoint(Endpoint("one", 5001), _ => new TrackingFactory())
            .UseConnectionPool(static options => options.MaxConnections = 0)
            .BuildCore([new ThrowingScopeManifest()]));

        Ensure(ContainsException<ArgumentOutOfRangeException>(failure),
            "Client rollback must retain the original build failure");
        Ensure(ContainsMessage(failure, "runtime context cleanup failed"),
            "Client rollback must retain Runtime Context cleanup failure");
    }

    [Test]
    public async Task StaticClusterShouldOwnEveryFactoryExactlyOnce()
    {
        var first = new TrackingFactory();
        var second = new TrackingFactory();
        var client = SharpClientBuilder.Create()
            .UseEndpoints(
                [Endpoint("first", 5001), Endpoint("second", 5002)],
                endpoint => endpoint.Id == "first" ? first : second)
            .Build();

        await client.DisposeAsync();
        Ensure(first.DisposeCount == 1, "first cluster factory disposal count");
        Ensure(second.DisposeCount == 1, "second cluster factory disposal count");
    }

    [Test]
    public async Task BuilderShouldTakeFreshEndpointSnapshotsAfterPreflightBuilds()
    {
        var endpoints = new List<SharpLinkEndpoint> { Endpoint("first", 5001) };
        var createdEndpointIds = new List<string>();
        var builder = SharpClientBuilder.Create()
            .UseEndpoints(endpoints, endpoint =>
            {
                createdEndpointIds.Add(endpoint.Id);
                return new TrackingFactory();
            });

        Ensure(builder.GetConfiguredMaximumConnections() == 1,
            "one endpoint should reserve the fixed-client connection budget");
        await using (var first = builder.Build())
        {
        }

        endpoints[0] = Endpoint("second", 5002);
        await using (var second = builder.Build())
        {
        }

        Ensure(createdEndpointIds.SequenceEqual(["first", "second"]),
            "a reused builder must take a fresh endpoint snapshot for each build");
    }

    [Test]
    public async Task ClusterBuildCleanupShouldReleaseEveryFactoryWhenOneDisposalFails()
    {
        var throwing = new TrackingFactory(throwOnDispose: true);
        var remaining = new TrackingFactory();
        await EnsureThrows<AggregateException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints(
                    [Endpoint("first", 5001), Endpoint("second", 5002), Endpoint("duplicate", 5003)],
                    endpoint => endpoint.Id switch
                    {
                        "first" => throwing,
                        "second" => remaining,
                        _ => remaining
                    })
                .Build();
            return Task.CompletedTask;
        });

        Ensure(throwing.DisposeCount == 1, "throwing factory must be attempted once");
        Ensure(remaining.DisposeCount == 1, "later factory cleanup must run after an earlier disposal failure");
    }

    [Test]
    public async Task ClusterStopShouldReachStoppedWhenFactoryCleanupFails()
    {
        var throwing = new TrackingFactory(throwOnDispose: true);
        var remaining = new TrackingFactory();
        var client = SharpClientBuilder.Create()
            .UseEndpoints(
                [Endpoint("first", 5001), Endpoint("second", 5002)],
                endpoint => endpoint.Id == "first" ? throwing : remaining)
            .Build();

        await EnsureThrows<InvalidOperationException>(() => client.StopAsync().AsTask());

        Ensure(((SharpLinkClient)client).State == SharpLinkConnectionState.Stopped,
            "outer client cleanup must reach Stopped after cluster cleanup fails");
        Ensure(remaining.DisposeCount == 1, "cluster cleanup must still dispose later factories");
    }

    [Test]
    public async Task BuilderShouldRejectConflictingModesAndOptions()
    {
        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseTransport(new TrackingFactory())
                .UseEndpoints([Endpoint("first", 5001), Endpoint("second", 5002)], _ => new TrackingFactory())
                .Build();
            return Task.CompletedTask;
        });

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints([Endpoint("first", 5001), Endpoint("second", 5002)], _ => new TrackingFactory())
                .UseConnectionPool(static options => options.MaxConnections = 2)
                .Build();
            return Task.CompletedTask;
        });

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseLoadBalancing(SharpLinkLoadBalancingStrategy.Random)
                .UseEndpointSelector(new FirstSelector());
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuilderShouldValidateEndpointIdsAndClusterBounds()
    {
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints([Endpoint("duplicate", 5001), Endpoint("duplicate", 5002)], _ => new TrackingFactory())
                .Build();
            return Task.CompletedTask;
        });

        await EnsureThrows<ArgumentException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints([Endpoint("one", 5001), Endpoint("two", 5002)], _ => new TrackingFactory())
                .UseCluster(static options =>
                {
                    options.MinReadyEndpoints = 5;
                    options.MaxConnections = 1;
                    options.MaxConnectionsPerEndpoint = 1;
                })
                .Build();
            return Task.CompletedTask;
        });

        await EnsureThrows<ArgumentException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints([], _ => new TrackingFactory())
                .Build();
            return Task.CompletedTask;
        });

        await EnsureThrows<ArgumentException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints(
                    Enumerable.Range(0, SharpLinkClusterOptions.MaximumEndpoints + 1)
                        .Select(index => Endpoint($"endpoint-{index}", 5001 + index)),
                    _ => new TrackingFactory())
                .Build();
            return Task.CompletedTask;
        });

        await EnsureThrows<ArgumentException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints(
                    [new SharpLinkEndpoint
                    {
                        Id = "one",
                        Address = new SharpLinkTcpAddress("127.0.0.1", 5001),
                        Attributes = new Dictionary<string, string> { [" "] = "invalid" }
                    }],
                    _ => new TrackingFactory())
                .Build();
            return Task.CompletedTask;
        });

        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints([Endpoint("one", 5001), Endpoint("two", 5002)], _ => new TrackingFactory())
                .UseCluster(static options => options.MaxRetiringConnections = -1)
                .Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task ClusterMinReadyShouldUseTheEndpointCountAsItsEffectiveUpperBound()
    {
        var first = new TrackingFactory();
        var second = new TrackingFactory();
        await using var client = SharpClientBuilder.Create()
            .UseEndpoints(
                [Endpoint("one", 5001), Endpoint("two", 5002)],
                endpoint => endpoint.Id == "one" ? first : second)
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 5;
                options.MaxConnections = 4;
                options.MaxConnectionsPerEndpoint = 2;
            })
            .Build();

        Ensure(first.DisposeCount == 0 && second.DisposeCount == 0, "factories remain client-owned until stop");
    }

    [Test]
    public async Task ClusterShouldRejectAFactoryInstanceSharedAcrossEndpoints()
    {
        var shared = new TrackingFactory();
        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseEndpoints([Endpoint("one", 5001), Endpoint("two", 5002)], _ => shared)
                .Build();
            return Task.CompletedTask;
        });
        Ensure(shared.DisposeCount == 1, "rejected shared factory must be disposed exactly once");
    }

    private static SharpLinkEndpoint Endpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress("127.0.0.1", port)
    };

    private static T ReadPrivate<T>(object instance, string fieldName) where T : class
        => instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
           ?? throw new Exception($"cannot find {fieldName}");

    private static async Task EnsureThrows<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static Exception CaptureFailure(Action action)
    {
        try
        {
            action();
            throw new Exception("expected operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(inner => ContainsMessage(inner, message));
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private static bool ContainsException<TException>(Exception exception) where TException : Exception
    {
        if (exception is TException)
            return true;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(ContainsException<TException>);
        return exception.InnerException is { } nested && ContainsException<TException>(nested);
    }

    private sealed class TrackingFactory(bool throwOnDispose = false) : IClientTransportFactory
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            if (throwOnDispose)
                return ValueTask.FromException(new InvalidOperationException("test disposal failure"));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProfileBindingFailureFactory(bool throwOnDispose = false)
        : IClientTransportFactory, IPerformanceProfileAwareTransport
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
            => throw new InvalidOperationException("test profile binding failure");

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return throwOnDispose
                ? ValueTask.FromException(new InvalidOperationException("profile cleanup failure"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingScopeManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ThrowingScopeManifest).Assembly;
        public string CompileTimeDescriptor => "client-build-rollback";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [new ThrowingScopeCodecFactory()];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class ThrowingScopeCodecFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(BuilderValue);
        public string SchemaId => "builder-value/v1";
        public string WireFormatId => "builder-wire/v1";
        public string AdapterId => "builder-adapter/v1";
        public IRpcCodecAdapter Adapter { get; } = new ThrowingScopeAdapter();
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<BuilderValue>();
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<BuilderValue>;
    }

    private sealed class ThrowingScopeAdapter : IRpcCodecAdapter
    {
        public string AdapterId => "builder-adapter/v1";
        public string WireFormatId => "builder-wire/v1";
        public IRpcCodecAdapterScope CreateScope() => new ThrowingScope();
    }

    private sealed class ThrowingScope : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>() => new EmptyCodec<T>();
        public void Dispose() => throw new InvalidOperationException("runtime context cleanup failed");
    }

    private sealed class EmptyCodec<T> : IRpcCodec<T>
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer) { }
        public T? Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class BuilderValue;

    private sealed class FirstSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context) => 0;
    }
}
