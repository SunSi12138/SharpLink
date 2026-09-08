namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ulong _endpointAdmissionPolicyGeneration;

    public SharpLinkEndpointAdmissionPolicySnapshot GetEndpointAdmissionPolicySnapshot()
    {
        lock (_stateGate)
        {
            return new SharpLinkEndpointAdmissionPolicySnapshot(
                _endpointAdmissionPolicyGeneration,
                GetEndpointAdmissionPolicyKind(_endpointAdmissionPolicy));
        }
    }

    public void UpdateEndpointAdmissionPolicy(ISharpLinkEndpointAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy is SharpLinkCircuitBreaker)
        {
            throw new ArgumentException(
                "The built-in circuit breaker cannot be published through the custom endpoint admission API.",
                nameof(policy));
        }

        lock (_stateGate)
        {
            EnsureEndpointAdmissionPublicationAllowed();
            var current = Volatile.Read(ref _endpointAdmissionPolicy);
            if (current is SharpLinkCircuitBreaker)
            {
                throw new InvalidOperationException(
                    "Custom endpoint admission and the built-in circuit breaker are mutually exclusive. Disable the circuit breaker first.");
            }
            if (ReferenceEquals(current, policy))
                return;
            if (_endpointAdmissionPolicyGeneration == ulong.MaxValue)
                throw new InvalidOperationException("The endpoint admission policy generation is exhausted.");

            _endpointAdmissionPolicyGeneration++;
            Volatile.Write(ref _endpointAdmissionPolicy, policy);
        }
    }

    public void DisableEndpointAdmissionPolicy()
    {
        lock (_stateGate)
        {
            EnsureEndpointAdmissionPublicationAllowed();
            var current = Volatile.Read(ref _endpointAdmissionPolicy);
            if (current is SharpLinkCircuitBreaker)
            {
                throw new InvalidOperationException(
                    "The built-in circuit breaker must be disabled through the circuit-breaker runtime configuration API.");
            }
            if (current is null)
                return;
            if (_endpointAdmissionPolicyGeneration == ulong.MaxValue)
                throw new InvalidOperationException("The endpoint admission policy generation is exhausted.");

            _endpointAdmissionPolicyGeneration++;
            Volatile.Write(ref _endpointAdmissionPolicy, null);
        }
    }

    private void EnsureEndpointAdmissionPublicationAllowed()
    {
        var state = State;
        if (Volatile.Read(ref _stopStarted) != 0 ||
            state is SharpLinkConnectionState.Draining or
                SharpLinkConnectionState.Stopped or
                SharpLinkConnectionState.Faulted)
        {
            throw new InvalidOperationException(
                $"Client state '{state}' does not accept endpoint admission policy updates.");
        }
    }

    private static SharpLinkEndpointAdmissionPolicyKind GetEndpointAdmissionPolicyKind(
        ISharpLinkEndpointAdmissionPolicy? policy)
        => policy switch
        {
            null => SharpLinkEndpointAdmissionPolicyKind.Disabled,
            SharpLinkCircuitBreaker => SharpLinkEndpointAdmissionPolicyKind.CircuitBreaker,
            _ => SharpLinkEndpointAdmissionPolicyKind.Custom
        };
}
