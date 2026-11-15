namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private readonly Guid _bindingRegistryEpoch = Guid.NewGuid();
    private readonly ContractBindingQuota _bindingQuota = new();
    private long _nextStaticRegistrationId;
    internal ContractBindingDiagnosticSnapshot CaptureContractBindingDiagnostics()
        => new(_bindingQuota.Bindings, _bindingQuota.Operations, _bindingQuota.ProtectedRequests, 0, 0, 0);

    private async ValueTask HandleBindContractAsync(ServerConnectionState connection, ulong operation,
        ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        connection.Bindings.AcceptOperation(operation);
        var request = ContractBindingWire.ReadRequest(payload);
        if (!_bindingQuota.TryReserveOperation())
        {
            _ = ContractBindingWire.TrySend(connection.Session, ProtocolV2FrameType.BindAck, operation,
                ContractBindingWire.WriteAck(new(ContractBindStatus.Capacity, request.ContractId, 0, 0, 0)));
            return;
        }
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), _runtimeContext.TimeProvider);
        var sendOwnsReconciliation = false;
        try
        {
            var ack = PrepareStaticBinding(connection, request);
            if (deadline.IsExpired(_runtimeContext.TimeProvider)) throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Binding operation deadline exceeded.");
            sendOwnsReconciliation = true;
            await ContractBindingWire.SendAndFlushWithinBudgetAsync(connection.Session, ProtocolV2FrameType.BindAck, operation,
                ContractBindingWire.WriteAck(ack), deadline, _runtimeContext.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!sendOwnsReconciliation)
        {
            connection.Session.NotifyDisconnected(exception);
            try { await connection.Session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(exception, cleanup); }
            throw;
        }
        finally { _bindingQuota.ReturnOperation(); }
    }

    private ContractBindAck PrepareStaticBinding(ServerConnectionState connection, ContractBindRequest request)
    {
        ContractBindAck Reject(ContractBindStatus status) => new(status, request.ContractId, 0, 0, 0);
        ServiceRegistration service; ISharpLinkGeneratedAssemblyManifest manifest; long registrationId;
        ServiceRegistration.BindingMetadataRead metadataRead;
        lock (_lifecycle.StateGate)
        lock (_registryGate)
        {
            if (CurrentState != ServerState.Running) return Reject(ContractBindStatus.Unavailable);
            if (!_services.TryGetValue(request.ContractId, out service!)) return Reject(ContractBindStatus.ContractMissing);
            if (service.HasModule) return Reject(ContractBindStatus.DynamicUnsupported);

            registrationId = service.BindingRegistrationId;
            metadataRead = service.AcquireBindingMetadataRead();
        }
        using var metadataProtection = metadataRead;
        manifest = _staticManifests.Single(m => m.Contracts.Any(c => c.ContractId == request.ContractId && c.ContractType == service.ContractType));
        if (manifest.RpcAssemblyHash != request.Hash) return Reject(ContractBindStatus.HashMismatch);
        if (request.Receipt is { } receipt &&
            (receipt.ServerInstanceId != _desiredSessionServerInstanceId || receipt.RegistryEpoch != _bindingRegistryEpoch ||
             request.Requirement != RpcRegistrationRequirement.Compatible && receipt.Generation > 0 ||
             request.Requirement == RpcRegistrationRequirement.ExactRegistration && receipt.RegistrationId != registrationId))
            return Reject(ContractBindStatus.ReceiptMismatch);
        var contract = manifest.Contracts.Single(c => c.ContractId == request.ContractId);
        if (service.Stub.InterfaceHash != request.ContractId) return Reject(ContractBindStatus.UnsupportedShape);
        if (contract.Methods.Count > 256) return Reject(ContractBindStatus.Capacity);
        var methods = new Dictionary<long, RpcMethodDescriptor>(contract.Methods.Count);
        foreach (var declaration in contract.Methods)
        {
            if (!service.Stub.TryResolveMethod(declaration.MethodId, out var facts) || facts != declaration.Facts ||
                facts.Kind != declaration.Kind || facts.SupportsCancellation != declaration.SupportsCancellation ||
                !ContractMethodFacts.IsValid(facts, request.ContractId) || !methods.TryAdd(facts.MethodId, facts))
                return Reject(ContractBindStatus.UnsupportedShape);
        }
        var immutableMethods = methods.ToFrozenDictionary();
        lock (_lifecycle.StateGate)
        lock (_registryGate)
        {
            if (CurrentState != ServerState.Running || !_services.TryGetValue(request.ContractId, out var current) || !ReferenceEquals(current, service))
                return Reject(ContractBindStatus.Retired);
            return connection.Bindings.Install(service, immutableMethods, request.ContractId, registrationId, 0, _bindingQuota);
        }
    }
    private void ClaimBoundBusinessStart(ServerBindingRequest binding)
    {
        lock (_lifecycle.StateGate)
        {
            if (CurrentState != ServerState.Running) throw ContractBindingWire.Error(ContractBindStatus.Retired);
            binding.ClaimBusinessStart();
        }
    }
}
