namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal sealed partial class SendPump
    {
        private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

        // Protocol-progress isolation constants (issue #163): the normal class
        // cannot occupy the final ProgressReserveBytes of the queue, and the
        // pump drains the progress queue at the loop top and between every
        // NormalFramesPerInterleave normal frames. The interleave frequency
        // bounds progress service, and ProgressFramesPerDrain bounds each
        // drain so a concurrent progress producer cannot starve the normal
        // queue forever (observable under LowLatency, where every flush
        // releases capacity and the progress channel never observes empty).
        private const int NormalFramesPerInterleave = 64;
        private const int ProgressFramesPerDrain = 256;
        private const int ProgressReserveMinimumBytes = 4 * 1024;
        private const int ProgressReserveMaximumBytes = 64 * 1024;
        private const int ProgressReserveDivisor = 512;

        private readonly PipeWriter _output;
        private readonly RpcSessionFlushPolicyState _flushPolicyState;
        private readonly Action _flushPolicyChanged;
        private readonly int _maxQueuedBytes;
        private readonly int _normalQueueLimit;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationToken _sessionCancellation;
        private readonly Action<IRpcByteBufferWriter> _returnBuffer;
        private readonly Action<Exception> _onTransportFaulted;
        private readonly Action<Exception> _onPublicationFaulted;
        private readonly Channel<OwnedFrame> _progressQueue;
        private readonly Channel<OwnedFrame> _normalQueue;
        private readonly Lock _admissionGate = new();
        private readonly WakeupSignal _wakeup = new();
        // Usually the pump Task itself. A genuinely capacity-waiting operation
        // replaces this cold lifetime slot with its intrinsic queue, which
        // retains the same Task until all physical cleanup has finished.
        private object _pumpLifetime;
        private PendingEnqueueQueue? _capacityChanged;
        private long _queuedBytes;
        private int _stopped;
        private int _faulted;
        private Exception? _pumpCleanupFailure; // Single consumer, exceptional cleanup only.

        internal bool IsStopRequested => Volatile.Read(ref _stopped) != 0;
        internal bool HasPendingIdleWait => _wakeup.HasPendingIdleWait;

        public SendPump(
            PipeWriter output,
            RpcSessionFlushPolicyState flushPolicyState,
            int maxQueuedBytes,
            TimeProvider timeProvider,
            CancellationToken sessionCancellation,
            Action<IRpcByteBufferWriter> returnBuffer,
            Action<Exception> onTransportFaulted,
            Action<Exception> onPublicationFaulted)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(flushPolicyState);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedBytes);
            _output = output;
            _flushPolicyState = flushPolicyState;
            _maxQueuedBytes = maxQueuedBytes;
            _normalQueueLimit = maxQueuedBytes - ComputeProgressReserveBytes(maxQueuedBytes);
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _sessionCancellation = sessionCancellation;
            _returnBuffer = returnBuffer ?? throw new ArgumentNullException(nameof(returnBuffer));
            _onTransportFaulted = onTransportFaulted ?? throw new ArgumentNullException(nameof(onTransportFaulted));
            _onPublicationFaulted = onPublicationFaulted ?? throw new ArgumentNullException(nameof(onPublicationFaulted));
            _flushPolicyChanged = _wakeup.Signal;
            _flushPolicyState.RegisterChanged(_flushPolicyChanged);

            _progressQueue = CreateFrameQueue();
            _normalQueue = CreateFrameQueue();
            _pumpLifetime = RunAsync();
        }

        private static int ComputeProgressReserveBytes(int maxQueuedBytes)
        {
            // The headroom applies to production-sized queues. Below this
            // floor the queue is smaller than realistic frames and reserving a
            // slice would change the single-frame admission semantics that the
            // runtime's own small-queue tests rely on.
            if (maxQueuedBytes < 32 * 1024)
                return 0;
            var reserve = Math.Clamp(
                maxQueuedBytes / ProgressReserveDivisor,
                ProgressReserveMinimumBytes,
                ProgressReserveMaximumBytes);
            // Keep at least three quarters of a small queue available to the
            // normal class so a degenerate queue cannot become progress-only.
            return Math.Min(reserve, maxQueuedBytes / 4);
        }

        private bool HasProgressFrames() => _progressQueue.Reader.TryPeek(out _);

        private bool HasNormalFrames() => _normalQueue.Reader.TryPeek(out _);

        private static Channel<OwnedFrame> CreateFrameQueue()
            => Channel.CreateUnbounded<OwnedFrame>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        public SendEnqueueResult TryEnqueue(OwnedFrame frame)
            => TryEnqueue(frame, returnFrameWhenFull: true);

        public SendEnqueueResult TryEnqueueForBackpressure(OwnedFrame frame)
            => TryEnqueue(frame, returnFrameWhenFull: false);

        internal PreparedFrameEnqueue PrepareEnqueue(OwnedFrame frame)
        {
            var admission = new PreparedFrameEnqueue { Pump = this, Frame = frame, Result = SendEnqueueResult.Closed };
            if (Volatile.Read(ref _stopped) != 0) return admission;
            try
            {
                admission.Reserved = TryReserve(frame.Length, frame.IsProtocolProgress);
                admission.Result = admission.Reserved ? SendEnqueueResult.Closed : SendEnqueueResult.Full;
                return admission;
            }
            catch (Exception failure)
            {
                try { ReturnUnreserved(frame, failure); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw new UnreachableException();
            }
        }

        internal bool TryTransferPrepared(ref PreparedFrameEnqueue admission)
        {
            if (!admission.Reserved) return false;
            if (Volatile.Read(ref _stopped) != 0)
            {
                admission.Result = SendEnqueueResult.Closed;
                return false;
            }
            var frame = admission.Frame;
            if (!(frame.IsProtocolProgress ? _progressQueue : _normalQueue).Writer.TryWrite(frame))
            {
                admission.Result = SendEnqueueResult.Closed;
                return false;
            }
            // Transfer is final before any further producer code, even if the consumer wins now.
            admission.Result = SendEnqueueResult.Accepted;
            admission.Reserved = false;
            admission.Frame = default;
            RecordTerminalQueueAcceptance(frame);
            return true;
        }

        private static void RecordTerminalQueueAcceptance(OwnedFrame frame)
        {
            if (frame.PublicationRole == RpcFramePublicationRole.StreamTerminal &&
                frame.Publication is RpcStreamPublication group)
                group.RecordTerminalQueued(frame.StreamId);
        }

        internal void FinishAcceptedPublication() => SignalAcceptedPublication();

        internal void FinishRejectedPrepared(ref PreparedFrameEnqueue admission, Exception rejection)
        {
            var frame = admission.Frame;
            admission.Frame = default;
            if (admission.Reserved)
            {
                admission.Reserved = false;
                CompleteReserved(frame, rejection);
            }
            else ReturnUnreserved(frame, rejection);
        }

        private SendEnqueueResult TryEnqueue(OwnedFrame frame, bool returnFrameWhenFull)
        {
            var admission = PrepareEnqueue(frame);
            admission.TransferAttempted = true;
            bool accepted;
            try { accepted = TryTransferPrepared(ref admission); }
            catch (Exception failure)
            {
                // Channel transfer threw before ownership was accepted. Never abandon the
                // already-published byte reservation merely because queue growth failed.
                if (!admission.Accepted)
                {
                    try { FinishRejectedPrepared(ref admission, failure); }
                    catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
                }
                else SignalAcceptedPublication();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw new UnreachableException();
            }
            if (accepted)
            {
                SignalAcceptedPublication();
                return SendEnqueueResult.Accepted;
            }
            if (admission.Result == SendEnqueueResult.Full && !returnFrameWhenFull)
                return SendEnqueueResult.Full; // Backpressure caller still owns its original frame.
            var rejection = admission.Result == SendEnqueueResult.Full
                ? SharpLinkResourceExhaustion.Create(SharpLinkResourceExhaustion.SendQueueCapacity,
                    $"Session send queue exceeded its {_maxQueuedBytes}-byte limit (send_queue_capacity).")
                : CreateTransportClosedException();
            FinishRejectedPrepared(ref admission, rejection);
            return admission.Result;
        }

        public ValueTask<SendEnqueueResult> EnqueueAsync(
            OwnedFrame frame,
            CancellationToken cancellationToken)
        {
            bool reserved;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopped) != 0)
                    throw CreateTransportClosedException();
                reserved = TryReserve(frame.Length, frame.IsProtocolProgress);
            }
            catch (Exception exception)
            {
                try
                {
                    ReturnUnreserved(frame, exception);
                }
                catch (Exception cleanupException)
                {
                    return ValueTask.FromException<SendEnqueueResult>(RpcPublicationErrors.Combine(exception, cleanupException)!);
                }
                return ValueTask.FromException<SendEnqueueResult>(exception);
            }

            try
            {
                if (!reserved)
                    return EnqueueWhenFull(frame, cancellationToken);

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
            catch (Exception exception)
            {
                return ValueTask.FromException<SendEnqueueResult>(exception);
            }
        }

        private async Task RunAsync()
        {
            var pending = new List<OwnedFrame>(32);
            Exception terminalException = CreateTransportClosedException();
            var bytesAccumulated = 0;
            var batchStartTimestamp = 0L;

            try
            {
                while (true)
                {
                    if (!HasProgressFrames() && !HasNormalFrames())
                    {
                        if (Volatile.Read(ref _stopped) != 0)
                            break;

                        // Arm the reusable wakeup signal and always await it. WaitAsync
                        // consumes any signal that arrived before the arm was published,
                        // so a frame written between the empty-queue check above and the
                        // arm cannot leave the await hanging, and the arm never has to be
                        // abandoned. Idle wakeups allocate nothing.
                        var wakeup = _wakeup.WaitAsync();
                        await wakeup.ConfigureAwait(false);
                        continue;
                    }

                    if (await DrainProgressQueueAsync(pending).ConfigureAwait(false))
                    {
                        // Progress frames must not wait for a full batch:
                        // flush whatever the batch still holds (LowLatency
                        // already flushed per frame inside the drain).
                        if (pending.Count > 0)
                        {
                            await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                            bytesAccumulated = 0;
                        }
                        batchStartTimestamp = 0;
                    }

                    var normalFramesSinceInterleave = 0;
                    while (_normalQueue.Reader.TryRead(out var frame))
                    {
                        // Take ownership of the frame before any write can fail: a fault during
                        // the batch copy/FlushAsync must still release the frame and complete its
                        // flush waiter through the terminal ReleaseBatch in the finally block.
                        if (ClaimAndAddFrame(pending, frame))
                        {
                            if (pending.Count == 1)
                                batchStartTimestamp = _timeProvider.GetTimestamp();
                            // The frame already carries the wire TimeBudget stamped once by the
                            // producer, so the pump never samples, rewrites, or compacts a
                            // process-local deadline. It only coalesces the batch's frames into one
                            // output span at flush time.
                            bytesAccumulated += frame.Length;

                            var flushPolicy = _flushPolicyState.Capture();
                            if (frame.ForceFlush ||
                                flushPolicy.FlushEveryFrame ||
                                bytesAccumulated >= flushPolicy.FlushSizeThreshold)
                            {
                                await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                                bytesAccumulated = 0;
                                batchStartTimestamp = 0;
                            }
                        }

                        // Bounded progress interleave: the progress check is
                        // independent of flush boundaries, otherwise frames at
                        // or above the flush threshold would flush every time
                        // and the interleave would never fire, starving the
                        // progress queue while the normal queue never empties.
                        normalFramesSinceInterleave++;
                        if (normalFramesSinceInterleave >= NormalFramesPerInterleave)
                        {
                            normalFramesSinceInterleave = 0;
                            if (await DrainProgressQueueAsync(pending).ConfigureAwait(false))
                            {
                                if (pending.Count > 0)
                                {
                                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                                    bytesAccumulated = 0;
                                }
                                batchStartTimestamp = 0;
                            }
                        }
                    }

                    if (pending.Count == 0)
                        continue;

                    // Profile batching still coalesces queued frames up to the configured
                    // latency bound before the transport flush; request deadlines no longer
                    // participate in that decision, they are enforced end to end by the
                    // caller's pending deadline and the remote cancellation path.
                    if (_flushPolicyState.Capture().ExplicitBatchWindowEnabled &&
                        await WaitForMoreUntilFlushBoundaryAsync(
                            batchStartTimestamp,
                            bytesAccumulated).ConfigureAwait(false) &&
                        (HasProgressFrames() || HasNormalFrames()))
                    {
                        continue;
                    }

                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                    bytesAccumulated = 0;
                    batchStartTimestamp = 0;
                }
            }
            catch (OperationCanceledException) when (_sessionCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                terminalException = NormalizeTransportException(ex);
                try { ReportFaultOnce(terminalException); }
                catch (Exception cleanup) { terminalException = RpcPublicationErrors.Combine(terminalException, cleanup)!; }
            }
            finally
            {
                // A concurrent Stop can publish its flag before closing either
                // writer. Seal publication here before the final drain, so a
                // producer that already reserved cannot enqueue behind it.
                Interlocked.Exchange(ref _stopped, 1);
                _progressQueue.Writer.TryComplete();
                _normalQueue.Writer.TryComplete();
                Exception? cleanupFailure = null;
                try { _flushPolicyState.UnregisterChanged(_flushPolicyChanged); }
                catch (Exception cleanup) { cleanupFailure = cleanup; }
                try { ReleaseBatch(pending, terminalException); }
                catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
                try { DrainQueuedFrames(terminalException); }
                catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
                try { PulseCapacityWaiters(); }
                catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
                cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, _pumpCleanupFailure);
                if (cleanupFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                        RpcPublicationErrors.Combine(terminalException, cleanupFailure)!).Throw();
            }
        }

        private async ValueTask<bool> DrainProgressQueueAsync(List<OwnedFrame> pending)
        {
            // The drain runs until the progress queue is empty so the service
            // rate always matches the arrival rate.
            var drained = false;
            var drainedCount = 0;
            while (drainedCount < ProgressFramesPerDrain &&
                   _progressQueue.Reader.TryRead(out var frame))
            {
                ClaimAndAddFrame(pending, frame);
                drained = true;
                drainedCount++;
                if (_flushPolicyState.Capture().FlushEveryFrame)
                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
            }
            return drained;
        }

        /// <summary>
        /// Copies every frame the batch still owns into one output span before the flush.
        /// Serializing at flush time keeps the transport segments large instead of emitting one
        /// span per arriving frame; the frames have already been serialized with their wire
        /// TimeBudget, so this copy inspects no deadline.
        /// </summary>
        private void WritePendingBatch(List<OwnedFrame> pending)
        {
            var length = 0;
            for (var index = 0; index < pending.Count; index++)
                length = checked(length + pending[index].Length);
            if (length == 0)
                return;

            var destination = _output.GetSpan(length);
            var offset = 0;
            for (var index = 0; index < pending.Count; index++)
            {
                var frame = pending[index];
                if (frame.Length == 0)
                    continue;
                frame.Memory.Span.CopyTo(destination[offset..]);
                offset += frame.Length;
            }
            _output.Advance(offset);
            SharpLinkTelemetry.RecordSentBytes(offset);
        }

        private async ValueTask FlushAndReleaseAsync(List<OwnedFrame> pending)
        {
            if (pending.Count == 0)
                return;

            WritePendingBatch(pending);
            var flush = _output.FlushAsync(_sessionCancellation);
            var result = await flush.ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
                throw CreateTransportClosedException();
            ReleaseBatch(pending, exception: null);
        }

        private async ValueTask<bool> WaitForMoreUntilFlushBoundaryAsync(
            long batchStartTimestamp,
            int bytesAccumulated)
        {
            // Queue publication and policy publication share one wake authority. Every wake
            // rechecks the queue, policy, stop state, and original batch window: a producer
            // may signal only after the pump has already consumed its published frame.
            while (true)
            {
                if (HasProgressFrames() || HasNormalFrames())
                    return true;

                var policy = _flushPolicyState.Capture();
                if (policy.FlushEveryFrame || bytesAccumulated >= policy.FlushSizeThreshold)
                    return false;
                if (!policy.ExplicitBatchWindowEnabled)
                    return false;

                var deadline = SharpLinkTime.AddDuration(
                    batchStartTimestamp,
                    policy.MaxLatency,
                    _timeProvider.TimestampFrequency);
                var remaining = SharpLinkTime.GetRemaining(
                    deadline,
                    _timeProvider.GetTimestamp(),
                    _timeProvider.TimestampFrequency);
                if (remaining == TimeSpan.Zero)
                    return false;

                _wakeup.ConsumeLatched();
                if (Volatile.Read(ref _stopped) != 0)
                    return false;
                if (HasProgressFrames() || HasNormalFrames())
                    return true;
                if (!ReferenceEquals(policy, _flushPolicyState.Capture()))
                    continue;

                var delay = remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
                var woke = await _wakeup.WaitAsync(_timeProvider, delay).ConfigureAwait(false);

                // A concurrent policy replacement wins over either a stale data wake or a stale
                // timer completion. Recompute threshold/latency from the original batch start.
                if (!ReferenceEquals(policy, _flushPolicyState.Capture()))
                    continue;

                // A wake is a request to recheck state, not an independent flush boundary.
                // Delayed signals for already consumed frames must not flush a batched window early.
                if (woke)
                    continue;

                if (remaining <= MaximumTimerDelay)
                    return false;
                // One chunk of a very long MaxLatency expired without a policy change. Recompute
                // the remaining part of the same batch window before arming the next chunk.
            }
        }

        private bool TryReserve(int bytes, bool isProtocolProgress)
        {
            if (bytes < 0)
                return false;
            if (bytes == 0)
                return true;

            // Protocol-progress frames may use the full queue budget; normal
            // frames may not occupy the reserved progress headroom. A normal
            // frame larger than its limit is rejected, even on an empty queue,
            // so it cannot consume the reserve and break liveness isolation
            // under transport saturation. When the queue is too small to hold
            // any reserve the headroom does not exist and the base single-frame
            // oversized exception is preserved; progress frames keep the base
            // oversized semantics (admitted once when the queue is empty).
            var limit = isProtocolProgress ? _maxQueuedBytes : _normalQueueLimit;

            while (true)
            {
                var current = Volatile.Read(ref _queuedBytes);
                var canReserve = bytes <= limit
                    ? current <= limit - bytes
                    : current == 0 && (isProtocolProgress || _normalQueueLimit == _maxQueuedBytes);
                if (!canReserve)
                    return false;
                if (Interlocked.CompareExchange(ref _queuedBytes, current + bytes, current) == current)
                {
                    try { SharpLinkTelemetry.AddSendQueueBytes(bytes); }
                    catch (Exception failure)
                    {
                        // The caller has not received a reservation yet. Roll back the
                        // scalar claim here, not by pretending an unreserved frame owns it.
                        Interlocked.Add(ref _queuedBytes, -bytes);
                        try { SharpLinkTelemetry.AddSendQueueBytes(-bytes); }
                        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
                        try { PulseCapacityWaiters(); }
                        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    }
                    return true;
                }
            }
        }

        // This is the sole wire-admission authority. No producer-side eligibility check
        // substitutes for ordering these claims on this Session's normal FIFO.
        private bool ClaimAndAddFrame(List<OwnedFrame> pending, OwnedFrame frame)
        {
            var transferred = false;
            try
            {
                var publication = frame.Publication;
                var role = frame.PublicationRole;
                if (publication is null)
                {
                    if (role != RpcFramePublicationRole.None)
                        throw new InvalidOperationException("Publication role has no control.");
                }
                else
                {
                    if (frame.IsProtocolProgress)
                        throw new InvalidOperationException("Publication controls cannot bypass normal FIFO ordering.");
                    var group = publication as RpcStreamPublication;
                    var binding = group?.Binding ?? publication as RpcBindingPublication
                        ?? throw new InvalidOperationException("Unknown publication control.");
                    var reject = false;
                    var lateData = false;
                    switch (role)
                    {
                        case RpcFramePublicationRole.RequestHead:
                            if (group is not null)
                            {
                                if (group.HeadDisposition != RpcStreamHeadDisposition.Pending)
                                    throw new InvalidOperationException("Stream group has more than one request head.");
                                // Publish before any observer, buffer return or waiter can run.
                                group.HeadDisposition = binding.ReleaseClaimed
                                    ? RpcStreamHeadDisposition.Rejected : RpcStreamHeadDisposition.Admitted;
                            }
                            reject = binding.ReleaseClaimed;
                            break;
                        case RpcFramePublicationRole.ReleaseBinding:
                            if (group is not null || binding.ReleaseClaimed)
                                throw new InvalidOperationException("Duplicate or invalid binding release claim.");
                            binding.ReleaseClaimed = true;
                            break;
                        case RpcFramePublicationRole.StreamData:
                            if (group is null || group.HeadDisposition == RpcStreamHeadDisposition.Pending)
                                throw new InvalidOperationException("Stream data preceded its exact request head.");
                            lateData = group.IsTerminalConsumed(frame.StreamId);
                            reject = group.HeadDisposition == RpcStreamHeadDisposition.Rejected || lateData;
                            break;
                        case RpcFramePublicationRole.StreamTerminal:
                            if (group is null || group.HeadDisposition == RpcStreamHeadDisposition.Pending)
                                throw new InvalidOperationException("Stream terminal preceded its exact request head.");
                            reject = group.HeadDisposition == RpcStreamHeadDisposition.Rejected || group.IsTerminalConsumed(frame.StreamId);
                            if (!reject) group.MarkTerminalConsumed(frame.StreamId); // Before cleanup/observers.
                            break;
                        case RpcFramePublicationRole.StreamCleanupFence:
                        case RpcFramePublicationRole.StreamFence:
                            if (group is null || group.HeadDisposition == RpcStreamHeadDisposition.Pending ||
                                frame.Length != 0 || frame.ForceFlush)
                                throw new InvalidOperationException("Invalid physical stream fence.");
                            break;
                        default:
                            throw new InvalidOperationException("Unknown publication role.");
                    }
                    if (reject)
                    {
                        transferred = true; // Cleanup owns the frame even if a callback throws.
                        CompleteReserved(frame, RpcPublicationErrors.Rejected(), accepted: true);
                        // A late data frame already spent sender credit. Rather than fabricate
                        // a successful emission/refund against a terminal route, fail this exact
                        // Session; its existing flow-control teardown releases all credit owners.
                        if (lateData) throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                            "Client stream data reached publication after its terminal cutoff.");
                        return false;
                    }
                }
                pending.Add(frame);
                transferred = true;
                return true;
            }
            catch (Exception failure)
            {
                if (!transferred)
                {
                    try { CompleteReserved(frame, failure, accepted: true); }
                    catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw new UnreachableException();
            }
        }

        private void ReleaseBatch(List<OwnedFrame> pending, Exception? exception)
        {
            Exception? failure = null;
            for (var index = 0; index < pending.Count; index++)
            {
                var frame = pending[index];
                pending[index] = default; // Detach before arbitrary cleanup or reentrancy.
                try { CompleteReserved(frame, exception, accepted: true); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            }
            pending.Clear();
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private void DrainQueuedFrames(Exception exception)
        {
            Exception? failure = null;
            while (_progressQueue.Reader.TryRead(out var frame))
            {
                RejectUnclaimedHead(frame);
                try { CompleteReserved(frame, exception, accepted: true); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            }
            while (_normalQueue.Reader.TryRead(out var frame))
            {
                RejectUnclaimedHead(frame);
                try { CompleteReserved(frame, exception, accepted: true); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            }
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void RejectUnclaimedHead(OwnedFrame frame)
        {
            if (frame.PublicationRole == RpcFramePublicationRole.RequestHead &&
                frame.Publication is RpcStreamPublication group &&
                group.HeadDisposition == RpcStreamHeadDisposition.Pending)
                group.HeadDisposition = RpcStreamHeadDisposition.Rejected;
        }

        private void CompleteReserved(
            OwnedFrame frame,
            Exception? exception,
            bool completeFlushWaiter = true,
            bool accepted = false)
        {
            Exception? cleanupFailure = null;
            var observer = completeFlushWaiter ? frame.FailureObserver : null;
            long requestId = 0;
            // Capture BEFORE returning the pooled buffer, including successful-flush cleanup
            // faults which may still need to notify the independent failure observer.
            if (observer is not null)
            {
                try { requestId = BinaryPrimitives.ReadInt64LittleEndian(frame.Memory.Span.Slice(7, sizeof(long))); }
                catch (Exception cleanup) { cleanupFailure = cleanup; observer = null; }
            }
            try { _returnBuffer(frame.Owner); }
            catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
            Interlocked.Add(ref _queuedBytes, -frame.Length);
            try { SharpLinkTelemetry.AddSendQueueBytes(-frame.Length); }
            catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
            try { PulseCapacityWaiters(); }
            catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
            var outcome = RpcPublicationErrors.Combine(exception, cleanupFailure);
            if (outcome is not null && observer is not null)
            {
                try { observer.OnRequestEmissionFailure(requestId, outcome); }
                catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
            }
            var group = accepted ? frame.Publication as RpcStreamPublication : null;
            if (accepted && cleanupFailure is not null)
            {
                _pumpCleanupFailure = RpcPublicationErrors.Combine(_pumpCleanupFailure, cleanupFailure);
                if (group is not null)
                    group.CleanupFailure = RpcPublicationErrors.Combine(group.CleanupFailure, cleanupFailure);
            }
            outcome = RpcPublicationErrors.Combine(exception, cleanupFailure);
            if (frame.PublicationRole is (RpcFramePublicationRole.StreamFence or RpcFramePublicationRole.StreamCleanupFence) && group is not null)
                outcome = RpcPublicationErrors.Combine(outcome, group.CleanupFailure);
            // Last: a physical fence is not complete until all preceding frame cleanup,
            // capacity notification and failure observation have physically returned.
            if (completeFlushWaiter)
            {
                if (outcome is null) frame.FlushCompletion?.TrySetResult(true);
                else frame.FlushCompletion?.TrySetException(outcome);
            }
            if (cleanupFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        private void ReturnUnreserved(OwnedFrame frame, Exception? exception = null)
        {
            // Refusal owns every independent obligation too; a throwing return must not
            // bypass the failure observer or waiter. This runs only outside call gates.
            Exception? failure = null;
            long requestId = 0;
            var observer = frame.FailureObserver;
            if (observer is not null)
            {
                try { requestId = BinaryPrimitives.ReadInt64LittleEndian(frame.Memory.Span.Slice(7, sizeof(long))); }
                catch (Exception cleanup) { failure = cleanup; observer = null; }
            }
            try { _returnBuffer(frame.Owner); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            exception ??= CreateTransportClosedException();
            var outcome = RpcPublicationErrors.Combine(exception, failure);
            if (outcome is not null && observer is not null)
            {
                try { observer.OnRequestEmissionFailure(requestId, outcome); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            }
            outcome = RpcPublicationErrors.Combine(exception, failure);
            frame.FlushCompletion?.TrySetException(outcome!);
            if (frame.FlushCompletion is not null)
                ObserveAbandonedFlushCompletion(frame.FlushCompletion.Task);
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private void SignalAcceptedPublication()
        {
            try { _wakeup.Signal(); }
            catch (Exception failure)
            {
                // TryWrite already transferred ownership. Do not tell the caller that this
                // was neveraccepted: retain accepted semantics, terminalize, and report the
                // failure through the physical stop join. Publish error ownership BEFORE a
                // terminal callback can reenter that join.
                RecordPublicationCleanupFailure(failure);
                try { Stop(); }
                catch (Exception cleanup)
                {
                    RecordPublicationCleanupFailure(cleanup);
                    failure = RpcPublicationErrors.Combine(failure, cleanup)!;
                }
                // This is a producer failure, NOT proof that the output await is quiescent.
                // Keep the ordinary terminal path distinct from the pump-catch callback.
                try { _onPublicationFaulted(failure); }
                catch (Exception cleanup) { RecordPublicationCleanupFailure(cleanup); }
            }
        }

        private void RecordPublicationCleanupFailure(Exception failure)
        {
            lock (_admissionGate)
                _backpressureCleanupFailure = RpcPublicationErrors.Combine(_backpressureCleanupFailure, failure);
        }

        public long QueuedBytes => Volatile.Read(ref _queuedBytes);

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            _progressQueue.Writer.TryComplete();
            _normalQueue.Writer.TryComplete();
            Exception? failure = null;
            try { _wakeup.Signal(); }
            catch (Exception cleanup) { failure = cleanup; }
            try { PulseCapacityWaiters(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private void ReportFaultOnce(Exception exception)
        {
            if (Interlocked.Exchange(ref _faulted, 1) != 0)
                return;
            Interlocked.Exchange(ref _stopped, 1);
            _progressQueue.Writer.TryComplete(exception);
            _normalQueue.Writer.TryComplete(exception);
            Exception? cleanupFailure = null;
            try { _wakeup.Signal(); }
            catch (Exception cleanup) { cleanupFailure = cleanup; }
            try { PulseCapacityWaiters(); }
            catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
            try { _onTransportFaulted(exception); }
            catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
            if (cleanupFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        private static SharpLinkException NormalizeTransportException(Exception exception)
            => exception as SharpLinkException ??
               new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Transport output failed.", exception);

        private static SharpLinkException CreateTransportClosedException()
            => new(SharpLinkErrorCode.ConnectionClosed, "Transport output completed.");

        public async ValueTask WaitForStopAsync()
        {
            Exception? failure = null;
            try
            {
                var lifetime = Volatile.Read(ref _pumpLifetime);
                var pumpTask = lifetime is Task task ? task : ((PendingEnqueueQueue)lifetime).PumpTask;
                await pumpTask.ConfigureAwait(false);
            }
            catch (Exception exception) { failure = exception; }
            // Stop prevents new pending nodes. Join cancellation/failed reservation
            // cleanup even if the pump failed, then retain its actual cleanup errors.
            try { await CaptureBackpressuredSendDrain().ConfigureAwait(false); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            lock (_admissionGate)
                failure = RpcPublicationErrors.Combine(failure, _backpressureCleanupFailure);
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
