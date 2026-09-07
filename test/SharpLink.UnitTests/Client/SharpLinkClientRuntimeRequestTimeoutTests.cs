using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientRuntimeRequestTimeoutTests
{
    [Test]
    public async Task RuntimeUpdateShouldOnlyAffectFutureLogicalCalls()
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

        client.UpdateRequestTimeout(TimeSpan.FromSeconds(1));
        var newGenerationCall = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var newFailure = await CaptureSharpLinkException(newGenerationCall);
        Ensure(newFailure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "new logical call should use the newly published one-second timeout");
        Ensure(!oldGenerationCall.IsCompleted,
            "in-flight logical call must retain its original ten-second deadline");

        timeProvider.Advance(TimeSpan.FromSeconds(9));
        var oldFailure = await CaptureSharpLinkException(oldGenerationCall);
        Ensure(oldFailure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "old logical call should expire only at its captured deadline");
    }

    [Test]
    public async Task UpdateDisableAndValidationShouldPublishCompleteGenerations()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);

        var initial = client.GetRequestTimeoutPolicySnapshot();
        Ensure(initial.Generation == 0 && !initial.Enabled &&
               initial.Source == SharpLinkRequestTimeoutPolicySource.Disabled,
            "builder-disabled fallback should be generation zero");

        client.UpdateRequestTimeout(TimeSpan.FromSeconds(7));
        var enabled = client.GetRequestTimeoutPolicySnapshot();
        Ensure(enabled.Generation == 1 && enabled.Enabled &&
               enabled.Timeout == TimeSpan.FromSeconds(7) &&
               enabled.Source == SharpLinkRequestTimeoutPolicySource.Custom,
            "runtime enable should publish one complete custom generation");

        var invalid = CaptureException(() => client.UpdateRequestTimeout(TimeSpan.Zero));
        Ensure(invalid is ArgumentOutOfRangeException,
            "invalid timeout candidate should fail before publication");
        Ensure(client.GetRequestTimeoutPolicySnapshot() == enabled,
            "invalid candidate must not mutate the published generation");

        client.DisableRequestTimeout();
        var disabled = client.GetRequestTimeoutPolicySnapshot();
        Ensure(disabled.Generation == 2 && !disabled.Enabled && disabled.Timeout is null &&
               disabled.Source == SharpLinkRequestTimeoutPolicySource.Disabled,
            "runtime disable should publish one complete disabled generation");

        client.DisableRequestTimeout();
        Ensure(client.GetRequestTimeoutPolicySnapshot() == disabled,
            "idempotent disable should not manufacture a new generation");
    }

    [Test]
    public async Task MethodTimeoutShouldKeepPrecedenceAcrossRuntimeUpdates()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRequestTimeout(TimeSpan.FromSeconds(30)));

        client.UpdateRequestTimeout(TimeSpan.FromSeconds(2));

        var explicitMethod = client.ResolveCallControl(
            metadata: null,
            includeClientDefault: true,
            hasMethodTimeout: true,
            methodTimeout: TimeSpan.FromSeconds(9));
        Ensure(explicitMethod.LifetimeSource == ClientCallLifetimeSource.MethodTimeout,
            "explicit method timeout must remain stronger than the runtime client fallback");
        Ensure(explicitMethod.Deadline.GetRemaining(timeProvider) == TimeSpan.FromSeconds(9),
            "explicit method timeout duration");

        var parameterlessMethod = client.ResolveCallControl(
            metadata: null,
            includeClientDefault: false,
            hasMethodTimeout: true,
            methodTimeout: null);
        Ensure(parameterlessMethod.LifetimeSource == ClientCallLifetimeSource.ClientCustomTimeout,
            "parameterless method timeout should capture the current client generation");
        Ensure(parameterlessMethod.Deadline.GetRemaining(timeProvider) == TimeSpan.FromSeconds(2),
            "parameterless method timeout fallback duration");
    }

    [Test]
    public async Task ConcurrentEnableDisableShouldNeverExposeTornPolicy()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);

        Parallel.For(0, 128, index =>
        {
            if ((index & 1) == 0)
                client.UpdateRequestTimeout(TimeSpan.FromMilliseconds(index + 1));
            else
                client.DisableRequestTimeout();

            var snapshot = client.GetRequestTimeoutPolicySnapshot();
            if (snapshot.Enabled)
            {
                Ensure(snapshot.Source == SharpLinkRequestTimeoutPolicySource.Custom &&
                       snapshot.Timeout is { } timeout && timeout > TimeSpan.Zero,
                    "enabled snapshot must expose one complete custom generation");
            }
            else
            {
                Ensure(snapshot.Source == SharpLinkRequestTimeoutPolicySource.Disabled &&
                       snapshot.Timeout is null,
                    "disabled snapshot must expose one complete disabled generation");
            }
        });
    }

    [Test]
    public async Task RuntimeUpdateShouldBeRejectedAfterStopBegins()
    {
        var transport = new TestClientTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport);
        try
        {
            await client.StopAsync();

            Ensure(CaptureException(() => client.UpdateRequestTimeout(TimeSpan.FromSeconds(1)))
                   is InvalidOperationException,
                "timeout enable must be rejected after stop begins");
            Ensure(CaptureException(client.DisableRequestTimeout) is InvalidOperationException,
                "timeout disable must be rejected after stop begins");
        }
        finally
        {
            await client.DisposeAsync();
        }
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
}
