namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>Provides the cluster-specific portion of client connection routing.</summary>
    private interface IEndpointClusterRuntime
    {
        int ReadyConnectionCount { get; }
        int PendingCallCount { get; }
        int ActiveCallCount { get; }
        int ActiveStreamCount { get; }
        ClientConnection[] CaptureReadyConnections();
        ValueTask ConnectAsync(CancellationToken cancellationToken);
        void BeginStop();
        ClientConnection GetReadyConnection(
            RpcMethodDescriptor? method,
            EndpointRetrySelectionState? retrySelection,
            AttemptOutcomeState? attemptOutcome);
        bool TryGetEndpointCandidate(ClientConnection connection, out SharpLinkEndpointCandidate candidate);
        void HandleConnectionFailure(ClientConnection connection, Exception exception);
        void MarkConnectionDraining(ClientConnection connection);
        void RetireDrainingConnectionIfIdle(ClientConnection connection);
        ValueTask StopAsync();
        ValueTask DisposeResourcesAsync();
    }

    /// <summary>Keeps the zero-allocation per-logical-call endpoint exclusion mask for retry attempts.</summary>
    private sealed class EndpointRetrySelectionState
    {
        private object? _snapshot;
        private ulong _excludedMask;

        public ulong GetExcludedMask(object snapshot, int count)
        {
            if (!ReferenceEquals(_snapshot, snapshot))
            {
                _snapshot = snapshot;
                _excludedMask = 0;
            }
            var availableMask = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
            if ((_excludedMask & availableMask) == availableMask)
                _excludedMask = 0;
            return _excludedMask;
        }

        public void Exclude(object snapshot, int index)
        {
            if (!ReferenceEquals(_snapshot, snapshot))
            {
                _snapshot = snapshot;
                _excludedMask = 0;
            }
            _excludedMask |= 1UL << index;
        }
    }
}
