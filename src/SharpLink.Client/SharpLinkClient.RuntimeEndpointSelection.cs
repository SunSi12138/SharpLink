namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal SharpLinkEndpointSelectionPolicySnapshot CaptureEndpointSelectionPolicySnapshot()
    {
        var cluster = _cluster ?? throw new NotSupportedException(
            "Fixed-endpoint clients do not have an endpoint-selection policy.");
        return cluster.GetEndpointSelectionPolicySnapshot();
    }

    internal void PublishEndpointSelectionStrategy(SharpLinkLoadBalancingStrategy strategy)
    {
        if (!Enum.IsDefined(strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy));
        var cluster = _cluster ?? throw new NotSupportedException(
            "Fixed-endpoint clients do not support endpoint-selection policy updates.");

        lock (_stateGate)
        {
            EnsureEndpointSelectionPublicationAllowed();
            cluster.UpdateLoadBalancing(strategy);
        }
    }

    internal void PublishEndpointSelector(ISharpLinkEndpointSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var cluster = _cluster ?? throw new NotSupportedException(
            "Fixed-endpoint clients do not support endpoint-selection policy updates.");

        lock (_stateGate)
        {
            EnsureEndpointSelectionPublicationAllowed();
            cluster.UpdateEndpointSelector(selector);
        }
    }

    private void EnsureEndpointSelectionPublicationAllowed()
    {
        var state = State;
        if (Volatile.Read(ref _stopStarted) != 0 ||
            state is SharpLinkConnectionState.Draining or
                SharpLinkConnectionState.Stopped or
                SharpLinkConnectionState.Faulted)
        {
            throw new InvalidOperationException(
                $"Endpoint selection policy cannot be updated while the client is {state}.");
        }
    }
}
