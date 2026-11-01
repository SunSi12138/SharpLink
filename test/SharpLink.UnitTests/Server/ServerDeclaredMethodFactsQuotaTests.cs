using SharpLink.Sdk;
using System.Reflection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

// Current combined-staging ledger. Exact earlier facts-only oracles are preserved under
// fixtures/pre-staging-facts-only; these do not claim the old 384-byte total is still valid.
public sealed class ServerDeclaredMethodFactsQuotaTests
{
    [Test]
    public void LocalFactsReuseOnlyTheExactPreparedMethodDomainButChargeBothArrays()
    {
        var authority = new ColdMetadataAuthority();
        using var preparation = authority.CreateDomain().BeginPreparation();
        var manifest = PreparedGeneratedManifest.Capture(new Manifest(1), preparation);
        var quota = new ServerDeclaredMethodFactsQuota(preparation.Domain);
        var before = authority.Bytes;
        using var group = quota.BeginGroup(manifest);
        var reservation = group.ReserveContract(8);
        reservation.SetMethodCount(1);
        // group List header+slot, retained facts array, simultaneous descriptor workspace.
        Ensure(authority.Bytes == before + 384 + 384 + 384);
        Ensure(authority.Methods == 1 && manifest.GetContractScope(8).Methods == 1);
        group.Dispose();
        Ensure(authority.Bytes == before && authority.Methods == 1);
        preparation.Dispose();
        Ensure(authority.Bytes == 0 && authority.Methods == 0);
    }

    [Test]
    public void CombinedContractBoundaryIncludesMetadataFactsAndTemporaryCopy()
    {
        const int boundary = (int)((ColdMetadataAuthority.ContractByteLimit - 3 * ColdMetadataAuthority.HeaderCharge) /
            (3 * ColdMetadataAuthority.SlotCharge));
        Verify(boundary, succeeds: true);
        Verify(boundary + 1, succeeds: false);
        static void Verify(int count, bool succeeds)
        {
            var authority = new ColdMetadataAuthority();
            using var preparation = authority.CreateDomain().BeginPreparation();
            var manifest = PreparedGeneratedManifest.Capture(new Manifest(count), preparation);
            using var group = new ServerDeclaredMethodFactsQuota(preparation.Domain).BeginGroup(manifest);
            var reservation = group.ReserveContract(8);
            try
            {
                reservation.SetMethodCount(count);
                Ensure(succeeds);
                Ensure(manifest.GetContractScope(8).Bytes == 3 * (256UL + 128UL * (ulong)count));
            }
            catch (ColdMetadataException error)
            { Ensure(!succeeds && error.Code == SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded); }
            group.Dispose();
            preparation.Dispose();
            Ensure(authority.Methods == 0 && authority.Bytes == 0);
        }
    }

    [Test]
    public void ImportedFactsAndReplacementKeepIndependentMethodDomainsAndRollback()
    {
        var authority = new ColdMetadataAuthority();
        var domain = authority.CreateDomain();
        using var dependencyOwner = domain.BeginPreparation();
        var dependency = PreparedGeneratedManifest.Capture(new Manifest(1), dependencyOwner);
        using var incomingOwner = domain.BeginPreparation();
        var incoming = PreparedGeneratedManifest.Capture(new Manifest(0, hasContract: false), incomingOwner);
        using var group = new ServerDeclaredMethodFactsQuota(domain).BeginGroup(incoming);
        group.ReserveContract(8).SetMethodCount(1);
        Ensure(authority.Methods == 2 && dependency.GetContractScope(8).Methods == 1 && incoming.RegistrationScope.Methods == 1);
        group.Dispose();
        Ensure(authority.Methods == 1 && incoming.RegistrationScope.Methods == 0);
        incomingOwner.Dispose();
        dependencyOwner.Dispose();
        Ensure(authority.Methods == 0 && authority.Bytes == 0);
    }

