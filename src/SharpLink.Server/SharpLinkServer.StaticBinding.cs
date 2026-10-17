#if SHARPLINK_STATIC_BINDING_PROTOTYPE
namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ValueTask BindStaticContractAsync(ServerConnectionState connection, ulong operation,
        ReadOnlySequence<byte> payload)
    {
        var request = StaticBindingWire.ReadRequest(payload);
        StaticBindingAck ack;
        // Shares the existing registration publication boundary. Static registrations cannot
        // be runtime replaced/unregistered; dynamic registrations are explicitly rejected.
        lock (_registryGate)
        {
            if (CurrentState != ServerState.Running || !connection.Session.CanAcceptCalls)
                ack = StaticBindingTable.Failure(StaticBindingFailure.Unavailable, request.ContractId);
            else if (!Volatile.Read(ref _services).TryGetValue(request.ContractId, out var registration))
                ack = StaticBindingTable.Failure(StaticBindingFailure.ContractMissing, request.ContractId);
            else if (registration.HasModule)
                ack = StaticBindingTable.Failure(StaticBindingFailure.DynamicUnsupported, request.ContractId);
            else
            {
                var manifest = FindManifest(registration.ContractType.Assembly, []);
                if (request.Hash.IsEmpty || manifest.RpcAssemblyHash != request.Hash)
                    ack = StaticBindingTable.Failure(StaticBindingFailure.HashMismatch, request.ContractId);
                else
                {
                    var contract = manifest.Contracts.Single(c => c.ContractId == request.ContractId);
                    ack = connection.StaticBindings.Install(request.ContractId, registration, contract,
                        _registryGeneration);
                }
            }
        }
        // The entry is visible before success can be sent. Bind does not activate a service,
        // decode business bytes, reserve request capacity, or freeze authorization decisions.
        return StaticBindingWire.SendAckAsync(connection.Session, operation, ack, connection.ConnectionToken);
    }

    private static StaticBindingFailure ResolveStaticRequest(ServerConnectionState connection,
        ProtocolV2FrameFlags flags, ref ServerRequestEnvelope request, out ServiceRegistration registration)
    {
        var failure = connection.StaticBindings.Resolve(unchecked((ulong)request.InterfaceHash),
            request.MethodHash, flags, out registration, out var method);
        if (failure == StaticBindingFailure.UnsupportedShape)
            throw StaticBindingWire.Malformed("Static binding prototype rejects conflicting/streaming call shapes.");
        if (failure == StaticBindingFailure.None) request = request with { BoundMethod = method };
        return failure;
    }
}
#endif
