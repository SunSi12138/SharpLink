namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ClientRequestTimeoutGeneration? _requestTimeoutGeneration;

    public SharpLinkRequestTimeoutPolicySnapshot GetRequestTimeoutPolicySnapshot()
    {
        var current = CaptureRequestTimeoutGeneration();
        return new SharpLinkRequestTimeoutPolicySnapshot(
            current.Generation,
            current.Policy.Source switch
            {
                ClientRequestTimeoutSource.None => SharpLinkRequestTimeoutPolicySource.Disabled,
                ClientRequestTimeoutSource.Recommended => SharpLinkRequestTimeoutPolicySource.Recommended,
                ClientRequestTimeoutSource.Custom => SharpLinkRequestTimeoutPolicySource.Custom,
                _ => throw new InvalidOperationException("Unknown request-timeout policy source.")
            },
            current.Policy.TimeoutOrNull);
    }

    public void UpdateRequestTimeout(TimeSpan timeout)
        => PublishRequestTimeoutPolicy(ClientRequestTimeoutPolicy.Custom(timeout));

    public void DisableRequestTimeout()
        => PublishRequestTimeoutPolicy(ClientRequestTimeoutPolicy.Disabled);

    private void PublishRequestTimeoutPolicy(ClientRequestTimeoutPolicy policy)
    {
        lock (_stateGate)
        {
            var state = State;
            if (Volatile.Read(ref _stopStarted) != 0 ||
                state is SharpLinkConnectionState.Draining or
                    SharpLinkConnectionState.Stopped or
                    SharpLinkConnectionState.Faulted)
            {
                throw new InvalidOperationException(
                    $"Request-timeout policy cannot be updated while the client is {state}.");
            }

            var current = CaptureRequestTimeoutGeneration();
            if (current.Policy == policy)
                return;
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("Request-timeout policy generation is exhausted.");

            Volatile.Write(
                ref _requestTimeoutGeneration,
                new ClientRequestTimeoutGeneration(current.Generation + 1, policy));
        }
    }

    private ClientRequestTimeoutGeneration CaptureRequestTimeoutGeneration()
    {
        var current = Volatile.Read(ref _requestTimeoutGeneration);
        if (current is not null)
            return current;

        var initialPolicy = !_hasRequestTimeout
            ? ClientRequestTimeoutPolicy.Disabled
            : _requestTimeoutSource switch
            {
                ClientRequestTimeoutSource.Recommended =>
                    ClientRequestTimeoutPolicy.Recommended(_requestTimeoutValue),
                ClientRequestTimeoutSource.Custom =>
                    ClientRequestTimeoutPolicy.Custom(_requestTimeoutValue),
                _ => throw new InvalidOperationException(
                    "An enabled request timeout must have a configured source.")
            };
        var initial = new ClientRequestTimeoutGeneration(0, initialPolicy);
        return Interlocked.CompareExchange(ref _requestTimeoutGeneration, initial, null) ?? initial;
    }

    private sealed class ClientRequestTimeoutGeneration(
        ulong generation,
        ClientRequestTimeoutPolicy policy)
    {
        internal ulong Generation { get; } = generation;
        internal ClientRequestTimeoutPolicy Policy { get; } = policy;
    }
}
