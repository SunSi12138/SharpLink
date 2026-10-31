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

    private ServerBindingAdmissionGroup[] CaptureModuleBindingGroupsLocked(SharpLinkDynamicModule module)
    {
        var registrations = _detachedModuleServices.TryGetValue(module, out var detached)
            ? detached
            : _services.Values.Where(service => ReferenceEquals(service.Module, module)).ToArray();
        return registrations.Where(static service => service.Binding is not null)
            .Select(static service => service.Binding!.Group).Distinct().ToArray();
    }

    private static Task BindingGroupsDrained(ServerBindingAdmissionGroup[] groups)
    {
        if (groups.Any(static group => !group.IsRetired))
            throw new InvalidOperationException("Binding groups must be retired before their payloads can be released.");
        return Task.WhenAll(groups.Select(static group => group.WaitForDrainAsync()));
    }

    private static int RemainingBindingCalls(ServerBindingAdmissionGroup[] groups)
    {
        long remaining = 0;
        foreach (var group in groups)
        {
            remaining += Math.Min(int.MaxValue, group.PhysicalOwnerCount);
            if (remaining >= int.MaxValue)
                return int.MaxValue;
        }
        return (int)remaining;
    }

    private long NextBindingPublicationGeneration()
    {
        if (_registryGeneration == long.MaxValue)
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                "The registry publication domain is exhausted; a new server epoch is required.");
        return _registryGeneration + 1;
    }
}
