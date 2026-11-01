using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

// Actual striped module lease + linked CTS owner; not full dynamic proxy/ALC integration proof.
public sealed class DynamicProducerLifetimeTests
{
    [Test]
    public void LogicalExitRetainsLinkedSourceAndModuleUntilPhysicalExit()
    {
        using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, fixture.Module.ForcedCancellation);
        var token = linked.Token;
        var owner = fixture.Owner(linked);
        Ensure(owner.TryClaimPhysical(), "claim exact producer owner once");
        owner.Dispose();
        caller.Cancel();
        Ensure(token.IsCancellationRequested && token.WaitHandle.WaitOne(0) && fixture.Module.RemainingStreams == 1,
            "logical exit cannot dispose linked source/module while physical producer remains");
        owner.CompletePhysical();
        Ensure(IsDisposed(token) && fixture.Module.RemainingCalls == 0, "last physical exit claims both cleanups once");
        owner.CompletePhysical(); owner.Dispose();
        fixture.Module.AssertAccountingInvariant();
    }

    [Test]
    public void ProducerPhysicalExitReturnsItsLeaseButKeepsLogicalLinkedSource()
    {
        using var fixture = new Fixture();
        var source = new CancellationTokenSource();
        var token = source.Token;
        var owner = fixture.Owner(source);
        Ensure(owner.TryClaimPhysical(), "claim");
        owner.CompletePhysical();
        Ensure(!IsDisposed(token) && fixture.Module.RemainingCalls == 0,
            "producer-only lease ends physically; logical linked source remains until response owner exits");
        owner.Dispose();
        Ensure(IsDisposed(token), "logical final owner disposes linked source");
    }

    [Test]
    public void OneWayTransfersOneOriginalLeaseAndWaitsForBothOwners()
    {
        using var fixture = new Fixture();
        var source = new CancellationTokenSource();
        var token = source.Token;
        var owner = fixture.Owner(source, oneWay: true);
        Ensure(fixture.Module.RemainingCalls == 1 && owner.TryClaimPhysical(), "one original module acquisition only");
        owner.CompletePhysical();
        Ensure(fixture.Module.RemainingCalls == 1 && !IsDisposed(token), "OneWay original lease also protects remaining logical owner");
        owner.Dispose();
        Ensure(fixture.Module.RemainingCalls == 0 && IsDisposed(token), "both owners end the same lease/source once");
    }

    [Test]
    public void LogicalExitWithoutClaimRejectsLatePhysicalClaim()
    {
        using var fixture = new Fixture();
        var source = new CancellationTokenSource();
        var token = source.Token;
        var owner = fixture.Owner(source);
        owner.Dispose();
        Ensure(!owner.TryClaimPhysical() && IsDisposed(token) && fixture.Module.RemainingCalls == 0,
            "late context cannot resurrect cleaned ownership or run user producer work");
    }

    [Test]
    public void SourceDisposeFailureDoesNotSkipOriginalModuleLeaseCleanup()
    {
        using var fixture = new Fixture();
        var expected = new InvalidOperationException("source-dispose-marker");
        var source = new ThrowingSource(expected);
        var owner = fixture.Owner(source);
        Exception? observed = null;
        try { owner.Dispose(); } catch (Exception failure) { observed = failure; }
        Ensure(ReferenceEquals(observed, expected) && source.Disposals == 1 && fixture.Module.RemainingCalls == 0,
            "independent cleanup returns original module lease despite source disposal failure");
        owner.Dispose();
        Ensure(source.Disposals == 1, "failed cleanup claim cannot be replayed by a second logical exit");
    }

    private static bool IsDisposed(CancellationToken token)
    { try { _ = token.WaitHandle; return false; } catch (ObjectDisposedException) { return true; } }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ThrowingSource(Exception error) : CancellationTokenSource
    {
        internal int Disposals;
        protected override void Dispose(bool disposing)
        { Disposals++; base.Dispose(disposing); throw error; }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly SharpLinkRuntimeContext _runtime;
        private readonly RpcGeneratedManifestRegistration _registration;
        private readonly ManifestLoadContext _loadContext;
        internal SharpLinkDynamicModule Module { get; }
        internal Fixture()
        {
            _runtime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
            RpcGeneratedManifestRegistration? registration = null;
            ManifestLoadContext? loadContext = null;
            try
            {
                loadContext = new ManifestLoadContext();
                var assembly = CreateManifestAssembly(loadContext);
                using var preparation = _runtime.Domain.BeginPreparation();
                var result = SharpLinkAssemblyManifestLoader.TryLoad(assembly, preparation, out var manifest);
                if (!result.Succeeded || manifest is null)
                    throw new InvalidOperationException($"Current test manifest could not be loaded: {result.Error}");
                registration = _runtime.PrepareGeneratedManifest(manifest);
                Module = new SharpLinkDynamicModule(assembly, manifest, registration);
                _registration = registration;
                _loadContext = loadContext;
            }
            catch
            {
                try { registration?.Dispose(); }
                finally
                {
                    try { _runtime.Dispose(); }
                    finally { loadContext?.Unload(); }
                }
                throw;
            }
        }
        internal SharpLinkClientStreamModuleLeaseOwner Owner(CancellationTokenSource source, bool oneWay = false)
        {
            Ensure(Module.TryAcquire(true, out var lease), "actual striped module lease acquired");
            return new SharpLinkClientStreamModuleLeaseOwner(lease, source, oneWay);
        }
        public void Dispose()
        {
            try { Module.AssertAccountingInvariant(); _registration.Dispose(); _runtime.Dispose(); }
            finally { _loadContext.Unload(); }
        }
    }
    private static Assembly CreateManifestAssembly(ManifestLoadContext loadContext)
    {
        var assembly = new System.Reflection.Emit.PersistedAssemblyBuilder(
            new AssemblyName($"SharpLink.DynamicProducerLifetime.{Guid.NewGuid():N}"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Manifest");
        var builder = module.DefineType("OwnedEmptyManifest", TypeAttributes.Public | TypeAttributes.Sealed,
            typeof(EmptyManifest));
        var constructorBuilder = builder.DefineConstructor(MethodAttributes.Public,
            CallingConventions.Standard, Type.EmptyTypes);
        var il = constructorBuilder.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(EmptyManifest).GetConstructor(Type.EmptyTypes)!);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        var manifestType = builder.CreateType();
        var constructor = typeof(SharpLinkGeneratedAssemblyManifestAttribute).GetConstructor(
            [typeof(Type), typeof(int), typeof(int), typeof(string), typeof(string)])
            ?? throw new MissingMethodException("Current manifest locator constructor is missing.");
        assembly.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(constructor,
            [manifestType, SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol,
             "test", SharpLinkGeneratedManifestVersions.AbiIdentity]));
        using var image = new MemoryStream();
        assembly.Save(image);
        image.Position = 0;
        return loadContext.LoadFromStream(image);
    }

    private sealed class ManifestLoadContext()
        : System.Runtime.Loader.AssemblyLoadContext($"ProducerLifetime.{Guid.NewGuid():N}", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
            => System.Linq.Enumerable.FirstOrDefault(Default.Assemblies, candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
    }

    // Public base only so the isolated emitted owner can implement the real manual manifest SPI.
    // No test-assembly locator, global catalog entry, module initializer or compatibility bypass.
    public class EmptyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => GetType().Assembly;
        public RpcHash128 RpcAssemblyHash => TestGeneratedIdentity.ManifestHash;
        public string CompileTimeDescriptor => "physical-producer-owner";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
