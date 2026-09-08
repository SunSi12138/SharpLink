using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientRuntimeEndpointAdmissionTests
{
    [Test]
    public async Task ReplacedPolicyMustNotReceiveAnOlderAttemptsReport()
    {
        var transport = new TestClientTransportFactory();
        var first = new RecordingAdmissionPolicy(token: 11);
        var second = new RecordingAdmissionPolicy(token: 22);
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("runtime-admission", 5001),
            transport,
            builder => builder.UseEndpointAdmission(first));
        await client.ConnectAsync();

        var firstInvocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var firstRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        Ensure(first.AcquireCount == 1, "the initial policy must acquire the first attempt");

        client.UpdateEndpointAdmissionPolicy(second);
        var updated = client.GetEndpointAdmissionPolicySnapshot();
        Ensure(updated.Generation == 1 && updated.Kind == SharpLinkEndpointAdmissionPolicyKind.Custom,
            "replacement must publish one custom admission generation");

        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)firstRequest.RequestId));
        Ensure(await firstInvocation == 0, "first invocation result");
        Ensure(first.ReportCount == 1 && first.LastReportedToken == 11,
            "the old attempt must report to the exact policy/token that admitted it");
        Ensure(second.ReportCount == 0,
            "the replacement policy must not receive the old attempt report");

        var secondInvocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var secondRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        Ensure(second.AcquireCount == 1, "the next attempt must use the replacement policy");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)secondRequest.RequestId));
        Ensure(await secondInvocation == 0, "second invocation result");
        Ensure(second.ReportCount == 1 && second.LastReportedToken == 22,
            "the replacement policy must own its own attempt report");
    }

    [Test]
    public async Task DisableMustBypassFutureAttemptsWithoutDroppingAnExistingLease()
    {
        var transport = new TestClientTransportFactory();
        var policy = new RecordingAdmissionPolicy(token: 31);
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("runtime-admission-disable", 5002),
            transport,
            builder => builder.UseEndpointAdmission(policy));
        await client.ConnectAsync();

        var oldInvocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var oldRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        client.DisableEndpointAdmissionPolicy();
        var disabled = client.GetEndpointAdmissionPolicySnapshot();
        Ensure(disabled.Generation == 1 && disabled.Kind == SharpLinkEndpointAdmissionPolicyKind.Disabled,
            "disable must publish a disabled generation");

        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)oldRequest.RequestId));
        Ensure(await oldInvocation == 0, "old invocation result");
        Ensure(policy.ReportCount == 1 && policy.LastReportedToken == 31,
            "disable must not discard the old attempt lease");

        var newInvocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var newRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)newRequest.RequestId));
        Ensure(await newInvocation == 0, "new invocation result");
        Ensure(policy.AcquireCount == 1 && policy.ReportCount == 1,
            "disabled future attempts must bypass the retired custom policy");
    }

    [Test]
    public async Task RetryMustObserveThePolicyPublishedBeforeItsNextAttempt()
    {
        var transport = new TestClientTransportFactory();
        var first = new RecordingAdmissionPolicy(token: 41);
        var second = new RecordingAdmissionPolicy(token: 42);
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("runtime-admission-retry", 5003),
            transport,
            builder =>
            {
                builder.UseEndpointAdmission(first);
                builder.UseRetry(options =>
                {
                    options.MaxAttempts = 2;
                    options.InitialBackoff = TimeSpan.FromMilliseconds(250);
                    options.MaxBackoff = TimeSpan.FromMilliseconds(250);
                    options.JitterRatio = 0;
                });
            });
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var firstRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, firstRequest, SharpLinkErrorCode.Unavailable);
        await first.Reported.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(first.ReportCount == 1, "first attempt must report before replacement");

        client.UpdateEndpointAdmissionPolicy(second);
        var secondRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        Ensure(second.AcquireCount == 1,
            "the retry attempt must capture the policy that is current at its own attempt boundary");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)secondRequest.RequestId));
        Ensure(await invocation == 0, "retry result");
        Ensure(first.ReportCount == 1 && second.ReportCount == 1,
            "each attempt must report exactly once to its own policy generation");
    }

    [Test]
    public async Task StopMustRejectFurtherAdmissionPublication()
    {
        var transport = new TestClientTransportFactory();
        var policy = new RecordingAdmissionPolicy(token: 51);
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("runtime-admission-stop", 5004),
            transport);
        client.UpdateEndpointAdmissionPolicy(policy);
        var before = client.GetEndpointAdmissionPolicySnapshot();

        await client.StopAsync();

        EnsureThrowsInvalidOperation(() => client.UpdateEndpointAdmissionPolicy(new RecordingAdmissionPolicy(52)));
        EnsureThrowsInvalidOperation(client.DisableEndpointAdmissionPolicy);
        Ensure(client.GetEndpointAdmissionPolicySnapshot() == before,
            "failed publication after Stop must leave the current generation unchanged");
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

    private sealed class RecordingAdmissionPolicy(long token) : ISharpLinkEndpointAdmissionPolicy
    {
        private readonly TaskCompletionSource _reported =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _acquireCount;
        private int _reportCount;
        private long _lastReportedToken;

        internal int AcquireCount => Volatile.Read(ref _acquireCount);
        internal int ReportCount => Volatile.Read(ref _reportCount);
        internal long LastReportedToken => Volatile.Read(ref _lastReportedToken);
        internal Task Reported => _reported.Task;

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            Interlocked.Increment(ref _acquireCount);
            return new SharpLinkEndpointAdmissionDecision(true, token, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long reportToken)
        {
            Volatile.Write(ref _lastReportedToken, reportToken);
            Interlocked.Increment(ref _reportCount);
            _reported.TrySetResult();
        }
    }
}
