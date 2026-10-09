using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamManagerTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LastReleaseMustNotRenotifyAfterDrainContinuationDetaches(bool throwOnDrain)
    {
        var failure = new InvalidOperationException("controlled drain callback failure");
        var dispatcher = new DrainOwnershipDispatcher(throwOnDrain ? failure : null);
        var manager = new StreamManager();
        manager.Register(9301, dispatcher);
        var dispatch = manager.DispatchChunkAsync(9301,
            new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();

        // Deterministically schedule the legitimate drain -> Detach continuation before
        // Release resumes after signaling it. Production's asynchronous continuation can
        // reach the same point on another worker. No production hook or timer is needed.
        ForceInlineDrainContinuation(dispatcher.State);
        var drain = manager.CompleteStreamAfterDispatchesAsync(9301, 0, null).AsTask();
        Ensure(!drain.IsCompleted, "the outstanding dispatch must keep drain pending");
        dispatcher.Release();
        Exception? dispatchFailure = null;
        Exception? drainFailure = null;
        try { await dispatch.WaitAsync(RaceCoordinationTimeout); }
        catch (Exception exception) { dispatchFailure = exception; }
        try { await drain.WaitAsync(RaceCoordinationTimeout); }
        catch (Exception exception) { drainFailure = exception; }

        Ensure(dispatchFailure is null,
            "the last release must not invoke a callback already owned by drain finalization");
        Ensure(ReferenceEquals(drainFailure, throwOnDrain ? failure : null),
            "drain finalization alone must observe the exact callback error");
        Ensure(dispatcher.DrainCalls == 1 && dispatcher.State.IsDetached,
            "the detached dispatcher must be notified exactly once, including a throwing callback");
    }

    [Test]
    public async Task SameKeyRegistrationCannotCaptureReceiveLeaseBeforeOldRoutePublishesTerminal()
    {
        var controller = new StreamFlowController(
            streamWindow: 4,
            connectionWindow: 4,
            maxFramePayloadBytes: 1024,
            maxConcurrentStreams: 2);
        var keyedTerminalCalls = 0;

        var resolveCalls = 0;
        StreamFlowController.ResolvedReceiveCreditLease Resolve(long requestId, ushort streamId)
        {
            Interlocked.Increment(ref resolveCalls);
            return controller.ResolveReceiveCreditLease(requestId, streamId);
        }
        void Accept(
            in StreamFlowController.ResolvedReceiveCreditLease lease,
            int bytes)
            => controller.AcceptReceived(in lease, bytes);
        void Consumed(
            in StreamFlowController.ResolvedReceiveCreditLease lease,
            int bytes)
            => _ = controller.RecordConsumed(in lease, bytes);
        void CompleteResolved(StreamFlowController.ResolvedReceiveCreditLease lease)
            => _ = controller.FlushConsumed(in lease);

        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            acceptBytes: null,
            bytesConsumed: null,
            streamCompleted: (requestId, streamId) =>
            {
                Interlocked.Increment(ref keyedTerminalCalls);
                _ = controller.FlushConsumed(requestId, streamId);
            },
            maxActiveStreams: 2,
            activeStreamCapacityExceeded: null,
            resolveReceiveCreditLease: Resolve,
            acceptResolvedBytes: Accept,
            resolvedBytesConsumed: Consumed,
            resolvedStreamCompleted: CompleteResolved);

        const long requestId = 9401;
        const ushort streamId = 7;
        var firstDispatcher = new ImmediateConsumptionDispatcher();
        manager.Register(requestId, streamId, firstDispatcher);
        var firstLease = firstDispatcher.ReceiveCreditLease;
        Ensure(firstLease.IsResolved && Volatile.Read(ref resolveCalls) == 1,
            "first route must retain exactly one resolved receive lease");

        var terminalEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unregister = Task.Run(() =>
        {
            StreamManagerTestHooks.BeforeReceiveTerminalPublish = (blockedRequest, blockedStream) =>
            {
                if (blockedRequest != requestId || blockedStream != streamId)
                    return;
                terminalEntered.TrySetResult();
                releaseTerminal.Task.GetAwaiter().GetResult();
            };
            try
            {
                manager.Unregister(requestId, streamId);
            }
            finally
            {
                StreamManagerTestHooks.BeforeReceiveTerminalPublish = null;
            }
        });

        await terminalEntered.Task.WaitAsync(RaceCoordinationTimeout);

        var racingDispatcher = new ImmediateConsumptionDispatcher();
        try
        {
            manager.Register(requestId, streamId, racingDispatcher);
            throw new Exception("a retiring route key became reusable before its receive generation completed");
        }
        catch (InvalidOperationException)
        {
        }

        Ensure(!racingDispatcher.ReceiveCreditLease.IsResolved,
            "failed overlapping registration must clear the old resolved lease");
        Ensure(Volatile.Read(ref resolveCalls) == 1,
            "a still-mapped retiring key must reject registration before resolving flow state");
        releaseTerminal.TrySetResult();
        await unregister.WaitAsync(RaceCoordinationTimeout);

        var replacement = new ImmediateConsumptionDispatcher();
        manager.Register(requestId, streamId, replacement);
        var replacementLease = replacement.ReceiveCreditLease;
        Ensure(replacementLease.IsResolved &&
            replacementLease.Generation != firstLease.Generation &&
            Volatile.Read(ref resolveCalls) == 2,
            "same-key replacement must resolve exactly one new generation after old terminal publication");

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[] { 1 }));
        Ensure(replacement.DispatchCount == 1,
            "replacement route must accept its first frame through the new generation");
        Ensure(keyedTerminalCalls == 0,
            "resolved production routes must not fall back to keyed terminal completion");

        manager.Unregister(requestId, streamId);
    }

    private static void ForceInlineDrainContinuation(IStreamDispatchState state)
    {
        _ = state.WaitForDispatchesDrainedAsync(); // materialize the lazy completion holder
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var holder = state.GetType().GetField("_completions", flags)?.GetValue(state)
            ?? throw new InvalidOperationException("Expected the lazy drain completion holder.");
        var field = holder.GetType().GetField("_dispatchesDrainedCompletion", flags)
            ?? throw new InvalidOperationException("Expected the drain completion field.");
        field.SetValue(holder, new TaskCompletionSource());
    }

    private sealed class ImmediateConsumptionDispatcher : IStreamConsumptionAwareDispatcher
    {
        private ResolvedStreamBytesCallback? _resolvedBytesConsumed;
        private StreamFlowController.ResolvedReceiveCreditLease _receiveCreditLease;
        private Action<long, ushort, int>? _bytesConsumed;
        private long _requestId;
        private ushort _streamId;

        internal int DispatchCount { get; private set; }
        internal StreamFlowController.ResolvedReceiveCreditLease ReceiveCreditLease
            => _receiveCreditLease;

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
            => DispatchAsync(payload, Math.Max(1, checked((int)payload.Length)));

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            _ = payload;
            DispatchCount++;
            if (_resolvedBytesConsumed is { } resolved)
                resolved(in _receiveCreditLease, encodedByteCount);
            else
                _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
        }

        public void Complete(Exception? exception) => _ = exception;

        public void SetBytesConsumedCallback(
            Action<long, ushort, int>? callback,
            long requestId,
            ushort streamId)
        {
            _bytesConsumed = callback;
            _requestId = requestId;
            _streamId = streamId;
        }

        public bool TrySetResolvedBytesConsumedCallback(
            ResolvedStreamBytesCallback? callback,
            in StreamFlowController.ResolvedReceiveCreditLease lease)
        {
            _resolvedBytesConsumed = callback;
            _receiveCreditLease = lease;
            return true;
        }
    }

    private sealed class DrainOwnershipDispatcher(Exception? failure) : IStreamDispatcher, IStreamDispatchLease
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _drainCalls;
        internal IStreamDispatchState State { get; private set; } = null!;
        internal int DrainCalls => Volatile.Read(ref _drainCalls);
        internal void Release() => _release.TrySetResult();
        public void BindDispatchState(IStreamDispatchState state) => State = state;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => new(_release.Task);
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => DispatchAsync(payload);
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
        public void OnDispatchesDrained()
        {
            Interlocked.Increment(ref _drainCalls);
            if (failure is not null) throw failure;
        }
    }
}
