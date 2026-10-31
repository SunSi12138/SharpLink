namespace SharpLink.Server;

/// <summary>Cold publication/retirement metadata, never an RPC lifetime counter.</summary>
internal sealed class ServerBindingAdmissionGroup
{
    private int _retired;
    private ServerBindingRegistration[] _members = [];
    private bool _sealed;

    // Construction and retirement are serialized by the registry publication owner.
    internal void Seal(ServerBindingRegistration[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (_sealed || IsRetired || members.Any(member => member is null || !ReferenceEquals(member.Group, this)))
            throw new InvalidOperationException("Binding group must be sealed once with its exact members.");
        _members = (ServerBindingRegistration[])members.Clone();
        _sealed = true;
    }

    internal bool IsRetired => Volatile.Read(ref _retired) != 0;

    // The existing dynamic module Running cut precedes this cold metadata cut.
    // Static physical ownership remains with existing request permits, not this group.
    internal void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
            return;
        foreach (var member in _members)
            member.DetachPayloadAndFacts();
        _members = [];
    }
}

/// <summary>
/// Exact host-owned identity and weak payload selection. Capturing this control never
/// authorizes payload access or pins a collectible registration across policy waiting.
/// </summary>
internal sealed class ServerBindingRegistration
{
    private WeakReference<ServiceRegistration>? _payload;
    private ServerDeclaredMethodFacts? _facts;

    internal ServerBindingRegistration(ServiceRegistration payload, long contractId, RpcHash128 hash,
        long registrationId, long publicationGeneration, ServerDeclaredMethodFacts facts)
        : this(payload, contractId, hash, registrationId, publicationGeneration, facts, new ServerBindingAdmissionGroup())
    {
        Group.Seal([this]);
    }

    internal ServerBindingRegistration(ServiceRegistration payload, long contractId, RpcHash128 hash,
        long registrationId, long publicationGeneration, ServerDeclaredMethodFacts facts, ServerBindingAdmissionGroup group)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(group);
        if (contractId == 0 || hash.IsEmpty || registrationId <= 0 || publicationGeneration <= 0)
            throw new ArgumentException("A binding registration requires a complete positive identity.");
        _payload = new WeakReference<ServiceRegistration>(payload);
        _facts = facts;
        Group = group;
        ContractId = contractId;
        Hash = hash;
        RegistrationId = registrationId;
        PublicationGeneration = publicationGeneration;
        IsDynamicModule = payload.HasModule;
        ModuleCancellation = payload.ModuleCancellation;
    }

    internal ServerBindingAdmissionGroup Group { get; }
    internal long ContractId { get; }
    internal RpcHash128 Hash { get; }
    internal long RegistrationId { get; }
    internal long PublicationGeneration { get; }
    internal bool IsRetired => Group.IsRetired;
    internal bool IsDynamicModule { get; }
    // Existing cancellation infrastructure is not literally scalar. Its queued callbacks
    // must retain call state only, never the collectible registration/module payload.
    internal CancellationToken ModuleCancellation { get; }

    // Read once for synchronous lookup and retain only the selected RpcMethodFacts across
    // a wait. Retirement detaches the full table; its quota owner remains ServiceRegistration.
    internal ServerDeclaredMethodFacts? Facts => Volatile.Read(ref _facts);

    // Call only at the post-permit boundary. Recovery is identity, NOT a lease: dynamic
    // callers must still win the original module Running gate before decode/activation.
    internal bool TryGetExactPayload(out ServiceRegistration payload)
    {
        var weak = Volatile.Read(ref _payload);
        if (!IsRetired && weak is not null && weak.TryGetTarget(out var selected) &&
            ReferenceEquals(selected.Binding, this) && !IsRetired)
        {
            payload = selected;
            return true;
        }
        payload = null!;
        return false;
    }

    internal void Retire() => Group.Retire();

    internal void DetachPayloadAndFacts()
    {
        Interlocked.Exchange(ref _payload, null);
        Interlocked.Exchange(ref _facts, null);
    }
}
