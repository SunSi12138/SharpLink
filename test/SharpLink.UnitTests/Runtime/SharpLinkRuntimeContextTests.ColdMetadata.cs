namespace SharpLink.UnitTests.Runtime;

public partial class SharpLinkRuntimeContextTests
{
    [Test]
    public void PreparedManifestShouldFreezeGettersBeforeCodecPublication()
    {
        using var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var preparation = context.Domain.BeginPreparation();
        var source = new PublicationGuardManifest();
        var manifest = PreparedGeneratedManifest.Capture(source, preparation);
        source.RejectCallbacks = true;
        using var registration = context.PrepareGeneratedManifest(manifest);
        using var publication = context.PrepareGeneratedCodecPublication(registration.Codecs, registration, true);
        context.CommitGeneratedCodecPublication(publication);
        Ensure(context.Codecs.GetCodec<ThirdAdapterValue>() is TaggedThirdAdapterValueCodec,
            "prepared capture, Codec preparation and publication do not replay manifest getters");
        Ensure(ReferenceEquals(((PreparedCodecFactory)manifest.Codecs[0]).Original,
            registration.Codecs[typeof(ThirdAdapterValue)].FactoryIdentity),
            "cached factory metadata preserves the exact original dispatch identity");
    }

    [Test]
    public void RuntimePlanShouldTransferSnapshotOnceAndClearPlanRoots()
    {
        var domain = new ColdMetadataAuthority().CreateDomain();
        using var plan = CreateRuntimeBuilder().Compile(FixedGeneratedManifestSource.Empty, domain);
        var snapshot = plan.GeneratedManifests;
        using var context = plan.Materialize();
        Ensure(ReferenceEquals(snapshot, context.GeneratedManifests), "the exact charged snapshot transfers");
        Ensure(CaptureFailure(() => plan.Materialize()) is InvalidOperationException,
            "a transferred plan cannot invoke callbacks again");
        Ensure(CaptureFailure(() => { _ = plan.GeneratedManifests; }) is InvalidOperationException,
            "a transferred plan no longer exposes or owns snapshot roots");
        plan.Dispose();
        Ensure(domain.Authority.Bytes > 0, "disposing the transferred plan does not steal Runtime ownership");
        context.Dispose();
        Ensure(domain.Authority.Bytes == 0, "Runtime physical cleanup returns its complete metadata budget");
    }
}
