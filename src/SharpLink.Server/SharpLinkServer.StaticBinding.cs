#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    internal StaticBindingBudget BindingBudget { get; } = new();
    internal Func<ServerConnectionState, ulong, StaticBindingAck, CancellationToken, ValueTask>? BeforeBindingAckTestHook;
    private async ValueTask BindStaticContractAsync(ServerConnectionState connection, ulong operation,
        ReadOnlySequence<byte> payload)
    {
        var request = StaticBindingWire.ReadRequest(payload);
        connection.StaticBindings.ObserveOperation(operation);
        using var control = BindingBudget.TryReserveOperation();
        if (control is null)
        {
            StaticBindingWire.SendCapacityAck(connection.Session, operation, request.ContractId);
            return;
        }
        var ack = CreateBindingAck(connection, request);
        // The entry is visible before success can be sent. Bind does not activate a service,
        // decode business bytes, reserve request capacity, or freeze authorization decisions.
        if (BeforeBindingAckTestHook is { } hook)
            await hook(connection, operation, ack, connection.ConnectionToken).ConfigureAwait(false);
        await StaticBindingWire.SendAckAsync(connection.Session, operation, ack, connection.ConnectionToken).ConfigureAwait(false);
    }

    private StaticBindingAck CreateBindingAck(ServerConnectionState connection,
        (long ContractId, RpcHash128 Hash) request)
    {
        StaticBindingAck ack;
        // Bind and group retirement share the same cold publication boundary.
        lock (_registryGate)
        {
            if (CurrentState != ServerState.Running || !connection.Session.CanAcceptCalls)
                ack = StaticBindingTable.Failure(StaticBindingFailure.Unavailable, request.ContractId);
            else if (!Volatile.Read(ref _services).TryGetValue(request.ContractId, out var registration))
                ack = StaticBindingTable.Failure(StaticBindingFailure.ContractMissing, request.ContractId);
            else if (!registration.AcceptsCalls)
                ack = StaticBindingTable.Failure(StaticBindingFailure.Retired, request.ContractId);
            else
            {
                var manifest = registration.Module?.Manifest ?? FindManifest(registration.ContractType.Assembly, []);
                if (request.Hash.IsEmpty || manifest.RpcAssemblyHash != request.Hash)
                    ack = StaticBindingTable.Failure(StaticBindingFailure.HashMismatch, request.ContractId);
                else
                {
                    var contract = manifest.Contracts.Single(c => c.ContractId == request.ContractId);
                    ack = connection.StaticBindings.Install(request.ContractId, registration, contract,
                        registration.Module?.BindingPublicationGeneration ?? 0);
                }
            }
        }
        return ack;
    }
}
#endif
