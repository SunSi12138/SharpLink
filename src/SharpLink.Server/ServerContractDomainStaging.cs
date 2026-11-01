namespace SharpLink.Server;

// Cross-manifest lookup workspace. It is a separate physical copy, not a second
// declared-method domain. Prepared descriptors are immutable and never reread live sources.
internal sealed class ServerContractDomainStaging : IDisposable
{
    private readonly ColdMetadataReservation _charge;
    private readonly Dictionary<long, Entry> _entries;
    internal ServerContractDomainStaging(ColdMetadataPreparation preparation)
    {
        _charge = preparation.ReserveCollection(0);
        _entries = [];
    }
    internal IReadOnlyDictionary<long, Entry> Entries => _entries;

    internal void AddManifest(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        if (manifest is not PreparedGeneratedManifest)
            throw new InvalidOperationException("Server domain staging requires the exact prepared manifest.");
        var source = manifest.Contracts;
        for (var index = 0; index < source.Count; index++)
        {
            var descriptor = source[index];
            _charge.GrowTo(checked(_entries.Count + 1));
            if (!_entries.TryAdd(descriptor.ContractId, new(descriptor, manifest)))
                throw new ServerDeclaredMethodFactsException(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    "Contract IDs are duplicated in the exact registration domain.");
        }
    }
    internal void Verify() { } // Runtime capture already froze every descriptor/list identity.
    public void Dispose()
    {
        _entries.Clear();
        _charge.Dispose();
    }
    internal sealed record Entry(SharpLinkGeneratedContractDescriptor Contract,
        ISharpLinkGeneratedAssemblyManifest Manifest);
}
