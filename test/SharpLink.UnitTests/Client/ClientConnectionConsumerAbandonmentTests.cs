using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class ClientConnectionConsumerAbandonmentTests
{
    private static readonly TimeSpan RaceCoordinationTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NotInParallel]
    public async Task ConsumerAbandonmentShouldEnqueueFinalCreditBeforeCancelAfterDetach()
    {
        await using var owner = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var runtimeContext = (SharpLinkRuntimeContext)owner.RuntimeContext;
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "consumer-abandon-credit-order",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(runtimeContext),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl | ProtocolV2Capabilities.CancellationReason,
            streamReceiveWindowBytes: 4,
            connectionReceiveWindowBytes: 4);
        using var connectionCancellation = new CancellationTokenSource();
        await using var connection = new ClientConnection(
            owner,
            session,
            connectionCancellation,
            maxPendingCalls: 8,
            runtimeContext);
        var dispatcher = new CreditHoldingLeaseDispatcher();
        var requestId = connection.PendingCalls.RegisterStream(
            PendingCallKind.ServerStreaming,
            dispatcher,
            deadline: default,
            cancellationToken: CancellationToken.None);
        session.StreamManager.Register(requestId, dispatcher);
        await session.StreamManager.DispatchChunkAsync(
            requestId,
            new ReadOnlySequence<byte>(new byte[] { 1 }));

        var remoteCompletion = Task.Run(
            () => connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.RemoteStreamComplete));
        await dispatcher.CompleteEntered.WaitAsync(RaceCoordinationTimeout);
        Ensure(!connection.PendingCalls.Contains(requestId),
            "the remote terminal winner must remove the pending slot before the abandon loser joins it");

        var consumerAbandonment = connection.OnConsumerAbandonedAsync(
            requestId,
            dispatcher.DispatchState).AsTask();
        Ensure(!consumerAbandonment.IsCompleted,
            "consumer abandonment must wait for remote final-credit completion and detach");

        dispatcher.ReleaseCompletion();
        Ensure(await remoteCompletion.WaitAsync(RaceCoordinationTimeout),
            "the remote terminal transition must own the pending completion race");
        await consumerAbandonment.WaitAsync(RaceCoordinationTimeout);
        Ensure(connection.ActiveCallCount == 0 && !connection.PendingCalls.Contains(requestId),
            "remote completion and consumer abandonment must settle the pending slot and active count exactly once");

        var frames = await FlushAndReadFramesAsync(session, output);
        var orderedFrames = frames
            .Where(frame => frame.RequestId == unchecked((ulong)requestId))
            .Select(frame => frame.Type)
            .ToArray();
        Ensure(orderedFrames.SequenceEqual([
                ProtocolV2FrameType.WindowUpdate,
                ProtocolV2FrameType.Cancel
            ]),
            "the final WindowUpdate must enter the shared send pump before ConsumerAbandoned Cancel");
    }

    [Test]
    [NotInParallel]
    public async Task SessionDisconnectShouldCancelDetachWaitWithoutSendingCancel()
    {
        await using var owner = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var runtimeContext = (SharpLinkRuntimeContext)owner.RuntimeContext;
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "consumer-abandon-disconnect",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(runtimeContext));
        using var connectionCancellation = new CancellationTokenSource();
        await using var connection = new ClientConnection(
            owner,
            session,
            connectionCancellation,
            maxPendingCalls: 8,
            runtimeContext);
        var dispatchState = new ControlledDispatchState();

        var consumerAbandonment = connection.OnConsumerAbandonedAsync(72, dispatchState).AsTask();
        await dispatchState.WaitEntered.WaitAsync(RaceCoordinationTimeout);
        Ensure(!consumerAbandonment.IsCompleted && !dispatchState.IsDetached,
            "the still-connected consumer abandonment path must wait for detach");

        session.NotifyDisconnected(new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "controlled disconnect"));
        await consumerAbandonment.WaitAsync(RaceCoordinationTimeout);

        Ensure(!session.IsConnected && session.LifetimeToken.IsCancellationRequested,
            "session teardown must cancel the framework-owned detach-wait lifetime token");
        Ensure(!dispatchState.IsDetached,
            "disconnect must end the wait rather than requiring a never-arriving detach");
        if (output.Reader.TryRead(out var read))
        {
            try
            {
                Ensure(read.Buffer.IsEmpty,
                    "disconnect completion must not enqueue a Cancel control frame after the terminal boundary");
            }
            finally
            {
                output.Reader.AdvanceTo(read.Buffer.End);
            }
        }
    }

    private static async Task<List<(ProtocolV2FrameType Type, ulong RequestId)>> FlushAndReadFramesAsync(
        RpcSession session,
        Pipe output)
    {
        await session.FlushSendQueueAsync().AsTask().WaitAsync(RaceCoordinationTimeout);
        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(RaceCoordinationTimeout);
        var remaining = read.Buffer;
        var frames = new List<(ProtocolV2FrameType, ulong)>();
        while (ProtocolV2FrameParser.TryReadFrame(
                   ref remaining,
                   session.RuntimeContext.Protocol,
                   out var header,
                   out _))
        {
            frames.Add((header.Type, header.RequestId));
        }

        Ensure(remaining.IsEmpty, "the send-pump output must contain complete Protocol v2 frames");
        output.Reader.AdvanceTo(read.Buffer.End);
        return frames;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class CreditHoldingLeaseDispatcher :
        IStreamConsumptionAwareDispatcher,
        IStreamDispatchLease
    {
        private readonly TaskCompletionSource _completeEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<long, ushort, int>? _bytesConsumed;
        private long _requestId;
        private ushort _streamId;
        private int _completed;

        internal IStreamDispatchState DispatchState { get; private set; } = null!;

        internal Task CompleteEntered => _completeEntered.Task;

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
            => DispatchAsync(payload, checked((int)payload.Length));

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            _ = payload;
            _ = encodedByteCount;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
            => Complete(isError
                ? new SharpLinkException(
                    SharpLinkErrorCode.RemoteError,
                    errorMessage ?? "Remote stream error.")
                : null);

        public void Complete(Exception? exception)
        {
            _ = exception;
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            _bytesConsumed?.Invoke(_requestId, _streamId, 1);
            _completeEntered.TrySetResult();
            _releaseCompletion.Task.GetAwaiter().GetResult();
        }

        public void SetBytesConsumedCallback(
            Action<long, ushort, int>? callback,
            long requestId,
            ushort streamId)
        {
            _bytesConsumed = callback;
            _requestId = requestId;
            _streamId = streamId;
        }

        internal void ReleaseCompletion() => _releaseCompletion.TrySetResult();

        void IStreamDispatchLease.BindDispatchState(IStreamDispatchState state)
            => DispatchState = state;

        ValueTask IStreamDispatchLease.DispatchAcquiredAsync(
            ReadOnlySequence<byte> payload,
            int encodedByteCount)
            => DispatchAsync(payload, encodedByteCount);

        void IStreamDispatchLease.OnDispatchesDrained()
        {
        }
    }

    private sealed class ControlledDispatchState : IStreamDispatchState
    {
        private readonly TaskCompletionSource _waitEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _detached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WaitEntered => _waitEntered.Task;

        public bool HasActiveDispatches => false;

        public bool IsDetached => false;

        public ValueTask WaitForDispatchesDrainedAsync() => ValueTask.CompletedTask;

        public ValueTask WaitForDetachedAsync(CancellationToken cancellationToken)
        {
            _waitEntered.TrySetResult();
            if (IsDetached)
                return ValueTask.CompletedTask;

            return cancellationToken.CanBeCanceled
                ? new ValueTask(_detached.Task.WaitAsync(cancellationToken))
                : new ValueTask(_detached.Task);
        }

        public void Close()
        {
        }
    }
}
