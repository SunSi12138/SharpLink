using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientCircuitBreakerSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientRuntimeCircuitBreakerTests
{
    [Test]
    public void BreakDurationUpdateMustNotMoveAnAlreadyOpenBoundary()
    {
        var provider = new ManualTimeProvider();
        var initial = BreakerOptions(minimumThroughput: 1, failureRatio: 1);
        var breaker = new SharpLinkCircuitBreaker(initial, provider);
        var method = BreakerMethod();
        var endpoint = BreakerEndpoint();
        var failure = BreakerOutcome(
            endpoint,
            method,
            SharpLinkEndpointOutcomeKind.RemoteError,
            SharpLinkErrorCode.Unavailable);

        RecordBreakerOutcome(breaker, endpoint, method, failure);
        Ensure(!breaker.TryAcquire(endpoint, method).IsAllowed, "breaker must start Open");

        breaker.UpdateConfiguration(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(30),
            HalfOpenMaxCalls = 1
        });

        provider.Advance(TimeSpan.FromSeconds(5));
        var probe = breaker.TryAcquire(endpoint, method);
        Ensure(probe.IsAllowed && probe.Token != 0,
            "updating BreakDuration must not extend the openUntil timestamp of an already Open state");
    }

    [Test]
    public void HalfOpenLimitUpdateMustBeProspectiveAndNonPreemptive()
    {
        var provider = new ManualTimeProvider();
        var breaker = new SharpLinkCircuitBreaker(
            new SharpLinkCircuitBreakerOptions
            {
                MinimumThroughput = 1,
                FailureRatio = 1,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(1),
                HalfOpenMaxCalls = 2
            },
            provider);
        var method = BreakerMethod();
        var endpoint = BreakerEndpoint();
        var failure = BreakerOutcome(
            endpoint,
            method,
            SharpLinkEndpointOutcomeKind.RemoteError,
            SharpLinkErrorCode.Unavailable);

        RecordBreakerOutcome(breaker, endpoint, method, failure);
        provider.Advance(TimeSpan.FromSeconds(1));
        var first = breaker.TryAcquire(endpoint, method);
        var second = breaker.TryAcquire(endpoint, method);
        Ensure(first.IsAllowed && second.IsAllowed && first.Token == second.Token,
            "two probes must be admitted under the initial HalfOpen limit");

        breaker.UpdateConfiguration(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(1),
            HalfOpenMaxCalls = 1
        });
        var blocked = breaker.TryAcquire(endpoint, method);
        Ensure(!blocked.IsAllowed,
            "shrinking HalfOpenMaxCalls must block future probes without cancelling existing probes");
    }

    [Test]
    public void SamplingShrinkThenGrowMustNotResurrectPrunedSamples()
    {
        var provider = new ManualTimeProvider();
        var breaker = new SharpLinkCircuitBreaker(
            new SharpLinkCircuitBreakerOptions
            {
                MinimumThroughput = 3,
                FailureRatio = 0.3,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                HalfOpenMaxCalls = 1
            },
            provider);
        var method = BreakerMethod();
        var endpoint = BreakerEndpoint();
        var failure = BreakerOutcome(endpoint, method, SharpLinkEndpointOutcomeKind.RemoteError, SharpLinkErrorCode.Unavailable);
        var success = BreakerOutcome(endpoint, method, SharpLinkEndpointOutcomeKind.Success, null);

        RecordBreakerOutcome(breaker, endpoint, method, failure);
        provider.Advance(TimeSpan.FromSeconds(6));
        breaker.UpdateConfiguration(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 3,
            FailureRatio = 0.3,
            SamplingDuration = TimeSpan.FromSeconds(5),
            BreakDuration = TimeSpan.FromSeconds(5),
            HalfOpenMaxCalls = 1
        });
        RecordBreakerOutcome(breaker, endpoint, method, success);

        breaker.UpdateConfiguration(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 3,
            FailureRatio = 0.3,
            SamplingDuration = TimeSpan.FromSeconds(20),
            BreakDuration = TimeSpan.FromSeconds(5),
            HalfOpenMaxCalls = 1
        });
        RecordBreakerOutcome(breaker, endpoint, method, success);

        Ensure(breaker.TryAcquire(endpoint, method).IsAllowed,
            "growing SamplingDuration must not resurrect the failure pruned under the shorter window");
    }

    [Test]
    public void MinimumThroughputGrowthMustPreserveHistoryWhileGrowingTheRing()
    {
        var provider = new ManualTimeProvider();
        var breaker = new SharpLinkCircuitBreaker(
            new SharpLinkCircuitBreakerOptions
            {
                MinimumThroughput = 20,
                FailureRatio = 1,
                SamplingDuration = TimeSpan.FromMinutes(5),
                BreakDuration = TimeSpan.FromSeconds(5),
                HalfOpenMaxCalls = 1
            },
            provider);
        var method = BreakerMethod();
        var endpoint = BreakerEndpoint();
        var success = BreakerOutcome(endpoint, method, SharpLinkEndpointOutcomeKind.Success, null);
        var failure = BreakerOutcome(endpoint, method, SharpLinkEndpointOutcomeKind.RemoteError, SharpLinkErrorCode.Unavailable);

        for (var index = 0; index < 30; index++)
            RecordBreakerOutcome(breaker, endpoint, method, success);

        breaker.UpdateConfiguration(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 100,
            FailureRatio = 0.01,
            SamplingDuration = TimeSpan.FromMinutes(5),
            BreakDuration = TimeSpan.FromSeconds(5),
            HalfOpenMaxCalls = 1
        });
        for (var index = 0; index < 69; index++)
            RecordBreakerOutcome(breaker, endpoint, method, success);
        RecordBreakerOutcome(breaker, endpoint, method, failure);

        Ensure(!breaker.TryAcquire(endpoint, method).IsAllowed,
            "the 30 pre-update samples must survive ring growth so the 100th retained sample can open the breaker");
    }

    [Test]
    public async Task DisableThenEnableMustStartWithFreshClosedState()
    {
        var transport = new TestClientTransportFactory();
        var provider = new ManualTimeProvider();
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("runtime-breaker", 5010),
            transport,
            builder =>
            {
                builder.UseTimeProvider(provider);
                builder.UseCircuitBreaker(options =>
                {
                    options.MinimumThroughput = 1;
                    options.FailureRatio = 1;
                    options.SamplingDuration = TimeSpan.FromSeconds(10);
                    options.BreakDuration = TimeSpan.FromMinutes(1);
                    options.HalfOpenMaxCalls = 1;
                });
            });
        await client.ConnectAsync();

        var failing = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var failingRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, failingRequest, SharpLinkErrorCode.Unavailable);
        await EnsureThrows<SharpLinkException>(failing);

        var rejected = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var rejection = await EnsureThrows<SharpLinkException>(rejected);
        Ensure(rejection.Code == SharpLinkErrorCode.Unavailable, "open breaker rejection code");

        client.DisableCircuitBreaker();
        var disabled = client.GetCircuitBreakerPolicySnapshot();
        Ensure(!disabled.Enabled, "runtime disable must publish disabled breaker state");

        var bypassed = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var bypassRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)bypassRequest.RequestId));
        Ensure(await bypassed == 0, "disabled breaker must bypass old Open state");

        var options = new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromMinutes(1),
            HalfOpenMaxCalls = 1
        };
        client.UpdateCircuitBreaker(options);
        options.MinimumThroughput = 100;
        var enabled = client.GetCircuitBreakerPolicySnapshot();
        Ensure(enabled.Enabled && enabled.MinimumThroughput == 1,
            "runtime publication must copy options instead of retaining the mutable caller object");

        var fresh = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var freshRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)freshRequest.RequestId));
        Ensure(await fresh == 0, "re-enabled breaker must start fresh Closed rather than revive the retired Open state");
    }

    [Test]
    public async Task CircuitBreakerRuntimeUpdatesMustRespectMutualExclusionAndStop()
    {
        var transport = new TestClientTransportFactory();
        var custom = new AllowAllAdmissionPolicy();
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("runtime-breaker-guard", 5011),
            transport,
            builder => builder.UseEndpointAdmission(custom));

        EnsureThrowsInvalidOperation(() => client.UpdateCircuitBreaker(BreakerOptions(1, 1)));
        client.DisableEndpointAdmissionPolicy();
        client.UpdateCircuitBreaker(BreakerOptions(1, 1));
        var beforeStop = client.GetCircuitBreakerPolicySnapshot();
        Ensure(beforeStop.Enabled, "breaker should be enabled after custom admission is disabled");

        await client.StopAsync();
        EnsureThrowsInvalidOperation(() => client.UpdateCircuitBreaker(BreakerOptions(2, 0.5)));
        EnsureThrowsInvalidOperation(client.DisableCircuitBreaker);
        Ensure(client.GetCircuitBreakerPolicySnapshot() == beforeStop,
            "Stop-rejected breaker updates must leave the published generation unchanged");
    }

    private static void EnsureThrowsInvalidOperation(Action action)
    {
        try
        {
            action();
            throw new Exception("expected InvalidOperationException");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class AllowAllAdmissionPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
            => new(true, Token: 1, RetryAfter: null);

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }
}
