using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public partial class SharpLinkRuntimeContextTests
{

    [Test]
    public async Task TenThousandCodecPublicationRacesShouldPreserveRegistrationIdentity()
    {
        var oldCounters = new AdapterCounters();
        var newCounters = new AdapterCounters();
        var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var oldRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "old-generation",
            new ConfigurableAdapterFactory<AdapterValue>(
                new CountingAdapter(oldCounters),
                CountingAdapter.Id,
                CountingAdapter.Wire,
                new TaggedAdapterValueCodec(1))));
        var newRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "new-generation",
            new ConfigurableAdapterFactory<AdapterValue>(
                new CountingAdapter(newCounters),
                CountingAdapter.Id,
                CountingAdapter.Wire,
                new TaggedAdapterValueCodec(2))));
        context.AdoptGeneratedManifest(oldRegistration);
        context.AdoptGeneratedManifest(newRegistration);
        context.PublishGeneratedCodecs(oldRegistration.Codecs);

        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var next = iteration % 2 == 0 ? newRegistration : oldRegistration;
            var expectedTag = iteration % 2 == 0 ? 2 : 1;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racedLookup = Task.Run(() =>
            {
                ready.SetResult();
                return context.Codecs.GetCodec<AdapterValue>();
            });
            await ready.Task;
            context.PublishGeneratedCodecs(next.Codecs);
            var racedCodec = await racedLookup;
            Ensure(racedCodec is TaggedAdapterValueCodec { Tag: 1 or 2 },
                $"raced lookup {iteration} returns a complete published generation");
            Ensure(context.Codecs.GetCodec<AdapterValue>() is TaggedAdapterValueCodec { Tag: var tag } &&
                   tag == expectedTag,
                $"post-publication lookup {iteration} uses the current registration");
        }

        context.PublishGeneratedCodecs(newRegistration.Codecs);
        context.ReleaseGeneratedManifest(oldRegistration);
        Ensure(context.Codecs.GetCodec<AdapterValue>() is TaggedAdapterValueCodec { Tag: 2 },
            "old owner cleanup cannot evict the replacement Codec");
        Ensure(oldCounters.ScopeDisposeCount == 1, "old generation Scope is disposed exactly once");
        Ensure(newCounters.ScopeDisposeCount == 0, "new generation Scope remains active");
        context.Dispose();
        context.Dispose();
        Ensure(newCounters.ScopeDisposeCount == 1, "new generation Scope is disposed exactly once");
    }


    [Test]
    public async Task GeneratedCodecResolutionCrossingPublicationShouldUseCurrentGeneration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = CreateRuntimeBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var oldRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "blocking-old-generation",
            new BlockingNativeFactory<ThirdAdapterValue>(
                new TaggedThirdAdapterValueCodec(1), entered, release)));
        var newRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "new-generation",
            new FixedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(2))));
        context.AdoptGeneratedManifest(oldRegistration);
        context.AdoptGeneratedManifest(newRegistration);
        context.PublishGeneratedCodecs(oldRegistration.Codecs);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.PublishGeneratedCodecs(newRegistration.Codecs);
            release.TrySetResult();

            var resolved = await racedLookup.WaitAsync(RaceCoordinationTimeout);
            Ensure(resolved is TaggedThirdAdapterValueCodec { Tag: 2 },
                "a Codec resolution returning after publication must use the current generation");
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
        }
    }


    [Test]
    public async Task FallbackCodecResolutionCrossingPublicationShouldUseGeneratedCodec()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = CreateRuntimeBuilder()
            .UseCodecResolver(type =>
            {
                if (type != typeof(ThirdAdapterValue))
                    return null;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return new TaggedThirdAdapterValueCodec(1);
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new TestManifest(
            "generated-during-fallback",
            new FixedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(2))));
        context.AdoptGeneratedManifest(registration);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.PublishGeneratedCodecs(registration.Codecs);
            release.TrySetResult();

            var resolved = await racedLookup.WaitAsync(RaceCoordinationTimeout);
            Ensure(resolved is TaggedThirdAdapterValueCodec { Tag: 2 },
                "a fallback resolution must not cross a generated publication boundary");
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
        }
    }


    [Test]
    public async Task NullFallbackResolutionCrossingPublicationShouldUseGeneratedCodec()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = CreateRuntimeBuilder()
            .UseCodecResolver(type =>
            {
                if (type != typeof(ThirdAdapterValue))
                    return null;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return null;
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new TestManifest(
            "generated-during-null-fallback",
            new FixedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(2))));
        context.AdoptGeneratedManifest(registration);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.PublishGeneratedCodecs(registration.Codecs);
            release.TrySetResult();

            var resolved = await racedLookup.WaitAsync(RaceCoordinationTimeout);
            Ensure(resolved is TaggedThirdAdapterValueCodec { Tag: 2 },
                "a null fallback result must recheck generated publication");
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
        }
    }


    [Test]
    public async Task CodecResolutionCrossingContextDisposalShouldFail()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = CreateRuntimeBuilder()
            .UseCodecResolver(type =>
            {
                if (type != typeof(ThirdAdapterValue))
                    return null;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return new TaggedThirdAdapterValueCodec(1);
            })
            .Build(includeGeneratedAssemblyCatalog: false);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.Dispose();
            release.TrySetResult();

            try
            {
                _ = await racedLookup.WaitAsync(RaceCoordinationTimeout);
                throw new Exception("expected in-flight Codec resolution to observe Context disposal");
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
            context.Dispose();
        }
    }


    [Test]
    public async Task NullCodecResolutionCrossingContextDisposalShouldFailAsDisposed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = CreateRuntimeBuilder()
            .UseCodecResolver(type =>
            {
                if (type != typeof(ThirdAdapterValue))
                    return null;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return null;
            })
            .Build(includeGeneratedAssemblyCatalog: false);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.Dispose();
            release.TrySetResult();

            try
            {
                _ = await racedLookup.WaitAsync(RaceCoordinationTimeout);
                throw new Exception("expected null Codec resolution to observe Context disposal");
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
            context.Dispose();
        }
    }


    [Test]
    public void UnchangedCodecShouldRefreshAcrossAnUnrelatedSnapshotRemoval()
    {
        using var context = CreateRuntimeBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var stableRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "stable-codec",
            new FixedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(3))));
        var removedRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "removed-codec",
            new FixedNativeFactory<SecondAdapterValue>(new AdapterCodec<SecondAdapterValue>())));
        context.AdoptGeneratedManifest(stableRegistration);
        context.AdoptGeneratedManifest(removedRegistration);
        var combined = stableRegistration.Codecs
            .Concat(removedRegistration.Codecs)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        context.PublishGeneratedCodecs(combined);
        var before = context.Codecs.GetCodec<ThirdAdapterValue>();

        context.PublishGeneratedCodecs(stableRegistration.Codecs);
        var after = context.Codecs.GetCodec<ThirdAdapterValue>();

        Ensure(ReferenceEquals(before, after),
            "an unchanged registration refreshes its snapshot identity without recreating its Codec");
        context.ReleaseGeneratedManifest(removedRegistration);
    }


    [Test]
    public void AdapterFreeCustomWireCodecShouldBeAccepted()
    {
        using var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new TestManifest(
            "custom-codec",
            new CustomWireFactory<ThirdAdapterValue>(
                new TaggedThirdAdapterValueCodec(7),
                "custom-wire/v1")));
        context.AdoptGeneratedManifest(registration);
        context.PublishGeneratedCodecs(registration.Codecs);

        Ensure(context.Codecs.GetCodec<ThirdAdapterValue>() is TaggedThirdAdapterValueCodec { Tag: 7 },
            "an adapter-free Codec with a custom deterministic identity must resolve through the generated registration");
    }
}
