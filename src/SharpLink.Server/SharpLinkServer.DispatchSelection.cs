namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // A policy waiter owns only host scalar facts and an exact weak registration control.
    internal readonly record struct SelectedMethodCall(QueuedServiceSelection Selection, RpcMethodFacts Method)
    {
        internal int ClientStreamCount => Method.ClientStreamCount;
        internal CancellationToken ModuleCancellation => Selection.ModuleCancellation;
    }

    internal readonly struct QueuedServiceSelection
    {
        private readonly ServerBindingRegistration? _binding;
        private readonly ServiceRegistration? _staticService;
        private readonly WeakReference<ServiceRegistration>? _dynamicService;
        private readonly CancellationToken _moduleCancellation;

        internal QueuedServiceSelection(ServerBindingRegistration binding)
        {
            _binding = binding;
            _moduleCancellation = binding.ModuleCancellation;
        }

        // Retained for exact component callers that already selected a registration. Production
        // binding dispatch constructs directly from the host control without recovering payload.
        internal QueuedServiceSelection(ServiceRegistration service)
        {
            _binding = service.Binding;
            _moduleCancellation = service.ModuleCancellation;
            if (_binding is not null)
                return;
            if (service.HasModule)
                _dynamicService = service.QueuedWeakIdentity;
            else
                _staticService = service;
        }

        internal CancellationToken ModuleCancellation => _moduleCancellation;
        internal bool MayAccept => _binding is null || !_binding.IsRetired;

        internal bool TryGetService(out ServiceRegistration service)
        {
            if (_binding is not null)
                return _binding.TryGetExactPayload(out service);
            if (_staticService is { } staticService)
            {
                service = staticService;
                return true;
            }
            service = null!;
            return _dynamicService is not null && _dynamicService.TryGetTarget(out service!);
        }
    }

    private static bool TrySelectBoundMethod(ServerBindingTable table, ulong token, long methodId,
        out SelectedMethodCall selected)
    {
        selected = default;
        var entry = table.Capture(token);
        var facts = entry?.Registration.Facts;
        if (entry is null || facts is null || !table.CanUseCaptured(entry))
            return false;
        selected = new SelectedMethodCall(new QueuedServiceSelection(entry.Registration), facts.Resolve(methodId));
        return true;
    }

    private async ValueTask RejectRetiredSelectedRpcAsync(ServerConnectionState connection, long requestId,
        ServerCallCancellationState? callState, StripedLongMap<ServerCallCancellationState> cancellations,
        ServerRequestPermit owner, ServerRetainedAdmissionPayload? retained)
    {
        try
        {
            retained?.Dispose();
            await connection.Session.SendRpcErrorWithBackpressureAsync(requestId,
                new SharpLinkException(SharpLinkErrorCode.Unavailable, "The exact selected RPC module is draining."),
                connection.ConnectionToken).ConfigureAwait(false);
        }
        finally { ReleaseDispatchResources(callState, requestId, cancellations, connection, owner); }
    }

    internal Action<ServiceRegistration>? BeforeInvocationModuleAcquireForTesting { get; set; }
    internal Action<ServiceRegistration>? AfterInvocationModuleAcquireForTesting { get; set; }

    private bool TryProtectSelectedInvocation(QueuedServiceSelection selection, RpcMethodFacts method,
        ServerRequestPermit owner, out ResolvedMethodCall resolved)
    {
        resolved = default;
        if (!selection.TryGetService(out var service))
            return false;
        BeforeInvocationModuleAcquireForTesting?.Invoke(service);
        if (!owner.TryProtectRegistration(service, method))
            return false;
        resolved = new ResolvedMethodCall(service, method);
        AfterInvocationModuleAcquireForTesting?.Invoke(service);
        return true;
    }
}
