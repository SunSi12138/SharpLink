using System.Linq;
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
    public void CodecPublicationShouldRejectAnOwnerAdoptedDuringValidation()
    {
        var expectedHash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var provider = new TestManifest("referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), expectedHash));
        var validationTrigger = new ReferencedCodecManifest("validation-trigger", []);
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { provider, validationTrigger });
        using var consumer = context.PrepareGeneratedManifest(new ReferencedCodecManifest(
            "concurrently-adopted-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), expectedHash)]));
        var before = context.CreateGeneratedCodecSnapshot();
        var revision = context.RegistrationRevision;
        validationTrigger.BeforeDependenciesRead = () =>
        {
            validationTrigger.BeforeDependenciesRead = null;
            context.AdoptGeneratedManifest(consumer);
        };
        try
        {
            var failure = CaptureFailure(() => context.PublishGeneratedCodecs(
                new Dictionary<Type, RpcGeneratedCodecRegistration>()));
            Ensure(failure is InvalidOperationException &&
                   failure.Message.Contains("catalog changed", StringComparison.Ordinal),
                "an owner adopted after the snapshot must invalidate the validated publication");
            Ensure(context.RegistrationRevision == revision + 1,
                "only the reentrant adoption may change the revision");
            Ensure(ReferenceEquals(before, context.CreateGeneratedCodecSnapshot()),
                "the rejected map must not remove a newly adopted owner's referenced Codec");
        }
        finally
        {
            validationTrigger.BeforeDependenciesRead = null;
            context.ReleaseGeneratedManifest(consumer);
        }
    }

    [Test]
    public void CodecPublicationMustAllowCompletedUnrelatedManifestRetirement()
    {
        var trigger = new ReferencedCodecManifest("publication-retirement-trigger", []);
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { trigger });
        var retired = context.PrepareGeneratedManifest(new ReferencedCodecManifest("retiring-owner", []));
        context.AdoptGeneratedManifest(retired);
        var before = context.CreateGeneratedCodecSnapshot();
        var revision = context.RegistrationRevision;
        using var started = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Exception? retirementFailure = null;
        var retirement = new Thread(() =>
        {
            started.Set();
            try { context.ReleaseGeneratedManifest(retired); }
            catch (Exception exception) { retirementFailure = exception; }
            finally { finished.Set(); }
        }) { IsBackground = true };
        trigger.BeforeDependenciesRead = () =>
        {
            trigger.BeforeDependenciesRead = null;
            retirement.Start();
            Ensure(started.Wait(RaceCoordinationTimeout) && finished.Wait(RaceCoordinationTimeout),
                "metadata validation must not hold a gate needed by independently finishing retirement");
        };
        Exception? publicationFailure = null;
        try
        {
            try { context.PublishGeneratedCodecs(before); }
            catch (Exception exception) { publicationFailure = exception; }
            Ensure(retirement.Join(RaceCoordinationTimeout), "physical retirement must finish after publication");
            Ensure(publicationFailure is null,
                $"concurrent physical retirement must not invalidate an otherwise legal publication: {publicationFailure}");
            Ensure(retirementFailure is null && finished.IsSet,
                $"the independently owned retired manifest must finish cleanup: {retirementFailure}");
            Ensure(context.RegistrationRevision == revision + 2,
                "publication and retirement each retain their original revision transition");
            Ensure(ReferenceEquals(before, context.CreateGeneratedCodecSnapshot()),
                "retirement tolerance must preserve the exact validated codec mapping");
        }
        finally
        {
            trigger.BeforeDependenciesRead = null;
            if ((retirement.ThreadState & ThreadState.Unstarted) == 0)
                Ensure(retirement.Join(RaceCoordinationTimeout), "retirement worker must not escape the fixture");
            else
                context.ReleaseGeneratedManifest(retired);
        }
    }

    [Test]
    public void CodecPublicationMustRejectRetiringAReferencedCodecOwnerDuringValidation()
    {
        var hash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var provider = new TestManifest("retiring-referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), hash));
        var consumer = new ReferencedCodecManifest("remaining-dependent",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), hash)]);
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { provider, consumer });
        var before = context.CreateGeneratedCodecSnapshot();
        var owner = before[typeof(ThirdAdapterValue)].Owner;
        var revision = context.RegistrationRevision;
        consumer.BeforeDependenciesRead = () =>
        {
            consumer.BeforeDependenciesRead = null;
            context.ReleaseGeneratedManifest(owner);
        };
        var failure = CaptureFailure(() => context.PublishGeneratedCodecs(before.ToDictionary(static pair => pair.Key, static pair => pair.Value)));
        Ensure(failure is InvalidOperationException && failure.Message.Contains("catalog changed", StringComparison.Ordinal),
            "a subset of owners cannot authorize publishing a dependent codec map that revives a removed provider");
        Ensure(context.RegistrationRevision == revision + 1 &&
               ReferenceEquals(before, context.CreateGeneratedCodecSnapshot()),
            "only retirement may advance the revision; the invalid candidate must remain unpublished");
    }

    [Test]
    public void CodecPublicationMustAllowRetirementWhenAnotherSameAssemblyOwnerSuppliesDependency()
    {
        var hash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var retiredProvider = new TestManifest("retired-same-assembly-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), hash));
        var remainingProvider = new TestManifest("remaining-same-assembly-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(2), hash));
        var consumer = new ReferencedCodecManifest("remaining-dependent",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), hash)]);
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { retiredProvider, remainingProvider, consumer });
        var retiredOwner = context.FindGeneratedCodec(retiredProvider, typeof(ThirdAdapterValue))!.Owner;
        var before = context.CreateGeneratedCodecSnapshot();
        var survivingCodec = before[typeof(ThirdAdapterValue)];
        Ensure(ReferenceEquals(survivingCodec.Owner.Manifest, remainingProvider),
            "the exact candidate dependency must be supplied by the surviving registration");
        var revision = context.RegistrationRevision;
        consumer.BeforeDependenciesRead = () =>
        {
            consumer.BeforeDependenciesRead = null;
            context.ReleaseGeneratedManifest(retiredOwner);
        };
        var candidate = before.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        context.PublishGeneratedCodecs(candidate);
        Ensure(ReferenceEquals(candidate, context.CreateGeneratedCodecSnapshot()) &&
               ReferenceEquals(context.CreateGeneratedCodecSnapshot()[typeof(ThirdAdapterValue)], survivingCodec),
            "retiring another owner of the same assembly must preserve the validated Type/hash dependency");
        Ensure(context.RegistrationRevision == revision + 2,
            "successful retirement and publication must each advance their original revision");
    }

    [Test]
    public void CodecPublicationMustRejectACompetingPublicationDuringValidation()
    {
        var trigger = new ReferencedCodecManifest("competing-publication-trigger", []);
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { trigger });
        var before = context.CreateGeneratedCodecSnapshot();
        var revision = context.RegistrationRevision;
        trigger.BeforeDependenciesRead = () =>
        {
            trigger.BeforeDependenciesRead = null;
            context.PublishGeneratedCodecs(before);
        };
        var failure = CaptureFailure(() => context.PublishGeneratedCodecs(before.ToDictionary(static pair => pair.Key, static pair => pair.Value)));
        Ensure(failure is InvalidOperationException && failure.Message.Contains("catalog changed", StringComparison.Ordinal),
            "another publication remains a conflict even when its codec dictionary identity is unchanged");
        Ensure(context.RegistrationRevision == revision + 1 &&
               ReferenceEquals(before, context.CreateGeneratedCodecSnapshot()),
            "only the nested publication may commit; the stale outer map must not replace it");
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
        internal Action? BeforeDependenciesRead { get; set; }
        public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies
        {
            get
            {
                BeforeDependenciesRead?.Invoke();
                return referencedCodecDependencies;
            }
        }
    }
}
