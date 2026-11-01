using System.Collections;
using System.Collections.Generic;

namespace SharpLink.UnitTests.Runtime;

public sealed class ColdMetadataAuthorityTests
{
    [Test]
    public void CollectionGrowthAndFinalBundleReleaseShouldChargeExactlyOnce()
    {
        var authority = new ColdMetadataAuthority();
        var preparation = authority.CreateDomain().BeginPreparation();
        Ensure(authority.Bytes == 256);
        var group = preparation.CreateRegistrationScope();
        var contract = group.CreateContractScope();
        var collection = preparation.ReserveCollection(2, contract);
        Ensure(authority.Bytes == 768 && contract.Bytes == 512 && group.Bytes == 512);
        collection.GrowTo(3);
        Ensure(authority.Bytes == 896);
        using var retained = preparation.Retain();
        preparation.Dispose();
        Ensure(authority.Bytes == 896);
        collection.Dispose();
        collection.Dispose();
        Ensure(authority.Bytes == 256 && group.Bytes == 0);
        retained.Dispose();
        Ensure(authority.Bytes == 0 && authority.Methods == 0);
    }

    [Test]
    public void DeclaredCountAndPhysicalCopyChargesShouldBeIndependent()
    {
        var authority = new ColdMetadataAuthority();
        using var preparation = authority.CreateDomain().BeginPreparation();
        var group = preparation.CreateRegistrationScope();
        var contract = group.CreateContractScope();
        using var count = preparation.ReserveMethods(ColdMetadataAuthority.ContractMethodLimit, contract);
        Ensure(authority.Methods == ColdMetadataAuthority.ContractMethodLimit);
        using var first = preparation.ReserveCollection(1, contract);
        using var second = preparation.ReserveCollection(1, contract);
        Ensure(contract.Methods == ColdMetadataAuthority.ContractMethodLimit && contract.Bytes == 768);
        contract.ClaimFactsTable();
        var duplicateFactsRejected = false;
        try { contract.ClaimFactsTable(); }
        catch (InvalidOperationException) { duplicateFactsRejected = true; }
        Ensure(duplicateFactsRejected);
        var error = Failure(() => preparation.ReserveMethods(1, contract));
        Ensure(error.Code == SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded);
        Ensure(contract.Methods == ColdMetadataAuthority.ContractMethodLimit && contract.Bytes == 768);
    }

    [Test]
    public async Task ChildClosingShouldJoinItsOwnerWithoutClosingSiblingAdmission()
    {
        var authority = new ColdMetadataAuthority();
        var root = authority.CreateDomain();
        var left = authority.CreateDomain(root);
        var right = authority.CreateDomain(root);
        var owner = left.BeginPreparation();
        var joined = left.SealAndJoinAsync();
        Ensure(!joined.IsCompleted);
        Ensure(Failure(() => left.BeginPreparation()).Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState);
        using var sibling = right.BeginPreparation();
        owner.Dispose();
        await joined;
        Ensure(authority.Bytes == 256);
        sibling.Dispose();
        await root.SealAndJoinAsync();
        Ensure(authority.Bytes == 0);
    }

    [Test]
    public void CallbackReentryShouldRejectBeforeSelfJoinOrNewAdmission()
    {
        var domain = new ColdMetadataAuthority().CreateDomain();
        using var preparation = domain.BeginPreparation();
        using (preparation.EnterCallback())
        {
            Ensure(Failure(() => domain.BeginPreparation()).Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState);
            Ensure(Failure(() => domain.SealAndJoinAsync()).Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState);
        }
        using var allowed = domain.BeginPreparation();
    }

    [Test]
    public void CountRejectionShouldPrecedeFrameworkAllocationAndItemCallbacks()
    {
        using var preparation = new ColdMetadataAuthority().CreateDomain().BeginPreparation();
        var oversized = new CountOnlyList(int.MaxValue);
        Ensure(Failure(() => ColdMetadataCollections.Copy(oversized, preparation)).Code ==
            SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded);
        var negative = new CountOnlyList(-1);
        Ensure(Failure(() => ColdMetadataCollections.Copy(negative, preparation)).Code ==
            SharpLinkAssemblyRegistrationErrorCode.InvalidManifest);
        Ensure(oversized.Reads == 0 && negative.Reads == 0 && preparation.Authority.Bytes == 256);
    }

    [Test]
    public void PendingRuntimePlanShouldReleaseOnAbandonmentAndRejectReplay()
    {
        var domain = new ColdMetadataAuthority().CreateDomain();
        var plan = new SharpLinkRuntimeContextBuilder().Compile(FixedGeneratedManifestSource.Empty, domain);
        Ensure(domain.Authority.Bytes != 0);
        plan.Dispose();
        plan.Dispose();
        Ensure(domain.Authority.Bytes == 0);
        try { plan.Materialize(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("A discarded plan materialized.");
    }

    private static ColdMetadataException Failure(Action action)
    {
        try { action(); }
        catch (ColdMetadataException exception) { return exception; }
        throw new Exception("Expected bounded metadata failure.");
    }
    private static void Ensure(bool condition)
    { if (!condition) throw new Exception("Cold metadata ownership invariant failed."); }
    private sealed class CountOnlyList(int count) : IReadOnlyList<string>
    {
        internal int Reads;
        public int Count => count;
        public string this[int index] { get { Reads++; throw new Exception("Indexer must not run."); } }
        public IEnumerator<string> GetEnumerator() { Reads++; throw new Exception("Enumerator must not run."); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
