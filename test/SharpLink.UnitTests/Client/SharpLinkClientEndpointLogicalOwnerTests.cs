using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientEndpointLogicalOwnerTests
{
    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task DeadlineShouldWinWhenEndpointAdmissionCrossesBoundary(RpcMethodKind kind)
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        var policy = new DeadlineCrossingAdmissionPolicy(timeProvider);
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            FixedEndpoint,
            transport,
            builder =>
            {
                builder.UseTimeProvider(timeProvider);
                builder.UseEndpointAdmission(policy);
            });
        await client.ConnectAsync();

        var hasClientStreams = kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming;
        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 293 + (long)kind,
            Kind: kind,
            HasResponsePayload: kind != RpcMethodKind.OneWay,
            HasClientStreams: hasClientStreams,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: hasClientStreams ? 1 : 0);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);

        Exception failure;
        switch (kind)
        {
            case RpcMethodKind.Unary:
                failure = await CaptureFailureAsync(channel.InvokeUnaryAsync(
                    method,
                    in request,
                    RpcEmptyRequestCodec.Instance,
                    RpcEmptyRequestCodec.Instance,
                    metadata: null).AsTask());
                break;
            case RpcMethodKind.OneWay:
                failure = await CaptureFailureAsync(channel.InvokeOneWayAsync(
                    method,
                    in request,
                    RpcEmptyRequestCodec.Instance,
                    in streams,
                    metadata: null).AsTask());
                break;
            case RpcMethodKind.ClientStreaming:
                failure = await CaptureFailureAsync(channel.InvokeClientStreamingAsync(
                    method,
                    in request,
                    RpcEmptyRequestCodec.Instance,
                    RpcEmptyRequestCodec.Instance,
                    in streams,
                    metadata: null).AsTask());
                break;
            case RpcMethodKind.ServerStreaming:
                await using (var enumerator = channel.InvokeServerStreamingAsync(
                    method,
                    in request,
                    RpcEmptyRequestCodec.Instance,
                    RpcEmptyRequestCodec.Instance,
                    metadata: null).GetAsyncEnumerator())
                {
                    failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
                }
                break;
            case RpcMethodKind.DuplexStreaming:
                await using (var enumerator = channel.InvokeDuplexStreamingAsync(
                    method,
                    in request,
                    RpcEmptyRequestCodec.Instance,
                    RpcEmptyRequestCodec.Instance,
                    in streams,
                    metadata: null).GetAsyncEnumerator())
                {
                    failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            $"{kind}: the frozen logical deadline must replace a later endpoint-admission rejection");
        Ensure(policy.AcquireCount == 1,
            $"{kind}: endpoint admission should be entered exactly once");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            $"{kind}: no Request may be emitted after endpoint admission crosses the logical deadline");
    }

    private static readonly SharpLinkEndpoint FixedEndpoint = new()
    {
        Id = "logical-owner",
        Address = new SharpLinkTcpAddress("127.0.0.1", 5001)
    };

    private sealed class DeadlineCrossingAdmissionPolicy(ManualTimeProvider timeProvider)
        : ISharpLinkEndpointAdmissionPolicy
    {
        internal int AcquireCount;

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            _ = endpoint;
            _ = method;
            Interlocked.Increment(ref AcquireCount);
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
            return new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            _ = outcome;
            _ = token;
        }
    }

    private static async Task<Exception> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new Exception("expected call failure");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
