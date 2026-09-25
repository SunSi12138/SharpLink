#if SHARPLINK_READY_WRITER_EXPERIMENT
using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

// Research-only bounded producer rings. The SAME rings/pump/flush contract serve
// A-ready (full existing producer-side controller) and B3-ready (pump-owned credit).
// Dynamic lifecycle APIs are owner-ordered research controls, not a full RPC adapter.
internal sealed partial class ReadyWriterCoordinator : IReadyStreamWorkSource
{
    private readonly RpcSession _session;
    private readonly SharpLinkRuntimeContext _context;
    private readonly CancellationTokenSource _cancel;
    private readonly StreamFlowController? _reference;
    private readonly int _window, _connectionWindow, _items, _bytes, _quantum;
    private int _turnStream = -1, _turnRemaining, _lastEmitted = -1, _consecutive, _maxConsecutive;
    private long _writerTurns;
    private readonly Stream[] _streams;
    private readonly Channel<ReadyNotification> _notifications;
    private readonly Channel<WriterEvent> _updates;
    private readonly LinkedList<Stream> _ready = new();
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _connectionCredit, _updatesWritten, _releases, _returned;
    private int _notificationsPending, _updatesPending;
    private long _channelReadCalls;
    private long _creditDebits, _selectionChecks, _streamBlocked, _connectionBlocked, _normalQueueRejected;
    private bool _blocked;
    private int _stopped;
    private readonly CancellationTokenRegistration _capacityCancellation;
    private readonly CancellationToken _stopToken;
    private Exception? _preparationCleanupFailure;
    private long _discarded;

