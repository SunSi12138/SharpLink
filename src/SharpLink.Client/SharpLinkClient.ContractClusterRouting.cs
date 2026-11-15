namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private sealed partial class StaticClusterRuntime
    {
        public ContractClusterSnapshot CreateContractSnapshot(BoundContractTarget[] targets)
        {
            lock (_gate)
            {
                var source = _topology.SelectionSnapshot;
                return ContractClusterSnapshot.Create(source, source.Candidates,
                    source.Endpoints.Select(static e => e.ReadyConnections).ToArray(),
                    source.Endpoints.Cast<object>().ToArray(), targets);
            }
        }
        public BoundContractTarget GetReadyBoundTarget(ContractClusterSnapshot snapshot, ContractSiteKey key,
            RpcMethodDescriptor method, EndpointRetrySelectionState? retrySelection, AttemptOutcomeState? attemptOutcome)
        {
            var policy = _topology.CaptureSelectionPolicy();
            var endpoints = snapshot.Endpoints;
            if (endpoints.Length == 0) throw ContractBindingWire.Error(ContractBindStatus.NoBoundRoute);
            var excluded = retrySelection?.GetExcludedMask(snapshot, endpoints.Length) ?? 0;
            for (var attempt = 0; attempt < endpoints.Length; attempt++)
            {
                var index = _topology.SelectContractEndpoint(snapshot, policy, excluded);
                if ((uint)index >= (uint)endpoints.Length || (excluded & (1UL << index)) != 0)
                    throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The endpoint selector returned an unavailable candidate index.");
                if (attemptOutcome is not null && !attemptOutcome.TryAcquire(snapshot.Candidates[index]))
                { retrySelection?.Exclude(snapshot, index); excluded |= 1UL << index; continue; }
                var target = endpoints[index].TryReserve(key);
                retrySelection?.Exclude(snapshot, index);
                if (target is not null)
                {
                    if (target.Connection.ActiveCallCount != 0)
                        EnsureExpansion((StaticClientRuntimeEndpointState)endpoints[index].SourceOwner);
                    return target;
                }
                attemptOutcome?.CompleteWithoutPending(PendingCallCompletionReason.ConnectionClosed,
                    CreateConnectionClosedException("The selected qualified endpoint connection is no longer ready."));
                excluded |= 1UL << index;
            }
            throw ContractBindingWire.Error(ContractBindStatus.NoBoundRoute);
        }
    }
    private sealed partial class DynamicClusterRuntime
    {
        public ContractClusterSnapshot CreateContractSnapshot(BoundContractTarget[] targets)
        {
            lock (_gate)
            {
                var source = _current.SelectionSnapshot;
                return ContractClusterSnapshot.Create(source, source.Candidates,
                    source.Endpoints.Select(static e => e.ReadyConnections).ToArray(),
                    source.Endpoints.Cast<object>().ToArray(), targets);
            }
        }
        public BoundContractTarget GetReadyBoundTarget(ContractClusterSnapshot snapshot, ContractSiteKey key,
            RpcMethodDescriptor method, EndpointRetrySelectionState? retrySelection, AttemptOutcomeState? attemptOutcome)
        {
            var policy = _current.CaptureSelectionPolicy();
            var endpoints = snapshot.Endpoints;
            if (endpoints.Length == 0) throw ContractBindingWire.Error(ContractBindStatus.NoBoundRoute);
            var excluded = retrySelection?.GetExcludedMask(snapshot, endpoints.Length) ?? 0;
            for (var attempt = 0; attempt < endpoints.Length; attempt++)
            {
                int index;
                try { index = _current.SelectContractEndpoint(snapshot, policy, excluded); }
                catch (Exception exception) when (policy.HasCustomSelector)
                {
                    _client._logger.LogError(exception, "SharpLink endpoint selector failed.");
                    throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The endpoint selector failed.", exception);
                }
                if ((uint)index >= (uint)endpoints.Length || (excluded & (1UL << index)) != 0)
                    throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The endpoint selector returned an unavailable candidate index.");
                var candidate = snapshot.Candidates[index];
                if (attemptOutcome is not null && !attemptOutcome.TryAcquire(candidate))
                { retrySelection?.Exclude(snapshot, index); excluded |= 1UL << index; continue; }
                var target = endpoints[index].TryReserve(key);
                retrySelection?.Exclude(snapshot, index);
                if (target is not null)
                {
                    if (target.Connection.ActiveCallCount != 0) EnsureExpansion((DynamicEndpointState)endpoints[index].SourceOwner);
                    return target;
                }
                attemptOutcome?.CompleteWithoutPending(PendingCallCompletionReason.ConnectionClosed,
                    CreateConnectionClosedException("The selected qualified endpoint connection is no longer ready."));
                RetireAdmissionStateIfReleased((DynamicEndpointState)endpoints[index].SourceOwner, candidate);
                excluded |= 1UL << index;
            }
            throw ContractBindingWire.Error(ContractBindStatus.NoBoundRoute);
        }
    }
}
