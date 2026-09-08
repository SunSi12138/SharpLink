namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public SharpLinkCircuitBreakerPolicySnapshot GetCircuitBreakerPolicySnapshot()
    {
        lock (_stateGate)
        {
            if (_endpointAdmissionPolicy is not SharpLinkCircuitBreaker breaker)
            {
                return new SharpLinkCircuitBreakerPolicySnapshot(
                    _endpointAdmissionPolicyGeneration,
                    Enabled: false,
                    MinimumThroughput: 0,
                    FailureRatio: 0,
                    SamplingDuration: TimeSpan.Zero,
                    BreakDuration: TimeSpan.Zero,
                    HalfOpenMaxCalls: 0);
            }

            var configuration = breaker.CaptureConfiguration();
            return new SharpLinkCircuitBreakerPolicySnapshot(
                _endpointAdmissionPolicyGeneration,
                Enabled: true,
                configuration.MinimumThroughput,
                configuration.FailureRatio,
                configuration.SamplingDuration,
                configuration.BreakDuration,
                configuration.HalfOpenMaxCalls);
        }
    }

    public void UpdateCircuitBreaker(ISharpLinkCircuitBreakerOptions options)
    {
        var candidate = SharpLinkCircuitBreakerOptions.CopyValidated(options);

        lock (_stateGate)
        {
            EnsureEndpointAdmissionPublicationAllowed();
            var current = _endpointAdmissionPolicy;
            if (current is not null && current is not SharpLinkCircuitBreaker)
            {
                throw new InvalidOperationException(
                    "The built-in circuit breaker and custom endpoint admission are mutually exclusive. Disable custom endpoint admission first.");
            }

            if (current is SharpLinkCircuitBreaker breaker)
            {
                var existing = breaker.CaptureConfiguration();
                if (Matches(existing, candidate))
                    return;
                EnsureEndpointAdmissionGenerationAvailable();
                breaker.UpdateConfiguration(candidate);
                _endpointAdmissionPolicyGeneration++;
                return;
            }

            EnsureEndpointAdmissionGenerationAvailable();
            _endpointAdmissionPolicy = new SharpLinkCircuitBreaker(candidate, _runtimeContext.TimeProvider);
            _endpointAdmissionPolicyGeneration++;
        }
    }

    public void DisableCircuitBreaker()
    {
        lock (_stateGate)
        {
            EnsureEndpointAdmissionPublicationAllowed();
            var current = _endpointAdmissionPolicy;
            if (current is not null && current is not SharpLinkCircuitBreaker)
            {
                throw new InvalidOperationException(
                    "Custom endpoint admission is active; it must be disabled through the custom endpoint admission runtime API.");
            }
            if (current is null)
                return;

            EnsureEndpointAdmissionGenerationAvailable();
            _endpointAdmissionPolicy = null;
            _endpointAdmissionPolicyGeneration++;
        }
    }

    private void EnsureEndpointAdmissionGenerationAvailable()
    {
        if (_endpointAdmissionPolicyGeneration == ulong.MaxValue)
            throw new InvalidOperationException("The endpoint admission policy generation is exhausted.");
    }

    private static bool Matches(
        SharpLinkCircuitBreaker.CircuitBreakerConfiguration configuration,
        SharpLinkCircuitBreakerOptions options)
        => configuration.MinimumThroughput == options.MinimumThroughput &&
           configuration.FailureRatio == options.FailureRatio &&
           configuration.SamplingDuration == options.SamplingDuration &&
           configuration.BreakDuration == options.BreakDuration &&
           configuration.HalfOpenMaxCalls == options.HalfOpenMaxCalls;
}
