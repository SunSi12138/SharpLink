using System.Net;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Builder;

[NotInParallel]
public class BuilderOwnershipRollbackTests
{
    [Test]
    public void DirectClientProfileFailureShouldDisposeTransportAndPreserveBothFailures()
    {
        var transport = new TrackingClientTransport(
            bindingFailure: "direct Client profile binding failed",
            cleanupFailure: "direct Client transport cleanup failed");

        var failure = Capture(() => SharpClientBuilder.Create()
            .UseTransport(transport)
            .Build());

        Ensure(Contains(failure, "direct Client profile binding failed"),
            "direct Client build retains profile failure");
        Ensure(Contains(failure, "direct Client transport cleanup failed"),
            "direct Client build retains transport cleanup failure");
        Ensure(transport.DisposeCount == 1, "direct Client build disposes its transport once");
    }

    [Test]
    public void DirectClientConstructionFailureShouldDisposeTransportAndPreserveBothFailures()
    {
        var transport = new TrackingClientTransport(
            bindingFailure: null,
            cleanupFailure: "direct Client construction transport cleanup failed");
        var logger = new ThrowingLoggerFactory("direct Client logger construction failed");

        var failure = Capture(() => SharpClientBuilder.Create()
            .UseTransport(transport)
            .UseLoggerFactory(logger)
            .Build());

        Ensure(Contains(failure, "direct Client logger construction failed"),
            "direct Client build retains constructor failure");
        Ensure(Contains(failure, "direct Client construction transport cleanup failed"),
            "direct Client construction retains transport cleanup failure");
        Ensure(transport.DisposeCount == 1, "failed direct Client construction disposes its transport once");
        Ensure(logger.DisposeCount == 0, "Client build failure must not dispose the caller-owned logger factory");
    }

    [Test]
    public void ClientRuntimeContextConstructionFailureShouldRollbackTheConsumedTransport()
    {
        var transport = new TrackingClientTransport(
            bindingFailure: null,
            cleanupFailure: "Client context construction transport cleanup failed");

        var builder = SharpClientBuilder.Create().UseTransport(transport);
        var plan = builder.CompileForMultiCluster([new ThrowingRuntimeContextManifest()]);

        var failure = Capture(() => builder.MaterializeCompiledPlan(plan));

        Ensure(Contains(failure, "controlled Runtime Context construction failure"),
            "Client RuntimeContext construction failure must remain primary");
        Ensure(Contains(failure, "Client context construction transport cleanup failed"),
            "Client RuntimeContext construction failure must aggregate consumed transport cleanup");
        Ensure(transport.DisposeCount == 1, "Client RuntimeContext construction failure disposes transport once");
    }

    [Test]
    public void EndpointFactoryFailureShouldRollbackPreviouslyMaterializedFactories()
    {
        var first = new TrackingClientTransport(
            bindingFailure: null,
            cleanupFailure: "first endpoint factory cleanup failed");

        var failure = Capture(() => SharpClientBuilder.Create()
            .UseEndpoints(
                [CreateEndpoint("first", 6811), CreateEndpoint("second", 6812)],
                endpoint => endpoint.Id == "first"
                    ? first
                    : throw new InvalidOperationException("second endpoint factory failed"))
            .Build());

        Ensure(Contains(failure, "second endpoint factory failed"),
            "endpoint factory exception must remain primary");
        Ensure(Contains(failure, "first endpoint factory cleanup failed"),
            "endpoint factory exception must aggregate previous factory cleanup");
        Ensure(first.DisposeCount == 1, "previous endpoint factory must be disposed exactly once");
    }

