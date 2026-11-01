using System.Reflection;

namespace SharpLink.Server;

// Private Server planning ownership, charged to the exact Runtime preparation from capture onward.
internal sealed class ServerStaticFactsPreparation : IDisposable
{
    private readonly Dictionary<Assembly, ServerDeclaredMethodFactsGroupReservation> _groups;
    private readonly Dictionary<long, ContractEntry> _contracts;
    private bool _sealed;
    private bool _disposed;

    private readonly ColdMetadataPreparation _preparation;
    private readonly IDisposable _bundle;
    private readonly ColdMetadataReservation _groupsCharge;
    private readonly ColdMetadataReservation _contractsCharge;
    internal ServerDeclaredMethodFactsQuota Quota { get; }

    private ServerStaticFactsPreparation(ColdMetadataPreparation preparation)
    {
        _preparation = preparation;
        Quota = new(preparation.Domain);
        _groupsCharge = preparation.ReserveCollection(0);
        _contractsCharge = preparation.ReserveCollection(0);
        _groups = new(ReferenceEqualityComparer.Instance);
        _contracts = [];
        _bundle = preparation.Retain();
    }
    internal ColdMetadataPreparation Preparation => _preparation;
    private ColdMetadataReservation? _servicePlansCharge;
    private ColdMetadataReservation? _runtimeServicesCharge;
    private ColdMetadataReservation? _runtimeServicesWorkspaceCharge;
    internal ColdMetadataReservation TakeRuntimeServicesCharge()
        => Interlocked.Exchange(ref _runtimeServicesCharge, null) ?? throw new InvalidOperationException("Service map charge already transferred.");
    internal void ReleaseRuntimeServiceWorkspace() => Interlocked.Exchange(ref _runtimeServicesWorkspaceCharge, null)?.Dispose();
    internal ServerServiceRegistrationPlanEntry[] CaptureServicePlans(List<ServerServiceRegistrationPlanEntry> source)
    {
        _servicePlansCharge = _preparation.ReserveCollection(source.Count);
        return [.. source];
    }
    internal ColdMetadataReservation TakeServicePlansCharge()
        => Interlocked.Exchange(ref _servicePlansCharge, null) ??
            throw new InvalidOperationException("Service planning array charge was already transferred.");
    internal int ContractCount => _contracts.Count;
    internal IEnumerable<ContractEntry> Contracts => _contracts.Values;

