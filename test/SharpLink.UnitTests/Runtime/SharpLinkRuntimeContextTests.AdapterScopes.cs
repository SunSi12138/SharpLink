using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public partial class SharpLinkRuntimeContextTests
{

    [Test]
    public void AdapterTypesInOneManifestShouldShareOneScopeAndDisposeWithContext()
    {
        var counters = new AdapterCounters();
        using (var context = CreateRuntimeBuilder()
                   .Build([new AdapterManifest(counters, includeSecondCodec: true)]))
        {
            Ensure(context.Codecs.GetCodec<AdapterValue>() is AdapterCodec<AdapterValue>,
                "first Adapter Codec");
            Ensure(context.Codecs.GetCodec<SecondAdapterValue>() is AdapterCodec<SecondAdapterValue>,
                "second Adapter Codec");
            Ensure(counters.ScopeCreateCount == 1,
                "one Manifest and Adapter ID must create one Scope");
            Ensure(counters.CodecCreateCount == 2,
                "both closed Codecs are prepared transactionally");
            Ensure(counters.ScopeDisposeCount == 0, "scope remains live with Context");
        }
        Ensure(counters.ScopeDisposeCount == 1, "Context disposes Adapter Scope once");
    }


    [Test]
    public void SeparateContextsAndManifestsShouldOwnSeparateAdapterScopes()
    {
        var counters = new AdapterCounters();
        var manifest = new AdapterManifest(counters, includeSecondCodec: false);
        using var first = CreateRuntimeBuilder().Build([manifest]);
        using var second = CreateRuntimeBuilder().Build([manifest]);
        Ensure(counters.ScopeCreateCount == 2,
            "same Manifest in two Runtime Contexts must use separate Scopes");
    }


    [Test]
    public void DifferentManifestsInOneContextShouldOwnSeparateAdapterScopes()
    {
        var counters = new AdapterCounters();
        using var context = CreateRuntimeBuilder().Build([
            new TestManifest("first", new AdapterFactory<AdapterValue>(counters)),
            new TestManifest("second", new AdapterFactory<SecondAdapterValue>(counters))
        ]);

        Ensure(counters.ScopeCreateCount == 2,
            "the same Adapter ID in two Manifest instances must create separate Scopes");
        Ensure(context.Codecs.GetCodec<AdapterValue>() is AdapterCodec<AdapterValue>,
            "first Manifest Codec");
        Ensure(context.Codecs.GetCodec<SecondAdapterValue>() is AdapterCodec<SecondAdapterValue>,
            "second Manifest Codec");
    }


    [Test]
    public void DifferentAdaptersInOneManifestShouldOwnSeparateScopes()
    {
        var firstCounters = new AdapterCounters();
        var secondCounters = new AdapterCounters();
        using var context = CreateRuntimeBuilder().Build([
            new TestManifest(
                "two-adapters",
                new AdapterFactory<AdapterValue>(firstCounters),
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new AlternateCountingAdapter(secondCounters),
                    AlternateCountingAdapter.Id,
                    AlternateCountingAdapter.Wire))
        ]);

        Ensure(firstCounters.ScopeCreateCount == 1, "first Adapter owns one Scope");
        Ensure(secondCounters.ScopeCreateCount == 1, "second Adapter owns one Scope");
        Ensure(context.Codecs.GetCodec<AdapterValue>() is AdapterCodec<AdapterValue>,
            "first Adapter Codec");
        Ensure(context.Codecs.GetCodec<SecondAdapterValue>() is AdapterCodec<SecondAdapterValue>,
            "second Adapter Codec");
    }


    [Test]
    public void FailedAdapterCodecPreparationShouldDisposeCandidateScope()
    {
        var counters = new AdapterCounters { FailOnCodecNumber = 2 };
        try
        {
            using var _ = CreateRuntimeBuilder()
                .Build([new AdapterManifest(counters, includeSecondCodec: true)]);
            throw new Exception("expected second Adapter Codec creation to fail");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("candidate failure", StringComparison.Ordinal),
                "candidate failure is preserved");
        }
        Ensure(counters.ScopeDisposeCount == 1,
            "failed transaction disposes the candidate Scope");
    }


    [Test]
    public void ThirdAdapterCodecFailureShouldDisposeCandidateScope()
    {
        var counters = new AdapterCounters { FailOnCodecNumber = 3 };
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "third-codec-failure",
                    new AdapterFactory<AdapterValue>(counters),
                    new AdapterFactory<SecondAdapterValue>(counters),
                    new AdapterFactory<ThirdAdapterValue>(counters))
            ]);
            throw new Exception("expected third Adapter Codec creation to fail");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("candidate failure", StringComparison.Ordinal),
                "third candidate failure is preserved");
        }

        Ensure(counters.CodecCreateCount == 3, "the third closed Codec triggers the failure");
        Ensure(counters.ScopeDisposeCount == 1,
            "the shared candidate Scope is disposed after the third Codec fails");
    }


    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ScopeCreationFailureShouldRollbackEarlierScopes(bool returnNull)
    {
        var preparedCounters = new AdapterCounters();
        var failingCounters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "scope-failure",
                    new AdapterFactory<AdapterValue>(preparedCounters),
                    new ConfigurableAdapterFactory<SecondAdapterValue>(
                        new FailingScopeAdapter(failingCounters, returnNull),
                        FailingScopeAdapter.Id,
                        FailingScopeAdapter.Wire))
            ]);
            throw new Exception("expected Adapter Scope creation to fail");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains(returnNull ? "null scope" : "scope failure", StringComparison.Ordinal),
                "Scope failure reason is preserved");
        }

        Ensure(preparedCounters.ScopeCreateCount == 1, "the first Scope was prepared");
        Ensure(preparedCounters.ScopeDisposeCount == 1, "the first Scope was rolled back");
        Ensure(failingCounters.ScopeCreateCount == 1, "the failing Adapter was invoked exactly once");
    }


    [Test]
    public void ManifestPreparationRollbackShouldPreservePrimaryAndScopeCleanupFailures()
    {
        var failure = CaptureFailure(() =>
        {
            using var context = CreateRuntimeBuilder()
                .Build(includeGeneratedAssemblyCatalog: false);
            _ = context.PrepareGeneratedManifest(new TestManifest(
                "manifest-rollback-failure",
                new ConfigurableAdapterFactory<AdapterValue>(
                    new NamedThrowingDisposeAdapter("a.throwing/v1", "candidate scope cleanup failed"),
                    "a.throwing/v1",
                    "throwing-wire/v1"),
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new FailingScopeAdapter(new AdapterCounters(), returnNull: false),
                    FailingScopeAdapter.Id,
                    FailingScopeAdapter.Wire)));
        });

        Ensure(ContainsMessage(failure, "scope failure"),
            "Manifest rollback must retain the Scope creation failure");
        Ensure(ContainsMessage(failure, "candidate scope cleanup failed"),
            "Manifest rollback must retain earlier Scope cleanup failure");
    }


    [Test]
    public void ContextConstructionRollbackShouldPreserveManifestAndCleanupFailures()
    {
        var failure = CaptureFailure(() => _ = CreateRuntimeBuilder().Build([
            new TestManifest(
                "prepared-throwing-manifest",
                new ConfigurableAdapterFactory<AdapterValue>(
                    new NamedThrowingDisposeAdapter("a.prepared/v1", "prepared manifest cleanup failed"),
                    "a.prepared/v1",
                    "throwing-wire/v1")),
            new TestManifest(
                "failing-manifest",
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new FailingScopeAdapter(new AdapterCounters(), returnNull: false),
                    FailingScopeAdapter.Id,
                    FailingScopeAdapter.Wire))
        ]));

        Ensure(ContainsMessage(failure, "scope failure"),
            "Context rollback must retain the later Manifest failure");
        Ensure(ContainsMessage(failure, "prepared manifest cleanup failed"),
            "Context rollback must retain prepared Manifest cleanup failure");
    }


    [Test]
    public void AdapterIdentityMismatchShouldRejectAndDisposePreparedScopes()
    {
        var preparedCounters = new AdapterCounters();
        var mismatchedCounters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "identity-mismatch",
                    new AdapterFactory<AdapterValue>(preparedCounters),
                    new ConfigurableAdapterFactory<SecondAdapterValue>(
                        new CountingAdapter(mismatchedCounters),
                        "z.test.adapter/v1",
                        "test-wire/v1"))
            ]);
            throw new Exception("expected Adapter identity mismatch");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("lifecycle identity", StringComparison.Ordinal),
                "identity mismatch is reported before publication");
        }

        Ensure(preparedCounters.ScopeCreateCount == 1, "the earlier valid Scope was prepared");
        Ensure(preparedCounters.ScopeDisposeCount == 1, "the earlier valid Scope was rolled back");
        Ensure(mismatchedCounters.ScopeCreateCount == 0,
            "an Adapter with mismatched identity cannot create a Scope");
    }


    [Test]
    public void EveryFactoryAdapterInstanceShouldMatchGeneratedIdentity()
    {
        var preparedCounters = new AdapterCounters();
        var mismatchedCounters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "per-factory-identity",
                    new ConfigurableAdapterFactory<AdapterValue>(
                        new InstanceIdentityAdapter(
                            preparedCounters,
                            InstanceIdentityAdapter.Id,
                            InstanceIdentityAdapter.Wire),
                        InstanceIdentityAdapter.Id,
                        InstanceIdentityAdapter.Wire),
                    new ConfigurableAdapterFactory<SecondAdapterValue>(
                        new InstanceIdentityAdapter(
                            mismatchedCounters,
                            "mismatched-instance/v1",
                            InstanceIdentityAdapter.Wire),
                        InstanceIdentityAdapter.Id,
                        InstanceIdentityAdapter.Wire))
            ]);
            throw new Exception("expected every factory Adapter instance to be validated");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("lifecycle identity", StringComparison.Ordinal),
                "a later same-type Adapter instance cannot bypass generated AdapterId validation");
        }

        Ensure(preparedCounters.ScopeCreateCount == 1, "the first valid Adapter Scope was prepared");
        Ensure(preparedCounters.ScopeDisposeCount == 1, "the prepared Scope was rolled back");
        Ensure(mismatchedCounters.ScopeCreateCount == 0,
            "the mismatched later Adapter instance cannot create a Scope");
    }


    [Test]
    public void WrongTypedCodecShouldRejectAndDisposeCandidateScope()
    {
        var counters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "wrong-codec",
                    new ConfigurableAdapterFactory<AdapterValue>(
                        new CountingAdapter(counters),
                        CountingAdapter.Id,
                        CountingAdapter.Wire,
                        new CatalogCodec()))
            ]);
            throw new Exception("expected an incompatible Codec to be rejected");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("incompatible IRpcCodec", StringComparison.Ordinal),
                "wrong closed Codec type is rejected before publication");
        }

        Ensure(counters.ScopeCreateCount == 1, "candidate Scope was created");
        Ensure(counters.ScopeDisposeCount == 1, "candidate Scope was rolled back");
    }


    [Test]
    public void ExplicitCodecShouldWinAndRemainCallerOwned()
    {
        var counters = new AdapterCounters();
        var explicitCodec = new CallerOwnedAdapterValueCodec();
        var context = CreateRuntimeBuilder()
            .AddCodec(explicitCodec)
            .Build([new AdapterManifest(counters, includeSecondCodec: false)]);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<AdapterValue>(), explicitCodec),
            "explicit UseCodec registration wins over generated Adapter Codec");
        context.Dispose();
        context.Dispose();

        Ensure(counters.ScopeDisposeCount == 1, "Context-owned Adapter Scope is disposed once");
        Ensure(explicitCodec.DisposeCount == 0, "caller-owned explicit Codec is not disposed by Runtime");
    }


    [Test]
    public void ConflictingManifestCodecsShouldRollbackBothAdapterScopes()
    {
        var firstCounters = new AdapterCounters();
        var secondCounters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest("first-conflict", new AdapterFactory<AdapterValue>(firstCounters)),
                new TestManifest(
                    "second-conflict",
                    new ConfigurableAdapterFactory<AdapterValue>(
                        new AlternateCountingAdapter(secondCounters),
                        AlternateCountingAdapter.Id,
                        AlternateCountingAdapter.Wire,
                        codecHash: new RpcHash128(
                            0x636f6e666c696374UL,
                            0x2d636f6465632d32UL)))
            ]);
            throw new Exception("expected generated Codec conflict");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("Generated Codec conflict", StringComparison.Ordinal),
                "same-target CodecHash conflict is rejected");
        }

        Ensure(firstCounters.ScopeDisposeCount == 1, "first Manifest Scope is rolled back");
        Ensure(secondCounters.ScopeDisposeCount == 1, "second Manifest Scope is rolled back");
    }


    [Test]
    public void ScopeDisposeFailureShouldNotSkipRemainingAdapterScopes()
    {
        var remainingCounters = new AdapterCounters();
        var throwingCounters = new AdapterCounters();
        var context = CreateRuntimeBuilder().Build([
            new TestManifest(
                "dispose-failure",
                new AdapterFactory<AdapterValue>(remainingCounters),
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new ThrowingDisposeAdapter(throwingCounters),
                    ThrowingDisposeAdapter.Id,
                    ThrowingDisposeAdapter.Wire))
        ]);

        try
        {
            context.Dispose();
            throw new Exception("expected Scope disposal failure to be reported");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("scope dispose failure", StringComparison.Ordinal),
                "original Scope disposal failure is preserved");
        }

        Ensure(throwingCounters.ScopeDisposeCount == 1, "throwing Scope is attempted once");
        Ensure(remainingCounters.ScopeDisposeCount == 1,
            "remaining Scope is disposed even after another Scope throws");
    }


    [Test]
    public void ContextDisposeFailureShouldNotSkipRemainingManifestRegistrations()
    {
        var remainingCounters = new AdapterCounters();
        var throwingCounters = new AdapterCounters();
        var context = CreateRuntimeBuilder().Build([
            new TestManifest(
                "remaining-registration",
                new AdapterFactory<AdapterValue>(remainingCounters)),
            new TestManifest(
                "throwing-registration",
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new ThrowingDisposeAdapter(throwingCounters),
                    ThrowingDisposeAdapter.Id,
                    ThrowingDisposeAdapter.Wire))
        ]);

        try
        {
            context.Dispose();
            throw new Exception("expected Scope disposal failure to be reported");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("scope dispose failure", StringComparison.Ordinal),
                "first disposal failure is preserved across Manifest cleanup");
        }

        Ensure(throwingCounters.ScopeDisposeCount == 1, "throwing registration is attempted once");
        Ensure(remainingCounters.ScopeDisposeCount == 1,
            "remaining Manifest registration is disposed after another registration throws");
    }


    [Test]
    public void ContextDisposeShouldPreserveEveryAdapterScopeFailure()
    {
        var context = CreateRuntimeBuilder().Build([
            new TestManifest("first-throw", new ConfigurableAdapterFactory<AdapterValue>(
                new NamedThrowingDisposeAdapter("throwing.first/v1", "first scope cleanup failed"),
                "throwing.first/v1", "throwing-wire/v1")),
            new TestManifest("second-throw", new ConfigurableAdapterFactory<SecondAdapterValue>(
                new NamedThrowingDisposeAdapter("throwing.second/v1", "second scope cleanup failed"),
                "throwing.second/v1", "throwing-wire/v1"))
        ]);

        Exception failure;
        try
        {
            context.Dispose();
            throw new Exception("expected Adapter scope cleanup failures");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(ContainsMessage(failure, "first scope cleanup failed"), "first scope failure retained");
        Ensure(ContainsMessage(failure, "second scope cleanup failed"), "second scope failure retained");
    }
}
