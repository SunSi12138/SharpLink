using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Builder;

public sealed class BuildPlanBuilderTests
{
    private const string ConsumedBuilderMessage = "This SharpLink builder has already been consumed.";

    [Test]
    public async Task CrossTopologyConfigurationShouldFailAtTheSecondCall()
    {
        var cases = new[]
        {
            (First: ClientTopology.Fixed, Second: ClientTopology.Static),
            (First: ClientTopology.Static, Second: ClientTopology.Fixed),
            (First: ClientTopology.Fixed, Second: ClientTopology.Dynamic),
            (First: ClientTopology.Dynamic, Second: ClientTopology.Fixed),
            (First: ClientTopology.Static, Second: ClientTopology.Dynamic),
            (First: ClientTopology.Dynamic, Second: ClientTopology.Static)
        };

        foreach (var testCase in cases)
        {
            var builder = CreateClientBuilder();
            ConfigureTopology(builder, testCase.First);

            var failure = Capture(() => ConfigureTopology(builder, testCase.Second));
            Ensure(failure is InvalidOperationException &&
                   failure.Message == "UseTransport, UseEndpoint(s), and UseEndpointResolver are mutually exclusive.",
                $"{testCase.First} -> {testCase.Second} must fail immediately at the second configuration call");

            await using var client = builder.Build();
        }
    }

    [Test]
    public async Task SameTopologyReconfigurationShouldBeRejectedAndDocumentedByBehavior()
    {
        foreach (var topology in new[]
                 {
                     ClientTopology.Fixed,
                     ClientTopology.Static,
                     ClientTopology.Dynamic
                 })
        {
            var builder = CreateClientBuilder();
            ConfigureTopology(builder, topology);

            var failure = Capture(() => ConfigureTopology(builder, topology));
            Ensure(failure is InvalidOperationException &&
                   failure.Message == "A Client topology has already been configured for this builder.",
                $"same-kind {topology} configuration must be rejected instead of replacing a pending owner");

            await using var client = builder.Build();
        }
    }

