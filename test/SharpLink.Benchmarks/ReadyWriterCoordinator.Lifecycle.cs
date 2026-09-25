#if SHARPLINK_READY_WRITER_EXPERIMENT
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    internal readonly record struct StreamHandle(ReadyWriterCoordinator Owner, int Slot, long Generation);
    private readonly record struct StreamIdentity(long RequestId, ushort StreamId);
    private readonly record struct ReadyNotification(Stream Stream, long Generation);
    private readonly record struct WriterEvent(Update Update, IWriterOperation? Operation);
    private readonly Dictionary<StreamIdentity, Stream> _identities = new();
    private readonly Stack<Stream> _retiredStreams = new();
    private readonly bool _dynamicLifetimes;
    private int _retirementRequested;
    private long _staleEvents, _retiredLifetimes, _epochTargetsAllocated;

    private interface IWriterOperation
    {
        void Execute();
        void Fail(Exception error);
    }

    // Lifecycle commands are cold and independently owned even when results are held.
    // Unlike wire updates, they allocate. No lifecycle allocation is hidden in B/item.
    private sealed class WriterOperation<T>(ReadyWriterCoordinator owner, Func<T> execute) : IWriterOperation
    {
        internal readonly TaskCompletionSource<T> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Execute()
        {
            try
            {
                // Reap canceled registration before a cold owner operation observes
                // state. DATA selection and ordinary wire updates do not pay for this.
                owner.ServiceCapacityAdmission();
                Result.TrySetResult(execute());
            }
            catch (Exception error) { Result.TrySetException(error); }
        }
        public void Fail(Exception error) => Result.TrySetException(error);
    }

    private sealed class EpochCompletion(ReadyWriterCoordinator owner, int slot, long generation) : IReadyFrameCompletion
    {
        public void Complete(Exception? error) => owner.ReleaseGeneration(slot, generation, owner._bytes, true, error);
    }

    private async Task<T> OnWriterAsync<T>(Func<T> action)
    {
        var operation = new WriterOperation<T>(this, action);
        await _updates.Writer.WriteAsync(new WriterEvent(default, operation), _stopToken).ConfigureAwait(false);
        Interlocked.Increment(ref _updatesPending);
        _session.SignalReadyWriterExperiment();
        return await operation.Result.Task.ConfigureAwait(false);
    }

    // Open/Close and key-only updates use ONE ordered inbox. The writer alone owns
    // identity map/pool/ready-list mutations. Close currently targets B3 only; the
    // reference controller remains a separate oracle, not a dynamic production adapter.
    internal Task<StreamHandle> OpenStreamAsync(long requestId, ushort streamId)
        => OnWriterAsync(() => OpenStreamOnWriter(requestId, streamId));

    internal Task<bool> CloseStreamAsync(StreamHandle handle)
        => OnWriterAsync(() => CloseStreamOnWriter(handle));

    private void RequireLifecycleControl()
    {
        _stopToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _stopped) != 0) throw new InvalidOperationException("Ready writer stopped.");
        if (_reference is not null) throw new NotSupportedException("Dynamic B3 lifecycle control is not an A-ready adapter.");
        if (!_dynamicLifetimes) throw new NotSupportedException("Declare dynamic lifetimes before starting the writer.");
    }

    private void ValidateHandle(StreamHandle handle, Stream stream)
    {
        if (!ReferenceEquals(handle.Owner, this) || handle.Slot != stream.Index ||
            stream.Retired || stream.Generation != handle.Generation)
            throw new InvalidOperationException("Stale or foreign stream handle.");
    }

    private StreamHandle OpenStreamOnWriter(long requestId, ushort streamId)
    {
        RequireLifecycleControl();
        ServiceCapacityAdmission();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);
        var identity = new StreamIdentity(requestId, streamId);
        if (_identities.ContainsKey(identity)) throw new InvalidOperationException("Active stream or retained tombstone exists.");
        if (!_retiredStreams.TryPeek(out var stream)) throw new InvalidOperationException("No retired stream capacity.");
        lock (stream.Gate)
        {
            var generation = checked(stream.Generation + 1);
            var completion = new EpochCompletion(this, stream.Index, generation);
            if (!stream.Retired || stream.ProducerBusy != 0 || stream.HasHeldSpace || stream.NotificationPending ||
                stream.Frames.Count != 0 || stream.Outstanding != 0 || stream.Taken != stream.Released || stream.Node.List is not null)
                throw new InvalidOperationException("Pool contained a live stream.");
            _retiredStreams.Pop();
            stream.Generation = generation;
            stream.RequestId = requestId; stream.StreamId = streamId;
            stream.Retired = false; stream.Closed = false; stream.WireAttached = false;
            stream.Scheduled = false; stream.ReleaseTarget = completion;
            stream.Credit = _window; stream.Outstanding = 0;
            stream.Taken = stream.Released = 0;
            stream.Lease = default;
            _identities.Add(identity, stream);
            _epochTargetsAllocated++;
            return new StreamHandle(this, stream.Index, generation);
        }
    }

    private bool CloseStreamOnWriter(StreamHandle handle)
    {
        RequireLifecycleControl();
        if (!ReferenceEquals(handle.Owner, this) || (uint)handle.Slot >= (uint)_streams.Length)
            throw new InvalidOperationException("Foreign or invalid stream handle.");
        var stream = _streams[handle.Slot];
        lock (stream.Gate)
        {
            // Idempotent stale Close is a no-op, never an operation on a reused slot.
            if (stream.Generation != handle.Generation || stream.Retired || stream.Closed) return false;
            stream.Closed = true;
            if (stream.Node.List is not null) _ready.Remove(stream.Node);
            stream.Scheduled = false;
            if (_turnStream == stream.Index) { _turnStream = -1; _turnRemaining = 0; }
            List<Exception>? errors = null;
            DiscardPreparedLocked(stream, error => (errors ??= []).Add(error));
            stream.SignalSpace(new InvalidOperationException("Stream closed during preparation."));
            if (!stream.WireAttached || stream.Credit == _window) stream.WireAttached = false;
            RequestRetirement();
            if (errors is not null)
            {
                stream.CleanupFailed = true; // Quarantine; never recycle failed buffer ownership.
                throw new AggregateException("Stream close cleanup failed.", errors);
            }
        }
        TryRetireStream(stream);
        return true;
    }

    private void RequestRetirement()
    {
        // Only closed-generation cleanup signals this cold path. The normal item loop
        // neither scans the pool nor executes an unconditional connection-wide RMW.
        Interlocked.Exchange(ref _retirementRequested, 1);
        _session.SignalReadyWriterExperiment();
    }

    private void TryRetireStream(Stream stream)
    {
        if (!stream.Closed || stream.Retired) return;
        lock (stream.Gate)
        {
            if (stream.CleanupFailed || stream.ProducerBusy != 0 || stream.HasHeldSpace || stream.NotificationPending ||
                stream.Frames.Count != 0 || stream.Outstanding != 0 || stream.Taken != stream.Released || stream.Credit != _window)
                return;
            if (stream.Node.List is not null) throw new InvalidOperationException("Closed stream remains scheduled.");
            if (!_identities.Remove(new StreamIdentity(stream.RequestId, stream.StreamId)))
                throw new InvalidOperationException("Retiring an unknown stream identity.");
            stream.Retired = true; stream.WireAttached = false;
            _retiredStreams.Push(stream); _retiredLifetimes++;
        }
        ServiceCapacityAdmission();
    }
}
#endif