    [Test]
    public void EmptyFactsStillChargeEveryLiveCollectionAndReleaseExactlyOnce()
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        var unused = group.ReserveContract();
        // preparation header256 + List(header256+slot128) + empty table256.
        Ensure(quota.Bytes == 896 && quota.Methods == 0);
        group.DiscardContract(unused);
        Ensure(quota.Bytes == 640); // List reserved capacity remains until owner disposal.
        group.Dispose();
        group.Dispose();
        Ensure(quota.Bytes == 0 && quota.Methods == 0);
    }

    [Test]
    public void BoundLocalFactsKeepOnlyTheirExactArrayAndSharedMethodTokenAfterMetadataCleanup()
    {
        var authority = new ColdMetadataAuthority();
        var preparation = authority.CreateDomain().BeginPreparation();
        var manifest = PreparedGeneratedManifest.Capture(new Manifest(1), preparation);
        var group = new ServerDeclaredMethodFactsQuota(preparation.Domain).BeginGroup(manifest);
        var reservation = group.ReserveContract(8);
        reservation.SetMethodCount(1);
        group.Seal();
        using var facts = ServerDeclaredMethodFacts.Prepare(manifest.Contracts[0], new FactsStub(), reservation);
        group.PrepareCommit();
        group.Commit();
        group.Dispose();
        using var bound = facts.RetainForBinding();
        facts.Dispose(); // Actual service owner ends before its intake acknowledgment.
        manifest = null!;
        preparation.Dispose(); // Metadata's original shared count token ends, not the binding's.
        Ensure(authority.Bytes == 384 && authority.Methods == 1);
        Ensure(bound.Resolve(0).IsKnown);
        bound.Dispose();
        Ensure(authority.Bytes == 0 && authority.Methods == 0);
        facts.Dispose();
        Ensure(authority.Bytes == 0 && authority.Methods == 0);
    }

    [Test]
    public void PartialPrepareCommitFailureReturnsAlreadyRetainedTokensOnRollback()
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        var first = group.ReserveContract(); first.SetMethodCount(1);
        var second = group.ReserveContract(); second.SetMethodCount(1);
        group.Seal();
        var descriptor = new Manifest(1).Contracts[0];
        using var firstFacts = ServerDeclaredMethodFacts.Prepare(descriptor, new FactsStub(), first);
        using var secondFacts = ServerDeclaredMethodFacts.Prepare(descriptor, new FactsStub(), second);
        // Exact internal failure control: second token is already final-refunded, so
        // first table/count and second table retention must all unwind without Commit.
        second.MethodCharge!.Dispose();
        try { group.PrepareCommit(); throw new Exception("Expected failed token transfer."); }
        catch (ObjectDisposedException) { }
        firstFacts.Dispose(); secondFacts.Dispose(); group.Dispose();
        Ensure(quota.Bytes == 0 && quota.Methods == 0);
    }

    private sealed class FactsStub : IRpcStub
    {
        public long InterfaceHash => 8;
        public RpcMethodFacts ResolveMethodFacts(long id) => new(8, id, RpcMethodKind.Unary, 0, true, true);
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args) => throw new Exception("No business callback.");
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => throw new Exception("No business callback.");
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => throw new Exception("No business callback.");
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => throw new Exception("No business callback.");
    }

    private static void Ensure(bool condition)
    { if (!condition) throw new Exception("Shared cold metadata ledger assertion failed."); }

    private interface IContract : IService { }
    private sealed class Manifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal Manifest(int methods, bool hasContract = true)
        {
            var descriptors = new SharpLinkGeneratedMethodDescriptor[methods];
            for (var index = 0; index < descriptors.Length; index++)
                descriptors[index] = new("Method", index, RpcMethodKind.Unary, true, "request", "response", new string('a', 64));
            Contracts = hasContract ? [new(typeof(IContract), "Contract", 8, new string('a', 64), descriptors,
                static (_, _) => throw new Exception("No proxy callback during quota preflight."),
                static _ => throw new Exception("No stub callback during quota preflight."))] : [];
        }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "cold-ledger-test";
        public Assembly OwnerAssembly => typeof(Manifest).Assembly;
        public RpcHash128 RpcAssemblyHash => new(1, 2);
        public string CompileTimeDescriptor => "cold-ledger-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
