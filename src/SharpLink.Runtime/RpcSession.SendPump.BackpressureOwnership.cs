namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal sealed partial class SendPump
    {
        // Immutable marker: it never owns nodes or a session. Its publication
        // makes a concurrent release/stop join the gate before the final recheck,
        // without allocating a queue for an operation that recovers immediately.
        private static readonly PendingEnqueueQueue RegistrationInProgress = new(Task.CompletedTask);
        // Exceptional capacity-path cleanup only, protected by the existing cold gate.
        // A removed node cannot make its physical cleanup error disappear before a stop join.
        private Exception? _backpressureCleanupFailure;

        // Replaces the broadcast capacity signal with the pending operation's
        // own frame and completion. No second maintenance census is maintained.
        private ValueTask<SendEnqueueResult> EnqueueWhenFull(
            OwnedFrame frame,
            CancellationToken cancellationToken)
        {
            Exception? failure = null;
            PendingEnqueue? pending = null;
            var reserved = false;
            try
            {
                lock (_admissionGate)
                {
                    var queue = _pumpLifetime as PendingEnqueueQueue;
                    // Publish before the final capacity AND stop rechecks. A pulse may
                    // skip this gate only while the capacity slot is null.
                    Volatile.Write(ref _capacityChanged, queue ?? RegistrationInProgress);
                    if (cancellationToken.IsCancellationRequested)
                        failure = new OperationCanceledException(cancellationToken);
                    else if (Volatile.Read(ref _stopped) != 0)
                        failure = CreateTransportClosedException();
                    else if (!(reserved = TryReserve(frame.Length, frame.IsProtocolProgress)))
                    {
                        if (queue is null)
                        {
                            queue = new PendingEnqueueQueue((Task)_pumpLifetime);
                            Volatile.Write(ref _pumpLifetime, queue);
                            _capacityChanged = queue;
                        }
                        pending = new PendingEnqueue(this, queue, frame, cancellationToken);
                        queue.Add(pending);
                        try { pending.RegisterCancellation(); }
                        catch (Exception exception)
                        {
                            pending.Failure = exception;
                            queue.Retire(pending);
                            failure = exception;
                        }
                    }
                    // Synchronous metric callbacks can publish a new waiting queue.
                    queue = _pumpLifetime as PendingEnqueueQueue;
                    _capacityChanged = queue?.LastWaiting is null ? null : queue;
                }
            }
            catch (Exception exception) { failure = exception; }
            if (pending is not null)
            {
                if (failure is not null)
                    CompletePendingEnqueue(pending); // Outside the gate, node already owns frame.
                return new ValueTask<SendEnqueueResult>(pending.Task);
            }
            if (failure is not null)
            {
                try
                {
                    if (reserved) CompleteReserved(frame, exception: null, completeFlushWaiter: false);
                    else ReturnUnreserved(frame, failure);
                }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
                return ValueTask.FromException<SendEnqueueResult>(failure!);
            }
            if (Volatile.Read(ref _stopped) == 0 &&
                (frame.IsProtocolProgress ? _progressQueue : _normalQueue).Writer.TryWrite(frame))
            {
                RecordTerminalQueueAcceptance(frame);
                SignalAcceptedPublication();
                return ValueTask.FromResult(SendEnqueueResult.Accepted);
            }
            CompleteReserved(frame, exception: null, completeFlushWaiter: false);
            return ValueTask.FromResult(SendEnqueueResult.Closed);
        }

        private void PulseCapacityWaiters()
        {
            if (Volatile.Read(ref _capacityChanged) is null)
                return;
            ResumePendingEnqueues();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ResumePendingEnqueues()
        {
            PendingEnqueue? ready = null;
            lock (_admissionGate)
            {
                var queue = _capacityChanged;
                var pending = queue?.Head;
                while (pending is not null && !pending.Completing)
                {
                    var next = pending.Next;
                    if (Volatile.Read(ref _stopped) != 0)
                    {
                        pending.Failure = CreateTransportClosedException();
                    }
                    else if (pending.CancellationToken.IsCancellationRequested)
                    {
                        pending.Failure = new OperationCanceledException(pending.CancellationToken);
                    }
                    else
                    {
                        var frame = pending.Frame;
                        // TryReserve publishes synchronous telemetry. Claim the
                        // node before a listener can reenter cancellation/Stop
                        // and start a second cleanup owner. A failed reserve
                        // publishes nothing, so it can safely remain waiting.
                        pending.Completing = true;
                        try { pending.Reserved = TryReserve(frame.Length, frame.IsProtocolProgress); }
                        catch (Exception exception) { pending.Failure = exception; }
                        if (!pending.Reserved && pending.Failure is null)
                        {
                            pending.Completing = false;
                            pending = next;
                            continue;
                        }
                        if (pending.Reserved && Volatile.Read(ref _stopped) == 0 &&
                            (frame.IsProtocolProgress ? _progressQueue : _normalQueue).Writer.TryWrite(frame))
                        {
                            pending.Accepted = true;
                            RecordTerminalQueueAcceptance(frame);
                            // Ownership is already in FIFO before the producer
                            // is made runnable. No reservation-to-enqueue gap.
                            pending.Frame = default;
                        }
                    }
                    // This claimed node stays linked across telemetry, but a
                    // listener may retire its successor or append a waiter.
                    // Read the live successor before moving this node, without
                    // rescanning earlier capacity-blocked waiters.
                    next = pending.Next;
                    queue!.Retire(pending);
                    pending.ReadyNext = ready;
                    ready = pending;
                    pending = next;
                }
                // Completed-capacity nodes are owned by the cold lifetime slot,
                // not the wake slot. Held cleanup must not make every ordinary
                // successful release acquire this gate or scan completed nodes.
                _capacityChanged = queue?.LastWaiting is null ? null : queue;
            }

            while (ready is not null)
            {
                var pending = ready;
                ready = pending.ReadyNext;
                pending.ReadyNext = null;
                // Buffer return and cancellation deregistration can execute or
                // join external cleanup. Never do either under the queue gate.
                if (pending.Accepted)
                {
                    CompletePendingEnqueue(pending);
                    SignalAcceptedPublication();
                }
                else
                {
                    ThreadPool.UnsafeQueueUserWorkItem(pending, preferLocal: false);
                }
            }
        }

        private void CancelPendingEnqueue(PendingEnqueue pending)
        {
            lock (_admissionGate)
            {
                if (pending.Completing || !pending.CancellationRegistered)
                    return;
                pending.Failure = new OperationCanceledException(pending.CancellationToken);
                pending.Queue.Retire(pending);
                if (pending.Queue.LastWaiting is null)
                    _capacityChanged = null;
            }
            ThreadPool.UnsafeQueueUserWorkItem(pending, preferLocal: false);
        }

        private void CompletePendingEnqueue(PendingEnqueue pending)
        {
            var failure = pending.Failure;
            Exception? cleanupFailure = null;
            var frame = pending.Frame;
            pending.Frame = default; // Detach before unregistering or returning any owner.
            try { pending.CancellationRegistration.Dispose(); }
            catch (Exception exception) { cleanupFailure = exception; }
            try
            {
                if (!pending.Accepted)
                {
                    if (pending.Reserved)
                        CompleteReserved(frame, exception: null, completeFlushWaiter: false);
                    else
                        ReturnUnreserved(frame, failure);
                }
            }
            catch (Exception exception) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, exception); }
            lock (_admissionGate)
            {
                _backpressureCleanupFailure = RpcPublicationErrors.Combine(_backpressureCleanupFailure, cleanupFailure);
                pending.Queue.Remove(pending);
                if (pending.Queue.Head is null)
                    Volatile.Write(ref _pumpLifetime, pending.Queue.PumpTask);
            }
            failure = RpcPublicationErrors.Combine(failure, cleanupFailure);
            if (failure is OperationCanceledException && pending.CancellationToken.IsCancellationRequested)
                pending.TrySetCanceled(pending.CancellationToken);
            else if (failure is not null)
                pending.TrySetException(failure);
            else
                pending.TrySetResult(pending.Accepted ? SendEnqueueResult.Accepted : SendEnqueueResult.Closed);
        }

        internal Task CaptureBackpressuredSendDrain()
        {
            lock (_admissionGate)
            {
                if (_pumpLifetime is not PendingEnqueueQueue queue)
                    return Task.CompletedTask;
                var completions = new List<Task>();
                for (var pending = queue.Head; pending is not null; pending = pending.Next)
                    completions.Add(pending.Task);
                return ObserveEnqueueCompletionAsync(Task.WhenAll(completions));
            }
        }

        private static async Task ObserveEnqueueCompletionAsync(Task completion)
        {
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch
            {
                // This cold join certifies physical transfer/cleanup, not send
                // success. Send callers still observe the original failures.
            }
        }

        private sealed class PendingEnqueueQueue(Task pumpTask)
        {
            internal readonly Task PumpTask = pumpTask;
            internal PendingEnqueue? Head;
            internal PendingEnqueue? LastWaiting;
            private PendingEnqueue? _tail;

            // One ownership list: waiting prefix, retiring suffix. The same
            // links cover physical cleanup without making future capacity
            // pulses traverse writers whose external return is still blocked.
            internal void Add(PendingEnqueue pending)
            {
                System.Diagnostics.Debug.Assert(!ReferenceEquals(this, RegistrationInProgress));
                pending.Previous = LastWaiting;
                pending.Next = LastWaiting is null ? Head : LastWaiting.Next;
                if (pending.Previous is null)
                    Head = pending;
                else
                    pending.Previous.Next = pending;
                if (pending.Next is null)
                    _tail = pending;
                else
                    pending.Next.Previous = pending;
                LastWaiting = pending;
            }

            internal void Retire(PendingEnqueue pending)
            {
                pending.Completing = true;
                if (ReferenceEquals(LastWaiting, pending))
                    LastWaiting = pending.Previous;
                if (ReferenceEquals(_tail, pending))
                    return;
                Remove(pending);
                pending.Previous = _tail;
                _tail!.Next = pending;
                _tail = pending;
            }

            internal void Remove(PendingEnqueue pending)
            {
                if (pending.Previous is null)
                    Head = pending.Next;
                else
                    pending.Previous.Next = pending.Next;
                if (pending.Next is null)
                    _tail = pending.Previous;
                else
                    pending.Next.Previous = pending.Previous;
                pending.Previous = null;
                pending.Next = null;
            }
        }

        private sealed class PendingEnqueue(
            SendPump pump,
            PendingEnqueueQueue queue,
            OwnedFrame frame,
            CancellationToken cancellationToken) : TaskCompletionSource<SendEnqueueResult>(
                TaskCreationOptions.RunContinuationsAsynchronously), IThreadPoolWorkItem
        {
            internal readonly PendingEnqueueQueue Queue = queue;
            internal readonly CancellationToken CancellationToken = cancellationToken;
            internal OwnedFrame Frame = frame;
            internal PendingEnqueue? Previous;
            internal PendingEnqueue? Next;
            internal PendingEnqueue? ReadyNext;
            internal bool Completing;
            internal bool Reserved;
            internal bool Accepted;
            internal bool CancellationRegistered;
            internal Exception? Failure;
            internal CancellationTokenRegistration CancellationRegistration;

            internal void RegisterCancellation()
            {
                CancellationRegistration = CancellationToken.UnsafeRegister(
                    static state =>
                    {
                        var pending = (PendingEnqueue)state!;
                        pending.Cancel();
                    }, this);
                // An already-cancelled token invokes the callback inline before
                // registration assignment. Publish it first, then replay that
                // cancellation while the existing admission gate is still held.
                CancellationRegistered = true;
                if (CancellationToken.IsCancellationRequested)
                    Cancel();
            }

            private void Cancel() => pump.CancelPendingEnqueue(this);

            public void Execute() => pump.CompletePendingEnqueue(this);
        }
    }
}
