namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private void InitializeStaticRegistrationReceipts()
    {
        foreach (var pair in _services)
        {
            var hash = FindManifest(pair.Value.ContractType.Assembly, []).RpcAssemblyHash;
            pair.Value.PublishReceipt(CreateRegistrationReceipt(pair.Key, hash, generation: 0));
        }
    }

    private IReadOnlyList<ContractRegistrationReceipt> PublishRegistrationReceipts(
        FrozenDictionary<long, ServiceRegistration> services,
        SharpLinkDynamicModule module,
        long generation)
    {
        var receipts = new List<ContractRegistrationReceipt>();
        foreach (var pair in services.OrderBy(static pair => pair.Key))
        {
            if (!ReferenceEquals(pair.Value.Module, module))
                continue;
            var receipt = CreateRegistrationReceipt(pair.Key, pair.Value.RpcAssemblyHash, generation);
            pair.Value.PublishReceipt(receipt);
            receipts.Add(receipt);
        }
        return receipts.AsReadOnly();
    }

    private ContractRegistrationReceipt CreateRegistrationReceipt(long contractId, RpcHash128 hash, long generation)
        => new(
            _serviceModuleRegistry.ServerInstanceId,
            _serviceModuleRegistry.RegistryEpoch,
            checked((ulong)generation),
            contractId,
            hash,
            Guid.NewGuid());
}
