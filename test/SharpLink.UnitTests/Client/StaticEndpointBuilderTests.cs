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
    public async Task CompileValidationFailureShouldNotAcquireEndpointFactory()
    {
        var factory = new TrackingFactory();
        var builder = SharpClientBuilder.Create()
            .UseEndpoint(Endpoint("one", 5001), _ => factory)
            .UseConnectionPool(static options => options.MaxConnections = 0);
        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
        Ensure(factory.DisposeCount == 0,
            "Compile validation must not invoke or take ownership of an endpoint factory");
        await EnsureConsumed(builder.Build);
    }

    [Test]
    public void CompileValidationFailureShouldNotRunEndpointFactoryCleanup()
    {
        var factory = new TrackingFactory(throwOnDispose: true);

        var failure = CaptureFailure(() => SharpClientBuilder.Create()
            .UseEndpoint(Endpoint("one", 5001), _ => factory)
            .UseConnectionPool(static options => options.MaxConnections = 0)
            .Build());

        Ensure(ContainsException<ArgumentOutOfRangeException>(failure),
            "Compile validation must preserve the validation failure");
        Ensure(!ContainsMessage(failure, "test disposal failure") && factory.DisposeCount == 0,
            "Compile validation must not create or clean up the endpoint factory");
    }

    [Test]
    public void BuilderRollbackShouldNotDeadlockAsyncCleanupOnASynchronizationContext()
    {
        var factory = new ContextCapturingDisposeFactory();
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                _ = SharpClientBuilder.Create()
                    .UseTransport(factory)
                    .UseProtocol(static options =>
                        options.MaxFramePayloadBytes = SharpLinkProtocolOptions.MinMaxFramePayloadBytes - 1)
                    .Build();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true
        };

        thread.Start();
        Ensure(finished.Wait(TimeSpan.FromSeconds(10)),
            "synchronous Build deadlocked while awaiting async rollback cleanup");
        Ensure(failure is not null && ContainsException<ArgumentOutOfRangeException>(failure),
            "rollback must preserve the original validation failure");
        Ensure(factory.DisposeCompleted,
            "compile-failure cleanup must complete the context-capturing direct transport disposal");
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
    public void ClientMaterializeRollbackShouldPreserveBuildAndRuntimeContextCleanupFailures()
    {
        var builder = SharpClientBuilder.Create()
            .UseEndpoint(Endpoint("one", 5001), _ => new ProfileBindingFailureFactory());
        var plan = builder.CompileForMultiCluster([new ThrowingScopeManifest()]);

        var failure = CaptureFailure(() => builder.MaterializeCompiledPlan(plan));

        Ensure(ContainsMessage(failure, "test profile binding failure"),
            "Client materialization rollback must retain the profile binding failure");
        Ensure(ContainsMessage(failure, "runtime context cleanup failed"),
            "Client materialization rollback must retain Runtime Context cleanup failure");
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
    public async Task BuilderShouldCompileOneFrozenEndpointSnapshotAndThenBeConsumed()
    {
        var attributes = new Dictionary<string, string> { ["zone"] = "first" };
        var endpoints = new List<SharpLinkEndpoint>
        {
            new()
            {
                Id = "first",
                Address = new SharpLinkTcpAddress("127.0.0.1", 5001),
                Attributes = attributes
            }
        };
        var source = new SinglePassEndpointEnumerable(endpoints);
        var createdEndpointIds = new List<string>();
        var builder = SharpClientBuilder.Create()
            .UseEndpoints(source, endpoint =>
            {
                createdEndpointIds.Add(endpoint.Id);
                return new TrackingFactory();
            });

        await using var client = builder.Build();

        attributes["zone"] = "changed";
        endpoints[0] = Endpoint("second", 5002);
        var frozenEndpoint = ReadPrivate<SharpLinkEndpoint>(client, "_fixedEndpoint");

        Ensure(source.EnumerationCount == 1 && source.MoveNextCount == 2,
            "a static endpoint source must be enumerated exactly once during Compile");
        Ensure(createdEndpointIds.SequenceEqual(["first"]),
            "Materialize must use the endpoint frozen by Compile");
        Ensure(frozenEndpoint.Id == "first" && frozenEndpoint.Attributes["zone"] == "first",
            "post-Build source and attribute mutation must not affect the frozen Client plan");
        await EnsureConsumed(builder.Build);
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

    private static Task EnsureConsumed(Func<ISharpLinkClient> build)
    {
        try
        {
            _ = build();
            throw new Exception("expected consumed builder failure");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message == "This SharpLink builder has already been consumed.",
                "consumed builders must have a stable error message");
            return Task.CompletedTask;
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

    private sealed class SinglePassEndpointEnumerable(IReadOnlyList<SharpLinkEndpoint> endpoints)
        : IEnumerable<SharpLinkEndpoint>
    {
        private int _enumerationCount;
        private int _moveNextCount;

        internal int EnumerationCount => Volatile.Read(ref _enumerationCount);
        internal int MoveNextCount => Volatile.Read(ref _moveNextCount);

        public IEnumerator<SharpLinkEndpoint> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerationCount) != 1)
                throw new InvalidOperationException("endpoint source must not be enumerated twice");

            for (var index = 0; index < endpoints.Count; index++)
            {
                Interlocked.Increment(ref _moveNextCount);
                yield return endpoints[index];
            }
            Interlocked.Increment(ref _moveNextCount);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

    private sealed class ContextCapturingDisposeFactory : IClientTransportFactory
    {
        internal bool DisposeCompleted { get; private set; }

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            DisposeCompleted = true;
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
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
