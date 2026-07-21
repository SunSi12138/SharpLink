using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Buffers.Binary;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientCallOptionsTests
{
    [Test]
    public async Task WaitForReadyFalseShouldFailImmediatelyWhenDisconnected()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        var exception = await CaptureSharpLinkException(ClientInvokerTestHelper.InvokeUnaryAsync(client));
        Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "fail-fast error code");
    }

    [Test]
    public async Task WaitForReadyShouldResumeAfterConnectionBecomesReady()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            new SharpLinkCallOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                WaitForReady = true
            }).AsTask();

        await Task.Delay(50);
        Ensure(!invocation.IsCompleted, "call should wait while no connection is ready");
        await client.ConnectAsync();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.None,
            unchecked((long)request.RequestId));

        Ensure(await invocation == 0, "empty response should deserialize to default(int)");
    }

    [Test]
    public async Task WaitForReadyDeadlineShouldMapToDeadlineExceeded()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        var exception = await CaptureSharpLinkException(ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            new SharpLinkCallOptions
            {
                Timeout = TimeSpan.FromMilliseconds(80),
                WaitForReady = true
            }));
        Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded, "wait deadline error code");
    }

    [Test]
    public async Task WaitForReadyShouldRetryZeroAdmissionDelayWithABoundedYield()
    {
        var transport = new TestClientTransportFactory();
        var policy = new RejectOnceWithZeroDelayPolicy();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            fixedEndpoint: FixedEndpoint,
            endpointAdmissionPolicy: policy);
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            new SharpLinkCallOptions { WaitForReady = true }).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.None,
            unchecked((long)request.RequestId));

        Ensure(await invocation == 0, "zero-delay admission rejection should retry");
        Ensure(policy.AcquireCount >= 2, "admission should be retried after the bounded yield");
        Ensure(policy.ReportCount == 1, "only the granted admission lease should report");
    }

    [Test]
    public async Task WaitForReadyShouldDiscardAStaleAdmissionDelayAfterAnAdmittedEndpointDisconnects()
    {
        var first = new TestClientTransportFactory();
        var second = new TestClientTransportFactory();
        var blockingSecond = new ReconnectBlockingFactory(second);
        var policy = new RejectFirstThenBlockSecondAdmissionPolicy();
        var endpoints = new[]
        {
            new StaticEndpointConfiguration(Endpoint("first", 5001), first),
            new StaticEndpointConfiguration(Endpoint("second", 5002), blockingSecond)
        };
        await using var client = new SharpLinkClient(
            first,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            staticEndpoints: endpoints,
            clusterOptions: new SharpLinkClusterOptions { MinReadyEndpoints = 2, MaxConnections = 2 },
            endpointSelector: new FirstUnexcludedSelector(),
            endpointAdmissionPolicy: policy);
        await client.ConnectAsync();
        await WaitForReadyConnectionCountAsync(client, 2);

        var invocation = Task.Run(async () => await ClientInvokerTestHelper.InvokeUnaryAsync(
            client, new SharpLinkCallOptions { WaitForReady = true }));
        await policy.SecondAdmissionEntered.WaitAsync(TimeSpan.FromSeconds(2));
        await InjectGoAwayAsync(second.Connection);
        await blockingSecond.ReconnectStarted.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForReadyConnectionCountAsync(client, 1);
        policy.ReleaseSecondAdmission();

        var request = await first.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await first.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.None,
            unchecked((long)request.RequestId));

        Ensure(await invocation == 0, "a granted endpoint disconnect must not retain a previous rejection delay");
    }

    [Test]
    public async Task EndpointOutcomeElapsedShouldExcludeWaitForReadyTime()
    {
        var transport = new TestClientTransportFactory();
        var policy = new RecordingAdmissionPolicy();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            fixedEndpoint: FixedEndpoint,
            endpointAdmissionPolicy: policy);

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            new SharpLinkCallOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                WaitForReady = true
            }).AsTask();
        await Task.Delay(200);
        await client.ConnectAsync();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.None,
            unchecked((long)request.RequestId));

        Ensure(await invocation == 0, "wait-for-ready response");
        var measuredEndpointInterval = Stopwatch.GetElapsedTime(policy.LastAdmissionTimestamp, policy.LastReportTimestamp);
        var difference = (policy.LastOutcome.Elapsed - measuredEndpointInterval).Duration();
        Ensure(difference < TimeSpan.FromMilliseconds(100),
            "endpoint outcome elapsed must measure the interval after admission rather than pre-ready waiting");
    }

    [Test]
    public async Task StreamRegistrationFailuresShouldReportAcquiredAdmissionLeases()
    {
        var transport = new TestClientTransportFactory();
        var policy = new RecordingAdmissionPolicy();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            protocolOptions: new SharpLinkProtocolOptions { MaxPendingRequestsPerConnection = 1 },
            fixedEndpoint: FixedEndpoint,
            endpointAdmissionPolicy: policy);
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
        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.None,
            unchecked((long)occupiedRequest.RequestId));
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

        public Task SecondAdmissionEntered => _secondAdmissionEntered.Task;

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            if (endpoint.Endpoint.Id == "first")
            {
                return Interlocked.Increment(ref _firstAdmissions) == 1
                    ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: TimeSpan.FromSeconds(30))
                    : new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null);
            }

            _secondAdmissionEntered.TrySetResult();
            _secondAdmissionRelease.Task.GetAwaiter().GetResult();
            return new SharpLinkEndpointAdmissionDecision(true, Token: 2, RetryAfter: null);
        }

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