    internal static ServerStaticFactsPreparation Capture(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests, ColdMetadataPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var owner = new ServerStaticFactsPreparation(preparation);
        try
        {
            // manifests is the existing Runtime-owned frozen outer list. Never allocate an
            // array/dictionary from an externally supplied contract Count.
            foreach (var manifest in manifests)
            {
                var contracts = manifest.Contracts;
                var count = contracts.Count;
                if (count < 0)
                    throw Invalid("Static contract count cannot be negative.");
                var index = 0;
                foreach (var contract in contracts)
                {
                    if (index == count)
                        throw Invalid("Static contract enumeration exceeds its captured count.");
                    if (contract is null || !ReferenceEquals(contracts[index], contract))
                        throw Invalid("Static contract enumeration and indexed identity disagree.");
                    var assembly = manifest.OwnerAssembly;
                    if (!owner._groups.TryGetValue(assembly, out var group))
                    {
                        owner._groupsCharge.GrowTo(checked(owner._groups.Count + 1));
                        group = owner.Quota.BeginGroup((PreparedGeneratedManifest)manifest);
                        try { owner._groups.Add(assembly, group); }
                        catch { group.Dispose(); throw; }
                    }
                    owner._contractsCharge.GrowTo(checked(owner._contracts.Count + 1));
                    var reservation = group.ReserveContract(contract.ContractId);
                    // This entry and dictionary growth are covered by the already reserved header.
                    var entry = new ContractEntry(contract, manifest, reservation, contracts, index, count);
                    if (!owner._contracts.TryAdd(contract.ContractId, entry))
                        throw Invalid("Static contract IDs must be unique.", contract.ContractId);
                    index++;
                }
                if (index != count || contracts.Count != count || !ReferenceEquals(manifest.Contracts, contracts))
                    throw Invalid("Static contract count/list identity changed during capture.");
            }
            owner.VerifySnapshot();
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    internal void SealForPlan(ServerServiceRegistrationPlanEntry[] services)
    {
        if (_disposed || _sealed)
            throw new InvalidOperationException("Static facts preparation has already been sealed.");
        VerifySnapshot();
        _runtimeServicesCharge = _preparation.ReserveCollection(services.Length);
        _runtimeServicesWorkspaceCharge = _preparation.ReserveCollection(services.Length);
        foreach (var service in services)
        {
            if (!_contracts.TryGetValue(service.ContractId, out var entry) ||
                !ReferenceEquals(entry.Contract!.ContractType, service.Registration.ContractType))
                throw Invalid("Selected service does not match its exact declared contract.", service.ContractId);
            entry.Reservation.SetMethodCount(entry.Contract.Methods.Count);
        }
        // Dictionary removal during enumeration is supported by the target runtime. Clear the
        // descriptor roots before returning unused header charges; no uncharged snapshot escapes.
        foreach (var pair in _contracts)
        {
            var entry = pair.Value;
            if (entry.Reservation.CountAssigned)
                continue;
            entry.Clear();
            _contracts.Remove(pair.Key);
            entry.Reservation.Group.DiscardContract(entry.Reservation);
        }
        foreach (var pair in _groups)
        {
            if (!pair.Value.IsEmpty)
                continue;
            _groups.Remove(pair.Key);
            pair.Value.Dispose();
        }
        foreach (var entry in _contracts.Values)
            entry.DetachSnapshot();
        foreach (var group in _groups.Values)
            group.Seal();
        _sealed = true;
    }

    internal void Prepare(long contractId, ServiceRegistration registration)
    {
        if (!_sealed || _disposed || !_contracts.TryGetValue(contractId, out var entry))
            throw new InvalidOperationException("Static facts must use the sealed selected contract domain.");
        if (registration.DeclaredFacts is not null)
            throw new InvalidOperationException("A registration cannot replay declared-facts preparation.");
        registration.DeclaredFacts = ServerDeclaredMethodFacts.Prepare(entry.Contract!, registration.Stub, entry.Reservation);
    }

    internal void Commit()
    {
        if (!_sealed || _disposed)
            throw new InvalidOperationException("Static facts ownership is not ready to publish.");
        foreach (var group in _groups.Values)
        {
            group.PrepareCommit();
            group.Commit();
        }
        Dispose(); // Groups have transferred their charges to the prepared tables.
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var entry in _contracts.Values)
            entry.Clear();
        _contracts.Clear();
        foreach (var group in _groups.Values)
            group.Dispose();
        _groups.Clear();
        _contractsCharge.Dispose();
        _groupsCharge.Dispose();
        _servicePlansCharge?.Dispose();
        _runtimeServicesCharge?.Dispose();
        ReleaseRuntimeServiceWorkspace();
        _bundle.Dispose();
    }

    private static ServerDeclaredMethodFactsException Invalid(string message, long? contractId = null)
        => new(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest, message, contractId);

    private void VerifySnapshot()
    {
        foreach (var entry in _contracts.Values)
            entry.VerifySnapshot();
    }

    internal sealed class ContractEntry(SharpLinkGeneratedContractDescriptor contract,
        ISharpLinkGeneratedAssemblyManifest manifest, ServerDeclaredMethodFactsReservation reservation,
        IReadOnlyList<SharpLinkGeneratedContractDescriptor> source, int index, int count)
    {
        private IReadOnlyList<SharpLinkGeneratedContractDescriptor>? _source = source;
        internal SharpLinkGeneratedContractDescriptor? Contract { get; private set; } = contract;
        internal ISharpLinkGeneratedAssemblyManifest? Manifest { get; private set; } = manifest;
        internal ServerDeclaredMethodFactsReservation Reservation { get; } = reservation;
        internal void VerifySnapshot()
        {
            if (_source is null || _source.Count != count || !ReferenceEquals(_source[index], Contract) ||
                !ReferenceEquals(Manifest!.Contracts, _source))
                throw Invalid("Static contract identity/count changed after capture.", Contract?.ContractId);
        }
        internal void DetachSnapshot() { _source = null; Manifest = null; }
        internal void Clear() { Contract = null; DetachSnapshot(); }
    }
}
