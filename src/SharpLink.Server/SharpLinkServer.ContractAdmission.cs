namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private bool TryResolveContractRegistration(
        in ServerRequestEnvelope request,
        out ServiceRegistration registration,
        out bool identityMatches)
    {
        if (request.ContractConstraint.Match == ContractRegistrationMatch.Compatible)
        {
            var found = Volatile.Read(ref _services).TryGetValue(request.InterfaceHash, out registration!);
            identityMatches = found && !registration.ContractHash.IsEmpty && registration.ContractHash == request.ContractHash;
            return found;
        }

        // Capture the version and selected registration at one publication boundary. The exact
        // registration and bound codec travel with dispatch; module acquisition remains the
        // authoritative user-entry boundary if that registration concurrently starts retiring.
        lock (_registryGate)
        {
            var found = _services.TryGetValue(request.InterfaceHash, out registration!);
            var constraint = request.ContractConstraint;
            identityMatches = found && !registration.ContractHash.IsEmpty && registration.ContractHash == request.ContractHash &&
                constraint.ServerInstanceId == _serviceModuleRegistry.ServerInstanceId &&
                constraint.RegistryEpoch == _serviceModuleRegistry.RegistryEpoch &&
                _registryGeneration >= constraint.Generation &&
                (constraint.Match != ContractRegistrationMatch.ExactRegistration ||
                 constraint.RegistrationId == registration.RegistrationId);
            return found;
        }
    }

    private ContractRegistrationReceipt[] CreateRegistrationReceiptsLocked(
        SharpLinkDynamicModule module, FrozenDictionary<long, ServiceRegistration> services, long generation)
        => services.Where(pair => ReferenceEquals(pair.Value.Module, module)).OrderBy(static pair => pair.Key)
            .Select(pair => new ContractRegistrationReceipt(
                _serviceModuleRegistry.ServerInstanceId, _serviceModuleRegistry.RegistryEpoch,
                generation, pair.Key, pair.Value.ContractHash, pair.Value.RegistrationId)).ToArray();
}
