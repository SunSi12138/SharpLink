namespace SharpLink.Client;

internal sealed partial class ClientAssemblyRegistry
{
    private const long NStudyStaticPublication = 1;
    private readonly Guid _nStudyInventoryEpoch = Guid.NewGuid();

    // Use the next committed registry revision after the initial static publication.
    private long NextNStudyPublication => _generation + 1 + NStudyStaticPublication;

    internal NStudyInventorySnapshot CaptureNStudyInventory(NStudyAuthority authority, int capacity)
    {
        NStudyInventoryEntry[] entries;
        long revision;
        lock (_gate)
        {
            revision = _generation;
            if (_shutdownStarted)
                return new(authority, _nStudyInventoryEpoch, revision, NStudyInventoryStatus.Unavailable, []);
            var proxies = _proxyLookup.Capture();
            if (proxies.Count > capacity)
                return new(authority, _nStudyInventoryEpoch, revision, NStudyInventoryStatus.CapacityExceeded, []);

            entries = new NStudyInventoryEntry[proxies.Count];
            var i = 0;
            foreach (var registration in proxies.Values)
            {
                // Static assets are published together at client construction. Dynamic
                // generation identity belongs to the module, not a cloned proxy entry.
                var target = registration.Module is { } module
                    ? new NStudyTarget(module.NStudyRegistration, module.NStudyPublication)
                    : new NStudyTarget(_nStudyInventoryEpoch, NStudyStaticPublication);
                entries[i++] = new(registration.Descriptor.ContractId,
                    registration.NStudyAssemblyHash, target);
            }
        }
        Array.Sort(entries, static (left, right) => left.Contract.CompareTo(right.Contract));
        return new(authority, _nStudyInventoryEpoch, revision, NStudyInventoryStatus.Current, entries);
    }
}
