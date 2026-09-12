using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientRetryOutcomeReuseTests
{
    [Test]
    public async Task ReusedOutcomeShouldResetPerAttemptRetryContext()
    {
        var provider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        var retryPolicy = new RecordingSequenceRetryPolicy();
        var admission = new PermitRejectPermitAdmissionPolicy();
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("retry-outcome-reuse", 5001),
            transport,
            builder =>
            {
                builder.UseTimeProvider(provider);
                ConfigureRetry(builder, RetryOptions(3, TimeSpan.Zero));
                builder.UseRetry(retryPolicy);
                builder.UseEndpointAdmission(admission);
            });
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var firstRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        provider.Advance(TimeSpan.FromSeconds(1));
        await InjectErrorAsync(transport, firstRequest, SharpLinkErrorCode.Unavailable);

        // Attempt two is rejected synchronously by admission. The next emitted request is attempt three.
        var thirdRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)thirdRequest.RequestId));

        Ensure(await invocation == 0, "third attempt result");
        Ensure(retryPolicy.Contexts.Count == 2, "retry policy should observe the two failed attempts");
        Ensure(retryPolicy.Contexts[0].Attempt == 1, "first retry context attempt number");
        Ensure(retryPolicy.Contexts[0].ResponseObserved, "first remote error must be response-observed");
        Ensure(retryPolicy.Contexts[0].Elapsed == TimeSpan.FromSeconds(1), "first attempt elapsed time");
        Ensure(retryPolicy.Contexts[1].Attempt == 2, "second retry context attempt number");
        Ensure(!retryPolicy.Contexts[1].ResponseObserved,
            "synchronous admission rejection must not inherit response observation from attempt one");
        Ensure(retryPolicy.Contexts[1].Elapsed == TimeSpan.Zero,
            "attempt two elapsed time must restart when the outcome state is reused");
        Ensure(admission.AcquireCount == 3, "admission should evaluate all three attempts");
        Ensure(admission.ReportCount == 2, "only the two admitted attempts should report outcomes");
    }

    private sealed class RecordingSequenceRetryPolicy : ISharpLinkRetryPolicy
    {
        public List<SharpLinkRetryContext> Contexts { get; } = [];

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            Contexts.Add(context);
            return new SharpLinkRetryDecision(true, TimeSpan.Zero);
        }
    }

    private sealed class PermitRejectPermitAdmissionPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        public int AcquireCount { get; private set; }
        public int ReportCount { get; private set; }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            AcquireCount++;
            return AcquireCount == 2
                ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: null)
                : new SharpLinkEndpointAdmissionDecision(true, Token: AcquireCount, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            ReportCount++;
            Ensure(token is 1 or 3, "admission report token must belong to an admitted attempt");
        }
    }
}