    internal ReadyWriterCoordinator(RpcSession session, SharpLinkRuntimeContext context, CancellationTokenSource cancel,
        bool reference, int streams, int items, int bytes, int window, int connectionWindow, int slots, int quantum = 1, int preparedByteBudget = 0, bool dynamicLifetimes = false)
    {
        if (dynamicLifetimes && reference) throw new NotSupportedException("Dynamic lifetimes currently belong to the B3 control.");
        _dynamicLifetimes = dynamicLifetimes;
        if (preparedByteBudget < 0) throw new ArgumentOutOfRangeException(nameof(preparedByteBudget));
        if (streams < 1 || slots < 1 || slots > 256 || bytes <= 0 || items <= 0 || quantum < 1 || quantum > 64) throw new ArgumentOutOfRangeException(nameof(slots));
        _session = session; _context = context; _cancel = cancel; _window = window; _connectionWindow = connectionWindow;
        _items = items; _bytes = bytes; _connectionCredit = connectionWindow; _quantum = quantum;
        _stopToken = cancel.Token;
        if (reference) _reference = new StreamFlowController(window, connectionWindow, context.Protocol.MaxFramePayloadBytes, streams);
        _streams = new Stream[streams];
        for (var i = 0; i < streams; i++)
        {
            var stream = new Stream(i, slots, window, this, preparedByteBudget);
            _streams[i] = stream;
            if (dynamicLifetimes)
            {
                stream.Generation = 0; stream.Closed = true; stream.Retired = true;
            }
            else _identities.Add(new StreamIdentity(i + 1, 1), stream);
        }
        // Dynamic connections start empty. Initial capacity is not a set of fake
        // active wire identities that callers must close before their first Open.
        if (dynamicLifetimes)
            for (var i = streams - 1; i >= 0; i--) _retiredStreams.Push(_streams[i]);
        _notifications = Channel.CreateBounded<ReadyNotification>(new BoundedChannelOptions(streams)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _capacityCancellation = _stopToken.UnsafeRegister(static state => ((ReadyWriterCoordinator)state!).CancelCapacityWaits(), this);
        _updates = Channel.CreateBounded<WriterEvent>(new BoundedChannelOptions(checked(streams * 4))
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }

    private sealed class Stream : IValueTaskSource, IReadyFrameCompletion
    {
        internal readonly int Index;
        internal long Generation = 1, RequestId;
        internal ushort StreamId = 1;
        internal bool Closed, Retired, NotificationPending, WireAttached, CleanupFailed;
        internal bool HasHeldSpace => _spaceActive;
        internal IReadyFrameCompletion ReleaseTarget;
        private readonly ReadyWriterCoordinator? _completionOwner;
        internal readonly Lock Gate = new();
        internal readonly Queue<IRpcByteBufferWriter> Frames;
        internal readonly int Capacity;
        internal readonly int PreparedByteBudget;
        internal long QueuedBytes, MaximumQueuedBytes;
        internal int MaximumPacketBytes;
        private int _spaceRequiredBytes;
        internal int ProducerBusy, MaxDepth;
        internal long CapacityWaits, ReadyEvents;
        private ManualResetValueTaskSourceCore<bool> _space;
        private bool _spaceActive, _spaceSignaled;
        internal readonly LinkedListNode<Stream> Node;
        internal bool Scheduled;
        internal long Credit, Outstanding;
        internal int Taken, Released;
        internal StreamFlowController.ResolvedSendCreditLease Lease;
        internal Stream(int index, int slots, int window, ReadyWriterCoordinator? completionOwner = null, int preparedByteBudget = 0)
        { RequestId = index + 1; ReleaseTarget = this; PreparedByteBudget = preparedByteBudget; _completionOwner = completionOwner; Index = index; Frames = new Queue<IRpcByteBufferWriter>(slots); Capacity = slots; Node = new(this); Credit = window; _space.RunContinuationsAsynchronously = true; }

        public void Complete(Exception? error)
        {
            // This embedded completion belongs ONLY to generation one. Later lifetimes
            // have immutable epoch targets, so an old callback cannot address new DATA.
            _completionOwner!.ReleaseGeneration(Index, 1, _completionOwner._bytes, admitted: true, error);
        }

        internal bool CanFit(int packetBytes)
            => packetBytes >= 0 && Frames.Count < Capacity &&
                (PreparedByteBudget == 0 || Frames.Count == 0 || packetBytes <= PreparedByteBudget - QueuedBytes);

        internal ValueTask WaitForSpace(CancellationToken token, int packetBytes = 0)
        {
            lock (Gate) return WaitForSpaceLocked(token, packetBytes);
        }
        internal ValueTask WaitForSpaceLocked(CancellationToken token, int packetBytes)
        {
            token.ThrowIfCancellationRequested();
            if (CanFit(packetBytes)) return ValueTask.CompletedTask;
            if (_spaceActive) throw new InvalidOperationException("Capacity result is still owned by its consumer.");
            _space.Reset(); _spaceRequiredBytes = packetBytes; _spaceActive = true; _spaceSignaled = false; CapacityWaits++;
            return new ValueTask(this, _space.Version);
        }
        internal void SignalSpace(Exception? error = null)
        {
            lock (Gate)
            {
                if (!_spaceActive || _spaceSignaled || (error is null && _spaceRequiredBytes != 0 && !CanFit(_spaceRequiredBytes))) return;
                _spaceSignaled = true;
                if (error is null) _space.SetResult(true); else _space.SetException(error);
            }
        }
        public void GetResult(short token)
        {
            lock (Gate)
            {
                if (!_spaceActive || token != _space.Version || _space.GetStatus(token) == ValueTaskSourceStatus.Pending)
                    throw new InvalidOperationException("Invalid capacity result consumption.");
                try { _space.GetResult(token); }
                finally
                {
                    _spaceActive = false;
                    if (Closed) _completionOwner?.RequestRetirement();
                }
            }
        }
        public ValueTaskSourceStatus GetStatus(short token)
        {
            lock (Gate)
            {
                if (!_spaceActive || token != _space.Version) throw new InvalidOperationException("Expired capacity result.");
                return _space.GetStatus(token);
            }
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            lock (Gate)
            {
                if (!_spaceActive || token != _space.Version) throw new InvalidOperationException("Expired capacity continuation.");
                _space.OnCompleted(continuation, state, token, flags);
            }
        }
    }
    private readonly record struct Update(long RequestId, ushort StreamId, int Bytes);
    internal Task Completion => _settled.Task;
    internal void Attach() => _session.AttachReadyWriterExperiment(this);

    // Packet ownership transfers on entry, including cancellation/failure. Serialization
    // and arbitrary outbound extensions are completed by the caller BEFORE this entry.
    // Legacy control callers refer to the original lifetime, never a newly reused slot.
    internal ValueTask EnqueueAsync(int index, IRpcByteBufferWriter packet)
        => EnqueueAsync(new StreamHandle(this, index, 1), packet);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask EnqueueAsync(StreamHandle handle, IRpcByteBufferWriter packet)
    {
        Stream? stream = null;
        var debited = false; var transferred = false; var ownsProducer = false;
        try
        {
            if (!ReferenceEquals(handle.Owner, this) || (uint)handle.Slot >= (uint)_streams.Length)
                throw new InvalidOperationException("Foreign or invalid stream handle.");
            stream = _streams[handle.Slot];
            ValueTask capacity;
            lock (stream.Gate)
            {
                ValidateHandle(handle, stream);
                if (stream.Closed) throw new InvalidOperationException("Closed stream.");
                if (stream.ProducerBusy != 0) throw new InvalidOperationException("Only one producer per stream is supported by this control.");
                stream.ProducerBusy = 1; ownsProducer = true;
                capacity = stream.WaitForSpaceLocked(_stopToken, packet.WrittenCount);
            }
            await capacity.ConfigureAwait(false);
            if (_reference is not null)
            {
                if (stream.Lease.IsResolved) await _reference.AcquireSendCreditAsync(in stream.Lease, _bytes, _stopToken).ConfigureAwait(false);
                else stream.Lease = await _reference.AcquireSendCreditLeaseAsync(stream.RequestId, stream.StreamId, _bytes, _stopToken).ConfigureAwait(false);
                debited = true;
            }
            var notify = false;
            lock (stream.Gate)
            {
                _stopToken.ThrowIfCancellationRequested();
                ValidateHandle(handle, stream);
                if (stream.Closed) throw new InvalidOperationException("Closed stream.");
                if (Volatile.Read(ref _stopped) != 0) throw new InvalidOperationException("Ready writer stopped.");
                if (!stream.CanFit(packet.WrittenCount)) throw new InvalidOperationException("Producer ring count/byte bound exceeded.");
                stream.Frames.Enqueue(packet); transferred = true;
                stream.QueuedBytes = checked(stream.QueuedBytes + packet.WrittenCount);
                stream.MaximumPacketBytes = Math.Max(stream.MaximumPacketBytes, packet.WrittenCount);
                stream.MaximumQueuedBytes = Math.Max(stream.MaximumQueuedBytes, stream.QueuedBytes);
                stream.MaxDepth = Math.Max(stream.MaxDepth, stream.Frames.Count);
                if (!stream.Scheduled) { stream.Scheduled = true; stream.NotificationPending = true; stream.ReadyEvents++; notify = true; }
            }
            if (notify)
            {
                // One outstanding notification/node per stream, so this bounded queue
                // cannot overflow from producer activity. A violation is a failed run.
                if (!_notifications.Writer.TryWrite(new ReadyNotification(stream, handle.Generation))) throw new InvalidOperationException("Duplicate or lost ready notification.");
                // Publish queue contents before the hint. The single reader consumes
                // only counted messages; a late hint always signals after publication.
                Interlocked.Increment(ref _notificationsPending);
                _session.SignalReadyWriterExperiment();
            }
        }
        catch
        {
            if (!transferred)
            {
                if (debited) _reference!.ReturnUnsentCredit(in stream!.Lease, _bytes);
                _context.Buffers.Return(packet);

            }
            throw;
        }
        finally
        {
            if (ownsProducer)
            {
                Volatile.Write(ref stream!.ProducerBusy, 0);
                if (Volatile.Read(ref stream.Closed)) RequestRetirement();
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask EnqueueUpdateAsync(long requestId, ushort streamId, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        await _updates.Writer.WriteAsync(new WriterEvent(new Update(requestId, streamId, bytes), null), _stopToken).ConfigureAwait(false);
        _updatesWritten++; // The transport has exactly one sequential credit reader.
        Interlocked.Increment(ref _updatesPending);
        _session.SignalReadyWriterExperiment();
    }

    // The following methods, including all B3 credit reads/writes, are owner-only.
    public bool HasWork => !_stopToken.IsCancellationRequested && Volatile.Read(ref _stopped) == 0 &&
        (Volatile.Read(ref _retirementRequested) != 0 || Volatile.Read(ref _notificationsPending) != 0 ||
            Volatile.Read(ref _updatesPending) != 0 || (!_blocked && _ready.Count != 0));

    private void DrainNotifications()
    {
        while (Volatile.Read(ref _notificationsPending) != 0)
        {
            _channelReadCalls++;
            if (!_notifications.Reader.TryRead(out var notification)) throw new InvalidOperationException("Published ready count has no message.");
            Interlocked.Decrement(ref _notificationsPending);
            var stream = notification.Stream;
            lock (stream.Gate)
            {
                if (stream.Generation != notification.Generation || stream.Retired) { _staleEvents++; continue; }
                stream.NotificationPending = false;
                if (stream.Closed) { stream.Scheduled = false; TryRetireStream(stream); continue; }
                if (stream.Node.List is not null) throw new InvalidOperationException("A stream was scheduled twice.");
                _ready.AddLast(stream.Node); _blocked = false;
            }
        }
        var count = 0;
        while (count++ < 256 && Volatile.Read(ref _updatesPending) != 0)
        {
            _channelReadCalls++;
            if (!_updates.Reader.TryRead(out var update)) throw new InvalidOperationException("Published credit count has no message.");
            Interlocked.Decrement(ref _updatesPending);
            if (update.Operation is { } operation) operation.Execute();
            else ApplyWireUpdate(update.Update);
        }
        if (Volatile.Read(ref _retirementRequested) != 0 && Interlocked.Exchange(ref _retirementRequested, 0) != 0)
            foreach (var stream in _streams) TryRetireStream(stream);
        CheckSettled();
    }

    // Mechanism-only checks use this overload without an actual pump budget.
    internal bool TryTake(out ReadyStreamFrame frame) => TryTake(UnboundedAdmission.Instance, out frame);

    private sealed class UnboundedAdmission : IReadyFrameAdmission
    {
        internal static readonly UnboundedAdmission Instance = new();
        public bool TryReserve(int frameBytes) => true;
    }

    public bool TryTake(IReadyFrameAdmission admission, out ReadyStreamFrame frame)
    {
        frame = default;
        if (_stopToken.IsCancellationRequested || Volatile.Read(ref _stopped) != 0) return false;
        DrainNotifications();
        var node = _ready.First;
        while (node is not null)
        {
            var next = node.Next; var stream = node.Value;
            lock (stream.Gate)
            {
                // Cancellation cleans the same prepared ring under this gate. A frame
                // already removed belongs to the writer; a canceled queued head may not
                // acquire budget or credit after the cleanup has won this boundary.
                if (_stopToken.IsCancellationRequested || Volatile.Read(ref _stopped) != 0) return false;
                if (stream.Closed || stream.Frames.Count == 0)
                {
                    stream.Scheduled = false; _ready.Remove(node); node = next; continue;
                }
                _selectionChecks++;
                if (_reference is null)
                {
                    if (!Available(stream.Credit, _window, _bytes)) { _streamBlocked++; node = next; continue; }
                    if (!Available(_connectionCredit, _connectionWindow, _bytes))
                    { _connectionBlocked++; _blocked = true; return false; }
                }
                // The full serialized frame must fit the pump before B3 credit is
                // debited or either variant removes its ready head. A transient
                // miss keeps order and ownership unchanged for the next owner turn.
                if (!admission.TryReserve(stream.Frames.Peek().WrittenCount)) return false;
                if (_reference is null)
                {
                    stream.Credit -= _bytes; _connectionCredit -= _bytes;
                    stream.WireAttached = true;
                    _creditDebits++;
                }
                var packet = stream.Frames.Dequeue(); stream.QueuedBytes -= packet.WrittenCount; stream.Taken++;
                stream.Outstanding += _bytes; // Same owner-only settlement ledger in both modes.
                // A bounded scheduling quantum, NOT wire item batching or credit grants.
                // Peer updates/progress are still checked between frames by the pump.
                if (_turnStream != stream.Index || _turnRemaining == 0)
                { _turnStream = stream.Index; _turnRemaining = _quantum; _writerTurns++; }
                _turnRemaining--;
                if (stream.Frames.Count == 0 || _turnRemaining == 0)
                {
                    _ready.Remove(node);
                    if (stream.Frames.Count != 0) _ready.AddLast(node);
                    else stream.Scheduled = false;
                    _turnStream = -1;
                }
                _consecutive = _lastEmitted == stream.Index ? _consecutive + 1 : 1;
                _lastEmitted = stream.Index; _maxConsecutive = Math.Max(_maxConsecutive, _consecutive);
                stream.SignalSpace();
                frame = new ReadyStreamFrame(packet, stream.Index, _bytes, stream.ReleaseTarget);
                return true;
            }
        }
        _blocked = true;
        return false;
    }

    internal static bool Available(long credit, int window, int bytes)
        => bytes <= credit || (bytes > window && credit == window);

    // No-throw pump completion callback; a failure is published to the joined task.
    public void Released(int slot, int creditBytes, bool admitted, Exception? error)
        => ReleaseGeneration(slot, 1, creditBytes, admitted, error);

    private void ReleaseGeneration(int slot, long generation, int creditBytes, bool admitted, Exception? error)
    {
        try
        {
            var stream = _streams[slot];
            if (stream.Generation != generation || stream.Retired) { _staleEvents++; return; }
            if (creditBytes != _bytes || stream.Released >= stream.Taken) throw new InvalidOperationException("Invalid or duplicate settlement.");
            if (!admitted)
            {
                _normalQueueRejected++;
                ReturnWriterUnsent(stream, creditBytes);
            }
            stream.Released++; _releases++;
            if (error is not null) _settled.TrySetException(error);
            TryRetireStream(stream);
            CheckSettled();
        }
        catch (Exception failure) { _settled.TrySetException(failure); }
    }

    private void CheckSettled()
    {
        if (_dynamicLifetimes) return; // A dynamic caller joins explicit close operations, not a fixed item total.
        var total = checked((long)_streams.Length * _items);
        if (_releases != total || _returned != total * _bytes) return;
        if (_reference is null)
        {
            if (_connectionCredit != _connectionWindow) throw new InvalidOperationException("Connection permission leaked.");

        }
        foreach (var stream in _streams)
            if ((_reference is null && stream.Credit != _window) || stream.Outstanding != 0 || stream.Taken != _items || stream.Released != _items)
                throw new InvalidOperationException("Stream credit or a publication did not settle.");
        _settled.TrySetResult();
    }

    internal Dictionary<string, long> Metrics() => new()
    {
        ["SchedulingQuantum"] = _quantum,
        ["WriterTurns"] = _writerTurns,
        ["MaxConsecutiveFrames"] = _maxConsecutive,
        ["RingCapacityWaits"] = System.Linq.Enumerable.Sum(_streams, x => x.CapacityWaits),
        ["MaximumRingDepth"] = System.Linq.Enumerable.Max(_streams, x => x.MaxDepth),
        ["RingSlotsPerStream"] = _streams[0].Capacity,
        ["PreparedByteBudgetPerStream"] = _streams[0].PreparedByteBudget,
        ["MaximumObservedPacketBytes"] = System.Linq.Enumerable.Max(_streams, x => x.MaximumPacketBytes),
        ["MaximumQueuedBytesPerStream"] = System.Linq.Enumerable.Max(_streams, x => x.MaximumQueuedBytes),
        // Sum of individual peaks, not a simultaneous live-memory high-water mark.
        ["SumOfStreamQueuedBytePeaks"] = System.Linq.Enumerable.Sum(_streams, x => x.MaximumQueuedBytes),
        ["RemainingQueuedBytes"] = System.Linq.Enumerable.Sum(_streams, x => x.QueuedBytes),
        ["UnadmittedBuffersReturnedOnStop"] = _discarded,
        ["ReadyNotifications"] = System.Linq.Enumerable.Sum(_streams, x => x.ReadyEvents),
        ["EventChannelReadCalls"] = _channelReadCalls,
        ["WireUpdateNotifications"] = _updatesWritten,
        ["FramesReleased"] = _releases,
        ["CreditBytesApplied"] = _returned,
        ["WireCreditBytesObserved"] = _wireCreditBytesObserved,
        ["ExcessWireCreditBytes"] = _excessWireCreditBytes,
        ["IgnoredWireUpdates"] = _ignoredWireUpdates,
        ["PumpOwnedCreditDebits"] = _creditDebits,
        ["SelectionChecks"] = _selectionChecks,
        ["StreamBlockedChecks"] = _streamBlocked,
        ["ConnectionBlockedChecks"] = _connectionBlocked,
        ["NormalQueueRejections"] = _normalQueueRejected,
        ["ProducerSideCreditGateOperations"] = _reference is null ? 0 : _streams.Length * (long)_items,
        ["CreditOwnerQueueCompletions"] = 0,
        // Existing SendPump's byte-budget CAS + release RMW remain per frame.
        // This is an audited LOWER BOUND, not a measurement of retry/runtime atomics.
        ["ExistingPumpBudgetRmwLowerBound"] = 2 * _releases,
    };
}
#endif
