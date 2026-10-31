namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private readonly Protocol3IdSequence _bindingRegistrationIds = new();

    private void InitializeBindingRegistrationsLocked()
    {
        if (_services.Count != 0 && _registryGeneration == 0)
            _registryGeneration = 1;
        PrepareBindingRegistrations(_services, _registryGeneration);
    }

    // Fallible cold preparation only. Call before closing any old registration group.
    private void PrepareBindingRegistrations(IReadOnlyDictionary<long, ServiceRegistration> services, long generation)
    {
        if (services.Count == 0)
            return;
        if (generation <= 0)
            throw new InvalidOperationException("A nonempty registration publication requires a positive generation.");
        foreach (var registrations in services.Where(static pair => pair.Value.Binding is null)
                     .GroupBy(static pair => pair.Value.ContractType.Assembly))
        {
            if (!_bindingRegistrationIds.TryIssue(out var registrationId))
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    "The server registration identity domain is exhausted; a new server epoch is required.");
            var first = registrations.First().Value;
            var manifest = first.Module?.Manifest ??
                _staticManifests.Single(item => ReferenceEquals(item.OwnerAssembly, registrations.Key));
            var group = new ServerBindingAdmissionGroup();
            var members = registrations.Select(pair => new ServerBindingRegistration(pair.Value,
                pair.Key, manifest.RpcAssemblyHash, registrationId, generation, group)).ToArray();
            group.Seal(members);
            var index = 0;
            foreach (var registration in registrations)
                registration.Value.Binding = members[index++];
        }
    }

    private static void RetireBindingGroup(ServiceRegistration[] registrations)
    {
        // Every member of one assembly registration shares one group. Repeated Retire is idempotent.
        foreach (var registration in registrations)
            registration.Binding?.Retire();
    }

    private long NextBindingPublicationGeneration()
    {
        if (_registryGeneration == long.MaxValue)
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                "The registry publication domain is exhausted; a new server epoch is required.");
        return _registryGeneration + 1;
    }
}
