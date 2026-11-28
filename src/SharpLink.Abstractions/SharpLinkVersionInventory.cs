namespace SharpLink.Abstractions;

/// <summary>Identifies one physical session, independently of the contracts published on it.</summary>
/// <remarks>This value is a selector, not proof of authentication or permission to make calls.</remarks>
public readonly record struct SharpLinkVersionSession(Guid ServerInstanceId, Guid ServerRegistryEpoch, Guid PhysicalSessionId);

/// <summary>Describes the exact published registration of an RPC contract.</summary>
public readonly record struct SharpLinkVersionRegistration(
    long ContractId, RpcHash128 AssemblyHash, Guid RegistrationId, long PublicationGeneration);

/// <summary>A read-only snapshot of one endpoint's published contracts.</summary>
public sealed class SharpLinkVersionInventory
{
    internal SharpLinkVersionInventory(SharpLinkVersionSession? session, Guid publisherEpoch, long revision,
        SharpLinkVersionRegistration[] registrations)
    {
        Session = session;
        PublisherEpoch = publisherEpoch;
        Revision = revision;
        Registrations = Array.AsReadOnly(registrations);
    }

    /// <summary>Gets the physical session used for a peer query, or null for a local read.</summary>
    public SharpLinkVersionSession? Session { get; }

    /// <summary>Gets the epoch of the inventory publisher, which can differ from the server's registry epoch.</summary>
    public Guid PublisherEpoch { get; }

    /// <summary>Gets the inventory revision within <see cref="PublisherEpoch"/>.</summary>
    public long Revision { get; }

    /// <summary>Gets the published registrations, ordered by contract ID.</summary>
    public IReadOnlyList<SharpLinkVersionRegistration> Registrations { get; }
}
