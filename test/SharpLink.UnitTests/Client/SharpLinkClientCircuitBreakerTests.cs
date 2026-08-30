using System.Threading;
using System.Diagnostics;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientCircuitBreakerSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientCircuitBreakerTests
{
    [Test]
    public void CircuitBreakerShouldRejectZeroFailureRatio()
    {
        try
        {
            _ = new SharpLinkCircuitBreakerOptions { FailureRatio = 0 }.CloneValidated();
            throw new Exception("zero failure ratio should be rejected");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    [Test]
    public void CircuitBreakerShouldOpenPerGenerationForInfrastructureFailures()
    {
        var breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 2,
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(10),
            HalfOpenMaxCalls = 1
        }.CloneValidated());
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        var generationOne = new SharpLinkEndpointCandidate(Endpoint("breaker", 5001), 1, 0, generation: 1);
        var failure = new SharpLinkEndpointOutcome(
            generationOne,
            method,
            SharpLinkEndpointOutcomeKind.RemoteError,
            SharpLinkErrorCode.Unavailable,
            ResponseObserved: true,
            TimeSpan.Zero);

        var first = breaker.TryAcquire(generationOne, method);
        Ensure(first.IsAllowed, "closed breaker first acquisition");
        breaker.Report(failure, first.Token);
        var second = breaker.TryAcquire(generationOne, method);
        Ensure(second.IsAllowed, "closed breaker second acquisition");
        breaker.Report(failure, second.Token);

        var open = breaker.TryAcquire(generationOne, method);
        Ensure(!open.IsAllowed && open.RetryAfter > TimeSpan.Zero, "breaker opens after failure ratio threshold");
        var replacementGeneration = new SharpLinkEndpointCandidate(Endpoint("breaker", 5002), 1, 0, generation: 2);
        Ensure(breaker.TryAcquire(replacementGeneration, method).IsAllowed, "replacement generation starts closed");
    }

    [Test]
    public void CircuitBreakerShouldIgnoreLocalResourceExhaustionDuringHalfOpenProbe()
    {
        var breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromMilliseconds(1),
            HalfOpenMaxCalls = 1
        }.CloneValidated());
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        var endpoint = new SharpLinkEndpointCandidate(Endpoint("breaker", 5001), 1, 0, generation: 1);
        var infrastructureFailure = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.RemoteError, SharpLinkErrorCode.Unavailable, true, TimeSpan.Zero);
        var localCapacityFailure = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.SendFailure, SharpLinkErrorCode.ResourceExhausted, false, TimeSpan.Zero);

        breaker.Report(infrastructureFailure, breaker.TryAcquire(endpoint, method).Token);
        Thread.Sleep(20);
        var probe = breaker.TryAcquire(endpoint, method);
        Ensure(probe.IsAllowed && probe.Token != 0, "half-open probe should be admitted");
        breaker.Report(localCapacityFailure, probe.Token);

        var nextProbe = breaker.TryAcquire(endpoint, method);
        Ensure(nextProbe.IsAllowed && nextProbe.Token != 0,
            "local capacity pressure must not close the breaker as a successful probe");
    }

    [Test]
    public void CircuitBreakerShouldIgnoreLocalSendFailures()
    {
        var breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(10),
            HalfOpenMaxCalls = 1
        }.CloneValidated());
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        var endpoint = new SharpLinkEndpointCandidate(Endpoint("breaker", 5001), 1, 0, generation: 1);
        var codecFailure = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.SendFailure, null, false, TimeSpan.Zero);
        var validationFailure = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.SendFailure, SharpLinkErrorCode.InvalidArgument, false, TimeSpan.Zero);

        breaker.Report(codecFailure, breaker.TryAcquire(endpoint, method).Token);
        breaker.Report(validationFailure, breaker.TryAcquire(endpoint, method).Token);

        Ensure(breaker.TryAcquire(endpoint, method).IsAllowed,
            "local serialization and validation failures must not open the endpoint breaker");
    }

    [Test]
    public void CircuitBreakerShouldIgnoreReportsFromAnExpiredHalfOpenEpoch()
    {
        var breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromMilliseconds(1),
            HalfOpenMaxCalls = 2
        }.CloneValidated());
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        var endpoint = new SharpLinkEndpointCandidate(Endpoint("breaker", 5001), 1, 0, generation: 1);
        var failure = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.RemoteError, SharpLinkErrorCode.Unavailable, true, TimeSpan.Zero);
        var success = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.Success, null, true, TimeSpan.Zero);

        breaker.Report(failure, breaker.TryAcquire(endpoint, method).Token);
        Thread.Sleep(20);
        var firstEpochFirstProbe = breaker.TryAcquire(endpoint, method);
        var firstEpochSecondProbe = breaker.TryAcquire(endpoint, method);
        Ensure(firstEpochFirstProbe.IsAllowed && firstEpochFirstProbe.Token != 0, "first half-open probe token");
        Ensure(firstEpochSecondProbe.IsAllowed && firstEpochSecondProbe.Token == firstEpochFirstProbe.Token,
            "same half-open epoch token");

        breaker.Report(failure, firstEpochFirstProbe.Token);
        Thread.Sleep(20);
        var currentEpochProbe = breaker.TryAcquire(endpoint, method);
        Ensure(currentEpochProbe.IsAllowed && currentEpochProbe.Token != 0, "current half-open probe token");

        breaker.Report(success, firstEpochSecondProbe.Token);
        var stillHalfOpen = breaker.TryAcquire(endpoint, method);
        Ensure(stillHalfOpen.IsAllowed && stillHalfOpen.Token != 0,
            "stale success must not close a newer half-open epoch");
    }

    [Test]
    public void CircuitBreakerFakeTimeShouldRemainOpenBeforeAndEnterHalfOpenAtExactEquality()
    {
        var provider = new ManualTimeProvider();
        var breaker = new SharpLinkCircuitBreaker(
            BreakerOptions(minimumThroughput: 1, failureRatio: 1),
            provider);
        var method = BreakerMethod();
        var endpoint = BreakerEndpoint();
        var failure = BreakerOutcome(
            endpoint,
            method,
            SharpLinkEndpointOutcomeKind.RemoteError,
            SharpLinkErrorCode.Unavailable);
        var success = BreakerOutcome(
            endpoint,
            method,
            SharpLinkEndpointOutcomeKind.Success,
            errorCode: null);

        var admitted = breaker.TryAcquire(endpoint, method);
        breaker.Report(failure, admitted.Token);
        var opened = breaker.TryAcquire(endpoint, method);
        Ensure(!opened.IsAllowed && opened.RetryAfter == TimeSpan.FromSeconds(5),
            "the threshold failure must open for the complete provider break duration");

        provider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        var before = breaker.TryAcquire(endpoint, method);
        Ensure(!before.IsAllowed && before.RetryAfter == TimeSpan.FromTicks(1),
            "one provider tick before the boundary must remain Open with exact remaining time");

        provider.Advance(TimeSpan.FromTicks(1));
        var probe = breaker.TryAcquire(endpoint, method);
        var excessProbe = breaker.TryAcquire(endpoint, method);
        Ensure(probe.IsAllowed && probe.Token != 0,
            "exact provider equality must admit the first HalfOpen probe");
        Ensure(!excessProbe.IsAllowed && excessProbe.RetryAfter == TimeSpan.Zero,
            "HalfOpen equality must retain its configured single-probe bound");

        breaker.Report(success, probe.Token);
        var closed = breaker.TryAcquire(endpoint, method);
        Ensure(closed.IsAllowed && closed.Token == 0,
            "the successful HalfOpen probe must return the endpoint to Closed");
        Ensure(provider.ActiveTimerCount == 0,
            "the breaker must remain timestamp-driven and own no timer");
    }

    [Test]
    public void CircuitBreakerSamplingShouldRetainAtEqualityAndPruneOneTickAfter()
    {
        var exactProvider = new ManualTimeProvider();
        var afterProvider = new ManualTimeProvider();
        var options = BreakerOptions(minimumThroughput: 2, failureRatio: 0.5);
        var exact = new SharpLinkCircuitBreaker(options, exactProvider);
        var after = new SharpLinkCircuitBreaker(options, afterProvider);
        var method = BreakerMethod();
        var endpoint = BreakerEndpoint();
        var failure = BreakerOutcome(
            endpoint,
            method,
            SharpLinkEndpointOutcomeKind.RemoteError,
            SharpLinkErrorCode.Unavailable);
        var success = BreakerOutcome(
            endpoint,
            method,
            SharpLinkEndpointOutcomeKind.Success,
            errorCode: null);

        RecordBreakerOutcome(exact, endpoint, method, failure);
        exactProvider.Advance(TimeSpan.FromSeconds(10));
        RecordBreakerOutcome(exact, endpoint, method, success);
        Ensure(!exact.TryAcquire(endpoint, method).IsAllowed,
            "a sample exactly at SamplingDuration must remain and satisfy the failure threshold");

        RecordBreakerOutcome(after, endpoint, method, failure);
        afterProvider.Advance(TimeSpan.FromSeconds(10).Add(TimeSpan.FromTicks(1)));
        RecordBreakerOutcome(after, endpoint, method, success);
        Ensure(after.TryAcquire(endpoint, method).IsAllowed,
            "a sample one provider tick beyond SamplingDuration must be pruned before evaluation");
    }

    [Test]
    public void CircuitBreakersWithDifferentProvidersShouldAdvanceIndependently()
    {
        var firstProvider = new ManualTimeProvider();
        var secondProvider = new ManualTimeProvider();
        var options = BreakerOptions(minimumThroughput: 1, failureRatio: 1);
        var first = new SharpLinkCircuitBreaker(options, firstProvider);
        var second = new SharpLinkCircuitBreaker(options, secondProvider);
        var method = BreakerMethod();
        var endpoint = BreakerEndpoint();
        var failure = BreakerOutcome(
            endpoint,
            method,
            SharpLinkEndpointOutcomeKind.ConnectionClosed,
            SharpLinkErrorCode.ConnectionClosed);

        RecordBreakerOutcome(first, endpoint, method, failure);
        RecordBreakerOutcome(second, endpoint, method, failure);
        firstProvider.Advance(TimeSpan.FromSeconds(5));

        var firstProbe = first.TryAcquire(endpoint, method);
        var secondStillOpen = second.TryAcquire(endpoint, method);
        Ensure(firstProbe.IsAllowed && firstProbe.Token != 0,
            "advancing the first provider must move only its breaker to HalfOpen");
        Ensure(!secondStillOpen.IsAllowed &&
               secondStillOpen.RetryAfter == TimeSpan.FromSeconds(5),
            "the second breaker must retain its complete independent Open duration");

        secondProvider.Advance(TimeSpan.FromSeconds(5));
        var secondProbe = second.TryAcquire(endpoint, method);
        Ensure(secondProbe.IsAllowed && secondProbe.Token != 0,
            "the second breaker must enter HalfOpen only when its own provider advances");
    }
}