    [Test]
    public void StaticClientFactoryBindingFailureShouldRollbackFactoriesInReverseExactlyOnce()
    {
        var probe = new BuilderFaultInjectionProbe();
        var failure = Capture(() => SharpClientBuilder.Create()
            .UseEndpoints(
                [CreateEndpoint("first", 6801), CreateEndpoint("second", 6802)],
                endpoint =>
                {
                    probe.RecordAcquisition(endpoint.Id);
                    return new TrackingClientTransport(
                        bindingFailure: endpoint.Id == "second" ? "second factory binding failed" : null,
                        cleanupFailure: $"{endpoint.Id} factory cleanup failed",
                        probe,
                        endpoint.Id);
                })
            .Build());

        BuilderFaultInjectionProbe.AssertFailureOrder(
            failure,
            "second factory binding failed",
            "second factory cleanup failed",
            "first factory cleanup failed");
        probe.AssertAcquisitionOrder("first", "second");
        probe.AssertReverseCleanupAndExactlyOnce();
    }

    [Test]
    public void DynamicResolverValidationFailureShouldDisposeResolverAndPreserveBothFailures()
    {
        var resolver = new TrackingResolver("dynamic resolver cleanup failed");

        var failure = Capture(() => SharpClientBuilder.Create()
            .UseEndpointResolver(resolver, static _ => new NoopClientTransport())
            .UseConnectionPool(static _ => { })
            .Build());

        Ensure(Contains(failure, "UseConnectionPool is only available"),
            "dynamic Client build retains validation failure");
        Ensure(Contains(failure, "dynamic resolver cleanup failed"),
            "dynamic Client build retains resolver cleanup failure");
        Ensure(resolver.DisposeCount == 1, "failed dynamic Client build disposes its resolver once");
    }

    [Test]
    public void ClientConstructionFailureMustNotDisposeCallerProvidedCodec()
    {
        var transport = new TrackingClientTransport(bindingFailure: null, cleanupFailure: null);
        var codec = new TrackingCodec();
        var logger = new ThrowingLoggerFactory("Client codec ownership logger failure");

        var failure = Capture(() => SharpClientBuilder.Create()
            .UseTransport(transport)
            .UseCodec(codec)
            .UseLoggerFactory(logger)
            .Build());

        Ensure(Contains(failure, "Client codec ownership logger failure"),
            "Client construction failure must reach the final construction fault");
        Ensure(transport.DisposeCount == 1, "Client construction failure disposes its framework-owned transport");
        Ensure(codec.DisposeCount == 0, "Client construction failure must not dispose caller-provided codecs");
        Ensure(logger.DisposeCount == 0, "Client construction failure must not dispose caller-provided loggers");
    }

    [Test]
    public void MultiClusterConstructionFailureShouldRollbackCompletedChildren()
    {
        var childTransport = new TrackingClientTransport(
            bindingFailure: null,
            cleanupFailure: "multi-cluster child transport cleanup failed");
        var logger = new MultiClusterThrowingLoggerFactory("multi-cluster logger construction failed");
        var builder = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("dynamic", child => child.UseTransport(childTransport),
                slot => slot.AllowDynamicContracts = true);
        builder.UseLoggerFactoryIfUnset(logger);

        var failure = Capture(() => { _ = builder.Build(); });

        Ensure(Contains(failure, "multi-cluster logger construction failed"),
            "coordinator construction failure must remain primary");
        Ensure(Contains(failure, "multi-cluster child transport cleanup failed"),
            "coordinator construction failure must aggregate completed-child cleanup");
        Ensure(childTransport.DisposeCount == 1, "completed multi-cluster child must be disposed once");
        Ensure(logger.DisposeCount == 0, "MultiCluster build failure must not dispose the caller logger factory");
    }

