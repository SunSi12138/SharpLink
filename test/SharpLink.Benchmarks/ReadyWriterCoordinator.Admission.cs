#if SHARPLINK_READY_WRITER_EXPERIMENT
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    // Match the frozen controller's single pending state-capacity slot. This is
    // registration admission, not the still-separate global DATA credit FIFO.
    private CapacityAdmission? _pendingAdmission;
    private int _admissionRequested;

    // The existing OpenStreamAsync remains a fail-fast probe. This entry waits
    // only for retained CLOSED lifetimes; exhausting all live slots still fails.
    internal async Task<StreamHandle> AcquireStreamAsync(long requestId, ushort streamId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RequireLifecycleControl();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);
        var operation = new CapacityAdmission(this, requestId, streamId, token);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopToken);
        using var registration = linked.Token.UnsafeRegister(static state => ((CapacityAdmission)state!).Cancel(), operation);
        try
        {
            await _updates.Writer.WriteAsync(new WriterEvent(default, operation), linked.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _updatesPending);
            _session.SignalReadyWriterExperiment();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // The channel callback can complete first. Normalize cancellation to
            // the caller/connection token, not the private linked-token identity.
            operation.Cancel();
        }
        catch (Exception error) { operation.Fail(error); }
        // Cancellation after the owner claims admission cannot discard an opened
        // generation. Always return that result; do not wrap it in Task.WaitAsync.
        return await operation.Result.Task.ConfigureAwait(false);
    }

    // No request or completion reuse: delayed callbacks reference exactly one
    // registration request, never a pooled stream. All allocations are cold.
    private sealed class CapacityAdmission(ReadyWriterCoordinator owner, long requestId, ushort streamId,
        CancellationToken token) : IWriterOperation
    {
        internal readonly TaskCompletionSource<StreamHandle> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly StreamIdentity Identity = new(requestId, streamId);
        // 0 waiting; 1 owner claimed; 2 canceled; 3 failed; 4 admitted.
        private int _state;
        internal bool IsWaiting => Volatile.Read(ref _state) == 0;

        internal bool ObserveCancellation()
        {
            // The original token is marked before its callbacks run. A blocked
            // callback may delay forwarding to our linked token; it must not let
            // this request claim a generation after cancellation was observed.
            if (token.IsCancellationRequested || owner._stopToken.IsCancellationRequested) Cancel();
            return IsWaiting;
        }

        public void Execute()
        {
            if (!ObserveCancellation()) return;
            try { owner.QueueOrAdmit(this); }
            catch (Exception error) { Fail(error); }
        }

        internal void Admit()
        {
            if (!ObserveCancellation()) return;
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
            try
            {
                var handle = owner.OpenStreamOnWriter(Identity.RequestId, Identity.StreamId);
                Volatile.Write(ref _state, 4);
                Result.TrySetResult(handle);
            }
            catch (Exception error) { Fail(error); }
        }

        internal void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
            Result.TrySetCanceled(token.IsCancellationRequested ? token : owner._stopToken);
            // Only publish a coalesced hint. A callback cannot edit the owner map,
            // return writer credit, wait for Flush or occupy its bounded inbox.
            owner.RequestAdmission();
        }

        public void Fail(Exception error)
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if (state >= 2) return;
                if (Interlocked.CompareExchange(ref _state, 3, state) != state) continue;
                Result.TrySetException(error);
                return;
            }
        }
    }

    private void RequestAdmission()
    {
        Interlocked.Exchange(ref _admissionRequested, 1);
        _session.SignalReadyWriterExperiment();
    }

    private void QueueOrAdmit(CapacityAdmission operation)
    {
        RequireLifecycleControl();
        ServiceCapacityAdmission();
        if (!operation.IsWaiting) return;
        if (_identities.ContainsKey(operation.Identity))
            throw new InvalidOperationException("Active stream or retained tombstone exists.");
        if (_pendingAdmission is not null)
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                "The session is already waiting for completed stream capacity to be released.");
        if (_retiredStreams.Count != 0)
        {
            operation.Admit();
            return;
        }
        var active = 0;
        foreach (var stream in _streams)
            if (!stream.Retired && !stream.Closed) active++;
        if (active == _streams.Length)
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                "The session already owns its maximum active flow-controlled streams.");
        _pendingAdmission = operation;
        // Cancellation may win while this operation is being installed. Its hint
        // makes HasWork true; no polling or additional owner command is needed.
    }

    private void ServiceCapacityAdmission()
    {
        var pending = _pendingAdmission;
        if (pending is null) return;
        if (!pending.ObserveCancellation())
        {
            _pendingAdmission = null;
            return;
        }
        if (_retiredStreams.Count == 0) return;
        // Detach before admission (which calls the common Open implementation).
        // This also prevents a younger fail-fast Open from stealing released space.
        _pendingAdmission = null;
        pending.Admit();
    }
}
#endif
