using System.Reflection;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

// Source draft for the actual built-in factory path. These tests do not substitute model objects
// or reflection snapshots for Client construction, coordinator replacement, or demand rollback.
public sealed class ContractFactoryIdentityTests
{
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ColdFactoriesShouldShareExactSiteWithoutConnectingOrChangingLocalIdentity()
    {
        var manifest = new FactoryManifest();
        var transport = new TestClientTransportFactory();
        await using var client = BuildClient(manifest, transport);
        await using var foreign = BuildClient(new FactoryManifest(), new TestClientTransportFactory());
        var local = client.GetLocalContract<IFactoryContract>();
        var manual = client.GetLocalContract(typeof(IFactoryContract).Assembly, FactoryManifest.ContractId);
        var first = client.CreateContractChannel(local);
        var second = client.CreateContractChannel(manual);
        var proxy = client.CreateProxy<IFactoryContract>();

        Ensure(ReferenceEquals(local, manual), "generic/manual selectors must capture the same registration");
        Ensure(ReferenceEquals(first, second) && ReferenceEquals(first, ((FactoryProxy)proxy.Service).Channel),
            "same scalar demand key must reuse the same direct invocation site");
        Ensure(local.RegistrationGeneration > 0 && local.IssuerId != Guid.Empty,
            "static registration identity must be initialized before construction escapes");
        Ensure(transport.ConnectCount == 0, "cold contract factories must not initiate connectivity");
        var failure = Capture(() => foreign.CreateContractChannel(local));
        Ensure(failure is SharpLinkException
            { Code: SharpLinkErrorCode.FailedPrecondition, DetailCode: SharpLinkErrorDetails.FailedPrecondition.ForeignLocalContract },
            "a local selector must never be rebound through another client");
    }

    [Test]
    public async Task InvalidFactoryInputsAndPrepareBudgetShouldPrecedeTerminalState()
    {
        await using var client = BuildClient(new FactoryManifest(), new TestClientTransportFactory());
        var local = client.GetLocalContract<IFactoryContract>();
        var channel = (SharpLinkClient.ContractCallSite)client.CreateContractChannel(local);
        await client.StopAsync();
        Ensure(channel.LocalControl.IsRetired, "stop must retire the exact original local generation");
        Ensure(Capture(() => client.CreateContractChannel(null!)) is ArgumentNullException,
            "null selector validation precedes terminal client state");
        Ensure(Capture(() => client.CreateContractChannel(local,
                new RpcProxyOptions { Requirement = (RpcBindingRequirement)255 })) is ArgumentOutOfRangeException,
            "invalid requirement validation precedes terminal client state");
        Ensure(Capture(() => client.CreateContractChannel(local,
                new RpcProxyOptions { Requirement = RpcBindingRequirement.ExactRegistration })) is ArgumentException,
            "an exact requirement must contain a complete receipt");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var invalidTimeout = await CaptureAsync(() => channel.PrepareAsync(TimeSpan.Zero, cancelled.Token));
        Ensure(invalidTimeout is ArgumentOutOfRangeException,
            "finite timeout validation precedes cancellation and retirement");
        var cancellation = await CaptureAsync(() => channel.PrepareAsync(TimeSpan.FromSeconds(1), cancelled.Token));
        Ensure(cancellation is OperationCanceledException exception && exception.CancellationToken == cancelled.Token,
            "pre-cancelled Prepare preserves the caller token before checking terminal state");
    }

    [Test]
    public async Task RejectedPublicationShouldReleaseOnlyNewDemandAndPreserveSharedDemand()
    {
        var manifest = new FactoryManifest();
        await using var client = BuildClient(manifest, new TestClientTransportFactory());
        var transaction = (ISharpLinkContractFactoryTransaction)client;
        SharpLinkClient.ContractCallSite? rejected = null;
        manifest.BeforeProxyCreation = channel => rejected = (SharpLinkClient.ContractCallSite)channel;
        var failure = Capture(() => transaction.CreateProxyTransactional<IFactoryContract>(null,
            static () => throw new FactoryPublicationRejected()));
        Ensure(failure is FactoryPublicationRejected && rejected is { IsDemandRetiredForDiagnostics: true },
            "a failed unpublished factory must release its newly reserved demand");

        manifest.BeforeProxyCreation = null;
        var successful = client.CreateProxy<IFactoryContract>();
        var shared = (SharpLinkClient.ContractCallSite)((FactoryProxy)successful.Service).Channel;
        Ensure(!ReferenceEquals(shared, rejected), "a rejected site's cache slot must be rolled back");
        failure = Capture(() => transaction.CreateProxyTransactional<IFactoryContract>(null,
            static () => throw new FactoryPublicationRejected()));
        Ensure(failure is FactoryPublicationRejected && !shared.IsDemandRetiredForDiagnostics,
            "a failing factory must not revoke an already returned proxy's shared demand");
        var later = client.CreateProxy<IFactoryContract>();
        Ensure(ReferenceEquals(shared, ((FactoryProxy)later.Service).Channel),
            "unrelated publication failure must preserve the existing stable cache identity");
    }

