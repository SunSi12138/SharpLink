namespace SharpLink.Server;

// Cold temporary framework staging. Header charges precede dictionary/entry growth, including
// service-less contracts. This does not waive the separate earlier Runtime manifest-capture gate.
internal sealed class ServerContractDomainStaging : IDisposable
{
    private readonly ServerDeclaredMethodFactsQuota _quota;
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly List<ServerDeclaredMethodFactsGroupReservation> _groups = [];
    internal ServerContractDomainStaging(ServerDeclaredMethodFactsQuota quota) => _quota = quota;
    internal IReadOnlyDictionary<long, Entry> Entries => _entries;

    internal void AddManifest(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        var source = manifest.Contracts;
        var count = source.Count;
        if (count < 0)
            throw Invalid("Contract count cannot be negative.");
        ServerDeclaredMethodFactsGroupReservation? group = null;
        var index = 0;
        try
        {
            foreach (var descriptor in source)
            {
                group ??= _quota.BeginGroup();
                _ = group.ReserveContract(); // Charge before any descriptor/dictionary staging.
                if (index >= count || descriptor is null || !ReferenceEquals(source[index], descriptor))
                    throw Invalid("Contract enumeration does not match its captured count and identity.");
                if (!_entries.TryAdd(descriptor.ContractId, new(descriptor, manifest, source, index, count)))
                    throw Invalid("Contract IDs are duplicated in the exact registration domain.");
                index++;
            }
            if (index != count || source.Count != count || !ReferenceEquals(manifest.Contracts, source))
                throw Invalid("Contract list changed during staging.");
            if (group is not null)
            {
                _groups.Add(group);
                group = null;
            }
        }
        finally
        {
            if (group is not null)
            {
                // Remove temporary descriptor roots before returning this failed staging charge.
                _entries.Clear();
                group.Dispose();
            }
        }
    }

    internal void Verify()
    {
        foreach (var item in _entries.Values)
            if (item.Source.Count != item.Count || !ReferenceEquals(item.Source[item.Index], item.Contract) ||
                !ReferenceEquals(item.Manifest.Contracts, item.Source))
                throw Invalid("Contract identity changed while its candidate was prepared.");
    }

    public void Dispose()
    {
        _entries.Clear();
        foreach (var group in _groups)
            group.Dispose();
        _groups.Clear();
    }

    private static ServerDeclaredMethodFactsException Invalid(string message)
        => new(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest, message);

    internal sealed record Entry(SharpLinkGeneratedContractDescriptor Contract,
        ISharpLinkGeneratedAssemblyManifest Manifest, IReadOnlyList<SharpLinkGeneratedContractDescriptor> Source,
        int Index, int Count);
}
