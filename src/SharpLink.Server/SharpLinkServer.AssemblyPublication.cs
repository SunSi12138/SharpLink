using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
        => RegisterAssemblyCore(assembly, captureReceipts: false, out _);

    public SharpLinkServerAssemblyRegistrationResult RegisterAssemblyWithReceipts(Assembly assembly)
    {
        var registration = RegisterAssemblyCore(assembly, captureReceipts: true, out var receipts);
        return new(registration, receipts);
    }

    public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
        Assembly oldAssembly,
        Assembly newAssembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
        => ReplaceAssemblyCore(oldAssembly, newAssembly, gracefulTimeout, cancellationToken,
            captureReceipts: false, out _);

    public ValueTask<SharpLinkServerAssemblyReplacementResult> ReplaceAssemblyWithReceiptsAsync(
        Assembly oldAssembly, Assembly newAssembly, TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        var replacement = ReplaceAssemblyCore(oldAssembly, newAssembly, gracefulTimeout, cancellationToken,
            captureReceipts: true, out var receipts);
        return AwaitReplacementReceiptsAsync(replacement, receipts);
    }

    private static async ValueTask<SharpLinkServerAssemblyReplacementResult> AwaitReplacementReceiptsAsync(
        ValueTask<SharpLinkAssemblyReplacementResult> replacement, IReadOnlyList<RpcRegistrationReceipt> receipts)
        => new(await replacement.ConfigureAwait(false), receipts);

    public RpcRegistrationReceipt GetRegistrationReceipt(System.Reflection.Assembly registeredAssembly, long contractId)
    {
        ArgumentNullException.ThrowIfNull(registeredAssembly);
        lock (_registryGate)
        {
            if (!_services.TryGetValue(contractId, out var registration) || !registration.AcceptsCalls ||
                !ReferenceEquals(registration.Module?.Manifest.OwnerAssembly ?? registration.ContractType.Assembly,
                    registeredAssembly))
                throw StaticBindingWire.Error(StaticBindingFailure.ReceiptMismatch);
            var manifest = FindManifest(registration.ContractType.Assembly, _dynamicModules.Values.ToArray());
            return new(_desiredSessionServerInstanceId, _bindingRegistryEpoch,
                registration.Module?.BindingPublicationGeneration ?? 0, contractId, manifest.RpcAssemblyHash,
                registration.StaticBindingRegistrationId);
        }
    }
    // Called under the registry publication gate, before any irreversible publication/cut.
    // All allocations/hash-owner validation finish here; the committed result retains no module.
    private IReadOnlyList<RpcRegistrationReceipt> CapturePublicationReceipts(
        IReadOnlyDictionary<long, ServiceRegistration> services, SharpLinkDynamicModule module,
        SharpLinkDynamicModule[] currentModules, long generation)
    {
        var owned = services.Where(pair => ReferenceEquals(pair.Value.Module, module))
            .OrderBy(static pair => pair.Key).ToArray();
        if (owned.Length == 0) return Array.Empty<RpcRegistrationReceipt>();
        var modules = currentModules.Append(module).ToArray();
        var receipts = new RpcRegistrationReceipt[owned.Length];
        for (var index = 0; index < owned.Length; index++)
        {
            var registration = owned[index].Value;
            var contract = FindManifest(registration.ContractType.Assembly, modules);
            receipts[index] = new(_desiredSessionServerInstanceId, _bindingRegistryEpoch, generation,
                owned[index].Key, contract.RpcAssemblyHash, registration.StaticBindingRegistrationId);
        }
        return Array.AsReadOnly(receipts);
    }

}