    [Test]
    public async Task MetadataMustRemainPerHandleWhileReceiptAndScopeRemainInTheSiteKey()
    {
        await using var client = BuildClient(new FactoryManifest(), new TestClientTransportFactory());
        var local = client.GetLocalContract<IFactoryContract>();
        var plain = client.CreateContractChannel(local);
        var metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("tenant", "original"));
        var tagged = client.CreateContractChannel(local, new RpcProxyOptions { Metadata = metadata });
        Ensure(!ReferenceEquals(plain, tagged), "metadata uses an immutable per-handle wrapper");
        Ensure(ReferenceEquals(plain, client.CreateContractChannel(local)),
            "metadata must not change or duplicate the shared route key");
        var receipt = new RpcRegistrationReceipt(Guid.NewGuid(), Guid.NewGuid(), 1, 1,
            local.ContractId, local.RpcAssemblyHash);
        var exact = client.CreateContractChannel(local, new RpcProxyOptions
        {
            Requirement = RpcBindingRequirement.ExactRegistration,
            Receipt = receipt
        });
        Ensure(!ReferenceEquals(plain, exact), "receipt requirements must never share a compatible-only site");
    }

    [Test]
    public async Task CapturedMultiClusterFactoryMustFailAndRollbackWhenItsSlotIsReplacedBeforeReturn()
    {
        var manifest = new FactoryManifest();
        await using var coordinator = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .UseGeneratedDiscoverySources(new FixedGeneratedManifestSource([manifest]),
                new FixedGeneratedClusterRouteSource([new FactoryRouteManifest()]))
            .AddCluster("factory", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();
        var original = coordinator.GetLocalContract("factory", typeof(IFactoryContract).Assembly,
            FactoryManifest.ContractId);
        var entered = new TaskCompletionSource<SharpLinkClient.ContractCallSite>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        manifest.BeforeProxyCreation = channel =>
        {
            entered.TrySetResult((SharpLinkClient.ContractCallSite)channel);
            if (!release.Wait(BarrierTimeout))
                throw new TimeoutException("Factory publication barrier was not released.");
        };
        var creation = Task.Run(() => Capture(() => coordinator.CreateProxy<IFactoryContract>()));
        SharpLinkClient.ContractCallSite captured;
        try
        {
            captured = await entered.Task.WaitAsync(BarrierTimeout);
            var replacement = await coordinator.ReplaceClusterAsync("factory",
                child => child.UseTransport(new TestClientTransportFactory()), BarrierTimeout);
            Ensure(replacement.Succeeded, "the actual Created-state slot replacement must publish");
        }
        finally
        {
            release.Set();
        }
        var failure = await creation.WaitAsync(BarrierTimeout);
        Ensure(failure is InvalidOperationException or SharpLinkException,
            "a factory whose exact captured slot retired must not return a replacement-site handle");
        Ensure(captured.LocalControl.IsRetired && captured.IsDemandRetiredForDiagnostics,
            "the failed old-slot factory must leave no active original demand");
        manifest.BeforeProxyCreation = null;
        var current = coordinator.CreateProxy<IFactoryContract>();
        var currentSite = (SharpLinkClient.ContractCallSite)((FactoryProxy)current.Service).Channel;
        Ensure(!ReferenceEquals(captured, currentSite) && currentSite.Options.TargetScope == new SharpLinkClusterKey("factory"),
            "a new explicit factory must capture the new slot and freeze its explicit scope");
        failure = Capture(() => coordinator.CreateContractChannel("factory", original));
        Ensure(failure is SharpLinkException
            { DetailCode: SharpLinkErrorDetails.FailedPrecondition.ForeignLocalContract },
            "an old-slot selector must not upgrade to the replacement child");
    }

    private static SharpLinkClient BuildClient(FactoryManifest manifest, TestClientTransportFactory transport)
        => (SharpLinkClient)SharpClientBuilder.Create().DisableRequestTimeout()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource([manifest]))
            .UseTransport(transport).Build();

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private static async Task<Exception?> CaptureAsync(Func<ValueTask> action)
    {
        try { await action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FactoryPublicationRejected : Exception;
    private interface IFactoryContract : IService;
    private sealed class FactoryProxy(IRpcContractChannel channel) : IFactoryContract
    {
        internal IRpcContractChannel Channel { get; } = channel;
    }

    private sealed class FactoryManifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal const long ContractId = 8_775_006;
        internal Action<IRpcContractChannel>? BeforeProxyCreation { get; set; }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "contract-factory-source-tests";
        public Assembly OwnerAssembly => typeof(ContractFactoryIdentityTests).Assembly;
        public RpcHash128 RpcAssemblyHash => new(0x775006UL, 0xC0E0UL);
        public string CompileTimeDescriptor => "contract-factory-identity-tests";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts =>
        [
            new(typeof(IFactoryContract), typeof(IFactoryContract).FullName!, ContractId,
                "0101010101010101010101010101010101010101010101010101010101010101", [],
                (channel, _) =>
                {
                    BeforeProxyCreation?.Invoke(channel);
                    return new FactoryProxy(channel);
                },
                static _ => throw new NotSupportedException())
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class FactoryRouteManifest : ISharpLinkGeneratedClusterRouteManifest
    {
        public Assembly OwnerAssembly => typeof(ContractFactoryIdentityTests).Assembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes =>
        [
            new("factory", typeof(ContractFactoryIdentityTests).Assembly,
                typeof(ContractFactoryIdentityTests).Assembly.FullName!)
        ];
    }
}
