using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class ClientConnectionConsumerAbandonmentTests
{
    private static readonly TimeSpan RaceCoordinationTimeout = TimeSpan.FromSeconds(10);

    [Test]
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

        var remoteCompletion = LongRunningTestWorker.Run(
            () => connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.RemoteStreamComplete));
        Task? consumerAbandonment = null;
        try
        {
            await dispatcher.CompleteEntered.WaitAsync(RaceCoordinationTimeout);
            Ensure(!connection.PendingCalls.Contains(requestId),
                "the remote terminal winner must remove the pending slot before the abandon loser joins it");

            consumerAbandonment = connection.OnConsumerAbandonedAsync(
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
        finally
        {
            dispatcher.ReleaseCompletion();
            await LongRunningTestWorker.JoinAsync(remoteCompletion, RaceCoordinationTimeout);
            if (consumerAbandonment is not null)
                await LongRunningTestWorker.JoinAsync(consumerAbandonment, RaceCoordinationTimeout);
        }
    }

    [Test]
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

    [Test]
    [NotInParallel]
    public async Task ThrowingCancellationCompletionShouldEvictFatalConnectionBeforeReadinessWait()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new ReadyThenBlockingTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(timeProvider);
            builder.UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
            builder.UseReconnectJitterForTesting(new FixedReconnectJitter(
                TimeSpan.FromMilliseconds(100)));
        });
        await client.ConnectAsync();
        var connection = GetReadyConnections(client).Single();
        Ensure(client.GetReadinessSnapshot() == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Ready,
                ActiveEndpoints: 1,
                ReadyEndpoints: 1,
                ReadyConnections: 1,
                TargetReadyEndpoints: 1) &&
               client.ReadyConnectionCount == 1,
            "the real fixed client must begin with exactly one published ready connection");

        var triggerFailure = new InvalidOperationException(
            "deterministic server-stream completion failure");
        var ownerCleanupFailure = new InvalidOperationException(
            "deterministic owner-wide stream cleanup failure");
        var dispatcher = new ThrowingCompleteDispatcher(triggerFailure);
        var requestId = connection.PendingCalls.RegisterStream(
            PendingCallKind.ServerStreaming,
            dispatcher,
            deadline: default,
            cancellationToken: CancellationToken.None);
        connection.Session.StreamManager.Register(requestId, dispatcher);
        connection.Session.StreamManager.Register(
            requestId + 1,
            new ThrowingCompleteDispatcher(ownerCleanupFailure));

        var completionFailure = CaptureException(() => connection.PendingCalls.TryComplete(
            requestId,
            PendingCallCompletionReason.UserCancellation));
        Ensure(ReferenceEquals(completionFailure, ownerCleanupFailure),
            "the second registered stream must make ClientConnection.Fail surface its exact completion failure");

        var disconnected = client.GetReadinessSnapshot();
        Ensure(disconnected == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Reconnecting,
                ActiveEndpoints: 1,
                ReadyEndpoints: 0,
                ReadyConnections: 0,
                TargetReadyEndpoints: 1),
            "a fatal dispatcher completion failure must synchronously publish Reconnecting/0");
        Ensure(connection.State == ClientConnectionState.Closed &&
               !connection.PendingCalls.Contains(requestId),
            "fatal stream cleanup must close the connection and settle the pending call");
        Ensure(client.ReadyConnectionCount == 0 && GetReadyConnections(client).Length == 0,
            "the failed owner must be absent from both the ready count and selection snapshot");

        using var waiterCancellation = new CancellationTokenSource();
        var readiness = client.WaitForReadinessAsync(1, waiterCancellation.Token).AsTask();
        await transport.LaterAttemptStarted.Task.WaitAsync(RaceCoordinationTimeout);
        Ensure(!readiness.IsCompleted &&
               client.ReadyConnectionCount == 0 &&
               GetReadyConnections(client).Length == 0,
            "WaitForReadinessAsync(1) must remain pending instead of observing the retired snapshot");

        waiterCancellation.Cancel();
        var cancellation = await CaptureExceptionAsync(readiness);
        Ensure(cancellation is OperationCanceledException,
            "cancelling the test waiter must end only that pending readiness observation");
    }

    [Test]
    [NotInParallel]
    public async Task AsyncDrainFailureShouldEvictConnectionAfterOutstandingDispatchReleases()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new ReadyThenBlockingTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(timeProvider);
            builder.UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
            builder.UseReconnectJitterForTesting(new FixedReconnectJitter(
                TimeSpan.FromMilliseconds(100)));
        });
        await client.ConnectAsync();
        var connection = GetReadyConnections(client).Single();
        var drainFailure = new InvalidOperationException(
            "deterministic asynchronous drain finalization failure");
        var dispatcher = new AsyncDrainThrowingDispatcher(drainFailure);
        var requestId = connection.PendingCalls.RegisterStream(
            PendingCallKind.ServerStreaming,
            dispatcher,
            deadline: default,
            cancellationToken: CancellationToken.None);
        connection.Session.StreamManager.Register(requestId, dispatcher);

        var dispatch = connection.Session.StreamManager.DispatchChunkAsync(
            requestId,
            new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await dispatcher.DispatchEntered.Task.WaitAsync(RaceCoordinationTimeout);
        Ensure(!dispatch.IsCompleted,
            "the controlled stream dispatch must hold the manager's active lease");

        Ensure(connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.UserCancellation),
            "user cancellation must start the asynchronous drain path");
        await dispatcher.CompleteCalled.Task.WaitAsync(RaceCoordinationTimeout);
        Ensure(client.GetReadinessSnapshot().State == SharpLinkConnectionState.Ready &&
               client.ReadyConnectionCount == 1 &&
               connection.State == ClientConnectionState.Ready,
            "the owner must remain published until the outstanding dispatch actually drains");

        dispatcher.ReleaseDispatch();
        await dispatch.WaitAsync(RaceCoordinationTimeout);
        await dispatcher.DrainFailureRaised.Task.WaitAsync(RaceCoordinationTimeout);
        var disconnected = await WaitForReadinessSnapshotAsync(
            client,
            static snapshot => snapshot.State == SharpLinkConnectionState.Reconnecting &&
                               snapshot.ReadyConnections == 0);

        Ensure(disconnected == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Reconnecting,
                ActiveEndpoints: 1,
                ReadyEndpoints: 0,
                ReadyConnections: 0,
                TargetReadyEndpoints: 1),
            "an asynchronous drain finalization failure must publish Reconnecting/0");
        Ensure(connection.State == ClientConnectionState.Closed &&
               client.ReadyConnectionCount == 0 &&
               GetReadyConnections(client).Length == 0,
            "async fatal cleanup must close and remove the owner before another call can select it");

        await client.StopAsync().AsTask().WaitAsync(RaceCoordinationTimeout);
        Ensure(connection.ActiveCallCount == 0 &&
               client.FrameworkTaskSnapshotForDiagnostics.ActiveTasks == 0,
            "Stop must deterministically join the async cleanup and release its active call");
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

    private static ClientConnection[] GetReadyConnections(SharpLinkClient client)
        => (ClientConnection[])(typeof(SharpLinkClient).GetField(
                "_readyConnections",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find ready connection selection snapshot"));

    private static async Task<SharpLinkClientReadinessSnapshot> WaitForReadinessSnapshotAsync(
        SharpLinkClient client,
        Func<SharpLinkClientReadinessSnapshot, bool> predicate)
    {
        while (true)
        {
            var publication = client.ReadinessPublicationForTesting;
            if (predicate(publication.Snapshot))
                return publication.Snapshot;
            await publication.Changed.Task.WaitAsync(RaceCoordinationTimeout);
        }
    }

    private static async Task<Exception> CaptureExceptionAsync(Task operation)
    {
        try
        {
            await operation;
            return new Exception("expected the operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception CaptureException(Action operation)
    {
        try
        {
            operation();
            return new Exception("expected the operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class ReadyThenBlockingTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private readonly TaskCompletionSource _laterRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        internal TaskCompletionSource LaterAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) > 1)
            {
                LaterAttemptStarted.TrySetResult();
                await _laterRelease.Task.WaitAsync(cancellationToken);
            }

            var connection = new TestTransportConnection();
            using var payload = new PooledByteBufferWriter();
            ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024));
            await connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0,
                payload.WrittenMemory,
                cancellationToken);
            lock (_gate)
                _connections.Add(connection);
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            _laterRelease.TrySetResult();
            TestTransportConnection[] connections;
            lock (_gate)
                connections = [.. _connections];
            for (var index = 0; index < connections.Length; index++)
                await connections[index].DisposeAsync();
        }
    }

    private sealed class FixedReconnectJitter(TimeSpan delay) : ISharpLinkReconnectJitter
    {
        public TimeSpan AddQuarterWindow(int baseDelayMilliseconds)
        {
            _ = baseDelayMilliseconds;
            return delay;
        }

        public TimeSpan ScaleTwentyPercent(int baseDelayMilliseconds)
        {
            _ = baseDelayMilliseconds;
            return delay;
        }
    }

    private sealed class ThrowingCompleteDispatcher(Exception failure) : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
            throw failure;
        }

        public void Complete(Exception? exception)
        {
            _ = exception;
            throw failure;
        }
    }

    private sealed class AsyncDrainThrowingDispatcher(Exception failure) :
        IStreamDispatcher,
        IStreamDispatchLease
    {
        private readonly TaskCompletionSource _dispatchRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DispatchEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CompleteCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DrainFailureRaised { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
            => DispatchAcquiredAsync(payload, checked((int)payload.Length));

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
            CompleteCalled.TrySetResult();
        }

        public void Complete(Exception? exception)
        {
            _ = exception;
            CompleteCalled.TrySetResult();
        }

        public void BindDispatchState(IStreamDispatchState state)
        {
            _ = state;
        }

        public ValueTask DispatchAcquiredAsync(
            ReadOnlySequence<byte> payload,
            int encodedByteCount)
        {
            _ = payload;
            _ = encodedByteCount;
            DispatchEntered.TrySetResult();
            return new ValueTask(_dispatchRelease.Task);
        }

        public void OnDispatchesDrained()
        {
            DrainFailureRaised.TrySetResult();
            throw failure;
        }

        internal void ReleaseDispatch() => _dispatchRelease.TrySetResult();
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
