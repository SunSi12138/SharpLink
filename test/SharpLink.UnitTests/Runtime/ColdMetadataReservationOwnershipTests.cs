using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

// Exact accounting/control-owner source tests; no GC, finalizer, timer, or model ledger.
public sealed class ColdMetadataReservationOwnershipTests
{
    [Test]
    public void OriginalPreparationDropReleasesEveryUnretainedReservationAndItsHeader()
    {
        var authority = new ColdMetadataAuthority();
        using var owner = authority.CreateDomain().BeginPreparation();
        var registration = owner.CreateRegistrationScope();
        var contract = registration.CreateContractScope();
        var unrelated = owner.ReserveCollection(4, registration);
        var table = owner.ReserveCollection(2, contract);
        var methods = owner.ReserveMethods(2, contract);
        using var tableLease = table.Retain();
        using var methodLease = methods.Retain();
        Ensure(authority.Bytes == 256 + 768 + 512 && authority.Methods == 2);

        owner.Dispose();

        Ensure(table.IsDetached && methods.IsDetached && unrelated.IsDetached);
        Ensure(authority.Bytes == 512 && authority.Methods == 2);
        Ensure(registration.Bytes == 512 && contract.Bytes == 512);
        Ensure(registration.Methods == 2 && contract.Methods == 2);
        MustThrow<ObjectDisposedException>(() => unrelated.Retain());
        tableLease.Dispose();
        Ensure(authority.Bytes == 0 && authority.Methods == 2);
        methodLease.Dispose();
        tableLease.Dispose(); methodLease.Dispose(); owner.Dispose(); table.Dispose(); methods.Dispose();
        Ensure(authority.Bytes == 0 && authority.Methods == 0 && registration.Bytes == 0 && contract.Methods == 0);
    }

    [Test]
    public void RetainedMiddleNodeDetachesFromBothNeighborsAndCannotGrowAfterOriginalDrop()
    {
        var authority = new ColdMetadataAuthority();
        using var owner = authority.CreateDomain().BeginPreparation();
        var left = owner.ReserveCollection(1);
        var middle = owner.ReserveCollection(2);
        var right = owner.ReserveCollection(3);
        using var first = middle.Retain();
        middle.Dispose();
        Ensure(middle.IsDetached && ReferenceEquals(right.Next, left) && ReferenceEquals(left.Previous, right));
        owner.Dispose();
        Ensure(left.IsDetached && right.IsDetached && authority.Bytes == 512);
        MustThrow<ObjectDisposedException>(() => middle.GrowTo(3));
        Ensure(authority.Bytes == 512);
        using var second = middle.Retain(); // A still-owned, frozen exact token may transfer again.
        first.Dispose();
        Ensure(authority.Bytes == 512);
        second.Dispose();
        MustThrow<ObjectDisposedException>(() => middle.Retain());
        Ensure(authority.Bytes == 0);
    }

    [Test]
    public void PartialLeaseTransferFailureCanRollbackWithoutStealingOriginalOwnership()
    {
        var authority = new ColdMetadataAuthority();
        using var owner = authority.CreateDomain().BeginPreparation();
        var scope = owner.CreateRegistrationScope().CreateContractScope();
        var table = owner.ReserveCollection(3, scope);
        var count = owner.ReserveMethods(3, scope);
        var abandonedWorkspace = owner.ReserveCollection(7, scope);
        count.Dispose(); // Deterministic failure at the second transfer, after the first succeeds.
        IDisposable? first = null;
        try
        {
            first = table.Retain();
            MustThrow<ObjectDisposedException>(() => count.Retain());
        }
        finally { first?.Dispose(); }
        Ensure(authority.Bytes == 256 + (256 + 3 * 128) + (256 + 7 * 128));
        Ensure(authority.Methods == 0 && !table.IsDetached);
        owner.Dispose();
        abandonedWorkspace.Dispose();
        Ensure(authority.Bytes == 0 && authority.Methods == 0 && scope.Bytes == 0);
    }

    [Test]
    public void LocalFactsLeaseSharesTheExactPreparedCountAndRollbackLeavesMetadataCountAlive()
    {
        var authority = new ColdMetadataAuthority();
        using var owner = authority.CreateDomain().BeginPreparation();
        var manifest = PreparedGeneratedManifest.Capture(new Manifest(2), owner);
        var token = manifest.GetContractMethodReservation(8);
        Ensure(ReferenceEquals(token, manifest.GetContractMethodReservation(8)));
        var before = authority.Bytes;
        var table = owner.ReserveCollection(2, manifest.GetContractScope(8));
        using (var tableLease = table.Retain())
        using (var methodsLease = token.Retain())
        {
            table.Dispose();
            Ensure(table.IsDetached && !token.IsDetached);
            Ensure(authority.Methods == 2 && authority.Bytes == before + 512);
        }
        Ensure(authority.Bytes == before && authority.Methods == 2 && !token.IsDetached);
        owner.Dispose();
        Ensure(token.IsDetached && authority.Methods == 0 && authority.Bytes == 0);
    }

