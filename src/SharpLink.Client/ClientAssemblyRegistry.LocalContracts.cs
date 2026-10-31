using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class ClientAssemblyRegistry
{
    private readonly Guid _localIssuerId = Guid.NewGuid();
    private long _nextLocalRegistrationGeneration;

    private ClientLocalContractControl CreateLocalControl(long contractId, RpcHash128 hash)
    {
        if (hash.IsEmpty)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                SharpLinkErrorDetails.FailedPrecondition.InvalidLocalContract,
                "The local contract registration has an empty RPC assembly identity.");
        long generation;
        do
        {
            generation = Volatile.Read(ref _nextLocalRegistrationGeneration);
            if (generation == long.MaxValue)
                throw new InvalidOperationException("Local contract registration generations are exhausted.");
        }
        while (Interlocked.CompareExchange(ref _nextLocalRegistrationGeneration, generation + 1, generation) != generation);
        return new ClientLocalContractControl(new RpcLocalContract(_localIssuerId, generation + 1, contractId, hash));
    }

    internal RpcLocalContract GetLocalContract(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        lock (_gate)
        {
            ValidateLocalFactoryStateLocked();
            if (!_proxyLookup.Capture().TryGetValue(contractType, out var registration))
                throw ClientLocalContractControl.Unavailable();
            registration.LocalControl.ThrowIfRetired();
            return registration.LocalControl.Local;
        }
    }

    internal RpcLocalContract GetLocalContract(Assembly registeredAssembly, long contractId)
    {
        ArgumentNullException.ThrowIfNull(registeredAssembly);
        if (contractId == 0)
            throw new ArgumentOutOfRangeException(nameof(contractId));
        lock (_gate)
        {
            ValidateLocalFactoryStateLocked();
            SharpLinkClient.ClientProxyRegistration? selected = null;
            foreach (var registration in _proxyLookup.Capture().Values)
            {
                if (registration.Descriptor.ContractId != contractId ||
                    !ReferenceEquals(registration.Descriptor.ContractType.Assembly, registeredAssembly))
                    continue;
                if (selected is not null)
                    throw ClientLocalContractControl.Unavailable();
                selected = registration;
            }
            if (selected is null)
                throw ClientLocalContractControl.Unavailable();
            selected.LocalControl.ThrowIfRetired();
            return selected.LocalControl.Local;
        }
    }

    internal SharpLinkClient.ClientProxyRegistration AcquireLocalContract(
        RpcLocalContract local,
        out SharpLinkDynamicModuleLease moduleLease)
    {
        ArgumentNullException.ThrowIfNull(local);
        moduleLease = default;
        lock (_gate)
        {
            ValidateLocalFactoryStateLocked();
            if (local.IssuerId != _localIssuerId)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                    SharpLinkErrorDetails.FailedPrecondition.ForeignLocalContract,
                    "The local contract selector was issued by a different client.");
            foreach (var registration in _proxyLookup.Capture().Values)
            {
                var expected = registration.LocalControl.Local;
                if (expected.RegistrationGeneration != local.RegistrationGeneration)
                    continue;
                registration.LocalControl.ThrowIfRetired();
                if (expected.ContractId != local.ContractId || expected.RpcAssemblyHash != local.RpcAssemblyHash)
                    throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                        SharpLinkErrorDetails.FailedPrecondition.InvalidLocalContract,
                        "The exact local contract selector does not match its registered descriptor.");
                if (registration.Module is { } module && !module.TryAcquire(false, out moduleLease))
                    throw ClientLocalContractControl.Unavailable();
                return registration;
            }
            throw ClientLocalContractControl.Unavailable();
        }
    }

    // This is the factory's publication linearization point. User factories run outside _gate.
    internal void ValidateLocalFactoryPublication(SharpLinkClient.ClientProxyRegistration registration)
    {
        lock (_gate)
        {
            ValidateLocalFactoryStateLocked();
            registration.LocalControl.ThrowIfRetired();
            if (!_proxyLookup.Capture().TryGetValue(registration.Descriptor.ContractType, out var current) ||
                !ReferenceEquals(current, registration))
                throw ClientLocalContractControl.Unavailable();
        }
    }

    private void ValidateLocalFactoryStateLocked()
    {
        if (RejectMutationLocked(out var state))
            throw new InvalidOperationException($"Client state '{state}' does not accept local contract construction.");
    }

    private ClientLocalContractControl[] CaptureLocalControlsLocked(SharpLinkDynamicModule? module = null)
    {
        var controls = _proxyLookup.Capture().Values
            .Where(registration => module is null || ReferenceEquals(registration.Module, module))
            .Select(static registration => registration.LocalControl).ToArray();
        return controls;
    }

    private static void RetireLocalControls(ClientLocalContractControl[] controls)
    {
        foreach (var control in controls)
            control.Retire();
    }

    private static void NotifyLocalRetirement(ClientLocalContractControl[] controls)
    {
        foreach (var control in controls)
            control.NotifyRetired();
    }
}
