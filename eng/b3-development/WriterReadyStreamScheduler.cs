namespace SharpLink.Runtime;

// Development component, not enabled for normal RPC. The session adapter must select
// ONE send authority and preserve Request -> DATA -> terminal publication order.
internal sealed class WriterReadyStreamScheduler : IWriterReadySource
{
    private readonly Lock _registryGate = new();
    private readonly Dictionary<StreamKey, Stream> _streams = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Stream> _dirty = new();
    private readonly LinkedList<Stream> _ready = new();
    private readonly Action _signal;
    private readonly Action<IRpcByteBufferWriter> _returnBuffer;
    private readonly int _streamWindow, _connectionWindow, _maxStreams;
    private readonly int _frameSlots, _preparedByteLimit, _maxPacketBytes, _maxCreditBytes, _quantum;
    private long _connectionCredit;
    private Exception? _terminal;
    private bool _creditBlocked;
    private Stream? _turn;
    private int _turnRemaining;

    internal WriterReadyStreamScheduler(Action signal, Action<IRpcByteBufferWriter> returnBuffer,
        int streamWindow, int connectionWindow, int maxStreams, int frameSlots,
        int preparedByteLimit, int maxPacketBytes, int maxCreditBytes, int quantum = 16)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(returnBuffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamWindow);
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionWindow, streamWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStreams);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSlots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparedByteLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPacketBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCreditBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantum);
        _signal = signal; _returnBuffer = returnBuffer;
        _streamWindow = streamWindow; _connectionWindow = connectionWindow;
        _connectionCredit = connectionWindow; _maxStreams = maxStreams;
        _frameSlots = frameSlots; _preparedByteLimit = preparedByteLimit;
        _maxPacketBytes = maxPacketBytes; _maxCreditBytes = maxCreditBytes; _quantum = quantum;
    }

    private readonly record struct StreamKey(long RequestId, ushort StreamId);
    internal readonly record struct Prepared(IRpcByteBufferWriter Packet, int PacketBytes, int CreditBytes);

    // Immutable completion epoch. This first component does not pool Stream objects.
    // An old reference cannot address a replacement even when the wire key is reused.
    internal sealed class Stream : IWriterReadyCompletion
    {
        internal readonly WriterReadyStreamScheduler Owner;
        internal readonly long RequestId;
        internal readonly ushort StreamId;
        internal readonly Lock Gate = new();
        internal readonly Queue<Prepared> Frames = new();
        internal readonly LinkedListNode<Stream> Node;
        internal TaskCompletionSource? Drained, Capacity;
        internal long PreparedBytes, PendingCredit;
        internal bool DirtyQueued, Finishing, CompleteRequested, AbortApplied, Retired, ProducerBusy;
        internal Exception? Aborted;
        // Credit is writer-only. Pins protect the separate transport-buffer lifecycle.
        internal long Credit;
        internal int WriterPins;
        internal Stream(WriterReadyStreamScheduler owner, long requestId, ushort streamId, int window)
        {
            Owner = owner; RequestId = requestId; StreamId = streamId; Credit = window; Node = new(this);
        }
        void IWriterReadyCompletion.Complete(int creditBytes, Exception? error) => Owner.Released(this, error);
    }

    internal Stream Open(long requestId, ushort streamId)
    {
        lock (_registryGate)
        {
            ThrowIfStopped();
            var key = new StreamKey(requestId, streamId);
            if (_streams.ContainsKey(key)) throw new InvalidOperationException("A retained lifecycle owns this identity.");
            if (_streams.Count >= _maxStreams)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "Retained writer stream limit reached.");
            var stream = new Stream(this, requestId, streamId, _streamWindow);
            _streams.Add(key, stream);
            return stream;
        }
    }

    // Ownership transfers on entry, including rejection/cancellation. No codec runs here.
    // The ordinary async builder is intentional; no new pooling-builder Task-compatibility
    // changes are introduced by this component.
    internal async ValueTask EnqueueAsync(Stream stream, IRpcByteBufferWriter packet, int creditBytes,
        CancellationToken cancellationToken = default)
    {
        bool transferred = false, ownsProducer = false;
        try
        {
            ArgumentNullException.ThrowIfNull(packet);
            ValidateOwner(stream);
            cancellationToken.ThrowIfCancellationRequested();
            var packetBytes = packet.WrittenCount;
            if (packetBytes <= 0 || packetBytes > _maxPacketBytes || packet.WrittenMemory.Length != packetBytes ||
                creditBytes <= 0 || creditBytes > _maxCreditBytes)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "Invalid prepared packet or credit length.");
            var prepared = new Prepared(packet, packetBytes, creditBytes);
            lock (stream.Gate)
            {
                ValidateProducer(stream);
                if (stream.ProducerBusy) throw new InvalidOperationException("A stream supports one sequential producer.");
                stream.ProducerBusy = true; ownsProducer = true;
            }
            while (true)
            {
                Task? wait = null;
                bool notify = false;
                lock (stream.Gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateProducer(stream);
                    if (CanFit(stream, packetBytes))
                    {
                        stream.Frames.Enqueue(prepared);
                        stream.PreparedBytes = checked(stream.PreparedBytes + packetBytes);
                        transferred = true;
                        if (stream.Node.List is null) notify = PublishDirtyLocked(stream);
                    }
                    else
                    {
                        stream.Capacity ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                        wait = stream.Capacity.Task;
                    }
                }
                if (notify) _signal();
                if (transferred) return;
                // Never enter the async wait/cleanup path under a stream gate.
                await wait!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            bool notify = false;
            if (ownsProducer)
            {
                lock (stream.Gate)
                {
                    stream.ProducerBusy = false;
                    if (stream.CompleteRequested) notify = PublishDirtyLocked(stream);
                }
            }
            if (notify) _signal();
            if (!transferred && packet is not null) _returnBuffer(packet);
        }
    }

    private bool CanFit(Stream stream, int bytes) => stream.Frames.Count < _frameSlots &&
        (stream.Frames.Count == 0 || bytes <= _preparedByteLimit - stream.PreparedBytes);

    private bool PublishDirtyLocked(Stream stream)
    {
        if (stream.DirtyQueued || stream.Retired) return false;
        stream.DirtyQueued = true;
        _dirty.Enqueue(stream);
        return true;
    }

    // Await BEFORE publishing clean EOF through a different pump lane. Credit may still
    // be outstanding at drain completion; that is tombstone debt, not buffer ownership.
    internal Task FinishAsync(Stream stream)
    {
        ValidateOwner(stream);
        bool notify;
        Task result;
        lock (stream.Gate)
        {
            ThrowIfStopped();
            if (stream.Aborted is { } error) return Task.FromException(error);
            if (stream.Retired) throw new InvalidOperationException("Expired stream handle.");
            if (stream.ProducerBusy) throw new InvalidOperationException("Finish raced a pending producer.");
            stream.Finishing = true;
            stream.Drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            TrySetDrainedLocked(stream);
            notify = PublishDirtyLocked(stream);
            result = stream.Drained.Task;
        }
        if (notify) _signal();
        return result;
    }

    internal void Complete(Stream stream)
    {
        ValidateOwner(stream);
        bool notify;
        lock (stream.Gate)
        {
            if (stream.Retired) return;
            if (stream.Aborted is null && stream.Drained?.Task.IsCompletedSuccessfully != true)
                throw new InvalidOperationException("Clean terminal must follow successful DATA drain.");
            stream.CompleteRequested = true;
            notify = PublishDirtyLocked(stream);
        }
        if (notify) _signal();
    }

    // May run while transport Flush is blocked. Only PREPARED buffers are discarded;
    // already selected frames remain exclusively owned by SendPump.
    internal void Abort(Stream stream, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ValidateOwner(stream);
        List<IRpcByteBufferWriter>? discarded = null;
        bool notify;
        lock (stream.Gate)
        {
            if (stream.Retired || stream.Aborted is not null) return;
            stream.Aborted = error;
            while (stream.Frames.TryDequeue(out var frame)) (discarded ??= new()).Add(frame.Packet);
            stream.PreparedBytes = 0;
            stream.Capacity?.TrySetResult();
            stream.Capacity = null;
            if (stream.Drained is not null)
            {
                stream.Drained.TrySetException(error);
                _ = stream.Drained.Task.Exception;
            }
            notify = PublishDirtyLocked(stream);
        }
        if (notify) _signal();
        ReturnDiscarded(discarded);
    }

    internal void ApplyWindowUpdate(long requestId, ushort streamId, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        Stream? stream;
        lock (_registryGate)
        {
            ThrowIfStopped();
            if (!_streams.TryGetValue(new(requestId, streamId), out stream)) return;
        }
        bool notify;
        lock (stream.Gate)
        {
            if (stream.Retired) return;
            // Maximum possible single-stream debt is at most one positive Int32 frame.
            stream.PendingCredit = Math.Min(int.MaxValue, stream.PendingCredit + bytes);
            notify = PublishDirtyLocked(stream);
        }
        if (notify) _signal();
    }

    public bool HasWork => Volatile.Read(ref _terminal) is null &&
        (!_dirty.IsEmpty || (!_creditBlocked && _ready.Count != 0));

    public bool TryTake(IWriterReadyAdmission admission, out WriterReadyFrame frame)
    {
        frame = default;
        if (Volatile.Read(ref _terminal) is not null) return false;
        DrainChanges();
        var node = _ready.First;
        while (node is not null)
        {
            var stream = node.Value;
            var next = node.Next;
            lock (stream.Gate)
            {
                if (stream.Aborted is not null || stream.Retired || stream.Frames.Count == 0)
                {
                    _ready.Remove(node); TrySetDrainedLocked(stream); node = next; continue;
                }
                var prepared = stream.Frames.Peek();
                if (!Available(stream.Credit, _streamWindow, prepared.CreditBytes)) { node = next; continue; }
                if (!Available(_connectionCredit, _connectionWindow, prepared.CreditBytes))
                { _creditBlocked = true; return false; }
                if (!admission.TryReserve(prepared.PacketBytes)) return false;
                stream.Credit -= prepared.CreditBytes;
                _connectionCredit -= prepared.CreditBytes;
                stream.Frames.Dequeue(); stream.PreparedBytes -= prepared.PacketBytes; stream.WriterPins++;
                if (!ReferenceEquals(_turn, stream) || _turnRemaining == 0)
                { _turn = stream; _turnRemaining = _quantum; }
                _turnRemaining--;
                if (stream.Frames.Count == 0 || _turnRemaining == 0)
                {
                    _ready.Remove(node);
                    if (stream.Frames.Count != 0) _ready.AddLast(node);
                    _turn = null;
                }
                stream.Capacity?.TrySetResult(); stream.Capacity = null;
                frame = new WriterReadyFrame(prepared.Packet, prepared.CreditBytes, stream);
                return true;
            }
        }
        _creditBlocked = true;
        return false;
    }

    private void DrainChanges()
    {
        // A bounded turn, not a per-item registry lock or a scan of all streams.
        for (var index = 0; index < 64 && _dirty.TryDequeue(out var stream); index++)
        {
            lock (stream.Gate)
            {
                stream.DirtyQueued = false;
                if (stream.Retired) continue;
                if (stream.Aborted is not null && !stream.AbortApplied)
                {
                    _connectionCredit += _streamWindow - stream.Credit;
                    stream.Credit = _streamWindow; stream.AbortApplied = true;
                }
                var effective = Math.Min(stream.PendingCredit, _streamWindow - stream.Credit);
                stream.PendingCredit = 0;
                stream.Credit += effective; _connectionCredit += effective;
                if (stream.Aborted is null && stream.Frames.Count != 0 && stream.Node.List is null)
                    _ready.AddLast(stream.Node);
                if (stream.Aborted is not null && stream.Node.List is not null) _ready.Remove(stream.Node);
                TrySetDrainedLocked(stream); TryRetireLocked(stream);
            }
        }
        _creditBlocked = false;
    }

    private static bool Available(long credit, int window, int bytes)
        => bytes <= credit || (bytes > window && credit == window);
    private static void TrySetDrainedLocked(Stream stream)
    {
        if (stream.Finishing && stream.Aborted is null && stream.Frames.Count == 0 && stream.WriterPins == 0)
            stream.Drained?.TrySetResult();
    }
    private void Released(Stream stream, Exception? error)
    {
        lock (stream.Gate)
        {
            if (stream.WriterPins <= 0)
            {
                stream.Aborted ??= new InvalidOperationException("Duplicate writer release.");
                stream.Drained?.TrySetException(stream.Aborted);
                if (stream.Drained is not null) _ = stream.Drained.Task.Exception;
                return;
            }
            stream.WriterPins--;
            if (error is not null)
            {
                stream.Aborted ??= error;
                stream.Drained?.TrySetException(error);
                if (stream.Drained is not null) _ = stream.Drained.Task.Exception;
            }
            TrySetDrainedLocked(stream); TryRetireLocked(stream);
        }
    }
    private void TryRetireLocked(Stream stream)
    {
        if (!stream.CompleteRequested || stream.WriterPins != 0 || stream.Frames.Count != 0 ||
            stream.Credit != _streamWindow || stream.ProducerBusy || stream.DirtyQueued || stream.Retired) return;
        if (stream.Node.List is not null) _ready.Remove(stream.Node);
        lock (_registryGate)
        {
            var key = new StreamKey(stream.RequestId, stream.StreamId);
            if (_streams.TryGetValue(key, out var current) && ReferenceEquals(current, stream)) _streams.Remove(key);
            stream.Retired = true;
        }
    }

    public void Stopped(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.CompareExchange(ref _terminal, error, null) is not null) return;
        Stream[] streams;
        lock (_registryGate) { streams = new Stream[_streams.Count]; _streams.Values.CopyTo(streams, 0); }
        List<Exception>? failures = null;
        foreach (var stream in streams)
        {
            try { Abort(stream, error); }
            catch (Exception cleanup) { (failures ??= new()).Add(cleanup); }
            lock (stream.Gate) stream.Retired = true;
        }
        _ready.Clear();
        while (_dirty.TryDequeue(out _)) { }
        lock (_registryGate) _streams.Clear();
        if (failures is not null) throw new AggregateException("Prepared-frame cleanup failed.", failures);
    }
    private void ReturnDiscarded(List<IRpcByteBufferWriter>? packets)
    {
        if (packets is null) return;
        List<Exception>? failures = null;
        foreach (var packet in packets)
        {
            try { _returnBuffer(packet); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Prepared-frame cleanup failed.", failures);
    }
    private void ValidateProducer(Stream stream)
    {
        ThrowIfStopped();
        if (stream.Aborted is { } error) throw error;
        if (stream.Retired || stream.Finishing || stream.CompleteRequested)
            throw new InvalidOperationException("The stream no longer accepts DATA.");
    }
    private void ValidateOwner(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!ReferenceEquals(stream.Owner, this)) throw new InvalidOperationException("Foreign stream handle.");
    }
    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _terminal) is { } error) throw error;
    }
    // Writer-only or quiescent test diagnostics; not an atomic concurrent snapshot.
    internal long ConnectionCredit => _connectionCredit;
    internal int RetainedStreams { get { lock (_registryGate) return _streams.Count; } }
}