    [Test]
    public void PreparedLocalTableOutlivesMetadataUsingOnlyItsTableAndCountTokens()
    {
        var authority = new ColdMetadataAuthority();
        using var owner = authority.CreateDomain().BeginPreparation();
        var manifest = PreparedGeneratedManifest.Capture(new Manifest(2), owner);
        var scope = manifest.GetContractScope(8);
        var methods = manifest.GetContractMethodReservation(8);
        var table = owner.ReserveCollection(2, scope);
        using var tableLease = table.Retain();
        using var methodLease = methods.Retain();
        table.Dispose();
        owner.Dispose();
        Ensure(table.IsDetached && methods.IsDetached);
        Ensure(authority.Bytes == 512 && authority.Methods == 2 && scope.Bytes == 512 && scope.Methods == 2);
        methodLease.Dispose();
        Ensure(authority.Methods == 0 && authority.Bytes == 512);
        tableLease.Dispose();
        Ensure(authority.Bytes == 0 && scope.Bytes == 0 && scope.Parent!.Bytes == 0);
    }

    [Test]
    public void ImportedAndReplacementDomainsStayFreshWhileEqualLocalTablesOverlap()
    {
        var authority = new ColdMetadataAuthority();
        var domain = authority.CreateDomain();
        using var oldOwner = domain.BeginPreparation();
        var oldManifest = PreparedGeneratedManifest.Capture(new Manifest(2), oldOwner);
        var oldCount = oldManifest.GetContractMethodReservation(8);
        var oldTable = oldOwner.ReserveCollection(2, oldManifest.GetContractScope(8));
        using var oldCountLease = oldCount.Retain();
        using var oldTableLease = oldTable.Retain();
        oldOwner.Dispose();

        using var newOwner = domain.BeginPreparation();
        var newManifest = PreparedGeneratedManifest.Capture(new Manifest(2), newOwner);
        var newCount = newManifest.GetContractMethodReservation(8);
        Ensure(!ReferenceEquals(oldCount, newCount) && authority.Methods == 4);
        var newTable = newOwner.ReserveCollection(2, newManifest.GetContractScope(8));
        using var newCountLease = newCount.Retain();
        using var newTableLease = newTable.Retain();
        newOwner.Dispose();

        using var importedOwner = domain.BeginPreparation();
        var importedScope = importedOwner.CreateRegistrationScope().CreateContractScope();
        var importedCount = importedOwner.ReserveMethods(2, importedScope);
        var importedTable = importedOwner.ReserveCollection(2, importedScope);
        using var importedCountLease = importedCount.Retain();
        using var importedTableLease = importedTable.Retain();
        importedOwner.Dispose();
        Ensure(authority.Methods == 6 && authority.Bytes == 3 * 512);
        Ensure(!ReferenceEquals(importedCount, oldCount) && !ReferenceEquals(importedCount, newCount));
        oldCountLease.Dispose(); oldTableLease.Dispose();
        Ensure(authority.Methods == 4 && authority.Bytes == 2 * 512);
        importedCountLease.Dispose(); importedTableLease.Dispose();
        Ensure(authority.Methods == 2 && authority.Bytes == 512);
        newCountLease.Dispose(); newTableLease.Dispose();
        Ensure(authority.Methods == 0 && authority.Bytes == 0);
    }

    private static void Ensure(bool condition)
    { if (!condition) throw new Exception("Exact cold reservation ownership invariant failed."); }
    private static void MustThrow<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }
    private interface IContract : IService { }
    private sealed class Manifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal Manifest(int count)
        {
            var methods = new SharpLinkGeneratedMethodDescriptor[count];
            for (var index = 0; index < count; index++)
                methods[index] = new("Method", index, RpcMethodKind.Unary, true, "request", "response", new string('a', 64));
            Contracts = [new(typeof(IContract), "Contract", 8, new string('a', 64), methods,
                static (_, _) => throw new Exception("Quota capture must not create proxies."),
                static _ => throw new Exception("Quota capture must not create stubs."))];
        }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "exact-reservation-owner-test";
        public Assembly OwnerAssembly => typeof(Manifest).Assembly;
        public RpcHash128 RpcAssemblyHash => new(1, 2);
        public string CompileTimeDescriptor => "exact-reservation-owner-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
        public IReadOnlyList<string> Dependencies => [];
        public IReadOnlyList<string> ContractDependencies => [];
    }
}
