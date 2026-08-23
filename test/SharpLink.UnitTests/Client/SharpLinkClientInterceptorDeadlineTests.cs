using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientInterceptorDeadlineTests
{
    private static readonly RpcMethodDescriptor SMethod = new(
        ContractId: 1,
        MethodId: 2872,
        Kind: RpcMethodKind.Unary,
        HasResponsePayload: true,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null);

    [Test]
    public async Task ShortCircuitShouldStillHonorFrozenLogicalInvocationDeadline()
    {
        var clock = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(clock);
                builder.UseRequestTimeout(TimeSpan.FromSeconds(5));
                builder.AddInterceptor(new AdvancingShortCircuitInterceptor(clock));
            });

        var failure = await CaptureSharpLinkException(InvokeUnary(client).AsTask());

        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a short-circuit result returned after the frozen deadline must be rejected");
        Ensure(client.State == SharpLinkConnectionState.Created,
            "deadline validation of a short circuit must not establish a transport connection");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(25)),
            "a short-circuit path must not emit a request");
    }


    [Test]
    public async Task ShortCircuitStreamPendingMoveNextShouldBeInterruptedAtFrozenDeadline()
    {
        var clock = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(clock);
                builder.AddInterceptor(new BlockingStreamShortCircuitInterceptor());
            });

        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 2873,
            Kind: RpcMethodKind.ServerStreaming,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5));
        var stream = channel.InvokeServerStreamingAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default);
        await using var enumerator = stream.GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        await Task.Yield();
        Ensure(!moveNext.IsCompleted, "the short-circuited local MoveNext should initially be pending");

        clock.Advance(TimeSpan.FromSeconds(5));
        var failure = await CaptureSharpLinkException(moveNext);

        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "the frozen deadline must interrupt an in-flight local MoveNext");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(25)),
            "a local short-circuited stream must not emit a network request");
    }

    [Test]
    public async Task InterceptorDelayBeforeNextShouldReduceEmittedTimeBudget()
    {
        var clock = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(clock);
                builder.UseRequestTimeout(TimeSpan.FromSeconds(5));
                builder.AddInterceptor(new AdvanceThenNextInterceptor(clock));
            });
        await client.ConnectAsync();

        var invocation = InvokeUnary(client).AsTask();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
        var budgetTicks = BinaryPrimitives.ReadInt64LittleEndian(
            sent.Payload.AsSpan(ProtocolV2Constants.RequestPrefixBytes, sizeof(long)));
        Ensure(budgetTicks == TimeSpan.FromSeconds(2).Ticks,
            "the terminal invoker must reuse the pre-interceptor RpcDeadline");

        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId), 42);
        Ensure(await invocation == 42, "intercepted response");
    }

    private static ValueTask<int> InvokeUnary(SharpLinkClient client)
    {
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        return channel.InvokeUnaryAsync(
            SMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default);
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task)
    {
        try
        {
            await task;
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }


    private sealed class BlockingStreamShortCircuitInterceptor : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => ValueTask.FromResult(new SharpLinkClientInvocationResult(BlockForever()));

        private static async IAsyncEnumerable<int> BlockForever(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class AdvancingShortCircuitInterceptor(ManualTimeProvider clock)
        : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            return ValueTask.FromResult(new SharpLinkClientInvocationResult(42));
        }
    }

    private sealed class AdvanceThenNextInterceptor(ManualTimeProvider clock)
        : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            return next(context);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
