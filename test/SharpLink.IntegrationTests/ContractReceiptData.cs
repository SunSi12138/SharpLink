namespace SharpLink.IntegrationTests;

// An application-owned DTO intentionally transports the receipt over an independent control RPC.
// No runtime object, reflection handle or out-of-band Task result crosses into the data client.
[SharpPackable]
public sealed partial class ContractReceiptData
{
    [SharpPackOrder(0)] public Guid ServerInstanceId { get; set; }
    [SharpPackOrder(1)] public Guid RegistryEpoch { get; set; }
    [SharpPackOrder(2)] public ulong Generation { get; set; }
    [SharpPackOrder(3)] public long ContractId { get; set; }
    [SharpPackOrder(4)] public ulong HashHigh { get; set; }
    [SharpPackOrder(5)] public ulong HashLow { get; set; }
    [SharpPackOrder(6)] public Guid RegistrationId { get; set; }

    internal static ContractReceiptData FromReceipt(ContractRegistrationReceipt receipt) => new()
    {
        ServerInstanceId = receipt.ServerInstanceId,
        RegistryEpoch = receipt.RegistryEpoch,
        Generation = receipt.Generation,
        ContractId = receipt.ContractId,
        HashHigh = receipt.RpcAssemblyHash.High,
        HashLow = receipt.RpcAssemblyHash.Low,
        RegistrationId = receipt.RegistrationId
    };

    internal ContractRegistrationReceipt ToReceipt() => new(
        ServerInstanceId, RegistryEpoch, Generation, ContractId,
        new RpcHash128(HashHigh, HashLow), RegistrationId);
}
