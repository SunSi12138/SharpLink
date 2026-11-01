using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Runtime;

// Actual copied declarations, ALC binding, and reservation lifetimes; no private state mutation.
public sealed class PreparedDependencyBindingOwnershipTests
{
    [Test]
    public void EscapedPreparedAndSharedDependencyViewClearBindingsOnlyAtLastOwnedRelease()
    {
        var authority = new ColdMetadataAuthority();
        var domain = authority.CreateDomain();
        using var originalOwner = domain.BeginPreparation();
        using var sharedOwner = domain.BeginPreparation();
        var regular = typeof(string).Assembly;
        var contract = typeof(IRpcCodec).Assembly;
        Ensure(!ReferenceEquals(regular, contract), "distinct dependency graphs");
        var original = PreparedGeneratedManifest.Capture(new Manifest(typeof(PreparedDependencyBindingOwnershipTests).Assembly,
            [regular.FullName!], [contract.FullName!]), originalOwner);
        var view = original.ForDependencyOnly(sharedOwner);
        Ensure(ReferenceEquals(original.ResolveDeclaredDependency(contract.FullName!), contract), "contract identity bound");
        Ensure(view.ContractDependencies.Count == 0 && view.ReferencedCodecDependencies.Count == 0 &&
            view.ResolveDeclaredDependency(contract.FullName!) is null, "dependency-only view does not expose dropped graphs");
        originalOwner.Dispose();
        Ensure(authority.Bytes > 0 && ReferenceEquals(view.ResolveDeclaredDependency(regular.FullName!), regular) &&
            ReferenceEquals(original.ResolveDeclaredDependency(contract.FullName!), contract),
            "Share retains the original preparation, including its otherwise hidden contract graph charge");
        sharedOwner.Dispose();
        Ensure(authority.Bytes == 0 && original.RegistrationScope.Bytes == 0,
            "last owned release refunds all exact storage");
        Ensure(original.ResolveDeclaredDependency(regular.FullName!) is null &&
            original.ResolveDeclaredDependency(contract.FullName!) is null &&
            view.ResolveDeclaredDependency(regular.FullName!) is null,
            "escaped prepared objects no longer retain the added Assembly bindings after refund");
        originalOwner.Dispose(); sharedOwner.Dispose();
        Ensure(authority.Bytes == 0, "duplicate release is harmless");
    }

    [Test]
    public void ExactReservationRetainsPrivateBindingUntilItsLastLeaseAndClearsBeforeRefund()
    {
        var authority = new ColdMetadataAuthority();
        using var preparation = authority.CreateDomain().BeginPreparation();
        var scope = preparation.CreateRegistrationScope();
        var reservation = preparation.ReserveCollection(1, scope);
        Assembly?[] bindings = [typeof(string).Assembly];
        reservation.OwnDeclaredDependencyBindings(bindings);
        using var lease = reservation.Retain();
        preparation.Dispose();
        Ensure(bindings[0] is not null && scope.Bytes > 0 && reservation.IsDetached,
            "detached exact token retains both storage and charge");
        lease.Dispose();
        Ensure(bindings[0] is null && scope.Bytes == 0 && authority.Bytes == 0,
            "last token release clears its host-owned array and refunds exact charge");
        lease.Dispose(); reservation.Dispose(); preparation.Dispose();
        Ensure(authority.Bytes == 0, "repeat release cannot double refund");
    }

    [Test]
    public void UnresolvedDeclarationNeverRebindsAfterLaterAssemblyPublication()
    {
        var context = new BindingContext();
        try
        {
            var owner = Emit(context, $"BindingOwner.{Guid.NewGuid():N}", locator: true);
            var dependencyName = $"LateDependency.{Guid.NewGuid():N}";
            var identity = new AssemblyName(dependencyName) { Version = new Version(1, 0, 0, 0) }.FullName;
            var authority = new ColdMetadataAuthority();
            var domain = authority.CreateDomain();
            using var preparation = domain.BeginPreparation();
            var callbacks = 0;
            Exception? reentry = null;
            context.OnMissing = requested =>
            {
                if (requested.Name != dependencyName) return;
                callbacks++;
                try { using var illegal = domain.BeginPreparation(); }
                catch (Exception exception) { reentry = exception; }
            };
            var prepared = PreparedGeneratedManifest.Capture(new Manifest(owner, [identity], [identity]), preparation);
            Ensure(callbacks == 1 && prepared.ResolveDeclaredDependency(identity) is null,
                "matching regular/contract declarations resolve once, including unresolved identity");
            Ensure(reentry is ColdMetadataException { Code: SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState },
                "actual ALC callback runs within the existing same-domain capture callback scope");
            var dependency = Emit(context, dependencyName, locator: false);
            Ensure(ReferenceEquals(SharpLinkGeneratedDependencyBinding.Resolve(owner, identity), dependency),
                "the actual ALC can now resolve the subsequently loaded generation");
            Ensure(SharpLinkGeneratedDependencyBinding.ResolveDeclared(prepared, identity) is null &&
                !SharpLinkGeneratedDependencyBinding.ManifestDependsOn(prepared, dependency) && callbacks == 1,
                "prepared graph cannot silently acquire a later owner or invoke the resolver again");
            preparation.Dispose();
            Ensure(authority.Bytes == 0, "unresolved binding storage refunds independently");
        }
        finally { context.Unload(); }
    }

