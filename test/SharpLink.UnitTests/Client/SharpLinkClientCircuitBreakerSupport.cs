using SharpLink.Client;
using SharpLink.Sdk;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientCircuitBreakerSupport
{
    internal static SharpLinkCircuitBreakerOptions BreakerOptions(
        int minimumThroughput,
        double failureRatio)
        => new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = minimumThroughput,
            FailureRatio = failureRatio,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            HalfOpenMaxCalls = 1
        }.CloneValidated();

    internal static RpcMethodDescriptor BreakerMethod()
        => new(1, 2, RpcMethodKind.Unary, true, false, false, null);

    internal static SharpLinkEndpointCandidate BreakerEndpoint()
        => new(Endpoint("fake-time-breaker", 5001), 1, 0, generation: 1);

    internal static SharpLinkEndpointOutcome BreakerOutcome(
        SharpLinkEndpointCandidate endpoint,
        RpcMethodDescriptor method,
        SharpLinkEndpointOutcomeKind kind,
        SharpLinkErrorCode? errorCode)
        => new(
            endpoint,
            method,
            kind,
            errorCode,
            ResponseObserved: true,
            Elapsed: TimeSpan.Zero);

    internal static void RecordBreakerOutcome(
        SharpLinkCircuitBreaker breaker,
        SharpLinkEndpointCandidate endpoint,
        RpcMethodDescriptor method,
        SharpLinkEndpointOutcome outcome)
    {
        var admission = breaker.TryAcquire(endpoint, method);
        Ensure(admission.IsAllowed,
            "the setup outcome must be admitted while the breaker is Closed");
        breaker.Report(outcome, admission.Token);
    }
}
