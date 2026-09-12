using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Buffers.Binary;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientCallControlTests
{
    [Test]
    public async Task WaitForReadyFalseShouldFailImmediatelyWhenDisconnected()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);

        var exception = await CaptureSharpLinkException(ClientInvokerTestHelper.InvokeUnaryAsync(client));
        Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "fail-fast error code");
    }

    [Test]
    public async Task MaximumPositiveDefaultTimeoutShouldSaturateAndSendTheRequest()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseRequestTimeout(TimeSpan.MaxValue));
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var request = await transport.Connection
            .WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)request.RequestId));

        Ensure(await invocation == 0, "maximum positive timeout should not fail before send");
        Ensure((request.Flags & ProtocolV2FrameFlags.HasTimeBudget) != 0,
            "saturated timeout should retain an explicit far-future deadline");
    }

    [Test]
    public async Task EndpointAdmissionShouldReportMalformedResponsesAsRemoteErrors()
    {
        var transport = new TestClientTransportFactory();
        var policy = new RecordingAdmissionPolicy();
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            FixedEndpoint,
            transport,
            builder => builder.UseEndpointAdmission(policy));
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectFrameAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.None,
            request.RequestId,
            new byte[] { 0 });

        var exception = await CaptureSharpLinkException(invocation);
        Ensure(exception.Code == SharpLinkErrorCode.DataLoss, "malformed response error code");
        Ensure(policy.ReportCount == 1, "malformed response admission report count");
        Ensure(policy.LastOutcome.Kind == SharpLinkEndpointOutcomeKind.RemoteError,
            "malformed response must report a remote error rather than success");
        Ensure(policy.LastOutcome.ErrorCode == SharpLinkErrorCode.DataLoss,
            "malformed response outcome error code");
        Ensure(policy.LastOutcome.ResponseObserved,
            "malformed response must still record that the endpoint sent a response");
    }

    [Test]
    public async Task StreamRegistrationFailuresShouldReportAcquiredAdmissionLeases()
    {
        var transport = new TestClientTransportFactory();
        var policy = new RecordingAdmissionPolicy();
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            FixedEndpoint,
            transport,
            builder =>
            {
                builder.UseProtocol(options => options.MaxPendingRequestsPerConnection = 1);
                builder.UseEndpointAdmission(policy);
            });
        await client.ConnectAsync();

        var occupied = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var occupiedRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);
        var oneWayFailure = await CaptureSharpLinkException(channel.InvokeOneWayAsync(
            OneWayClientStreamingMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            default).AsTask());
        Ensure(oneWayFailure.Code == SharpLinkErrorCode.ResourceExhausted, "one-way stream registration failure");

        var stream = channel.InvokeServerStreamingAsync(
            ServerStreamingMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            default);
        await using var enumerator = stream.GetAsyncEnumerator();
        var serverFailure = await CaptureSharpLinkException(enumerator.MoveNextAsync().AsTask());
        Ensure(serverFailure.Code == SharpLinkErrorCode.ResourceExhausted, "server stream registration failure");

        Ensure(policy.ReportCount == 2, "both pre-registration failures must report their admission permits");
        Ensure(policy.Outcomes.TrueForAll(static outcome => outcome.Kind == SharpLinkEndpointOutcomeKind.SendFailure),
            "pre-registration failures must report send failure outcomes");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)occupiedRequest.RequestId));
        Ensure(await occupied == 0, "occupied pending call completion");
    }

    private static readonly RpcMethodDescriptor OneWayClientStreamingMethod = new(
        1,
        31,
        RpcMethodKind.OneWay,
        HasResponsePayload: false,
        HasClientStreams: true,
        HasMethodTimeout: false,
        MethodTimeout: null);

    private static readonly RpcMethodDescriptor ServerStreamingMethod = new(
        1,
        32,
        RpcMethodKind.ServerStreaming,
        HasResponsePayload: true,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null);

    private static readonly SharpLinkEndpoint FixedEndpoint = new()
    {
        Id = "fixed",
        Address = new SharpLinkTcpAddress("127.0.0.1", 5001)
    };

    private static SharpLinkEndpoint Endpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress("127.0.0.1", port)
    };

    private static async Task WaitForReadyConnectionCountAsync(SharpLinkClient client, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.ReadyConnectionCount != expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Ensure(client.ReadyConnectionCount == expected, $"expected {expected} ready connections");
    }

    private static async Task InjectGoAwayAsync(TestTransportConnection connection)
    {
        var payload = new PooledByteBufferWriter();
        var lastAccepted = payload.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(lastAccepted, 0);
        payload.Advance(sizeof(ulong));
        ProtocolV2PayloadCodec.WriteError(
            payload,
            SharpLinkErrorCode.Unavailable,
            "test endpoint disconnect",
            1024,
            out _);
        await connection.InjectFrameAsync(
            ProtocolV2FrameType.GoAway,
            ProtocolV2FrameFlags.Error,
            0,
            payload.WrittenMemory);
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(ValueTask<int> invocation)
    {
        try
        {
            _ = await invocation;
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task invocation)
    {
        try
        {
            await invocation;
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> CaptureException(Task invocation)
    {
        try
        {
            await invocation;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class RejectOnceWithZeroDelayPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        public int AcquireCount { get; private set; }
        public int ReportCount { get; private set; }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            AcquireCount++;
            return AcquireCount == 1
                ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: TimeSpan.Zero)
                : new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            ReportCount++;
            Ensure(token == 1, "zero-delay retry admission token");
        }
    }

    private sealed class RejectWithDelayPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        public int AcquireCount { get; private set; }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            AcquireCount++;
            return new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }

    private sealed class SignaledRejectWithDelayPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        private readonly TaskCompletionSource _rejectionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RejectionStarted => _rejectionStarted.Task;

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            _rejectionStarted.TrySetResult();
            return new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }

    private sealed class FirstUnexcludedSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            for (var index = 0; index < context.Count; index++)
                if ((context.ExcludedMask & (1UL << index)) == 0)
                    return index;
            return -1;
        }
    }

    private sealed class RejectFirstThenBlockSecondAdmissionPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        private readonly TaskCompletionSource _secondAdmissionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondAdmissionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _firstAdmissions;
        private long _nextFirstAdmissionRetryAfterTicks;
        private int _freshFirstRejectionCount;

        public Task SecondAdmissionEntered => _secondAdmissionEntered.Task;

        public int FreshFirstRejectionCount => Volatile.Read(ref _freshFirstRejectionCount);

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            if (endpoint.Endpoint.Id == "first")
            {
                var freshRetryAfterTicks = Interlocked.Exchange(ref _nextFirstAdmissionRetryAfterTicks, 0);
                if (freshRetryAfterTicks != 0)
                {
                    Interlocked.Increment(ref _freshFirstRejectionCount);
                    return new SharpLinkEndpointAdmissionDecision(false, Token: 0,
                        RetryAfter: TimeSpan.FromTicks(freshRetryAfterTicks));
                }
                return Interlocked.Increment(ref _firstAdmissions) == 1
                    ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: TimeSpan.FromSeconds(30))
                    : new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null);
            }

            _secondAdmissionEntered.TrySetResult();
            _secondAdmissionRelease.Task.GetAwaiter().GetResult();
            return new SharpLinkEndpointAdmissionDecision(true, Token: 2, RetryAfter: null);
        }

        public void RejectNextFirstAdmission(TimeSpan retryAfter)
            => Interlocked.Exchange(ref _nextFirstAdmissionRetryAfterTicks, retryAfter.Ticks);

        public void ReleaseSecondAdmission() => _secondAdmissionRelease.TrySetResult();

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }

    private sealed class ReconnectBlockingFactory(TestClientTransportFactory inner) : IClientTransportFactory
    {
        private readonly TaskCompletionSource _reconnectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        public Task ReconnectStarted => _reconnectStarted.Task;

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) == 1)
                return await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);

            _reconnectStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class RecordingAdmissionPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        public List<SharpLinkEndpointOutcome> Outcomes { get; } = [];
        public int ReportCount => Outcomes.Count;
        public SharpLinkEndpointOutcome LastOutcome => Outcomes[^1];
        public long LastAdmissionTimestamp { get; private set; }
        public long LastReportTimestamp { get; private set; }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            LastAdmissionTimestamp = Stopwatch.GetTimestamp();
            return new SharpLinkEndpointAdmissionDecision(true, Token: method.MethodId, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            LastReportTimestamp = Stopwatch.GetTimestamp();
            Outcomes.Add(outcome);
        }
    }
}
