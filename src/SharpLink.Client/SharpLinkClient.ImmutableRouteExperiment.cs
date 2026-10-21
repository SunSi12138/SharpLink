namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private bool _immutableRouteExperiment;
    private Guid _immutableRouteScope;
    internal Action<Guid>? ImmutableRouteScopeRejectedForTesting { get; set; }

    // Internal experiment only: no public prepared-proxy factory or compatibility promise.
    // A fresh client is required. This keeps cold binding explicit and avoids per-call checks.
    internal bool EnableImmutableRoutesForTesting
    {
        get => _immutableRouteExperiment;
        set
        {
            lock (_remoteContractManifestGate)
            {
                if (State != SharpLinkConnectionState.Created ||
                    _remoteContractManifests.Count != 0 ||
                    _assemblyRegistry.CaptureProxySnapshot().Values.Any(
                        static registration => Volatile.Read(ref registration.Proxy) is not null))
                    throw new InvalidOperationException("Route experiment must be selected before connecting or acquiring proxies.");
                _immutableRouteExperiment = value;
            }
        }
    }

    internal Guid ImmutableRouteScopeForTesting
    {
        get { lock (_remoteContractManifestGate) return _immutableRouteScope; }
    }

    // Called under the existing cold manifest lock, before handshake completion can publish
    // this Session to any ready pool or refresh redirect. A reconnect cannot reset this scope.
    private void ValidateImmutableRouteScopeLocked(ProtocolV2ContractManifest manifest)
    {
        if (!_immutableRouteExperiment)
        {
            if (manifest.RouteScope != Guid.Empty)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                    "The remote server requires the internal immutable-route experiment.");
            return;
        }

        if (manifest.RouteScope == Guid.Empty ||
            (_immutableRouteScope != Guid.Empty && _immutableRouteScope != manifest.RouteScope))
        {
            ImmutableRouteScopeRejectedForTesting?.Invoke(manifest.RouteScope);
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                "Prepared route scope mismatch. Use a new client and prepare again for the new server registry.");
        }
        _immutableRouteScope = manifest.RouteScope;
    }

    internal RpcMethodDescriptor PrepareMethodForTesting(RpcMethodDescriptor logical, RpcHash128 expectedHash)
    {
        if (!_immutableRouteExperiment)
            throw new InvalidOperationException("The immutable-route experiment is not enabled.");
        if (expectedHash.IsEmpty)
            throw new ArgumentException("An exact nonempty assembly hash is required.", nameof(expectedHash));
        if (logical.Kind is not (RpcMethodKind.Unary or RpcMethodKind.OneWay) || logical.HasClientStreams)
            throw new NotSupportedException("This experiment prepares only Unary and OneWay without client streams.");

        lock (_remoteContractManifestGate)
        {
            var readyCount = 0;
            long selectedRoute = 0;
            foreach (var binding in _remoteContractManifestSnapshot)
            {
                if (!binding.Session.IsConnected || binding.Session.ProtocolPhase != RpcSessionProtocolPhase.Ready)
                    continue;
                readyCount++;
                var manifest = binding.Manifest;
                if (manifest.RouteScope != _immutableRouteScope ||
                    !manifest.Contracts.TryGetValue(logical.ContractId, out var hash) || hash != expectedHash ||
                    !manifest.Routes.TryGetValue(logical.ContractId, out var route) ||
                    (selectedRoute != 0 && selectedRoute != route))
                    throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                        "Ready sessions do not agree on the exact prepared contract registration.");
                selectedRoute = route;
            }
            if (readyCount == 0 || State != SharpLinkConnectionState.Ready || readyCount < ReadyConnectionCount)
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                    "No complete ready route snapshot is available for preparation.");
            return logical with { WireRouteId = selectedRoute };
        }
    }

    private void RejectUnpreparedProxyForExperiment()
    {
        if (_immutableRouteExperiment)
            throw new NotSupportedException("Use the internal explicitly prepared test proxy; ordinary generated Get<T>() is not route-aware.");
    }
}