    [Test]
    public void ClientBuilderShouldStayConsumedAfterCompileFailureAndReleaseItsOwnedTransport()
    {
        var transport = new TrackingClientTransport();
        var builder = CreateClientBuilder()
            .UseTransport(transport)
            .UseProtocol(static options =>
                options.MaxFramePayloadBytes = SharpLinkProtocolOptions.MinMaxFramePayloadBytes - 1);

        var failure = Capture(() => _ = builder.Build());

        Ensure(failure is ArgumentOutOfRangeException,
            "invalid protocol options must fail during Compile");
        Ensure(transport.DisposeCount == 1,
            "a configured direct transport must be released exactly once when Compile fails");
        EnsureConsumed(() => _ = builder.Build());
        EnsureConsumed(() => builder.UseRequestTimeout(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void IncompatibleManifestShouldFailDuringCompileWithoutMaterializingAClientRuntime()
    {
        var transport = new TrackingClientTransport();
        var builder = CreateClientBuilder().UseTransport(transport);

        var failure = Capture(() => _ = builder.CompileForMultiCluster([new IncompatibleManifest()]));

        Ensure(failure is InvalidOperationException && failure.Message.Contains("API", StringComparison.Ordinal),
            "generated-manifest compatibility must fail during pure Compile");
        Ensure(transport.DisposeCount == 1,
            "a Compile-only manifest failure must release the unmaterialized transport once");
        EnsureConsumed(() => _ = builder.Build());
    }

    [Test]
    public void MalformedApi4ManifestShouldFailDuringClientCompileBeforeMaterializingResources()
        => AssertSemanticManifestCompileFailure(new MalformedApi4Manifest(), "malformed API 4 manifest");

    [Test]
    public void ForeignContractOwnershipShouldFailDuringClientCompileBeforeMaterializingResources()
        => AssertSemanticManifestCompileFailure(new ForeignContractOwnershipManifest(), "foreign contract ownership");

    [Test]
    public async Task SemanticManifestValidationShouldDeferCodecAndAdapterMaterialization()
    {
        var adapter = new DeferredAdapter();
        var factory = new DeferredAdapterCodecFactory(adapter);
        var builder = CreateClientBuilder().UseTransport(new TrackingClientTransport());

        var plan = builder.CompileForMultiCluster([new DeferredAdapterManifest(factory)]);

        Ensure(adapter.ScopeCreateCount == 0 && factory.CodecCreateCount == 0,
            "full Compile validation must not create adapter scopes or Codecs");

        await using var client = builder.MaterializeCompiledPlan(plan);

        Ensure(adapter.ScopeCreateCount == 1 && factory.CodecCreateCount == 1,
            "Materialize must create the deferred adapter scope and Codec exactly once");
    }

    [Test]
    public async Task ServerBuilderShouldStayConsumedAfterSuccessAndFailure()
    {
        var successfulTransport = new TrackingServerListener();
        var successfulBuilder = CreateServerBuilder().UseTransport(successfulTransport);
        await using var server = successfulBuilder.Build();

        EnsureConsumed(() => _ = successfulBuilder.Build());
        EnsureConsumed(() => successfulBuilder.UseHeartbeat(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));

        var failedTransport = new TrackingServerListener();
        var failedBuilder = CreateServerBuilder()
            .UseTransport(failedTransport)
            .RequireAuthentication();
        var failure = Capture(() => _ = failedBuilder.Build());

        Ensure(failure is InvalidOperationException &&
               failure.Message == "RequireAuthentication needs an ISharpLinkServerAuthenticator.",
            "server Compile failure must preserve the configuration error");
        Ensure(failedTransport.DisposeCount == 1,
            "server Compile failure must release its configured listener once");
        EnsureConsumed(() => _ = failedBuilder.Build());
        EnsureConsumed(() => failedBuilder.UseTransport(new TrackingServerListener()));
    }

    [Test]
    public async Task TcpDefaultsShouldBindLoopbackAndAllowSecureBuild()
    {
        var builder = CreateServerBuilder().UseTcp(0);

        var bound = builder.Transport!.LocalEndPoint as IPEndPoint;
        Ensure(bound is not null && bound.Address.Equals(IPAddress.Loopback),
            "UseTcp(port) must bind loopback by default.");

        await using var server = builder.Build();
        Ensure(server is not null, "loopback plaintext TCP must build by default.");
    }

    [Test]
    public async Task NonLoopbackPlaintextShouldRequireExplicitOptIn()
    {
        var failure = Capture(() => CreateServerBuilder()
            .UseTcp(0)
            .ListenOnAnyAddress()
            .Build());

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("AllowUnencrypted()", StringComparison.Ordinal),
            "non-loopback plaintext TCP must require AllowUnencrypted.");
    }

    [Test]
    public async Task NonLoopbackPlaintextShouldBuildAfterExplicitOptIn()
    {
        var unencryptedBuilder = CreateServerBuilder()
            .UseTcp(0)
            .ListenOnAnyAddress()
            .AllowUnencrypted();
        await using var unencryptedServer = unencryptedBuilder.Build();

        Ensure(unencryptedServer is not null,
            "AllowUnencrypted must be accepted for non-loopback plaintext TCP.");
    }

    [Test]
    public async Task NonLoopbackTlsShouldBuildWithoutLoweringEncryption()
    {
        var tlsOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateSelectionCallback = static (_, _) => null!
        };

        var builder = CreateServerBuilder()
            .UseTcp(0)
            .ListenOnAnyAddress()
            .UseTls(tlsOptions);

        await using var server = builder.Build();
        Ensure(server is not null, "non-loopback TLS must not require plaintext opt-in.");
    }

    [Test]
    public void ClientAndServerBuildersShouldStayConsumedAfterMaterializeFailure()
    {
        var clientTransport = new ProfileFailureClientTransport();
        var clientBuilder = CreateClientBuilder().UseTransport(clientTransport);

        var clientFailure = Capture(() => _ = clientBuilder.Build());

        Ensure(clientFailure is InvalidOperationException && clientFailure.Message == "phase11 Client profile failure",
            "Client Materialize must retain its primary failure");
        Ensure(clientTransport.DisposeCount == 1,
            "Client Materialize rollback must dispose the configured transport exactly once");
        EnsureConsumed(() => _ = clientBuilder.Build());
        EnsureConsumed(() => clientBuilder.UseRequestTimeout(TimeSpan.FromSeconds(1)));

        var serverTransport = new ProfileFailureServerListener();
        var serverBuilder = CreateServerBuilder().UseTransport(serverTransport);

        var serverFailure = Capture(() => _ = serverBuilder.Build());

        Ensure(serverFailure is InvalidOperationException && serverFailure.Message == "phase11 Server profile failure",
            "Server Materialize must retain its primary failure");
        Ensure(serverTransport.DisposeCount == 1,
            "Server Materialize rollback must dispose the configured listener exactly once");
        EnsureConsumed(() => _ = serverBuilder.Build());
        EnsureConsumed(() => serverBuilder.UseHeartbeat(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
    }

    [Test]
    public async Task ClientBuildAndConfigurationRaceShouldHaveOneWinnerAndOneStableConsumedFailure()
    {
        var transport = new BlockingClientTransport();
        var builder = CreateClientBuilder()
            .UseTransport(transport)
            .UseProtocol(static options => options.MaxFramePayloadBytes = 2_048);

        var build = LongRunningTestWorker.Run(builder.Build);
        ISharpLinkClient? client = null;
        try
        {
            Ensure(transport.ProfileBindingEntered.Wait(TimeSpan.FromSeconds(2)),
                "the first Build must reach deterministic materialization coordination");

            EnsureConsumed(() => _ = builder.Build());
            EnsureConsumed(() => builder.UseProtocol(static options => options.MaxFramePayloadBytes = 4_096));

            transport.ReleaseProfileBinding();
            client = await build.WaitAsync(TimeSpan.FromSeconds(2));
            var context = (SharpLinkRuntimeContext)((IRpcChannel)client).RuntimeContext;
            Ensure(context.Protocol.MaxFramePayloadBytes == 2_048,
                "a rejected concurrent configuration must not alter the frozen Client plan");
        }
        finally
        {
            transport.ReleaseProfileBinding();
            client ??= await build.WaitAsync(TimeSpan.FromSeconds(5));
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerBuildAndConfigurationRaceShouldHaveOneWinnerAndOneStableConsumedFailure()
    {
        var listener = new BlockingServerListener();
        var builder = CreateServerBuilder()
            .UseTransport(listener)
            .UseProtocol(static options => options.MaxFramePayloadBytes = 2_048);

        var build = LongRunningTestWorker.Run(builder.Build);
        ISharpLinkServer? server = null;
        try
        {
            Ensure(listener.ProfileBindingEntered.Wait(TimeSpan.FromSeconds(2)),
                "the first Server Build must reach deterministic materialization coordination");

            EnsureConsumed(() => _ = builder.Build());
            EnsureConsumed(() => builder.UseProtocol(static options => options.MaxFramePayloadBytes = 4_096));

            listener.ReleaseProfileBinding();
            server = await build.WaitAsync(TimeSpan.FromSeconds(2));
            var context = ReadPrivate<SharpLinkRuntimeContext>(server, "_runtimeContext");
            Ensure(context.Protocol.MaxFramePayloadBytes == 2_048,
                "a rejected concurrent configuration must not alter the frozen Server plan");
        }
        finally
        {
            listener.ReleaseProfileBinding();
            server ??= await build.WaitAsync(TimeSpan.FromSeconds(5));
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task ClientCompilePlanShouldEnumerateOnceFreezeInputsAndDeferEndpointFactoryCreation()
    {
        var attributes = new Dictionary<string, string> { ["zone"] = "before" };
        var endpoints = new List<SharpLinkEndpoint>
        {
            new()
            {
                Id = "before",
                Address = new SharpLinkTcpAddress("127.0.0.1", 5201),
                Attributes = attributes
            }
        };
        var source = new CountingEndpointEnumerable(endpoints);
        var factoryCalls = 0;
        SharpLinkEndpoint? materializedEndpoint = null;
        var builder = CreateClientBuilder().UseEndpoints(source, endpoint =>
        {
            factoryCalls++;
            materializedEndpoint = endpoint;
            return new TrackingClientTransport();
        });

        var plan = builder.CompileForMultiCluster([]);

        Ensure(source.EnumerationCount == 1 && source.MoveNextCount == 2,
            "Compile must take one complete static endpoint snapshot");
        Ensure(factoryCalls == 0,
            "Compile must not create a framework-owned endpoint transport factory");

        attributes["zone"] = "after";
        endpoints[0] = Endpoint("after", 5202);
        await using var client = builder.MaterializeCompiledPlan(plan);

        Ensure(factoryCalls == 1 && materializedEndpoint is { Id: "before" } &&
               materializedEndpoint.Attributes["zone"] == "before",
            "Materialize must use the frozen endpoint and attributes from the same ClientBuildPlan");
        Ensure(source.EnumerationCount == 1,
            "Materialize must not re-enumerate the source captured by Compile");
    }

    [Test]
    public void EndpointEnumerationFailureShouldConsumeTheBuilderWithoutAcquiringAFactory()
    {
        var source = new ThrowingEndpointEnumerable();
        var factoryCalls = 0;
        var builder = CreateClientBuilder().UseEndpoints(source, _ =>
        {
            factoryCalls++;
            return new TrackingClientTransport();
        });

        var failure = Capture(() => _ = builder.Build());

        Ensure(failure is InvalidOperationException && failure.Message == "endpoint enumeration failed",
            "a mid-enumeration failure must be reported from Compile");
        Ensure(source.EnumerationCount == 1 && source.MoveNextCount == 2 && factoryCalls == 0,
            "a failed static snapshot must not restart enumeration or acquire endpoint factories");
        EnsureConsumed(() => _ = builder.Build());
        EnsureConsumed(() => builder.UseEndpoints([Endpoint("other", 5203)], static _ => new TrackingClientTransport()));
    }

    [Test]
    public async Task ManifestInputShouldBeSnapshottedBeforeMaterialize()
    {
        var manifests = new CountingManifestList([new EmptyManifest()]);
        var builder = CreateClientBuilder().UseTransport(new TrackingClientTransport());

        var plan = builder.CompileForMultiCluster(manifests);
        var accessesAfterCompile = manifests.AccessCount;
        Ensure(accessesAfterCompile == 2,
            "Compile must read the caller manifest list exactly once to create its strong snapshot");
        manifests.RejectFurtherAccess = true;

        await using var client = builder.MaterializeCompiledPlan(plan);

        Ensure(manifests.AccessCount == accessesAfterCompile,
            "Runtime materialization must use the frozen manifest source instead of caller list access");
    }

    [Test]
    public async Task ServerAdmissionOptionsShouldFreezeBeforeMaterialize()
    {
        var listener = new BlockingServerListener();
        SharpLinkConcurrencyLimitOptions? capturedLimit = null;
        var builder = CreateServerBuilder()
            .UseTransport(listener)
            .UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                capturedLimit = options.Global.Concurrency;
            });

        var build = LongRunningTestWorker.Run(builder.Build);
        SharpLinkServer? server = null;
        try
        {
            Ensure(listener.ProfileBindingEntered.Wait(TimeSpan.FromSeconds(2)),
                "the Server Build must have completed Compile before the admission mutation");
            capturedLimit!.PermitLimit = 2;
            listener.ReleaseProfileBinding();

            server = (SharpLinkServer)await build.WaitAsync(TimeSpan.FromSeconds(2));
            var controller = ReadPrivate<SharpLinkAdmissionController>(server, "_admissionController");
            var context = new SharpLinkAdmissionContext(
                contractId: 1,
                methodId: 1,
                methodKind: RpcMethodKind.Unary,
                connectionId: "phase11-admission",
                authenticationContext: null,
                metadata: null,
                deadline: null);
            var first = await controller.AcquireAsync(
                context, retainedBytes: 1, allowQueue: false, CancellationToken.None);
            var second = await controller.AcquireAsync(
                context, retainedBytes: 1, allowQueue: false, CancellationToken.None);
            try
            {
                Ensure(first.IsAcquired && !second.IsAcquired && second.Reason == "concurrency",
                    "post-Compile mutation of admission options must not alter the frozen permit limit");
            }
            finally
            {
                first.Lease?.Dispose();
                second.Lease?.Dispose();
            }
        }
        finally
        {
            listener.ReleaseProfileBinding();
            server ??= (SharpLinkServer)await build.WaitAsync(TimeSpan.FromSeconds(5));
            await server.DisposeAsync();
        }
    }

    private static void ConfigureTopology(SharpClientBuilder builder, ClientTopology topology)
    {
        switch (topology)
        {
            case ClientTopology.Fixed:
                builder.UseTransport(new TrackingClientTransport());
                return;
            case ClientTopology.Static:
                builder.UseEndpoints([Endpoint("static", 5101)], static _ => new TrackingClientTransport());
                return;
            case ClientTopology.Dynamic:
                builder.UseEndpointResolver(new TrackingResolver(), static _ => new TrackingClientTransport());
                return;
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    private static SharpClientBuilder CreateClientBuilder()
        => SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);

    private static SharpLinkServerBuilder CreateServerBuilder()
        => SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);

    private static SharpLinkEndpoint Endpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    private static T ReadPrivate<T>(object instance, string fieldName) where T : class
        => instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
           ?? throw new Exception($"cannot find {fieldName}");

    private static void AssertSemanticManifestCompileFailure(
        ISharpLinkGeneratedAssemblyManifest manifest,
        string scenario)
    {
        var adapter = new DeferredAdapter();
        var factory = new DeferredAdapterCodecFactory(adapter);
        var transport = new ProfileTrackingClientTransport();
        var builder = CreateClientBuilder().UseTransport(transport);

        var failure = Capture(() => _ = builder.CompileForMultiCluster([
            new DeferredAdapterManifest(factory),
            manifest
        ]));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains(nameof(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest), StringComparison.Ordinal),
            $"{scenario} must fail during Client Compile with an invalid-manifest error");
        Ensure(adapter.ScopeCreateCount == 0 && factory.CodecCreateCount == 0,
            $"{scenario} must fail before a preceding valid manifest materializes adapter or Codec resources");
        Ensure(transport.ProfileBindingCount == 0,
            $"{scenario} must fail before Client materialization binds the transport profile");
        Ensure(transport.DisposeCount == 1,
            $"{scenario} must release the unmaterialized direct transport exactly once");
        EnsureConsumed(() => _ = builder.Build());
    }

    private static Exception Capture(Action action)
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

    private static void EnsureConsumed(Action action)
    {
        var failure = Capture(action);
        Ensure(failure is InvalidOperationException && failure.Message == ConsumedBuilderMessage,
            "the builder must have one stable terminal consumed error");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private enum ClientTopology : byte
    {
        Fixed,
        Static,
        Dynamic
    }

    private class TrackingClientTransport : IClientTransportFactory
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingClientTransport : TrackingClientTransport, IPerformanceProfileAwareTransport
    {
        private readonly ManualResetEventSlim _release = new();

        internal ManualResetEventSlim ProfileBindingEntered { get; } = new();

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            ProfileBindingEntered.Set();
            _release.Wait();
        }

        internal void ReleaseProfileBinding() => _release.Set();
    }

    private sealed class ProfileFailureClientTransport : TrackingClientTransport, IPerformanceProfileAwareTransport
    {
        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            throw new InvalidOperationException("phase11 Client profile failure");
        }
    }

    private sealed class ProfileTrackingClientTransport : TrackingClientTransport, IPerformanceProfileAwareTransport
    {
        private int _profileBindingCount;

        internal int ProfileBindingCount => Volatile.Read(ref _profileBindingCount);

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            Interlocked.Increment(ref _profileBindingCount);
        }
    }

    private sealed class TrackingResolver : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<SharpLinkEndpointSnapshot>(new NotSupportedException());

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class TrackingServerListener : IServerTransportListener
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingServerListener : TrackingServerListener, IPerformanceProfileAwareTransport
    {
        private readonly ManualResetEventSlim _release = new();

        internal ManualResetEventSlim ProfileBindingEntered { get; } = new();

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            ProfileBindingEntered.Set();
            _release.Wait();
        }

        internal void ReleaseProfileBinding() => _release.Set();
    }

    private sealed class ProfileFailureServerListener : TrackingServerListener, IPerformanceProfileAwareTransport
    {
        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            throw new InvalidOperationException("phase11 Server profile failure");
        }
    }

