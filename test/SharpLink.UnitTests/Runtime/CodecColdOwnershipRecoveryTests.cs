using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

// Fresh actual-source controls. The current ABI owner locator is CurrentAbiTestManifest.cs.
// No old lost source/runtime result or general manual borrowed-use lifetime is inherited.
public sealed class CodecColdOwnershipRecoveryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Test]
    public void WarmGlobalAndOwnerHitsDoNotAcquireColdQuota()
    {
        using var context = EmptyContext();
        var manifest = new Manifest(new Factory<ValueA>(new ValueCodec<ValueA>(1)));
        using var owner = context.PrepareGeneratedManifest(manifest);
        context.PublishGeneratedCodecs(owner.Codecs);
        var local = RpcGeneratedCodecResolver.GetProvider(owner);
        var globalCodec = context.Codecs.GetCodec<ValueA>(); var localCodec = local.GetCodec<ValueA>();
        context.Codecs.GetCodec<int>(); local.GetCodec<int>();
        var bytes = context.Authority.Bytes; var scopeBytes = owner.RegistrationScope.Bytes;
        for (var index = 0; index < 100; index++)
        {
            context.Codecs.GetCodec<int>(); local.GetCodec<int>();
            Ensure(ReferenceEquals(globalCodec, context.Codecs.GetCodec<ValueA>()), "global warm identity");
            Ensure(ReferenceEquals(localCodec, local.GetCodec<ValueA>()), "owner warm identity");
        }
        Ensure(context.Authority.Bytes == bytes && owner.RegistrationScope.Bytes == scopeBytes, "warm hits must not acquire metadata storage");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FinalRetirementJoinsHeldFactoryAndPreventsLateRefill(bool ownerLocal)
    {
        using var context = EmptyContext();
        var entered = Signal(); using var release = new ManualResetEventSlim();
        var factory = new Factory<ValueA>(new ValueCodec<ValueA>(1));
        var manifest = new Manifest(factory, new Factory<ValueB>(new ValueCodec<ValueB>(2), new Adapter()));
        using var owner = context.PrepareGeneratedManifest(manifest);
        context.PublishGeneratedCodecs(owner.Codecs);
        var provider = ownerLocal ? RpcGeneratedCodecResolver.GetProvider(owner) : context.Codecs;
        factory.CreateCallback = _ => { entered.TrySetResult(); if (!release.Wait(Bound)) throw new TimeoutException("factory release"); return new ValueCodec<ValueA>(3); };
        var resolve = Task.Run(() => Capture(() => provider.GetCodec<ValueA>()));
        Task? retire = null; Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(Bound);
            retire = Task.Run(owner.Dispose);
            await WaitClosed(owner);
            Ensure(!retire.IsCompleted && manifest.Adapter!.Disposals == 0, "physical factory use must keep scopes alive");
            release.Set();
            Ensure(await resolve.WaitAsync(Bound) is ObjectDisposedException, "closed owner cannot refill cache after callback");
            await retire.WaitAsync(Bound);
            Ensure(manifest.Adapter!.Disposals == 1 && Capture(() => provider.GetCodec<ValueA>()) is ObjectDisposedException, "retired escaped provider rejects new resolution");
        }
        catch (Exception error) { primary = error; throw; }
        finally { release.Set(); await Join(primary, resolve, retire); }
    }

    [Test]
    public void LogicalUnpublishDoesNotRetireExistingOwnerProvider()
    {
        using var context = EmptyContext();
        using var owner = context.PrepareGeneratedManifest(new Manifest(new Factory<ValueA>(new ValueCodec<ValueA>(7))));
        context.PublishGeneratedCodecs(owner.Codecs);
        var provider = RpcGeneratedCodecResolver.GetProvider(owner);
        context.PublishGeneratedCodecs(new Dictionary<Type, RpcGeneratedCodecRegistration>());
        Ensure(((ValueCodec<ValueA>)provider.GetCodec<ValueA>()).Tag == 7, "old physical owner remains usable after global unpublish");
        Ensure(Capture(() => context.Codecs.GetCodec<ValueA>()) is NotSupportedException, "new global selection cannot find unpublished owner");
        owner.Dispose();
        Ensure(Capture(() => provider.GetCodec<ValueA>()) is ObjectDisposedException, "final physical cut rejects warm provider access");
    }

    [Test]
    public void ScopedSnapshotKeepsOnlyMetadataAndDoesNotBlockPhysicalRetirement()
    {
        using var context = EmptyContext(); var adapter = new Adapter();
        using var owner = context.PrepareGeneratedManifest(new Manifest(new Factory<ValueA>(new ValueCodec<ValueA>(1), adapter)));
        context.PublishGeneratedCodecs(owner.Codecs);
        using var snapshot = context.CaptureGeneratedCodecSnapshot();
        context.PublishGeneratedCodecs(new Dictionary<Type, RpcGeneratedCodecRegistration>());
        owner.Dispose(); context.Dispose();
        Ensure(adapter.Disposals == 1, "standing metadata reference is not an active physical user");
        Ensure(snapshot.Registrations[typeof(ValueA)].TargetType == typeof(ValueA), "lexical snapshot metadata remains available");
        Ensure(context.Authority.Bytes != 0, "snapshot keeps its exact standing charge");
        snapshot.Dispose();
        Ensure(context.Authority.Bytes == 0, "last snapshot clears roots before refund");
        Ensure(Capture(() => _ = snapshot.Registrations) is ObjectDisposedException, "disposed lexical view rejects access");
    }

    [Test]
    public void UnadoptedOwnerCleanupSurvivesRuntimeRootDisposal()
    {
        using var context = EmptyContext(); var adapter = new Adapter();
        using var owner = context.PrepareGeneratedManifest(new Manifest(new Factory<ValueA>(new ValueCodec<ValueA>(1), adapter)));
        _ = RpcGeneratedCodecResolver.GetProvider(owner);
        context.Dispose();
        owner.Dispose();
        Ensure(adapter.Disposals == 1 && context.Authority.Bytes == 0, "final cleanup must use its pre-owned budget, not a dead Runtime root");
    }

    [Test]
    public void AuthoritySaturationDoesNotRequireQuotaToRetireOwnedScopes()
    {
        using var context = EmptyContext(); var adapter = new Adapter();
        using var owner = context.PrepareGeneratedManifest(new Manifest(new Factory<ValueA>(new ValueCodec<ValueA>(1), adapter)));
        using var filler = context.Domain.BeginPreparation();
        var available = ColdMetadataAuthority.AuthorityByteLimit - context.Authority.Bytes - ColdMetadataAuthority.HeaderCharge;
        using var saturated = filler.ReserveCollection(checked((int)(available / ColdMetadataAuthority.SlotCharge)));
        Ensure(context.Authority.Bytes == ColdMetadataAuthority.AuthorityByteLimit, "actual authority is full before teardown");
        owner.Dispose();
        Ensure(adapter.Disposals == 1 && context.Authority.Bytes < ColdMetadataAuthority.AuthorityByteLimit, "retirement refunds rather than allocating new quota");
    }

    [Test]
    public async Task FlowedExpiredFactoryContextDoesNotConferOrBlockNewAdmission()
    {
        using var context = EmptyContext(); var start = Signal();
        var factory = new Factory<ValueA>(new ValueCodec<ValueA>(1));
        using var owner = context.PrepareGeneratedManifest(new Manifest(factory));
        var provider = RpcGeneratedCodecResolver.GetProvider(owner);
        Task<Exception?>? child = null; Exception? primary = null;
        factory.CreateCallback = _ =>
        {
            child = Task.Run(async () =>
            {
                await start.Task;
                using var fresh = context.Domain.BeginPreparation(); // Expired flowed frame is not a current callback.
                return Capture(() => provider.GetCodec<ValueA>()); // It is not an inherited physical lease either.
            });
            return new ValueCodec<ValueA>(2);
        };
        try
        {
            provider.GetCodec<ValueA>(); owner.Dispose(); start.TrySetResult();
            Ensure(await child!.WaitAsync(Bound) is ObjectDisposedException, "flowed child must reacquire and reject a retired owner");
        }
        catch (Exception error) { primary = error; throw; }
        finally { start.TrySetResult(); await Join(primary, child); }
    }

    [Test]
    public void RecursiveCrossOwnerCallbackCannotWaitForItsOwnHeldAncestor()
    {
        using var context = EmptyContext();
        var a = new Factory<ValueA>(new ValueCodec<ValueA>(1)); var b = new Factory<ValueB>(new ValueCodec<ValueB>(2));
        using var ownerB = context.PrepareGeneratedManifest(new Manifest(b)); context.PublishGeneratedCodecs(ownerB.Codecs);
        using var ownerA = context.PrepareGeneratedManifest(new Manifest(a, referenced: new(typeof(ValueB), b.CodecHash)));
        a.CreateCallback = provider => { provider.GetCodec<ValueB>(); return new ValueCodec<ValueA>(3); };
        b.CreateCallback = _ => { ownerA.Dispose(); return new ValueCodec<ValueB>(4); };
        var failure = Capture(() => RpcGeneratedCodecResolver.GetProvider(ownerA).GetCodec<ValueA>());
        Ensure(failure is InvalidOperationException && failure.Message.Contains("held by its current cold operation", StringComparison.Ordinal), "all held ancestors, not just top frame, reject self-wait");
        Ensure(!ownerA.Identity.IsClosed && !ownerB.Identity.IsClosed, "rejected self-retirement must not close either owner");
    }

    [Test]
    public void OldOwnerKeepsExactDependencyWhenSameTypeIsRepublished()
    {
        using var context = EmptyContext(); var b1 = new Factory<ValueB>(new ValueCodec<ValueB>(1));
        using var first = context.PrepareGeneratedManifest(new Manifest(b1)); context.PublishGeneratedCodecs(first.Codecs);
        var a = new Factory<ValueA>(new ValueCodec<ValueA>(0));
        using var owner = context.PrepareGeneratedManifest(new Manifest(a, referenced: new(typeof(ValueB), b1.CodecHash)));
        a.CreateCallback = provider => new ValueCodec<ValueA>(((ValueCodec<ValueB>)provider.GetCodec<ValueB>()).Tag);
        using var replacement = context.PrepareGeneratedManifest(new Manifest(new Factory<ValueB>(new ValueCodec<ValueB>(2))));
        context.PublishGeneratedCodecs(replacement.Codecs);
        Ensure(((ValueCodec<ValueA>)RpcGeneratedCodecResolver.GetProvider(owner).GetCodec<ValueA>()).Tag == 1, "old graph must not rebind to current same-Type publication");
        Ensure(((ValueCodec<ValueB>)context.Codecs.GetCodec<ValueB>()).Tag == 2, "new global selection uses replacement");
        first.Dispose();
        Ensure(Capture(() => RpcGeneratedCodecResolver.GetProvider(owner).GetCodec<ValueA>()) is ObjectDisposedException, "dependency retirement invalidates embedded dependent cache entries too");
    }

    [Test]
    public void MissingAndMismatchedDependenciesFailAtTheirPreciseGate()
    {
        using var context = EmptyContext(); var a = new Factory<ValueA>(new ValueCodec<ValueA>(1));
        var missing = Capture(() => context.PrepareGeneratedManifest(new Manifest(a, referenced: new(typeof(ValueB), new(1, 1)))));
        Ensure(missing?.GetType() == typeof(InvalidOperationException) && missing.Message.Contains("no generated Codec is registered for that exact Type", StringComparison.Ordinal), "missing dependency, not unrelated ABI/table capture, must fail");
        using var owner = context.PrepareGeneratedManifest(new Manifest(new Factory<ValueB>(new ValueCodec<ValueB>(2)))); context.PublishGeneratedCodecs(owner.Codecs);
        var mismatch = Capture(() => context.PrepareGeneratedManifest(new Manifest(a, referenced: new(typeof(ValueB), new(1, 1)))));
        Ensure(mismatch?.GetType() == typeof(InvalidOperationException) && mismatch.Message.Contains("but the exact registered Type has CodecHash", StringComparison.Ordinal), "exact hash mismatch gate");
    }

    [Test]
    public void DiscardedPreparationRefundsItsOwnCopiedMapAndMetadataReferences()
    {
        using var context = EmptyContext();
        using var owner = context.PrepareGeneratedManifest(new Manifest(new Factory<ValueA>(new ValueCodec<ValueA>(1))));
        var before = context.Authority.Bytes;
        using (var publication = context.PrepareGeneratedCodecPublication(owner.Codecs))
            Ensure(context.Authority.Bytes > before, "private publication must charge copies before commit");
        Ensure(context.Authority.Bytes == before, "discard clears all candidate maps and standing references before exact refund");
    }

    [Test]
    public async Task DependentFactoryHoldsExactDependencyThroughItsWholeCallback()
    {
        using var context = EmptyContext(); var adapter = new Adapter();
        var b = new Factory<ValueB>(new ValueCodec<ValueB>(5), adapter);
        using var dependency = context.PrepareGeneratedManifest(new Manifest(b)); context.PublishGeneratedCodecs(dependency.Codecs);
        var a = new Factory<ValueA>(new ValueCodec<ValueA>(1));
        using var owner = context.PrepareGeneratedManifest(new Manifest(a, referenced: new(typeof(ValueB), b.CodecHash)));
        var entered = Signal(); using var release = new ManualResetEventSlim();
        a.CreateCallback = provider =>
        {
            var borrowed = provider.GetCodec<ValueB>(); entered.TrySetResult();
            if (!release.Wait(Bound)) throw new TimeoutException("dependency use release");
            Ensure(adapter.Disposals == 0, "dependency scope must survive the entire dependent callback");
            borrowed.Deserialize(ReadOnlySequence<byte>.Empty); return new ValueCodec<ValueA>(8);
        };
        var resolving = Task.Run(() => Capture(() => RpcGeneratedCodecResolver.GetProvider(owner).GetCodec<ValueA>()));
        Task? retiring = null; Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(Bound); retiring = Task.Run(dependency.Dispose); await WaitClosed(dependency);
            Ensure(!retiring.IsCompleted && adapter.Disposals == 0, "dependency physical retirement joins the dependent cold callback");
            release.Set(); Ensure(await resolving.WaitAsync(Bound) is ObjectDisposedException, "closed dependent graph cannot refill late");
            await retiring.WaitAsync(Bound); Ensure(adapter.Disposals == 1, "dependency disposed after callback exits");
        }
        catch (Exception error) { primary = error; throw; }
        finally { release.Set(); await Join(primary, resolving, retiring); }
    }

    [Test]
    public void SnapshotSecondHeaderFailureRefundsTheAlreadyForkedStorage()
    {
        using var context = EmptyContext();
        using var filler = context.Domain.BeginPreparation();
        var capacity = ColdMetadataAuthority.AuthorityByteLimit - context.Authority.Bytes -
            ColdMetadataAuthority.HeaderCharge - ColdMetadataAuthority.HeaderCharge;
        using var saturated = filler.ReserveCollection(checked((int)(capacity / ColdMetadataAuthority.SlotCharge)));
        var before = context.Authority.Bytes;
        Ensure(before == ColdMetadataAuthority.AuthorityByteLimit - ColdMetadataAuthority.HeaderCharge,
            "one fork header fits, but the next owned list header cannot");
        var failure = Capture(() => { using var unexpected = context.CaptureGeneratedCodecSnapshot(); });
        Ensure(failure is ColdMetadataException { Code: SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded }, "exact second-header capacity gate");
        Ensure(context.Authority.Bytes == before, "failed snapshot must release its already-owned fork header");
    }

    [Test]
    public void ConstructorPostProviderQuotaFailureRollsBackIndependentStorageAndScopes()
    {
        var authority = new ColdMetadataAuthority();
        using var filler = authority.CreateDomain().BeginPreparation();
        ColdMetadataReservation? saturated = null;
        var adapter = new Adapter();
        adapter.Creating = () =>
        {
            // This is the real adapter callback reached after provider construction. Only
            // already-owned test quota is used; callback management admission stays forbidden.
            var capacity = ColdMetadataAuthority.AuthorityByteLimit - authority.Bytes - ColdMetadataAuthority.HeaderCharge;
            saturated = filler.ReserveCollection(checked((int)(capacity / ColdMetadataAuthority.SlotCharge)));
        };
        using var plan = new SharpLinkRuntimeContextBuilder()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource([new Manifest(new Factory<ValueA>(new ValueCodec<ValueA>(1), adapter))]))
            .Compile(authority.CreateDomain());
        try
        {
            var failure = Capture(() => { using var unexpected = plan.Materialize(); });
            Ensure(failure is ColdMetadataException { Code: SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded }, "post-provider preparation must fail at real quota");
            Ensure(adapter.Creations == 1 && adapter.Disposals == 1, "real scope creation proves provider construction was passed, and scope rollback completes");
            saturated!.Dispose(); filler.Dispose();
            Ensure(authority.Bytes == 0, "failed Runtime construction refunds independent provider storage as well as the original plan");
        }
        finally { saturated?.Dispose(); }
    }

    private static SharpLinkRuntimeContext EmptyContext() => new SharpLinkRuntimeContextBuilder().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).Build();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task WaitClosed(RpcGeneratedManifestRegistration owner)
    {
        var started = Stopwatch.StartNew();
        while (!owner.Identity.IsClosed)
        { if (started.Elapsed > Bound) throw new TimeoutException("owner close"); await Task.Delay(1); }
    }
    private static async Task Join(Exception? primary, params Task?[] tasks)
    {
        List<Exception>? errors = null;
        foreach (var task in tasks) if (task is not null) try { await task.WaitAsync(Bound); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is null) return;
        if (primary is not null) errors.Insert(0, primary);
        throw new AggregateException(errors);
    }
    private sealed class ValueA;
    private sealed class ValueB;
    private sealed class ValueCodec<T>(int tag) : IRpcCodec<T>
    {
        internal int Tag { get; } = tag;
        public void Serialize(in T value, IBufferWriter<byte> writer) { }
        public T? Deserialize(in ReadOnlySequence<byte> value) => default;
    }
    private sealed class Adapter : IRpcCodecAdapter, IRpcCodecAdapterScope
    {
        internal int Disposals;
        internal int Creations;
        internal Action? Creating;
        public string AdapterId => "test.codec-cold-recovery/v1";
        public IRpcCodecAdapterScope CreateScope() { Interlocked.Increment(ref Creations); Creating?.Invoke(); return this; }
        public IRpcCodec<T> CreateCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => new ValueCodec<T>(0);
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    private sealed class Factory<T>(IRpcCodec<T> codec, Adapter? adapter = null) : IRpcGeneratedCodecFactory
    {
        internal Func<IRpcCodecProvider, IRpcCodec>? CreateCallback;
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => new(0x775C01D, 0x1);
        public string? AdapterId => adapter?.AdapterId;
        public IRpcCodecAdapter? Adapter => adapter;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? scope) => CreateCallback?.Invoke(provider) ?? codec;
        public bool IsCompatibleCodec(IRpcCodec actual) => actual is IRpcCodec<T>;
    }
    private sealed class Manifest : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
    {
        internal Manifest(IRpcGeneratedCodecFactory factory, IRpcGeneratedCodecFactory? second = null, SharpLinkReferencedCodecDependency? referenced = null)
        { Codecs = second is null ? [factory] : [factory, second]; ReferencedCodecDependencies = referenced is null ? [] : [referenced]; Adapter = (factory.Adapter ?? second?.Adapter) as Adapter; }
        internal Adapter? Adapter { get; }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "fresh-codec-cold-recovery";
        public Assembly OwnerAssembly => typeof(Manifest).Assembly;
        public RpcHash128 RpcAssemblyHash => new(0x775C01D, 0x2);
        public string CompileTimeDescriptor => "fresh-codec-cold-recovery";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
        public IReadOnlyList<string> Dependencies => [];
        public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies { get; }
    }
}
