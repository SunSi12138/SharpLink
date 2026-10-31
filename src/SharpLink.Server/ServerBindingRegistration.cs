namespace SharpLink.Server;

/// <summary>One admission cut for an entire local registration transaction's contract group.</summary>
internal sealed class ServerBindingAdmissionGroup
{
    private long _state; // sign bit: retired; remaining bits: physical owners
    private ServerBindingRegistration[] _members = [];
    private bool _sealed;

    internal void Seal(ServerBindingRegistration[] members)
    {
        if (_sealed || members.Any(member => !ReferenceEquals(member.Group, this)))
            throw new InvalidOperationException("Binding group must be sealed once with its exact members.");
        _members = members;
        _sealed = true;
    }

    internal bool IsRetired => Volatile.Read(ref _state) < 0;
    internal long PhysicalOwnerCount => Volatile.Read(ref _state) & long.MaxValue;

    internal bool TryAcquire()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state < 0 || state == long.MaxValue)
                return false;
            if (Interlocked.CompareExchange(ref _state, state + 1, state) == state)
                return true;
        }
    }

    // Single cut precedes replacement publication; all fallible preparation happens before this.
    internal void Retire()
    {
        if (Interlocked.Or(ref _state, long.MinValue) < 0)
            return;
        foreach (var member in _members)
            member.DetachPayload();
        // The control group must not indefinitely retain a collectible payload through a sibling.
        _members = [];
    }

    internal void Release() => Interlocked.Decrement(ref _state);
}

/// <summary>Scalar contract identity plus the explicitly detachable selected payload.</summary>
internal sealed class ServerBindingRegistration
{
    private ServiceRegistration? _payload;

    internal ServerBindingRegistration(ServiceRegistration payload, long contractId, RpcHash128 hash,
        long registrationId, long publicationGeneration)
        : this(payload, contractId, hash, registrationId, publicationGeneration, new ServerBindingAdmissionGroup())
    {
        Group.Seal([this]);
    }

    internal ServerBindingRegistration(ServiceRegistration payload, long contractId, RpcHash128 hash,
        long registrationId, long publicationGeneration, ServerBindingAdmissionGroup group)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(group);
        if (contractId == 0 || hash.IsEmpty || registrationId <= 0 || publicationGeneration <= 0)
            throw new ArgumentException("A binding registration requires a complete positive identity.");
        _payload = payload;
        Group = group;
        ContractId = contractId;
        Hash = hash;
        RegistrationId = registrationId;
        PublicationGeneration = publicationGeneration;
    }

    internal ServerBindingAdmissionGroup Group { get; }
    internal long ContractId { get; }
    internal RpcHash128 Hash { get; }
    internal long RegistrationId { get; }
    internal long PublicationGeneration { get; }
    internal bool IsRetired => Group.IsRetired;
    internal long PhysicalOwnerCount => Group.PhysicalOwnerCount;

    internal bool TryAcquire(out ServerBindingLease lease)
    {
        if (!Group.TryAcquire())
        {
            lease = default;
            return false;
        }
        var payload = Volatile.Read(ref _payload);
        if (payload is null || Group.IsRetired)
        {
            Group.Release();
            lease = default;
            return false;
        }
        lease = new ServerBindingLease(this, payload);
        return true;
    }

    internal void Retire() => Group.Retire();
    internal void DetachPayload() => Interlocked.Exchange(ref _payload, null);
    internal void Release() => Group.Release();
}

// Owned by one dispatch, then transferred to its physical terminal owner. Never duplicate release.
internal readonly struct ServerBindingLease(ServerBindingRegistration control, ServiceRegistration registration)
{
    internal ServiceRegistration Registration { get; } = registration;
    internal ServerBindingRegistration Control { get; } = control;
    internal void Release() => Control.Release();
}
