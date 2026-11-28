using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class ClientAssemblyRegistry
{
    internal RpcLocalContract CaptureNStudyLocalContract(Type contractType) =>
        CreateNStudyLocalContract(ResolveNStudyLocalRegistration(contractType));

    internal SharpLinkClient.ClientProxyRegistration ResolveNStudyLocalRegistration(Type contractType)
    {
        lock (_gate)
        {
            if (_proxyLookup.TryGetValue(contractType, out var registration) && IsNStudyLocalCurrent(registration))
                return registration;
        }
        throw MissingNStudyLocalContract();
    }

    internal RpcLocalContract CaptureNStudyLocalContract(Assembly assembly, long contractId)
    {
        lock (_gate)
        {
            foreach (var registration in _proxyLookup.Capture().Values)
                if (registration.Descriptor.ContractId == contractId &&
                    ReferenceEquals(registration.Descriptor.ContractType.Assembly, assembly) && IsNStudyLocalCurrent(registration))
                    return CreateNStudyLocalContract(registration);
        }
        throw MissingNStudyLocalContract();
    }

    internal SharpLinkClient.ClientProxyRegistration ResolveNStudyLocalContract(RpcLocalContract local)
    {
        if (local.Issuer != _nStudyInventoryEpoch)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The local contract belongs to another client.");

        lock (_gate)
        {
            foreach (var registration in _proxyLookup.Capture().Values)
                if (registration.Descriptor.ContractId == local.ContractId && registration.NStudyAssemblyHash == local.Hash &&
                    ResolveNStudyLocalGeneration(registration) == local.Generation && IsNStudyLocalCurrent(registration))
                    return registration;
        }
        throw MissingNStudyLocalContract();
    }

    private RpcLocalContract CreateNStudyLocalContract(SharpLinkClient.ClientProxyRegistration registration) =>
        new(_nStudyInventoryEpoch, ResolveNStudyLocalGeneration(registration), registration.Descriptor.ContractId,
            registration.NStudyAssemblyHash);

    private static long ResolveNStudyLocalGeneration(SharpLinkClient.ClientProxyRegistration registration) =>
        registration.Module?.NStudyPublication ?? NStudyStaticPublication;

    private bool IsNStudyLocalCurrent(SharpLinkClient.ClientProxyRegistration registration) =>
        !_shutdownStarted && (registration.Module is null || registration.Module.State == SharpLinkDynamicModuleState.Running);

    private void CarryNStudyResumeReceipts(SharpLinkDynamicModule previous, SharpLinkDynamicModule replacement,
        FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> candidate)
    {
        if (previous.Manifest.RpcAssemblyHash != replacement.Manifest.RpcAssemblyHash)
            return;
        var sources = _proxyLookup.Capture().Values
            .Where(registration => ReferenceEquals(registration.Module, previous))
            .ToDictionary(registration => registration.Descriptor.ContractId);
        foreach (var registration in candidate.Values)
        {
            if (ReferenceEquals(registration.Module, replacement) &&
                sources.TryGetValue(registration.Descriptor.ContractId, out var source))
                registration.NStudyPreparation.InheritResumeReceipts(source.NStudyPreparation);
        }
    }

    private static SharpLinkException MissingNStudyLocalContract() =>
        new(SharpLinkErrorCode.Unavailable, "That local RPC contract registration is not current on this client.");
}
