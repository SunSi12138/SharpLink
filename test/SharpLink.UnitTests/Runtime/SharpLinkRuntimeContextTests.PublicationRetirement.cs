using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public partial class SharpLinkRuntimeContextTests
{
    [Test]
    public void CodecPublicationMustRejectAnOwnerAdoptedDuringValidation()
    {
        var hash = new RpcHash128(1, 2);
        var trigger = new ReferencedCodecManifest("adoption-trigger", []);
        using var context = CreateRuntimeBuilder().Build([
            new TestManifest("provider", new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), hash)), trigger]);
        var before = context.CreateGeneratedCodecSnapshot();
        var pending = context.PrepareGeneratedManifest(new ReferencedCodecManifest("new-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), hash)]));
        trigger.BeforeDependenciesRead = () => context.AdoptGeneratedManifest(pending);
        var failure = CaptureFailure(() => context.PublishGeneratedCodecs(new Dictionary<Type, RpcGeneratedCodecRegistration>()));
        Ensure(failure is InvalidOperationException && ReferenceEquals(before, context.CreateGeneratedCodecSnapshot()),
            "a newly adopted owner invalidates a candidate prepared without its dependency");
        context.ReleaseGeneratedManifest(pending);
    }

    [Test]
    public void CodecPublicationMustAllowCompletedUnrelatedManifestRetirement()
    {
        var trigger = new ReferencedCodecManifest("retirement-trigger", []);
        using var context = CreateRuntimeBuilder().Build([trigger]);
        var retired = context.PrepareGeneratedManifest(new ReferencedCodecManifest("unrelated-owner", []));
        context.AdoptGeneratedManifest(retired);
        var before = context.CreateGeneratedCodecSnapshot();
        Task? retirement = null;
        trigger.BeforeDependenciesRead = () =>
        {
            retirement = Task.Factory.StartNew(() => context.ReleaseGeneratedManifest(retired),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Ensure(retirement.Wait(RaceCoordinationTimeout), "metadata must not hold the gate needed by independent retirement");
            retirement.GetAwaiter().GetResult();
        };
        try
        {
            context.PublishGeneratedCodecs(before);
            Ensure(ReferenceEquals(before, context.CreateGeneratedCodecSnapshot()), "unrelated retirement preserves the exact candidate mapping");
        }
        finally
        {
            trigger.BeforeDependenciesRead = null;
            if (retirement is not null)
            {
                Ensure(retirement.Wait(RaceCoordinationTimeout), "retirement worker must not escape the fixture");
                retirement.GetAwaiter().GetResult();
            }
        }
    }

    [Test]
    public void CodecPublicationMustRejectProviderRetiredDuringValidation()
    {
        var hash = new RpcHash128(3, 4);
        var trigger = new ReferencedCodecManifest("consumer", [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), hash)]);
        using var context = CreateRuntimeBuilder().Build([
            new TestManifest("provider", new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), hash)), trigger]);
        var before = context.CreateGeneratedCodecSnapshot();
        trigger.BeforeDependenciesRead = () => context.ReleaseGeneratedManifest(before[typeof(ThirdAdapterValue)].Owner);
        var failure = CaptureFailure(() => context.PublishGeneratedCodecs(before.ToDictionary(static p => p.Key, static p => p.Value)));
        Ensure(failure is ObjectDisposedException && ReferenceEquals(before, context.CreateGeneratedCodecSnapshot()),
            "retired owners cannot be republished even when their Type and hash still match");
    }

    [Test]
    public void CodecPublicationMustKeepSurvivingExactProviderDuringOtherOwnerRetirement()
    {
        var hash = new RpcHash128(5, 6);
        var first = new TestManifest("first", new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), hash));
        var survivor = new TestManifest("survivor", new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(2), hash));
        var trigger = new ReferencedCodecManifest("consumer", [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), hash)]);
        using var context = CreateRuntimeBuilder().Build([first, survivor, trigger]);
        var retired = context.FindGeneratedCodec(first, typeof(ThirdAdapterValue))!.Owner;
        var candidate = context.CreateGeneratedCodecSnapshot().ToDictionary(static p => p.Key, static p => p.Value);
        trigger.BeforeDependenciesRead = () => context.ReleaseGeneratedManifest(retired);
        context.PublishGeneratedCodecs(candidate);
        Ensure(context.Codecs.GetCodec<ThirdAdapterValue>() is TaggedThirdAdapterValueCodec { Tag: 2 },
            "retiring another same-assembly owner cannot reject the surviving exact registration");
    }

    [Test]
    public void CodecPublicationMustRejectCompetingPublicationOfTheSameDictionary()
    {
        var trigger = new ReferencedCodecManifest("publication-trigger", []);
        using var context = CreateRuntimeBuilder().Build([trigger]);
        var before = context.CreateGeneratedCodecSnapshot();
        trigger.BeforeDependenciesRead = () => context.PublishGeneratedCodecs(before);
        var failure = CaptureFailure(() => context.PublishGeneratedCodecs(before.ToDictionary(static p => p.Key, static p => p.Value)));
        Ensure(failure is InvalidOperationException && ReferenceEquals(before, context.CreateGeneratedCodecSnapshot()),
            "a competing publication changes the transaction even when the dictionary object is unchanged");
    }
}