    [Test]
    public void PureCapacityUnionCountsRetainedIdentitiesWithoutDoubleCountingActiveRetirement()
    {
        // Pure admission arithmetic, NOT 4096 actual registrations or a runtime capacity workload.
        // Host identity dictionaries never call Assembly.Equals/GetHashCode/FullName overrides.
        var modules = new Dictionary<Assembly, int>(ReferenceEqualityComparer.Instance);
        var operations = new Dictionary<Assembly, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < 4095; index++) modules.Add(new IdentityAssembly(), index);
        var shared = modules.Keys.First();
        operations.Add(shared, 1);
        Ensure(SharpLinkModuleAdmissionCapacity.Count(modules, operations) == 4095,
            "active retirement counts once, with capacity for one more identity");
        var retained = new IdentityAssembly();
        operations.Add(retained, 2);
        Ensure(SharpLinkModuleAdmissionCapacity.Count(modules, operations) == 4096,
            "retained terminal outcome consumes the last existing admission slot");
        modules.Remove(shared);
        Ensure(SharpLinkModuleAdmissionCapacity.Count(modules, operations) == 4096,
            "physical module removal alone cannot refund its pending operation slot");
        operations.Remove(shared);
        Ensure(SharpLinkModuleAdmissionCapacity.Count(modules, operations) == 4095,
            "only exact operation removal releases the now-unshared identity slot");
    }

    private sealed class IdentityAssembly : Assembly
    {
        public override bool Equals(object? other) => throw new InvalidOperationException("Untrusted identity comparison.");
        public override int GetHashCode() => throw new InvalidOperationException("Untrusted identity hash.");
        public override string? FullName => throw new InvalidOperationException("Metadata callback during capacity count.");
    }

    private sealed class BindingContext() : AssemblyLoadContext($"PreparedBinding.{Guid.NewGuid():N}", isCollectible: true)
    {
        internal Action<AssemblyName>? OnMissing;
        protected override Assembly? Load(AssemblyName requested)
        {
            var existing = Default.Assemblies.FirstOrDefault(value => AssemblyName.ReferenceMatchesDefinition(value.GetName(), requested));
            if (existing is not null) return existing;
            OnMissing?.Invoke(requested);
            return null;
        }
    }
    private static Assembly Emit(BindingContext context, string name, bool locator)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = builder.DefineDynamicModule("Owner");
        var type = module.DefineType("OwnedManifest", TypeAttributes.Public | TypeAttributes.Sealed, typeof(LocatorManifest));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        var manifestType = type.CreateType();
        if (locator)
            builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(SharpLinkGeneratedAssemblyManifestAttribute).GetConstructor(
                [typeof(Type), typeof(int), typeof(int), typeof(string), typeof(string)])!,
                [manifestType, SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol,
                    "prepared-binding", SharpLinkGeneratedManifestVersions.AbiIdentity]));
        using var bytes = new MemoryStream(); builder.Save(bytes); bytes.Position = 0;
        return context.LoadFromStream(bytes);
    }
    public class LocatorManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "prepared-binding";
        public Assembly OwnerAssembly => GetType().Assembly;
        public string CompileTimeDescriptor => "prepared-binding";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } = [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; } = [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [];
        public IReadOnlyList<string> Dependencies { get; } = [];
    }
    private sealed class Manifest(Assembly owner, string[] dependencies, string[] contractDependencies) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "prepared-binding";
        public Assembly OwnerAssembly => owner;
        public string CompileTimeDescriptor => "prepared-binding";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } = [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; } = [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [];
        public IReadOnlyList<string> Dependencies { get; } = dependencies;
        public IReadOnlyList<string> ContractDependencies { get; } = contractDependencies;
    }
    private static void Ensure(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