    [Test]
    public void ServerCompileValidationFailureShouldNotMaterializeRuntimeContext()
    {
        RollbackState.TestIsolation.Wait();
        try
        {
            WithRollbackManifest(() =>
            {
                var transport = new TrackingServerTransport();
                var failure = Capture(() => SharpLinkServerBuilder.Create()
                    .UseTransport(transport)
                    .EnableService<IMissingService>()
                    .Build());

                Ensure(Contains(failure, "required contract"), "Server Compile retains service validation failure");
                Ensure(!Contains(failure, "rollback Adapter scope cleanup failed"),
                    "Server Compile validation must not create a RuntimeContext cleanup path");
                Ensure(RollbackState.ScopeDisposeCount == 0,
                    "Server Compile validation must not materialize generated adapter scopes");
                Ensure(transport.DisposeCount == 1, "Server Compile validation still disposes listener once");
            });
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public void ServerConstructorFailureShouldDisposeRuntimeContextAndPreserveBothFailures()
    {
        RollbackState.TestIsolation.Wait();
        try
        {
            WithRollbackManifest(() =>
            {
                var transport = new TrackingServerTransport("Server transport cleanup failed");
                var logger = new ThrowingLoggerFactory("Server logger construction failed");
                var failure = Capture(() => SharpLinkServerBuilder.Create()
                    .UseTransport(transport)
                    .UseLoggerFactory(logger)
                    .Build());

                Ensure(Contains(failure, "Server logger construction failed"),
                    "Server build retains constructor failure");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"),
                    "Server constructor rollback retains Runtime Context cleanup failure");
                Ensure(Contains(failure, "Server transport cleanup failed"),
                    "Server constructor rollback retains transport cleanup failure");
                Ensure(RollbackState.ScopeDisposeCount == 1, "Server constructor rollback disposes Context once");
                Ensure(transport.DisposeCount == 1, "failed Server build disposes its listener once");
                Ensure(logger.DisposeCount == 0, "Server build failure must not dispose the caller logger factory");
            });
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ServerListenerShouldBeTransferredByOnlyOneBuild()
    {
        var transport = new TrackingServerTransport();
        var builder = SharpLinkServerBuilder.Create().UseTransport(transport);
        var first = builder.Build();

        var failure = Capture(() => builder.Build());
        Ensure(failure is InvalidOperationException, "a second build must require a replacement listener");

        await first.DisposeAsync();
        Ensure(transport.DisposeCount == 1, "one Server must own and dispose the listener");
    }

    [Test]
    public void ServerRuntimeContextConstructionFailureShouldRollbackTheConsumedListener()
    {
        RollbackState.TestIsolation.Wait();
        var manifest = new ThrowingRuntimeContextManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        try
        {
            var transport = new TrackingServerTransport("Server context construction listener cleanup failed");
            var failure = Capture(() => SharpLinkServerBuilder.Create()
                .UseTransport(transport)
                .Build());

            Ensure(Contains(failure, "controlled Runtime Context construction failure"),
                "Server RuntimeContext construction failure must remain primary");
            Ensure(Contains(failure, "Server context construction listener cleanup failed"),
                "Server RuntimeContext construction failure must aggregate listener cleanup");
            Ensure(transport.DisposeCount == 1, "Server RuntimeContext construction failure disposes listener once");
        }
        finally
        {
            RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
            RollbackState.TestIsolation.Release();
            GC.KeepAlive(manifest);
        }
    }

    [Test]
    public void ServerProfileFailureShouldRollbackListenerAndRuntimeContext()
    {
        var transport = new TrackingServerTransport(
            cleanupFailure: "Server profile listener cleanup failed",
            bindingFailure: "Server listener profile bind failed");

        var failure = Capture(() => SharpLinkServerBuilder.Create()
            .UseTransport(transport)
            .Build());

        Ensure(Contains(failure, "Server listener profile bind failed"),
            "Server listener profile failure must remain primary");
        Ensure(Contains(failure, "Server profile listener cleanup failed"),
            "Server listener profile failure must aggregate listener cleanup");
        Ensure(transport.DisposeCount == 1, "Server listener profile failure disposes listener once");
    }

    [Test]
    public void ServerAdmissionFailureMustRollbackFrameworkResourcesWithoutDisposingCallerProvider()
    {
        var transport = new TrackingServerTransport();
        var provider = new TrackingServiceProvider();

        var failure = Capture(() => SharpLinkServerBuilder.Create()
            .UseTransport(transport)
            .UseServiceProvider(provider)
            .UseAdmissionControl(options => options.AddContract<IMissingService>(
                static rule => rule.UseConcurrency(1)))
            .Build());

        Ensure(Contains(failure, "required by admission control was not found"),
            "admission construction failure must remain primary");
        Ensure(transport.DisposeCount == 1, "admission construction failure disposes listener once");
        Ensure(provider.DisposeCount == 0, "admission failure must not dispose caller-provided service providers");
    }

    [Test]
    public void ServerRegistrationBuildFailureShouldRollbackPriorMaterializationsInReverse()
    {
        RollbackState.TestIsolation.Wait();
        var manifest = new RegistrationRollbackManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        try
        {
            var cleanupEvents = new List<string>();
            var first = new TrackingRegistrationServiceOne(cleanupEvents);
            var second = new TrackingRegistrationServiceTwo(cleanupEvents);
            var provider = new TrackingServiceProvider();
            var transport = new TrackingServerTransport(
                cleanupEvents: cleanupEvents,
                cleanupResource: "listener");
            var builder = SharpLinkServerBuilder.Create()
                .UseTransport(transport)
                .UseServiceProvider(provider)
                .UseAdmissionControl(static options => options.Global.UseConcurrency(1))
                .ReplaceService<IRegistrationServiceOne>(first)
                .ReplaceService<IRegistrationServiceTwo>(second)
                .ReplaceService<IRegistrationBuildFailure>(
                    static _ => new RegistrationBuildFailureService(),
                    SharpLinkServiceLifetime.Connection);
            MarkReplacementFrameworkOwned(builder, typeof(IRegistrationServiceOne));
            MarkReplacementFrameworkOwned(builder, typeof(IRegistrationServiceTwo));

            var failure = Capture(() => { _ = builder.Build(); });

            Ensure(Contains(failure, "Connection and Call SharpLink services require an IServiceScopeFactory"),
                "the third ServiceRegistrationDefinition.Build failure must remain primary");
            Ensure(provider.RequestedServices.Contains(typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory)),
                "the failing third registration must reach ServiceRegistrationDefinition.Build");
            Ensure(first.DisposeCount == 1 && second.DisposeCount == 1,
                "each framework-owned materialized ServiceRegistration must release its singleton once");
            EnsureSequence(cleanupEvents, "registration:second", "registration:first", "listener");
            Ensure(provider.DisposeCount == 0, "caller provider registration must remain non-disposing");
            Ensure(transport.DisposeCount == 1,
                "listener must release after prior registrations, admission, caller provider, and RuntimeContext rollback");
        }
        finally
        {
            RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
            RollbackState.TestIsolation.Release();
            GC.KeepAlive(manifest);
        }
    }

    [Test]
    public void ServerConstructionFailureMustNotDisposeCallerProvider()
    {
        var transport = new TrackingServerTransport();
        var provider = new TrackingServiceProvider();
        var logger = new ThrowingLoggerFactory("Server caller provider logger construction failed");

        var failure = Capture(() => SharpLinkServerBuilder.Create()
            .UseTransport(transport)
            .UseServiceProvider(provider)
            .UseLoggerFactory(logger)
            .Build());

        Ensure(Contains(failure, "Server caller provider logger construction failed"),
            "Server final construction failure must remain primary");
        Ensure(transport.DisposeCount == 1, "Server final construction failure disposes listener once");
        Ensure(provider.DisposeCount == 0, "Server final construction failure must not dispose caller providers");
        Ensure(logger.DisposeCount == 0, "Server final construction failure must not dispose caller loggers");
    }

    [Test]
    public void ServerFinalConstructionFailureMustNotDisposeCallerOwnedService()
    {
        RollbackState.TestIsolation.Wait();
        var manifest = new RegistrationRollbackManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        try
        {
            var transport = new TrackingServerTransport();
            var callerOwnedService = new TrackingRegistrationServiceOne([]);
            var logger = new ThrowingLoggerFactory("Server caller service logger construction failed");

            var failure = Capture(() => SharpLinkServerBuilder.Create()
                .UseTransport(transport)
                .ReplaceService<IRegistrationServiceOne>(callerOwnedService)
                .UseLoggerFactory(logger)
                .Build());

            Ensure(Contains(failure, "Server caller service logger construction failed"),
                "final Server construction failure must remain primary after a caller-owned registration materializes");
            Ensure(callerOwnedService.DisposeCount == 0,
                "rollback must dispose the registration but never the caller-owned service singleton");
            Ensure(logger.DisposeCount == 0, "rollback must not dispose the caller logger factory");
            Ensure(transport.DisposeCount == 1, "rollback must release the framework-owned listener");
        }
        finally
        {
            RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
            RollbackState.TestIsolation.Release();
            GC.KeepAlive(manifest);
        }
    }

    private static void WithRollbackManifest(Action action)
    {
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "builder-rollback-schema");
        RollbackState.ScopeDisposeCount = 0;
        var manifest = new RollbackManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        try
        {
            action();
        }
        finally
        {
            RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", null);
            GC.KeepAlive(manifest);
        }
    }

    private static Exception Capture(Action action)
    {
        try { action(); throw new Exception("expected build failure"); }
        catch (Exception exception) { return exception; }
    }

    private static bool Contains(Exception exception, string text)
    {
        if (exception.Message.Contains(text, StringComparison.Ordinal)) return true;
        if (exception is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) if (Contains(inner, text)) return true;
        return exception.InnerException is { } nested && Contains(nested, text);
    }

    private static SharpLinkEndpoint CreateEndpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void EnsureSequence(IReadOnlyList<string> actual, params string[] expected)
    {
        Ensure(actual.Count == expected.Length,
            $"expected {expected.Length} cleanup events but saw {actual.Count}: {string.Join(", ", actual)}");
        for (var index = 0; index < expected.Length; index++)
        {
            Ensure(string.Equals(actual[index], expected[index], StringComparison.Ordinal),
                $"cleanup event {index} must be '{expected[index]}' but was '{actual[index]}'");
        }
    }

    private static void MarkReplacementFrameworkOwned(SharpLinkServerBuilder builder, Type contractType)
    {
        var definitionsField = typeof(SharpLinkServerBuilder).GetField(
            "_replacementServices",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new Exception("cannot find Server Builder replacement services");
        var definitions = (System.Collections.IDictionary)(definitionsField.GetValue(builder) ??
            throw new Exception("cannot read Server Builder replacement services"));
        var replacement = definitions[contractType] ??
            throw new Exception($"cannot find replacement for '{contractType.FullName}'");
        var replacementType = replacement.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var instance = replacementType.GetProperty("Instance", flags)?.GetValue(replacement);
        var factory = replacementType.GetProperty("Factory", flags)?.GetValue(replacement);
        var lifetime = replacementType.GetProperty("Lifetime", flags)?.GetValue(replacement);
        ConstructorInfo? constructor = null;
        foreach (var candidate in replacementType.GetConstructors(flags))
        {
            if (candidate.GetParameters().Length == 4)
            {
                constructor = candidate;
                break;
            }
        }
        if (constructor is null || lifetime is null)
            throw new Exception("cannot construct framework-owned Server Builder replacement");

        definitions[contractType] = constructor.Invoke([instance, factory, lifetime, false]);
    }

    private interface IMissingService : IService;

    private interface IRegistrationServiceOne : IService;

    private interface IRegistrationServiceTwo : IService;

    private interface IRegistrationBuildFailure : IService;

    private sealed class CodecValue;

    private sealed class TrackingClientTransport(
        string? bindingFailure,
        string? cleanupFailure,
        BuilderFaultInjectionProbe? probe = null,
        string? resource = null) :
        IClientTransportFactory,
        IPerformanceProfileAwareTransport
    {
        public int DisposeCount { get; private set; }

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            if (bindingFailure is not null)
                throw new InvalidOperationException(bindingFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (probe is not null)
                probe.RecordCleanup(resource ?? throw new InvalidOperationException("Tracked resource name is required."));
            return cleanupFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(new InvalidOperationException(cleanupFailure));
        }
    }

    private sealed class NoopClientTransport : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingServerTransport(
        string? cleanupFailure = null,
        string? bindingFailure = null,
        List<string>? cleanupEvents = null,
        string? cleanupResource = null) : IServerTransportListener, IPerformanceProfileAwareTransport
    {
        public int DisposeCount { get; private set; }
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            if (bindingFailure is not null)
                throw new InvalidOperationException(bindingFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            cleanupEvents?.Add(cleanupResource ?? "listener");
            return cleanupFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(new InvalidOperationException(cleanupFailure));
        }
    }

    private sealed class TrackingResolver(string cleanupFailure) : ISharpLinkEndpointResolver
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
            return ValueTask.FromException(new InvalidOperationException(cleanupFailure));
        }
    }