    private sealed class CountingEndpointEnumerable(IReadOnlyList<SharpLinkEndpoint> endpoints)
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

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingEndpointEnumerable : IEnumerable<SharpLinkEndpoint>
    {
        private int _enumerationCount;
        private int _moveNextCount;

        internal int EnumerationCount => Volatile.Read(ref _enumerationCount);
        internal int MoveNextCount => Volatile.Read(ref _moveNextCount);

        public IEnumerator<SharpLinkEndpoint> GetEnumerator()
        {
            Interlocked.Increment(ref _enumerationCount);
            Interlocked.Increment(ref _moveNextCount);
            yield return Endpoint("first", 5301);
            Interlocked.Increment(ref _moveNextCount);
            throw new InvalidOperationException("endpoint enumeration failed");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingManifestList(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
        : IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>
    {
        private int _accessCount;

        internal int AccessCount => Volatile.Read(ref _accessCount);
        internal bool RejectFurtherAccess { get; set; }

        public int Count
        {
            get
            {
                RecordAccess();
                return manifests.Count;
            }
        }

        public ISharpLinkGeneratedAssemblyManifest this[int index]
        {
            get
            {
                RecordAccess();
                return manifests[index];
            }
        }

        public IEnumerator<ISharpLinkGeneratedAssemblyManifest> GetEnumerator()
            => throw new InvalidOperationException("the build plan must snapshot manifests by indexed access");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void RecordAccess()
        {
            if (RejectFurtherAccess)
                throw new InvalidOperationException("caller manifest list was accessed after Compile");
            Interlocked.Increment(ref _accessCount);
        }
    }

    private sealed class EmptyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public string CompileTimeDescriptor => "phase11-empty";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class IncompatibleManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api + 1;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public string CompileTimeDescriptor => "phase11-incompatible";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class MalformedApi4Manifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public string CompileTimeDescriptor => "phase11-malformed";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => null!;
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class ForeignContractOwnershipManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public string CompileTimeDescriptor => "phase11-foreign-contract";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [
            new(
                typeof(string),
                typeof(string).FullName!,
                11_001,
                new string('a', 64),
                [],
                static _ => null!,
                static _ => null!)
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class DeferredAdapterManifest(DeferredAdapterCodecFactory factory) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public string CompileTimeDescriptor => "phase11-deferred-adapter";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [factory];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class DeferredAdapterCodecFactory(DeferredAdapter adapter) : IRpcGeneratedCodecFactory
    {
        private int _codecCreateCount;

        internal int CodecCreateCount => Volatile.Read(ref _codecCreateCount);
        public Type TargetType => typeof(DeferredCodecValue);
        public string SchemaId => "phase11-deferred-adapter/v1";
        public string WireFormatId => "phase11-deferred-wire/v1";
        public string? AdapterId => "phase11-deferred-adapter/v1";
        public IRpcCodecAdapter Adapter { get; } = adapter;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        {
            Interlocked.Increment(ref _codecCreateCount);
            return (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<DeferredCodecValue>();
        }

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<DeferredCodecValue>;
    }

    private sealed class DeferredAdapter : IRpcCodecAdapter
    {
        private int _scopeCreateCount;

        internal int ScopeCreateCount => Volatile.Read(ref _scopeCreateCount);
        public string AdapterId => "phase11-deferred-adapter/v1";
        public string WireFormatId => "phase11-deferred-wire/v1";

        public IRpcCodecAdapterScope CreateScope()
        {
            Interlocked.Increment(ref _scopeCreateCount);
            return new DeferredAdapterScope();
        }
    }

    private sealed class DeferredAdapterScope : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>() => new DeferredCodec<T>();

        public void Dispose()
        {
        }
    }

    private sealed class DeferredCodecValue;

    private sealed class DeferredCodec<T> : IRpcCodec<T>
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer)
        {
        }

        public T? Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }
}
