using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientRuntimeConfigurationResultTests
{
    [Test]
    public async Task RequestTimeoutTryPathShouldPublishOrRejectWithoutPartialMutation()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);

        var initial = client.GetRequestTimeoutPolicySnapshot();
        Ensure(initial.Generation == 0 && !initial.Enabled,
            "builder-disabled timeout policy should start at generation zero");

        var published = client.TryUpdateRequestTimeout(TimeSpan.FromSeconds(7));
        Ensure(published.Succeeded &&
               published.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.None,
            "valid runtime timeout update should succeed");
        var enabled = client.GetRequestTimeoutPolicySnapshot();
        Ensure(enabled.Generation == 1 && enabled.Enabled &&
               enabled.Timeout == TimeSpan.FromSeconds(7),
            "successful Try update should publish exactly one complete generation");

        var invalid = CaptureException(() => client.TryUpdateRequestTimeout(TimeSpan.Zero));
        Ensure(invalid is ArgumentOutOfRangeException,
            "invalid timeout remains a programmer/configuration exception");
        Ensure(client.GetRequestTimeoutPolicySnapshot() == enabled,
            "invalid candidate must not mutate the published generation");

        await client.StopAsync();
        var rejected = client.TryUpdateRequestTimeout(TimeSpan.FromSeconds(9));
        Ensure(!rejected.Succeeded &&
               rejected.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.LifecycleClosed,
            "post-Stop update should be a structured lifecycle rejection");
        Ensure(client.GetRequestTimeoutPolicySnapshot() == enabled,
            "lifecycle rejection must leave the published generation unchanged");
    }

    [Test]
    public async Task RequestTimeoutTryPathShouldPreserveLogicalCallCaptureBoundary()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRequestTimeout(TimeSpan.FromSeconds(10)));
        await client.ConnectAsync();

        var oldGenerationCall = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);

        var update = client.TryUpdateRequestTimeout(TimeSpan.FromSeconds(1));
        Ensure(update.Succeeded, "structured timeout update should publish");
        var newGenerationCall = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var newFailure = await CaptureSharpLinkException(newGenerationCall);
        Ensure(newFailure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "new logical call should capture the new one-second generation");
        Ensure(!oldGenerationCall.IsCompleted,
            "in-flight logical call must retain the generation captured at invocation");

        timeProvider.Advance(TimeSpan.FromSeconds(9));
        var oldFailure = await CaptureSharpLinkException(oldGenerationCall);
        Ensure(oldFailure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "old logical call should expire only at its original captured deadline");
    }

    [Test]
    public async Task CircuitBreakerConflictShouldReturnModeConflictWithoutPublication()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("structured-result-mode-conflict", 5091),
            transport,
            builder => builder.UseEndpointAdmission(new AllowAllAdmissionPolicy()));

        var before = client.GetCircuitBreakerPolicySnapshot();
        var result = client.TryUpdateCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            HalfOpenMaxCalls = 1
        });

        Ensure(!result.Succeeded &&
               result.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.ModeConflict,
            "custom endpoint admission and built-in breaker conflict should be machine-readable");
        Ensure(client.GetCircuitBreakerPolicySnapshot() == before,
            "mode-conflict rejection must not publish a breaker generation");
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
            throw new Exception("expected exception");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
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
