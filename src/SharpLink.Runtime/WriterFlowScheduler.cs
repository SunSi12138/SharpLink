using System.Collections.Concurrent;

namespace SharpLink.Runtime;

/// <summary>
/// One writer owns protocol send credit. Producers own only bounded prepared
/// packets; this type must replace, never supplement, another send-credit ledger.
/// </summary>
internal sealed partial class WriterFlowScheduler : IWriterReadySource
{
    private readonly Lock _lifecycleGate = new();
    private readonly Dictionary<StreamKey, StreamLease> _streams = new();
    private readonly ConcurrentQueue<StreamLease> _notifications = new();
    private readonly LinkedList<StreamLease> _ready = new();
    private readonly PreCreditSerializedBudget _preparedBudget;
    private readonly int _streamWindow;
    private readonly int _connectionWindow;
    private readonly int _maxCreditBytes;
    private readonly int _maxStreams;
    private readonly int _slots;
    private readonly int _quantum;
    private readonly int _perStreamPreparedBytes;
    private readonly int _maxNormalFrameBytes;
    private readonly Action _signal;
    private readonly Action<IRpcByteBufferWriter> _returnPacket;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OpenWaiter? _openWaiter;
    private Exception? _terminal;
    private Exception? _cleanupFailure;
    private long _generation;
    private long _connectionCredit;
    private int _activeStreams;
    private int _pendingStopProducers = 1;
    private int _cleanupCount;
    private bool _blocked;
    private bool _writerStopped;
    private StreamLease? _turn;
    private int _turnRemaining;

    internal WriterFlowScheduler(
        int streamWindow,
        int connectionWindow,
        int maxCreditBytes,
        int maxStreams,
        long preparedByteLimit,
        int maxNormalFrameBytes,
        Action signal,
        Action<IRpcByteBufferWriter> returnPacket,
        int slots = 16,
        int quantum = 16,
        int perStreamPreparedBytes = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamWindow);
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionWindow, streamWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCreditBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStreams);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNormalFrameBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perStreamPreparedBytes);
        _streamWindow = streamWindow;
        _connectionWindow = connectionWindow;
        _maxCreditBytes = maxCreditBytes;
        _maxStreams = maxStreams;
        _slots = slots;
        _quantum = quantum;
        _perStreamPreparedBytes = perStreamPreparedBytes;
        _maxNormalFrameBytes = maxNormalFrameBytes;
        _connectionCredit = connectionWindow;
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _returnPacket = returnPacket ?? throw new ArgumentNullException(nameof(returnPacket));
        var maxWaiters = checked((int)Math.Min(maxStreams,
            Math.Max(1L, preparedByteLimit / ((long)maxCreditBytes + sizeof(ushort)))));
        _preparedBudget = new PreCreditSerializedBudget(preparedByteLimit, maxWaiters);
    }

    private readonly record struct StreamKey(long RequestId, ushort StreamId);

    internal readonly record struct PreparedFrame(
        IRpcByteBufferWriter Packet,
        int SerializedBytes,
        int BudgetBytes,
        int CreditBytes,
        bool Terminal,
        bool ForceFlush);

    internal sealed class StreamLease : IWriterReadyCompletion
    {
        internal readonly WriterFlowScheduler Owner;
        internal readonly long RequestId;
        internal readonly ushort StreamId;
        internal readonly long Generation;
        internal readonly Lock Gate = new();
        internal readonly LinkedListNode<StreamLease> ReadyNode;
        internal readonly Queue<PreparedFrame> Frames;
        internal readonly RpcDeadline Deadline;
        internal readonly TimeProvider? DeadlineClock;
        internal CancellationTokenRegistration Cancellation;
        internal TaskCompletionSource? SpaceChanged;
        internal TaskCompletionSource? PreparedDrained;
        internal bool ProducerBusy;
        internal bool JoinProducerOnExit;
        internal bool PreparationReleaseClaimed;
        internal long PreparationVersion;
        internal int ProducerBudgetBytes;
        internal bool CleanupInProgress;
        internal bool NotificationQueued;
        internal bool Scheduled;
        internal bool Finishing;
        internal bool Sealed;
        internal bool Active = true;
        internal bool Retired;
        internal bool StartAllowed;
        internal bool TerminalTaken;
        internal bool AbortApplied;
        internal Exception? AbortError;
        internal long QueuedBytes;
        internal long PendingReturn;
        internal long Credit;
        internal int WriterPins;

        internal StreamLease(WriterFlowScheduler owner, long requestId, ushort streamId,
            long generation, bool startAllowed, RpcDeadline deadline, TimeProvider? deadlineClock)
        {
            Owner = owner;
            RequestId = requestId;
            StreamId = streamId;
            Generation = generation;
            StartAllowed = startAllowed;
            Deadline = deadline;
            DeadlineClock = deadlineClock;
            Credit = owner._streamWindow;
            Frames = new Queue<PreparedFrame>(owner._slots);
            ReadyNode = new LinkedListNode<StreamLease>(this);
        }

        void IWriterReadyCompletion.Complete(int creditBytes, Exception? error)
            => Owner.Released(this, error);
    }

    internal long PreparedBytes => _preparedBudget.ReservedBytes;
    internal long ConnectionCredit => Volatile.Read(ref _connectionCredit);
    internal int ActiveStreams => Volatile.Read(ref _activeStreams);
    internal int RetainedStreams
    {
        get
        {
            lock (_lifecycleGate)
                return _streams.Count;
        }
    }
    internal Task StoppedTask => _stopped.Task;

    private static SharpLinkException Closed()
        => new(SharpLinkErrorCode.ConnectionClosed, "The stream send lifecycle is closed.");

    private static SharpLinkException Capacity(string message)
        => new(SharpLinkErrorCode.ResourceExhausted, message);

    private void ValidateLease(StreamLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!ReferenceEquals(lease.Owner, this))
            throw new InvalidOperationException("Foreign stream-send lease.");
    }

    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _terminal) is { } terminal)
            throw terminal;
    }

    private void NotifyLocked(StreamLease stream)
    {
        if (stream.NotificationQueued || stream.Retired || Volatile.Read(ref _writerStopped))
            return;
        stream.NotificationQueued = true;
        _notifications.Enqueue(stream);
    }

    private void MarkInactiveLocked(StreamLease stream)
    {
        if (!stream.Active)
            return;
        stream.Active = false;
        Interlocked.Decrement(ref _activeStreams);
    }

    private void RecordCleanupFailure(Exception error)
    {
        lock (_lifecycleGate)
            _cleanupFailure = _cleanupFailure is null ? error : new AggregateException(_cleanupFailure, error);
    }

    private void TryFinishStopped()
    {
        if (!Volatile.Read(ref _writerStopped) || Volatile.Read(ref _pendingStopProducers) != 0 ||
            Volatile.Read(ref _cleanupCount) != 0)
            return;
        lock (_lifecycleGate)
        {
            var error = _terminal ?? Closed();
            _stopped.TrySetException(_cleanupFailure is null
                ? error : new AggregateException(error, _cleanupFailure));
        }
    }
}
