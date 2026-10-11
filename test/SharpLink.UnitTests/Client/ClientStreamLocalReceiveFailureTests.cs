using System.Buffers;
using System.IO.Pipelines;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

[NotInParallel("dispatcher-pool")]
public class ClientStreamLocalReceiveFailureTests
{
    private const int ReceiveBufferLimit = 4096;
    private static readonly TimeSpan RaceCoordinationTimeout = TimeSpan.FromSeconds(10);
    private static readonly ReadOnlySequence<byte> Payload = Encode(17);

    [Test]
    public async Task LocalReceiveOverflowEarlyExitMustReleasePendingAndInboundRoute()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var runtimeContext = (SharpLinkRuntimeContext)client.RuntimeContext;
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "local-receive-overflow-owner",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(runtimeContext),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl | ProtocolV2Capabilities.CancellationReason,
            streamReceiveWindowBytes: 65_536,
            connectionReceiveWindowBytes: 65_536);
        using var connectionCancellation = new CancellationTokenSource();
        await using var connection = new ClientConnection(
            client,
            session,
            connectionCancellation,
            maxPendingCalls: 8,
            runtimeContext);
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var requestId = connection.PendingCalls.RegisterStream(
            PendingCallKind.ServerStreaming,
            dispatcher,
            deadline: default,
            cancellationToken: CancellationToken.None);
        dispatcher.SetConsumerAbandonedCallback(connection.ConsumerAbandonedCallback, requestId);
        session.StreamManager.Register(requestId, 0, dispatcher);

        for (var index = 0; index <= ReceiveBufferLimit; index++)
            await session.StreamManager.DispatchChunkAsync(requestId, 0, Payload);

        Ensure(connection.PendingCalls.Contains(requestId) &&
               connection.ActiveCallCount == 1 &&
               session.StreamManager.ActiveStreamCount == 1,
            "closing dispatch admission after local overflow must not be mistaken for removing pending ownership or the inbound route");

        var enumerator = dispatcher.GetAsyncEnumerator();
        Ensure(await enumerator.MoveNextAsync() && enumerator.Current == 17,
            "a previously accepted item must remain consumable after the local receive buffer fails");
        await enumerator.DisposeAsync();

        Ensure(!connection.PendingCalls.Contains(requestId) &&
               connection.PendingCalls.Count == 0 &&
               connection.ActiveCallCount == 0 &&
               session.StreamManager.ActiveStreamCount == 0,
            "disposing a locally failed stream must retire both pending ownership and its actual StreamManager route");

        await enumerator.DisposeAsync();
        Ensure(connection.ActiveCallCount == 0 && session.StreamManager.ActiveStreamCount == 0,
            "repeated disposal must not release an owner or route twice");
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task LocalReceiveOverflowMustPreserveResourceExhaustedAndAllReceiveCredits()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var creditedBytes = 0;
        var abandonments = 0;
        dispatcher.SetBytesConsumedCallback(
            (_, _, bytes) => Interlocked.Add(ref creditedBytes, bytes),
            requestId: 81,
            streamId: 0);
        dispatcher.SetConsumerAbandonedCallback(
            _ => Interlocked.Increment(ref abandonments),
            requestId: 81);

        for (var index = 0; index <= ReceiveBufferLimit; index++)
            await dispatcher.DispatchAsync(Payload);

        var enumerator = dispatcher.GetAsyncEnumerator();
        for (var index = 0; index < ReceiveBufferLimit; index++)
        {
            Ensure(await enumerator.MoveNextAsync() && enumerator.Current == 17,
                "buffered items must drain before the local receive error is surfaced");
        }

        Exception? failure = null;
        try
        {
            _ = await enumerator.MoveNextAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
            "the receive buffer limit must still surface ResourceExhausted after accepted items drain");

        await enumerator.DisposeAsync();
        Ensure(Volatile.Read(ref abandonments) == 1,
            "disposing after the local receive error must notify the owner exactly once");
        Ensure(Volatile.Read(ref creditedBytes) == (ReceiveBufferLimit + 1) * sizeof(int),
            "the accepted items and the rejected overflow frame must return their credits exactly once");
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task LocalReceiveOverflowRacingDisposeMustJoinPublicationAndAbandonmentOnce()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandonments = 0;
        dispatcher.SetConsumerAbandonedCallback(
            (_, _) =>
            {
                Interlocked.Increment(ref abandonments);
                callbackEntered.TrySetResult();
                return new ValueTask(releaseCallback.Task);
            },
            requestId: 82);

        for (var index = 0; index < ReceiveBufferLimit; index++)
            await dispatcher.DispatchAsync(Payload);

        dispatcher.SetBeforeRemoteTerminalPublicationPublishForTests(() =>
        {
            publicationEntered.TrySetResult();
            releasePublication.Task.GetAwaiter().GetResult();
        });

        Task? overflow = null;
        Task? firstDispose = null;
        Task? secondDispose = null;
        try
        {
            overflow = Task.Run(async () => await dispatcher.DispatchAsync(Payload));
            await publicationEntered.Task.WaitAsync(RaceCoordinationTimeout);

            firstDispose = dispatcher.DisposeAsync().AsTask();
            secondDispose = dispatcher.DisposeAsync().AsTask();
            Ensure(!firstDispose.IsCompleted && !secondDispose.IsCompleted,
                "concurrent disposals must join the in-flight local terminal publication");

            releasePublication.TrySetResult();
            await overflow.WaitAsync(RaceCoordinationTimeout);

            var firstCompletion = await Task.WhenAny(callbackEntered.Task, firstDispose)
                .WaitAsync(RaceCoordinationTimeout);
            Ensure(ReferenceEquals(firstCompletion, callbackEntered.Task),
                "local failure must invoke abandonment rather than completing disposal as a remote terminal");
            Ensure(!firstDispose.IsCompleted && !secondDispose.IsCompleted,
                "all disposals must wait for the asynchronous owner cleanup to finish");

            releaseCallback.TrySetResult();
            await Task.WhenAll(firstDispose, secondDispose).WaitAsync(RaceCoordinationTimeout);
            Ensure(Volatile.Read(ref abandonments) == 1,
                "the owner callback must execute exactly once across racing and repeated disposals");
        }
        finally
        {
            releasePublication.TrySetResult();
            releaseCallback.TrySetResult();
            if (overflow is not null)
                await overflow.WaitAsync(RaceCoordinationTimeout);
            if (firstDispose is not null)
                await firstDispose.WaitAsync(RaceCoordinationTimeout);
            if (secondDispose is not null)
                await secondDispose.WaitAsync(RaceCoordinationTimeout);
            PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        }
    }

    [Test]
    public async Task GenuineRemoteCompletionMustNeverNotifyConsumerAbandonment()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var abandonments = 0;
        dispatcher.SetConsumerAbandonedCallback(
            _ => Interlocked.Increment(ref abandonments),
            requestId: 83);
        dispatcher.Complete(exception: null);

        await dispatcher.GetAsyncEnumerator().DisposeAsync();
        Ensure(Volatile.Read(ref abandonments) == 0,
            "a genuine remote stream completion must not be converted to consumer abandonment");
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    private static ReadOnlySequence<byte> Encode(int item)
    {
        var writer = new ArrayBufferWriter<byte>();
        Int32Codec.Instance.Serialize(in item, writer);
        return new ReadOnlySequence<byte>(writer.WrittenMemory);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
