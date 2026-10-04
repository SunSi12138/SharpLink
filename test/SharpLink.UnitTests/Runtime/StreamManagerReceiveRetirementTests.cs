namespace SharpLink.UnitTests.Runtime;

[NotInParallel("dispatcher-pool")]
public sealed class StreamManagerReceiveRetirementTests
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false, (ushort)0, false, "request")]
    [Arguments(true, (ushort)0, false, "request")]
    [Arguments(false, (ushort)7, false, "request")]
    [Arguments(true, (ushort)7, false, "request")]
    [Arguments(false, (ushort)0, true, "request")]
    [Arguments(true, (ushort)0, true, "request")]
    [Arguments(false, (ushort)7, true, "request")]
    [Arguments(true, (ushort)7, true, "request")]
    [Arguments(false, (ushort)0, false, "stream")]
    [Arguments(true, (ushort)0, false, "stream")]
    [Arguments(false, (ushort)7, false, "stream")]
    [Arguments(true, (ushort)7, false, "stream")]
    [Arguments(false, (ushort)0, true, "stream")]
    [Arguments(true, (ushort)0, true, "stream")]
    [Arguments(false, (ushort)7, true, "stream")]
    [Arguments(true, (ushort)7, true, "stream")]
    [Arguments(false, (ushort)0, false, "unregister")]
    [Arguments(true, (ushort)0, false, "unregister")]
    [Arguments(false, (ushort)7, false, "unregister")]
    [Arguments(true, (ushort)7, false, "unregister")]
    [Arguments(false, (ushort)0, true, "unregister")]
    [Arguments(true, (ushort)0, true, "unregister")]
    [Arguments(false, (ushort)7, true, "unregister")]
    [Arguments(true, (ushort)7, true, "unregister")]
    public async Task RequestCompletionMustKeepAcquiredReceiveOwnershipUntilDispatchReleases(
        bool pauseAfterDebit, ushort streamId, bool compressed, string completion)
    {
        const long requestId = 9501;
        const int bytes = 4;
        var controller = new StreamFlowController(8, 8, 1024, maxConcurrentStreams: 1);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        using var dispatchEntered = new ManualResetEventSlim();
        using var resumeDispatch = new ManualResetEventSlim();
        var consumed = 0;
        var publishedCredit = 0;
        var terminals = 0;
        var originalLease = default(StreamFlowController.ResolvedReceiveCreditLease);
        StreamFlowController.ResolvedReceiveCreditLease Resolve(long id, ushort stream)
        {
            var lease = controller.ResolveReceiveCreditLease(id, stream);
            if (id == requestId) originalLease = lease;
            return lease;
        }
        void Accept(in StreamFlowController.ResolvedReceiveCreditLease lease, int count)
        {
            if (pauseAfterDebit) controller.AcceptReceived(in lease, count);
            dispatchEntered.Set();
            if (!resumeDispatch.Wait(CoordinationTimeout))
                throw new TimeoutException("The acquired dispatch was not released by the test.");
            if (!pauseAfterDebit) controller.AcceptReceived(in lease, count);
        }
        void Consumed(in StreamFlowController.ResolvedReceiveCreditLease lease, int count)
        {
            Require(lease.Generation == originalLease.Generation,
                "consumption must retain the acquired receive generation");
            Interlocked.Add(ref consumed, count);
            Interlocked.Add(ref publishedCredit, controller.RecordConsumed(in lease, count));
        }
        void Terminal(StreamFlowController.ResolvedReceiveCreditLease lease)
        {
            Interlocked.Increment(ref terminals);
            Interlocked.Add(ref publishedCredit, controller.FlushConsumed(in lease));
        }
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null,
            maxActiveStreams: 2, activeStreamCapacityExceeded: null,
            resolveReceiveCreditLease: Resolve, acceptResolvedBytes: Accept,
            resolvedBytesConsumed: Consumed, resolvedStreamCompleted: Terminal);
        var dispatcher = new PreAdmissionStreamDispatcher(buffers,
            static _ => true, static _ => { },
            static () => throw new InvalidOperationException("Unexpected mailbox capacity failure."));
        manager.Register(requestId, streamId, dispatcher);
        var dispatch = Task.Run(async () =>
        {
            if (compressed)
            {
                Require(manager.TryDispatchPreAdmissionCompressed(requestId, streamId,
                    new ReadOnlySequence<byte>(new byte[] { 1, 2 }), bytes, out var pending),
                    "the actual compressed pre-admission route must acquire the frame");
                await pending;
            }
            else
            {
                await manager.DispatchChunkAsync(requestId, streamId,
                    new ReadOnlySequence<byte>(new byte[bytes]));
            }
        });
        try
        {
            Require(dispatchEntered.Wait(CoordinationTimeout),
                "the route must be acquired before request failure cleanup");
            var failure = new OperationCanceledException("request failed");
            if (completion == "request") manager.CompleteRequestStreams(requestId, failure);
            else if (completion == "stream") manager.CompleteStream(requestId, streamId, failure);
            else
            {
                manager.Unregister(requestId, streamId);
                dispatcher.Complete(failure); // consumer/owner terminal follows route unregister
            }
            Require(!dispatch.IsCompleted,
                "request completion must not block on or counterfeit completion of the acquired dispatch");
            Require(Volatile.Read(ref terminals) == 0,
                "an acquired frame pins the old receive generation before and after its debit");
            manager.CompleteRequestStreams(requestId, new OperationCanceledException("duplicate completion"));
            resumeDispatch.Set();
            await dispatch.WaitAsync(CoordinationTimeout);
            Require(Volatile.Read(ref consumed) == bytes,
                "request completion lost the acquired frame's receive-credit callback");
            Require(Volatile.Read(ref publishedCredit) == bytes && Volatile.Read(ref terminals) == 1,
                "the old generation must publish its complete final credit and terminal exactly once");

            // A single retained receive state would exhaust this controller's capacity.
            var next = controller.ResolveReceiveCreditLease(requestId + 1, streamId);
            Require(next.Generation != originalLease.Generation,
                "the completed receive tombstone must retire before the next lifecycle");
            controller.AcceptReceived(in next, 8);
            _ = controller.RecordConsumed(in next, 8);
            _ = controller.FlushConsumed(in next);
            Require(manager.ActiveStreamCount == 0, "request retirement must release route accounting");
        }
        finally
        {
            resumeDispatch.Set();
            try { await dispatch.WaitAsync(CoordinationTimeout); } catch (Exception) { }
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            controller.Complete(new OperationCanceledException("fixture cleanup"));
        }
    }

    [Test]
    public async Task FailedDispatcherBindingMustNotAttachOwnerlessReceiveState()
    {
        PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        var alreadyBound = new StreamManager();
        var dispatcher = PooledAsyncStreamDispatcher<string?>.Rent(default, StringCodec.Instance);
        alreadyBound.Register(9510, 1, dispatcher);
        var controller = new StreamFlowController(8, 8, 1024, maxConcurrentStreams: 1);
        var resolves = 0;
        StreamFlowController.ResolvedReceiveCreditLease Resolve(long id, ushort stream)
        {
            resolves++;
            return controller.ResolveReceiveCreditLease(id, stream);
        }
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null,
            maxActiveStreams: 1, activeStreamCapacityExceeded: null,
            resolveReceiveCreditLease: Resolve);
        try
        {
            Exception? failure = null;
            try { manager.Register(9511, 1, dispatcher); }
            catch (Exception error) { failure = error; }
            Require(failure is InvalidOperationException && manager.ActiveStreamCount == 0,
                "bind failure must remain observable and roll back active-route accounting");
            Require(resolves == 0,
                "an already-bound dispatcher must be rejected before attaching receive flow state");
            var next = controller.ResolveReceiveCreditLease(9512, 1);
            controller.AcceptReceived(in next, 8);
            _ = controller.RecordConsumed(in next, 8);
            _ = controller.FlushConsumed(in next);
        }
        finally
        {
            alreadyBound.Unregister(9510, 1);
            dispatcher.Complete(exception: null);
            await dispatcher.DisposeAsync();
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            controller.Complete(new OperationCanceledException("fixture cleanup"));
            PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        }
    }

    [Test]
    public async Task FailedRebindMustNotClearTheAlreadyOwnedReceiveCallback()
    {
        PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        var controller = new StreamFlowController(32, 32, 1024, maxConcurrentStreams: 2);
        var consumed = 0;
        void Consumed(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
        {
            _ = controller.RecordConsumed(in lease, bytes);
            consumed += bytes;
        }
        void Accept(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
            => controller.AcceptReceived(in lease, bytes);
        var owner = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 2, null,
            controller.ResolveReceiveCreditLease, Accept, Consumed,
            lease => { _ = controller.FlushConsumed(in lease); });
        var dispatcher = PooledAsyncStreamDispatcher<string?>.Rent(default, StringCodec.Instance);
        owner.Register(9520, 1, dispatcher);
        var otherController = new StreamFlowController(8, 8, 1024, maxConcurrentStreams: 1);
        var rejected = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 1, null,
            otherController.ResolveReceiveCreditLease);
        try
        {
            Exception? failure = null;
            try { rejected.Register(9521, 1, dispatcher); }
            catch (Exception error) { failure = error; }
            Require(failure is InvalidOperationException, "the second binding must fail");
            var packet = new ArrayBufferWriter<byte>();
            var value = "old-route";
            StringCodec.Instance.Serialize(in value, packet);
            await owner.DispatchChunkAsync(9520, 1, new ReadOnlySequence<byte>(packet.WrittenMemory));
            dispatcher.Complete(exception: null);
            await dispatcher.DisposeAsync();
            Require(consumed == packet.WrittenCount,
                "failed registration erased the original route's generation-bound callback");
        }
        finally
        {
            owner.Unregister(9520, 1);
            dispatcher.Complete(exception: null);
            await dispatcher.DisposeAsync();
            controller.Complete(new OperationCanceledException("fixture cleanup"));
            otherController.Complete(new OperationCanceledException("fixture cleanup"));
            PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReleaseDuringDispatcherCompletionMustNotLoseDeferredRetirement(bool completionThrows)
    {
        var controller = new StreamFlowController(8, 8, 1024, maxConcurrentStreams: 1);
        var dispatcher = new ControlledConsumptionDispatcher();
        var consumed = 0;
        var terminalCredit = 0;
        void Accept(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
            => controller.AcceptReceived(in lease, bytes);
        void Consumed(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
        {
            consumed += bytes;
            _ = controller.RecordConsumed(in lease, bytes);
        }
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 1, null,
            controller.ResolveReceiveCreditLease, Accept, Consumed,
            lease => { terminalCredit += controller.FlushConsumed(in lease); });
        manager.Register(9530, 1, dispatcher);
        var dispatch = Task.Run(async () => await manager.DispatchChunkAsync(9530, 1,
            new ReadOnlySequence<byte>(new byte[] { 1 })));
        var expected = new InvalidOperationException("controlled completion failure");
        try
        {
            await dispatcher.Entered.Task.WaitAsync(CoordinationTimeout);
            dispatcher.OnComplete = () =>
            {
                dispatcher.Resume.Set();
                dispatch.WaitAsync(CoordinationTimeout).GetAwaiter().GetResult();
                if (completionThrows) throw expected;
            };
            Exception? failure = null;
            try { manager.CompleteRequestStreams(9530, expected); }
            catch (Exception error) { failure = error; }
            Require(ReferenceEquals(failure, completionThrows ? expected : null),
                "synchronous completion failure must retain its exact identity");
            Require(consumed == 1 && terminalCredit == 1 && !dispatcher.HasCallback,
                "release before continuation installation must still flush and detach exactly once");
            var next = controller.ResolveReceiveCreditLease(9531, 1);
            _ = controller.FlushConsumed(in next);
        }
        finally
        {
            dispatcher.Resume.Set();
            try { await dispatch.WaitAsync(CoordinationTimeout); } catch (Exception) { }
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            controller.Complete(new OperationCanceledException("fixture cleanup"));
            dispatcher.Resume.Dispose();
        }
    }

    [Test]
    public async Task DeferredCallbackFailureMustNotReleaseTheAcquisitionTwice()
    {
        var controller = new StreamFlowController(8, 8, 1024, maxConcurrentStreams: 1);
        var expected = new InvalidOperationException("controlled callback-detach failure");
        var dispatcher = new ControlledConsumptionDispatcher { ClearFailure = expected };
        var credited = 0;
        void Accept(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
            => controller.AcceptReceived(in lease, bytes);
        void Consumed(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
        {
            credited += bytes;
            _ = controller.RecordConsumed(in lease, bytes);
        }
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 1, null,
            controller.ResolveReceiveCreditLease, Accept, Consumed,
            lease => { _ = controller.FlushConsumed(in lease); });
        manager.Register(9540, 1, dispatcher);
        var dispatch = Task.Run(async () => await manager.DispatchChunkAsync(9540, 1,
            new ReadOnlySequence<byte>(new byte[] { 1 })));
        try
        {
            await dispatcher.Entered.Task.WaitAsync(CoordinationTimeout);
            manager.CompleteRequestStreams(9540, new OperationCanceledException("request failed"));
            dispatcher.Resume.Set();
            Exception? failure = null;
            try { await dispatch.WaitAsync(CoordinationTimeout); }
            catch (Exception error) { failure = error; }
            Require(ReferenceEquals(failure, expected),
                "final-release cleanup failure must not become an acquisition-underflow failure");
            Require(credited == 1 && !dispatcher.HasCallback && manager.ActiveStreamCount == 0,
                "throwing detach callback must not strand credit or route accounting");
            var next = controller.ResolveReceiveCreditLease(9541, 1);
            _ = controller.FlushConsumed(in next);
        }
        finally
        {
            dispatcher.Resume.Set();
            try { await dispatch.WaitAsync(CoordinationTimeout); } catch (Exception) { }
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            controller.Complete(new OperationCanceledException("fixture cleanup"));
            dispatcher.Resume.Dispose();
        }
    }

    [Test]
    public async Task DetachedDispatcherMustStayPinnedWhileDeferredTerminalPublishesCredit()
    {
        var creditEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeCredit = new ManualResetEventSlim();
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null,
            (_, _) =>
            {
                creditEntered.TrySetResult();
                if (!resumeCredit.Wait(CoordinationTimeout))
                    throw new TimeoutException("Deferred credit publication did not resume.");
            });
        var dispatcher = new ControlledConsumptionDispatcher();
        manager.Register(9550, 1, dispatcher);
        var dispatch = Task.Run(async () => await manager.DispatchChunkAsync(9550, 1,
            new ReadOnlySequence<byte>(new byte[] { 1 })));
        try
        {
            await dispatcher.Entered.Task.WaitAsync(CoordinationTimeout);
            manager.Unregister(9550, 1);
            await dispatcher.State.WaitForDetachedAsync(CancellationToken.None).AsTask().WaitAsync(CoordinationTimeout);
            dispatcher.Resume.Set();
            await creditEntered.Task.WaitAsync(CoordinationTimeout);
            Require(dispatcher.State.IsDetached && dispatcher.State.HasActiveDispatches && dispatcher.DrainedCalls == 0,
                "logical detach must not let the pool reuse the dispatcher during deferred credit publication");
            resumeCredit.Set();
            await dispatch.WaitAsync(CoordinationTimeout);
            Require(!dispatcher.State.HasActiveDispatches && dispatcher.DrainedCalls == 1,
                "the cleanup pin must release and notify the pool exactly once after credit publication");
        }
        finally
        {
            resumeCredit.Set();
            dispatcher.Resume.Set();
            try { await dispatch.WaitAsync(CoordinationTimeout); } catch (Exception) { }
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            dispatcher.Resume.Dispose();
        }
    }

    private sealed class ControlledConsumptionDispatcher : IStreamConsumptionAwareDispatcher, IStreamDispatchLease
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Resume = new();
        internal Action? OnComplete;
        internal Exception? ClearFailure;
        internal IStreamDispatchState State { get; private set; } = null!;
        private int _drainedCalls;
        internal int DrainedCalls => Volatile.Read(ref _drainedCalls);
        public void BindDispatchState(IStreamDispatchState state) => State = state;
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
            => DispatchAsync(payload, encodedByteCount);
        public void OnDispatchesDrained() => Interlocked.Increment(ref _drainedCalls);
        private ResolvedStreamBytesCallback? _consumed;
        private StreamFlowController.ResolvedReceiveCreditLease _lease;
        internal bool HasCallback => _consumed is not null;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => DispatchAsync(payload, (int)payload.Length);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            Entered.TrySetResult();
            if (!Resume.Wait(CoordinationTimeout)) throw new TimeoutException("Controlled dispatch did not resume.");
            _consumed?.Invoke(in _lease, encodedByteCount);
            return ValueTask.CompletedTask;
        }
        public void Complete(bool isError, string? errorMessage) => OnComplete?.Invoke();
        public void Complete(Exception? exception) => OnComplete?.Invoke();
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback, long requestId, ushort streamId) { }
        public bool TrySetResolvedBytesConsumedCallback(ResolvedStreamBytesCallback? callback,
            in StreamFlowController.ResolvedReceiveCreditLease lease)
        {
            _consumed = callback;
            _lease = lease;
            if (callback is null && ClearFailure is { } error) throw error;
            return true;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
