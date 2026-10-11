using System.Collections.Concurrent;

namespace SharpLink.Runtime;

/// <summary>
/// Schedules prepared stream frames on the existing connection writer. Send credit and the ready
/// list have exactly one writer; producers only publish owned buffers and coalesced change hints.
/// The caller must establish Request-before-DATA before opening a client request stream.
/// </summary>
internal sealed partial class WriterOwnedStreamScheduler : IWriterReadySource
{
    private readonly Lock _mapGate = new();
    private readonly Dictionary<StreamKey, StreamHandle> _streams = [];
    private readonly ConcurrentQueue<StreamHandle> _changes = new();
    private readonly LinkedList<StreamHandle> _ready = new();
    private readonly PreCreditSerializedBudget _prepared;
    private readonly Action _signal;
    private readonly Action<IRpcByteBufferWriter> _returnBuffer;
    private readonly int _streamWindow, _connectionWindow, _maxStreams, _slots, _maxPacketBytes, _maxCreditBytes;
    private readonly CancellationTokenRegistration _cancellation;
    private TaskCompletionSource? _capacityChanged;
    private int _capacityWaiters;
    private long _generation, _connectionCredit;
    private bool _blocked;
    private Exception? _closing, _cleanupFailure;
    private int _stopped;

    internal WriterOwnedStreamScheduler(
        int streamWindow,
        int connectionWindow,
        int maxStreams,
        int slots,
        int maxPacketBytes,
        int maxCreditBytes,
        int preparedByteLimit,
        Action signal,
        Action<IRpcByteBufferWriter> returnBuffer,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamWindow);
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionWindow, streamWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStreams);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPacketBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCreditBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparedByteLimit);
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _returnBuffer = returnBuffer ?? throw new ArgumentNullException(nameof(returnBuffer));
        _streamWindow = streamWindow;
        _connectionWindow = connectionWindow;
        _connectionCredit = connectionWindow;
        _maxStreams = maxStreams;
        _slots = slots;
        _maxPacketBytes = maxPacketBytes;
        _maxCreditBytes = maxCreditBytes;
        // Each blocked admission already owns one bounded packet. Limit that extra envelope
        // independently of both the retained-state count and the protocol's receive windows.
        var waiters = Math.Min(maxStreams, Math.Max(1, preparedByteLimit / maxPacketBytes));
        _prepared = new PreCreditSerializedBudget(preparedByteLimit, waiters);
        _cancellation = cancellationToken.UnsafeRegister(static state =>
        {
            var owner = (WriterOwnedStreamScheduler)state!;
            owner.StopPreparing(new OperationCanceledException("The connection is stopping."));
        }, this);
    }

    private readonly record struct StreamKey(long RequestId, ushort StreamId);
    internal readonly record struct PreparedFrame(
        IRpcByteBufferWriter Packet, int Bytes, int CreditBytes, bool Terminal, bool ForceFlush);

    // An immutable lifecycle identity, deliberately NOT recycled while arbitrary producers or
    // pump completions may retain it. Reopening the same key cannot revive an old handle.
    internal sealed class StreamHandle : IWriterReadyCompletion
    {
        internal readonly WriterOwnedStreamScheduler Owner;
        internal readonly long RequestId, Generation;
        internal readonly ushort StreamId;
        internal readonly Lock Gate = new();
        internal readonly Queue<PreparedFrame> Frames;
        internal readonly LinkedListNode<StreamHandle> Node;
        internal TaskCompletionSource? SpaceChanged;
        internal Exception? Error;
        internal bool Attached = true, EndQueued, OwnerClosed, WireAttached, RemoteAbort, AbortApplied;
        internal bool Scheduled;
        internal int ProducerActive, ChangeQueued, WriterReferences;
        internal long Credit, PendingCredit;
        internal long AcceptedFrames, ReleasedFrames;
        internal List<DrainWaiter>? DrainWaiters;

        internal StreamHandle(WriterOwnedStreamScheduler owner, long requestId, ushort streamId, long generation)
        {
            Owner = owner;
            RequestId = requestId;
            StreamId = streamId;
            Generation = generation;
            Credit = owner._streamWindow;
            Frames = new Queue<PreparedFrame>(owner._slots);
            Node = new LinkedListNode<StreamHandle>(this);
        }

        public void Complete(int creditBytes, Exception? error) => Owner.Released(this, error);
    }

    internal sealed class DrainWaiter(long target)
    {
        internal readonly long Target = target;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal StreamHandle Open(long requestId, ushort streamId)
    {
        lock (_mapGate)
        {
            ThrowIfClosing();
            var key = new StreamKey(requestId, streamId);
            if (_streams.TryGetValue(key, out var existing))
            {
                if (Volatile.Read(ref existing.EndQueued) || Volatile.Read(ref existing.Error) is not null ||
                    !Volatile.Read(ref existing.Attached))
                    throw Closed();
                return existing;
            }
            if (_streams.Count >= _maxStreams)
                throw CapacityExceeded();
            var handle = new StreamHandle(this, requestId, streamId, checked(++_generation));
            _streams.Add(key, handle);
            return handle;
        }
    }

    internal ValueTask<StreamHandle> OpenAsync(long requestId, ushort streamId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_mapGate)
        {
            ThrowIfClosing();
            if (_streams.Count < _maxStreams || _streams.ContainsKey(new StreamKey(requestId, streamId)))
                return new ValueTask<StreamHandle>(Open(requestId, streamId));
            var active = 0;
            foreach (var stream in _streams.Values)
                if (!Volatile.Read(ref stream.EndQueued) && Volatile.Read(ref stream.Error) is null) active++;
            if (active >= _maxStreams || _capacityWaiters != 0)
                throw CapacityExceeded();
            _capacityWaiters++;
        }
        return WaitForRetiredCapacityAsync(requestId, streamId, cancellationToken);
    }

    private async ValueTask<StreamHandle> WaitForRetiredCapacityAsync(long requestId, ushort streamId, CancellationToken token)
    {
        try
        {
            while (true)
            {
                Task wait;
                lock (_mapGate)
                {
                    ThrowIfClosing();
                    if (_streams.Count < _maxStreams)
                        return Open(requestId, streamId);
                    _capacityChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _capacityChanged.Task;
                }
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally { lock (_mapGate) _capacityWaiters--; }
    }

    private static bool CanReserve(long credit, int window, int bytes)
        => bytes <= credit || (bytes > window && credit == window);

    private void ValidateOwner(StreamHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!ReferenceEquals(handle.Owner, this)) throw new InvalidOperationException("Foreign stream handle.");
    }

    private void ValidateOpen(StreamHandle handle)
    {
        ThrowIfClosing();
        if (handle.Error is { } error) throw error;
        if (!handle.Attached || handle.EndQueued) throw Closed();
    }

    private void ThrowIfClosing()
    {
        if (Volatile.Read(ref _closing) is { } error) throw error;
    }

    private static SharpLinkException Closed()
        => new(SharpLinkErrorCode.ConnectionClosed, "The stream is closed.");
    private static SharpLinkException CapacityExceeded()
        => new(SharpLinkErrorCode.ResourceExhausted, "The session has no available stream state capacity.");

    internal long ConnectionCredit => Volatile.Read(ref _connectionCredit);
    internal long PreparedBytes => _prepared.ReservedBytes;
    internal Exception? CleanupFailure => Volatile.Read(ref _cleanupFailure);
    internal int RetainedStreams { get { lock (_mapGate) return _streams.Count; } }
}