    private sealed class ThrowingLoggerFactory(string failure) : ILoggerFactory
    {
        public int DisposeCount { get; private set; }
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => throw new InvalidOperationException(failure);
        public void Dispose() => DisposeCount++;
    }

    private sealed class MultiClusterThrowingLoggerFactory(string failure) : ILoggerFactory
    {
        public int DisposeCount { get; private set; }

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName)
            => categoryName.Contains(nameof(SharpLinkMultiClusterClient), StringComparison.Ordinal)
                ? throw new InvalidOperationException(failure)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingServiceProvider : IServiceProvider, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public List<Type> RequestedServices { get; } = [];

        public object? GetService(Type serviceType)
        {
            RequestedServices.Add(serviceType);
            return null;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingCodec : IRpcCodec<CodecValue>, IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Serialize(in CodecValue value, IBufferWriter<byte> buffer) { }

        public CodecValue? Deserialize(in ReadOnlySequence<byte> buffer) => null;

        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingRuntimeContextManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ThrowingRuntimeContextManifest).Assembly;
        public string CompileTimeDescriptor => "builder-runtime-context-throw";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [new ThrowingRuntimeContextCodecFactory()];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class ThrowingRuntimeContextCodecFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(CodecValue);
        public string SchemaId => "builder-runtime-context-throw/v1";
        public string WireFormatId => "builder-runtime-context-wire/v1";
        public string? AdapterId => "builder-runtime-context-adapter/v1";
        public IRpcCodecAdapter Adapter { get; } = new ThrowingRuntimeContextAdapter();

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => new TrackingCodec();

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<CodecValue>;
    }

    private sealed class ThrowingRuntimeContextAdapter : IRpcCodecAdapter
    {
        public string AdapterId => "builder-runtime-context-adapter/v1";
        public string WireFormatId => "builder-runtime-context-wire/v1";

        public IRpcCodecAdapterScope CreateScope()
            => throw new InvalidOperationException("controlled Runtime Context construction failure");
    }

    private sealed class TrackingRegistrationServiceOne(List<string> cleanupEvents) : IRegistrationServiceOne, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            cleanupEvents.Add("registration:first");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingRegistrationServiceTwo(List<string> cleanupEvents) : IRegistrationServiceTwo, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            cleanupEvents.Add("registration:second");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RegistrationBuildFailureService : IRegistrationBuildFailure
    {
    }

    private sealed class RegistrationRollbackManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(RegistrationRollbackManifest).Assembly;
        public string CompileTimeDescriptor => "builder-registration-rollback";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [
            CreateContract(typeof(IRegistrationServiceOne), 91_001),
            CreateContract(typeof(IRegistrationServiceTwo), 91_002),
            CreateContract(typeof(IRegistrationBuildFailure), 91_003)
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];

        private static SharpLinkGeneratedContractDescriptor CreateContract(Type contractType, long contractId)
            => new(
                contractType,
                contractType.FullName!,
                contractId,
                new string('a', 64),
                [],
                static _ => throw new NotSupportedException(),
                static _ => RegistrationStub.Instance);
    }

    private sealed class RegistrationStub : IRpcStub
    {
        internal static readonly RegistrationStub Instance = new();

        public long InterfaceHash => 91_000;

        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
