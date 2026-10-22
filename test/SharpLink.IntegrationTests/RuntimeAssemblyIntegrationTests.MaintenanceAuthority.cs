using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceRevalidatesAuthorityAfterCandidateCallbacks(bool disconnect)
    {
        Action? invalidate = null;
        await using var harness = await MaintenanceHarness.CreateAsync(expiringAuthentication: true,
            duringStubPreparation: () => invalidate?.Invoke());
        await harness.VerifyIdentityAndShapesAsync();
        var source = GetDynamicModule(harness.Server, harness.SourceAssembly);
        var entered = 0;
        invalidate = () =>
        {
            if (Interlocked.Increment(ref entered) != 1) return;
            if (disconnect)
                ((SharpLinkServer)harness.Server).CaptureMaintenanceCohort()[0].Session.NotifyDisconnected(
                    new IOException("authority lost inside candidate preparation"));
            else
                harness.Clock.Advance(TimeSpan.FromHours(2));
        };
        var result = await harness.Begin().WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(entered > 0 && result.Stage == SharpLinkMaintenanceStage.FailedClosed,
            $"candidate callback authority loss must fail closed: {result.Failure}");
        Ensure(source.State == SharpLinkDynamicModuleState.Running,
            "final authority failure must precede irreversible old gate retirement");
        Ensure(harness.Plugin.GetStaticInt("Disposed") == 0,
            "authority loss during preparation must not dispose the old singleton implementation");
        invalidate = null;
    }

    private sealed class MaintenancePreparationManifest : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
    {
        private readonly ISharpLinkGeneratedAssemblyManifest _inner;
        internal MaintenancePreparationManifest(ISharpLinkGeneratedAssemblyManifest inner, Action callback)
        {
            _inner = inner;
            Contracts = inner.Contracts.Select(contract => contract with
            {
                StubFactory = codecs => { callback(); return contract.StubFactory(codecs); }
            }).ToArray();
        }
        public int ApiVersion => _inner.ApiVersion;
        public int ProtocolVersion => _inner.ProtocolVersion;
        public string GeneratorVersion => _inner.GeneratorVersion;
        public Assembly OwnerAssembly => _inner.OwnerAssembly;
        public RpcHash128 RpcAssemblyHash => _inner.RpcAssemblyHash;
        public string CompileTimeDescriptor => _inner.CompileTimeDescriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => _inner.Services;
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => _inner.Codecs;
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => _inner.ContractCodecs;
        public IReadOnlyList<string> Dependencies => _inner.Dependencies;
        public IReadOnlyList<string> ContractDependencies => _inner.ContractDependencies;
        public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies
            => (_inner as ISharpLinkReferencedCodecDependencyManifest)?.ReferencedCodecDependencies ?? [];
    }
}
