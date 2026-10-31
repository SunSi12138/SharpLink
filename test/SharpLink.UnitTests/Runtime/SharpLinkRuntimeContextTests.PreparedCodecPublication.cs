using System.Collections.Generic;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public partial class SharpLinkRuntimeContextTests
{
    [Test]
    public void PreparedCodecPublicationShouldCopyInputAndCommitWithoutManifestCallbacks()
    {
        using var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new PublicationGuardManifest();
        using var registration = context.PrepareGeneratedManifest(manifest);
        var input = new Dictionary<Type, RpcGeneratedCodecRegistration>(registration.Codecs);
        var provider = RpcGeneratedCodecResolver.GetProvider(registration);
        using var publication = context.PrepareGeneratedCodecPublication(input, registration, true);
        input.Clear();
        manifest.RejectCallbacks = true;

        context.CommitGeneratedCodecPublication(publication);

        Ensure(context.CreateGeneratedCodecSnapshot().ContainsKey(typeof(ThirdAdapterValue)),
            "commit uses the privately frozen map, not the changed input dictionary");
        Ensure(ReferenceEquals(provider, context.GetManifestCodecProvider(typeof(PublicationGuardManifest).Assembly)),
            "adoption uses the provider and owner assembly captured before publication");
        Ensure(CaptureFailure(() => context.CommitGeneratedCodecPublication(publication)) is InvalidOperationException,
            "a successful publication token cannot replay");
    }

    [Test]
    public void PreparedCodecPublicationShouldRejectStaleVersionWithoutAdopting()
    {
        using var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new PublicationGuardManifest());
        using var publication = context.PrepareGeneratedCodecPublication(registration.Codecs, registration, true);
        context.PublishGeneratedCodecs(new Dictionary<Type, RpcGeneratedCodecRegistration>());

        Ensure(CaptureFailure(() => context.CommitGeneratedCodecPublication(publication)) is InvalidOperationException,
            "even an unrelated version change is rejected by this bounded foundation");
        Ensure(context.CreateGeneratedCodecSnapshot().Count == 0,
            "stale publication cannot overwrite the current map");
        Ensure(context.FindGeneratedCodec(registration.Manifest, typeof(ThirdAdapterValue)) is null,
            "stale publication cannot adopt its pending registration");
        Ensure(CaptureFailure(() => context.CommitGeneratedCodecPublication(publication)) is InvalidOperationException,
            "failed publication cannot replay callbacks or commit");
    }

    [Test]
    public void PreparedCodecPublicationShouldRejectForeignContextAndDisposedPendingOwner()
    {
        using var owner = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var foreign = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = owner.PrepareGeneratedManifest(new PublicationGuardManifest());
        using var foreignPublication = owner.PrepareGeneratedCodecPublication(registration.Codecs, registration, true);
        Ensure(CaptureFailure(() => foreign.CommitGeneratedCodecPublication(foreignPublication)) is InvalidOperationException,
            "publication belongs to its exact context");
        using var disposedPublication = owner.PrepareGeneratedCodecPublication(registration.Codecs, registration, true);
        registration.Dispose();
        Ensure(CaptureFailure(() => owner.CommitGeneratedCodecPublication(disposedPublication)) is ObjectDisposedException,
            "disposed candidate cannot publish or adopt");
        Ensure(owner.CreateGeneratedCodecSnapshot().Count == 0 && foreign.CreateGeneratedCodecSnapshot().Count == 0,
            "both rejected commits leave their maps unchanged");
    }

    [Test]
    public void CodecPublicationPreparationCallbackFailureShouldLeaveRegistryUnchanged()
    {
        using var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new PublicationGuardManifest();
        using var registration = context.PrepareGeneratedManifest(manifest);
        manifest.RejectCallbacks = true;
        Ensure(CaptureFailure(() => context.PrepareGeneratedCodecPublication(registration.Codecs, registration, true))
                is InvalidOperationException,
            "dependency callback failure is observed during private preparation");
        Ensure(context.CreateGeneratedCodecSnapshot().Count == 0 &&
               context.FindGeneratedCodec(manifest, typeof(ThirdAdapterValue)) is null,
            "failed preparation changes neither codec publication nor adoption");
        registration.ThrowIfDisposed();
    }

    private sealed class PublicationGuardManifest
        : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
    {
        internal bool RejectCallbacks;
        private void Check()
        {
            if (RejectCallbacks)
                throw new InvalidOperationException("Manifest callback is forbidden after preparation.");
        }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly { get { Check(); return typeof(PublicationGuardManifest).Assembly; } }
        public RpcHash128 RpcAssemblyHash => TestAssemblyHash;
        public string CompileTimeDescriptor => "prepared-publication";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } =
            [new FixedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1))];
        public IReadOnlyList<string> Dependencies => [];
        public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies
        {
            get { Check(); return []; }
        }
    }
}
