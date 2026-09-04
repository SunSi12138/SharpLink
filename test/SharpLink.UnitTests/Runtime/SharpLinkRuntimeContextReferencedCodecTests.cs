using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public partial class SharpLinkRuntimeContextTests
{
    [Test]
    public void StaticBuildShouldRejectReferencedCodecHashMismatchBeforePublication()
    {
        var actualHash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var expectedHash = new RpcHash128(0x3333333333333333UL, 0x4444444444444444UL);
        var provider = new TestManifest(
            "referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), actualHash));
        var consumer = new ReferencedCodecManifest(
            "referenced-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), expectedHash)]);

        var failure = CaptureFailure(() =>
        {
            using var context = CreateRuntimeBuilder().Build(
                new ISharpLinkGeneratedAssemblyManifest[] { provider, consumer });
        });

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("expected CodecHash", StringComparison.Ordinal),
            "static bootstrap must reject a referenced Codec hash mismatch before publication");
    }

    [Test]
    public void DynamicPrepareShouldRejectReferencedCodecHashMismatch()
    {
        var actualHash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var expectedHash = new RpcHash128(0x3333333333333333UL, 0x4444444444444444UL);
        var provider = new TestManifest(
            "referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), actualHash));
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { provider });
        var consumer = new ReferencedCodecManifest(
            "referenced-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), expectedHash)]);

        var failure = CaptureFailure(() => context.PrepareGeneratedManifest(consumer));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("expected CodecHash", StringComparison.Ordinal),
            "dynamic manifest preparation must reject a referenced Codec hash mismatch");
    }

    [Test]
    public void CandidatePublicationShouldRejectRemovingReferencedCodecDependency()
    {
        var expectedHash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var provider = new TestManifest(
            "referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), expectedHash));
        var consumer = new ReferencedCodecManifest(
            "referenced-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), expectedHash)]);
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { provider, consumer });

        var failure = CaptureFailure(() => context.PublishGeneratedCodecs(
            new Dictionary<Type, RpcGeneratedCodecRegistration>()));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("no generated Codec is registered for that exact Type", StringComparison.Ordinal),
            "candidate publication must preserve reverse referenced Codec dependants");
    }

    [Test]
    public void PendingManifestShouldBeValidatedAgainstFinalCandidateSnapshot()
    {
        var expectedHash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var provider = new TestManifest(
            "referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), expectedHash));
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { provider });
        var pending = context.PrepareGeneratedManifest(new ReferencedCodecManifest(
            "pending-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), expectedHash)]));
        try
        {
            var failure = CaptureFailure(() => context.PublishGeneratedCodecs(
                new Dictionary<Type, RpcGeneratedCodecRegistration>(), pending));

            Ensure(failure is InvalidOperationException &&
                   failure.Message.Contains("no generated Codec is registered for that exact Type", StringComparison.Ordinal),
                "an incoming not-yet-adopted manifest must be checked against the final candidate snapshot");
        }
        finally
        {
            pending.Dispose();
        }
    }

    [Test]
    public void DisposedContextShouldRejectCodecResolution()
    {
        var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        context.Dispose();
        context.Dispose();
        try
        {
            _ = context.Codecs.GetCodec<TaggedValue>();
            throw new Exception("expected disposed Context to reject Codec resolution");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class ReferencedCodecManifest(
        string descriptor,
        SharpLinkReferencedCodecDependency[] referencedCodecDependencies)
        : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ReferencedCodecManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => TestAssemblyHash;
        public string CompileTimeDescriptor => descriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
        public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies { get; } =
            referencedCodecDependencies;
    }
}
