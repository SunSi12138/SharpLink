namespace SharpLink.Runtime;

internal sealed class SharedMemoryPipeReader : PipeReader
{
    private readonly SharedMemoryRingDirection _direction;
    private readonly SharedMemoryControlChannel _control;
    private readonly int _spinCount;
    private readonly RingSequenceSegment _firstSegment = new();
    private readonly RingSequenceSegment _secondSegment = new();
    private ReadOnlySequence<byte> _currentBuffer;
    private PooledReadBuffer? _staging;
    private long _readPosition;
    private long _cachedWritePosition;
    private int _currentRingLength;
    private bool _currentIsStaging;
    private bool _stagingExaminedAll;
    private bool _hasOutstandingRead;
    private TaskCompletionSource<bool>? _readActivityReleased;
    private int _peerWriterArmed;
    private int _cancelPending;
    private int _readOperationPending;
    private int _completed;

    public SharedMemoryPipeReader(
        SharedMemoryRingDirection direction,
        SharedMemoryControlChannel control,
        int spinCount)
    {
        _direction = direction;
        _control = control;
        _spinCount = spinCount;
        _readPosition = direction.ReadReadPosition();
        _cachedWritePosition = RefreshWritePosition();
    }

    public override void AdvanceTo(SequencePosition consumed)
        => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        if (!Volatile.Read(ref _hasOutstandingRead))
            return;

        long consumedBytes;
        try
        {
            consumedBytes = _currentBuffer.Slice(0, consumed).Length;
            _ = _currentBuffer.Slice(0, examined).Length;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException("Consumed and examined positions must belong to the active shared-memory read buffer.", exception);
        }

        var examinedBytes = _currentBuffer.Slice(0, examined).Length;
        if (Volatile.Read(ref _completed) != 0)
        {
            // Connection disposal can race the consumer's finally/AdvanceTo. The mapping
            // may already be closing, so only release the locally staged buffer here.
            DisposeStaging();
        }
        else if (_currentIsStaging)
        {
            _staging!.Consume(checked((int)consumedBytes));
            _stagingExaminedAll = examinedBytes == _currentBuffer.Length && _staging.WrittenCount != 0;
            if (_staging.WrittenCount == 0)
            {
                _staging.Dispose();
                _staging = null;
                _stagingExaminedAll = false;
            }
        }
        else
        {
            var readPosition = _readPosition;
            var remaining = checked(_currentRingLength - (int)consumedBytes);
            // Pipe consumers are allowed to examine an incomplete frame without consuming it.
            // Move that examined tail out of the bounded ring so a frame larger than the ring,
            // or a frame crossing an arbitrary wrap boundary, can continue arriving.
            var shouldStage = remaining != 0 && examinedBytes == _currentBuffer.Length;
            if (shouldStage)
            {
                _staging = new PooledReadBuffer(remaining);
                _staging.Append(_currentBuffer.Slice(consumed));
                _stagingExaminedAll = true;
                _readPosition = unchecked(readPosition + _currentRingLength);
                _direction.PublishReadPosition(_readPosition);
                if (_direction.TakeWriterWaiting() | TakePeerWriterArmed())
                    _control.SignalSpaceAvailable();
            }
            else
            {
                _readPosition = unchecked(readPosition + consumedBytes);
                _direction.PublishReadPosition(_readPosition);
                if (consumedBytes != 0 &&
                    (_direction.TakeWriterWaiting() | TakePeerWriterArmed()))
                    _control.SignalSpaceAvailable();
            }
        }
        _currentBuffer = default;
        _currentRingLength = 0;
        _currentIsStaging = false;
        Volatile.Write(ref _hasOutstandingRead, false);
        Volatile.Read(ref _readActivityReleased)?.TrySetResult(true);
    }

    public override void CancelPendingRead()
    {
        Volatile.Write(ref _cancelPending, 1);
        _control.PulseDataWaiter();
    }

    public override void Complete(Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _direction.CloseReader();
        if (_direction.TakeWriterWaiting() | TakePeerWriterArmed())
            _control.SignalSpaceAvailable();
        _control.PulseDataWaiter();
        if (!HasReadActivity())
            DisposeStaging();
    }

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        Complete(exception);
        var readActivityReleased = WaitForReadActivityReleaseAsync();
        if (readActivityReleased.IsCompletedSuccessfully)
        {
            DisposeStaging();
            return ValueTask.CompletedTask;
        }

        return new ValueTask(CompleteAfterReadActivityAsync(readActivityReleased));
    }

    public override bool TryRead(out ReadResult result)
    {
        if (Interlocked.CompareExchange(ref _readOperationPending, 1, 0) != 0)
            throw new InvalidOperationException("Only one shared-memory read operation can be pending.");
        try
        {
            if (Volatile.Read(ref _hasOutstandingRead))
                throw new InvalidOperationException("AdvanceTo must be called before reading again.");
            if (Interlocked.Exchange(ref _cancelPending, 0) != 0)
            {
                result = new ReadResult(default, isCanceled: true, isCompleted: false);
                return true;
            }

            if (TryCreateAvailableReadResult(_stagingExaminedAll, out result))
                return true;
            result = default;
            return false;
        }
        finally
        {
            FinishReadOperation();
        }
    }

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _readOperationPending, 1, 0) != 0)
            throw new InvalidOperationException("Only one shared-memory read operation can be pending.");
        try
        {
            if (Volatile.Read(ref _hasOutstandingRead))
                throw new InvalidOperationException("AdvanceTo must be called before reading again.");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref _cancelPending, 0) != 0)
                    return new ReadResult(default, isCanceled: true, isCompleted: false);
                if (TryCreateAvailableReadResult(_stagingExaminedAll, out var result))
                    return result;

                SpinBriefly();
                if (TryCreateAvailableReadResult(_stagingExaminedAll, out result))
                    return result;

                _direction.SetReaderWaiting();
                if (TryCreateAvailableReadResult(_stagingExaminedAll, out result))
                {
                    _direction.ClearReaderWaiting();
                    return result;
                }
                if (_control.IsClosed)
                {
                    _direction.ClearReaderWaiting();
                    _control.ThrowIfFaulted();
                    return new ReadResult(default, isCanceled: false, isCompleted: true);
                }

                try
                {
                    SharpLinkTelemetry.RecordSharedMemoryWait("reader");
                    _control.SignalDataWaiterArmed();
                    await _control.WaitForDataAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SharpLinkException exception) when (
                    _control.IsClosed && exception.Code == SharpLinkErrorCode.ConnectionClosed)
                {
                    if (TryCreateAvailableReadResult(requireAdditionalStagingData: false, out result))
                        return result;
                    return new ReadResult(default, isCanceled: false, isCompleted: true);
                }
                finally
                {
                    _direction.ClearReaderWaiting();
                }
            }
        }
        finally
        {
            FinishReadOperation();
        }
    }

    private bool TryCreateReadResult(out ReadResult result)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            result = new ReadResult(default, isCanceled: false, isCompleted: true);
            return true;
        }

        var readPosition = _readPosition;
        var writePosition = _cachedWritePosition;
        var available = _direction.GetAvailableBytes(writePosition, readPosition);
        if (available == 0)
        {
            writePosition = RefreshWritePosition();
            available = _direction.GetAvailableBytes(writePosition, readPosition);
        }
        if (available == 0)
        {
            if (_direction.IsWriterClosed || _control.IsClosed)
            {
                _control.ThrowIfFaulted();
                result = new ReadResult(default, isCanceled: false, isCompleted: true);
                return true;
            }
            result = default;
            return false;
        }

        var index = (int)(unchecked((ulong)readPosition) & (uint)_direction.Mask);
        var firstLength = Math.Min(available, _direction.Capacity - index);
        if (firstLength == available)
        {
            _currentBuffer = new ReadOnlySequence<byte>(_direction.Memory.Slice(index, available));
        }
        else
        {
            _firstSegment.Reset(_direction.Memory.Slice(index, firstLength), 0);
            _secondSegment.Reset(_direction.Memory.Slice(0, available - firstLength), firstLength);
            _firstSegment.SetNext(_secondSegment);
            _currentBuffer = new ReadOnlySequence<byte>(
                _firstSegment,
                0,
                _secondSegment,
                _secondSegment.Memory.Length);
        }

        Volatile.Write(ref _hasOutstandingRead, true);
        _currentRingLength = available;
        _currentIsStaging = false;
        result = new ReadResult(_currentBuffer, isCanceled: false, isCompleted: false);
        return true;
    }

    private bool TryCreateAvailableReadResult(
        bool requireAdditionalStagingData,
        out ReadResult result)
    {
        // Once an incomplete frame has been staged, ring bytes are continuations of
        // that prefix and must never be surfaced as a standalone sequence.
        if (_staging is not null && _staging.WrittenCount != 0)
            return TryCreateStagedReadResult(requireAdditionalStagingData, out result);
        return TryCreateReadResult(out result);
    }

    private bool TryCreateStagedReadResult(bool requireAdditionalData, out ReadResult result)
    {
        if (_staging is null || _staging.WrittenCount == 0)
        {
            result = default;
            return false;
        }

        var appended = AppendAvailableRingToStaging();
        if (requireAdditionalData && appended == 0 &&
            !_direction.IsWriterClosed && !_control.IsClosed)
        {
            result = default;
            return false;
        }

        _stagingExaminedAll = false;
        _currentBuffer = _staging.WrittenSequence;
        _currentIsStaging = true;
        _currentRingLength = 0;
        Volatile.Write(ref _hasOutstandingRead, true);
        result = new ReadResult(
            _currentBuffer,
            isCanceled: false,
            isCompleted: _direction.IsWriterClosed || _control.IsClosed);
        return true;
    }

    private int AppendAvailableRingToStaging()
    {
        if (_staging is null)
            return 0;
        var readPosition = _readPosition;
        var writePosition = _cachedWritePosition;
        var available = _direction.GetAvailableBytes(writePosition, readPosition);
        if (available == 0)
        {
            writePosition = RefreshWritePosition();
            available = _direction.GetAvailableBytes(writePosition, readPosition);
        }
        if (available == 0)
            return 0;

        if (_staging.WrittenCount > SharedMemoryTransportOptions.MaxCapacityPerDirectionBytes - available)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                "Shared-memory fragmented read exceeded the maximum bounded staging size.");
        }

        var index = (int)(unchecked((ulong)readPosition) & (uint)_direction.Mask);
        var firstLength = Math.Min(available, _direction.Capacity - index);
        _staging.Append(_direction.Memory.Span.Slice(index, firstLength));
        if (firstLength != available)
            _staging.Append(_direction.Memory.Span[..(available - firstLength)]);
        _readPosition = unchecked(readPosition + available);
        _direction.PublishReadPosition(_readPosition);
        if (_direction.TakeWriterWaiting() | TakePeerWriterArmed())
            _control.SignalSpaceAvailable();
        return available;
    }

    internal void OnPeerWriterArmed()
    {
        if (Volatile.Read(ref _completed) != 0)
            return;

        Interlocked.Exchange(ref _peerWriterArmed, 1);
        if (!_direction.IsWriterWaiting)
        {
            Interlocked.Exchange(ref _peerWriterArmed, 0);
            return;
        }
        // This callback runs on the control-channel reader, concurrently with the
        // pipe consumer. A pair of cursor snapshots can therefore be transiently
        // inconsistent. Only decide whether a wake might help here; the pipe hot
        // path performs the authoritative bounded-cursor validation.
        var writePosition = _direction.ReadWritePosition();
        var readPosition = _direction.ReadReadPosition();
        var available = unchecked((ulong)(writePosition - readPosition));
        if (available != (ulong)_direction.Capacity &&
            Interlocked.Exchange(ref _peerWriterArmed, 0) != 0)
        {
            _control.SignalSpaceAvailable();
        }
    }

    private bool TakePeerWriterArmed()
        => Interlocked.Exchange(ref _peerWriterArmed, 0) != 0;

    private void SpinBriefly()
    {
        for (var index = 0; index < _spinCount; index++)
            Thread.SpinWait(4);
    }

    private Task WaitForReadActivityReleaseAsync()
    {
        if (!HasReadActivity())
            return Task.CompletedTask;

        var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = Interlocked.CompareExchange(ref _readActivityReleased, created, null) ?? created;
        if (!HasReadActivity())
            waiter.TrySetResult(true);
        return waiter.Task;
    }

    private async Task CompleteAfterReadActivityAsync(Task readActivityReleased)
    {
        await readActivityReleased.ConfigureAwait(false);
        DisposeStaging();
    }

    private bool HasReadActivity()
        => Volatile.Read(ref _readOperationPending) != 0 ||
           Volatile.Read(ref _hasOutstandingRead);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinishReadOperation()
    {
        Volatile.Write(ref _readOperationPending, 0);
        if (Volatile.Read(ref _completed) == 0)
            return;
        FinishReadOperationAfterCompletion();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FinishReadOperationAfterCompletion()
    {
        if (!Volatile.Read(ref _hasOutstandingRead))
        {
            DisposeStaging();
            Volatile.Read(ref _readActivityReleased)?.TrySetResult(true);
        }
    }

    private void DisposeStaging()
    {
        Interlocked.Exchange(ref _staging, null)?.Dispose();
        _stagingExaminedAll = false;
    }

    private long RefreshWritePosition()
    {
        SharpLinkTelemetry.RecordSharedMemoryCursorRefresh("reader_write");
        _cachedWritePosition = _direction.ReadWritePosition();
        return _cachedWritePosition;
    }

    private sealed class RingSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public void Reset(Memory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
            Next = null;
        }

        public void SetNext(RingSequenceSegment next) => Next = next;
    }

    private sealed class PooledReadBuffer : IDisposable
    {
        private const int MaxRetainedSegments = 256;
        private static readonly ConcurrentStack<StagingSegment> SegmentPool = [];
        private static int s_retainedSegments;

        private readonly int _minimumSegmentSize;
        private StagingSegment? _first;
        private StagingSegment? _last;
        private int _firstOffset;
        private int _writtenCount;
        private int _disposed;

        public PooledReadBuffer(int initialCapacity)
        {
            _minimumSegmentSize = Math.Max(256, initialCapacity);
        }

        public int WrittenCount => _writtenCount;

        public ReadOnlySequence<byte> WrittenSequence
            => _first is null
                ? ReadOnlySequence<byte>.Empty
                : new ReadOnlySequence<byte>(
                    _first,
                    _firstOffset,
                    _last!,
                    _last!.Memory.Length);

        public void Append(ReadOnlySequence<byte> sequence)
        {
            foreach (var segment in sequence)
                Append(segment.Span);
        }

        public void Append(ReadOnlySpan<byte> source)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var remaining = source;
            while (!remaining.IsEmpty)
            {
                if (_last is null || _last.AvailableCapacity == 0)
                    AppendSegment(Math.Max(_minimumSegmentSize, remaining.Length));
                var copied = _last!.Append(remaining);
                _writtenCount = checked(_writtenCount + copied);
                remaining = remaining[copied..];
            }
            SharpLinkTelemetry.RecordSharedMemoryStagingBytes(source.Length);
        }

        public void Consume(int count)
        {
            if (count < 0 || count > WrittenCount)
                throw new ArgumentOutOfRangeException(nameof(count));
            _writtenCount -= count;
            while (count != 0)
            {
                var available = _first!.Memory.Length - _firstOffset;
                if (count < available)
                {
                    _firstOffset += count;
                    return;
                }

                count -= available;
                var consumed = _first;
                _first = consumed.NextSegment;
                consumed.SetNext(null);
                ReturnSegment(consumed);
                _firstOffset = 0;
            }

            if (_first is null)
                _last = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            while (_first is not null)
            {
                var segment = _first;
                _first = segment.NextSegment;
                segment.SetNext(null);
                ReturnSegment(segment);
            }
            _last = null;
            _firstOffset = 0;
            _writtenCount = 0;
        }

        private void AppendSegment(int capacity)
        {
            var runningIndex = _last is null
                ? 0
                : checked(_last.RunningIndex + _last.Memory.Length);
            var segment = RentSegment(capacity, runningIndex);
            if (_last is null)
                _first = segment;
            else
                _last.SetNext(segment);
            _last = segment;
        }

        private static StagingSegment RentSegment(int capacity, long runningIndex)
        {
            if (!SegmentPool.TryPop(out var segment))
                segment = new StagingSegment();
            else
                Interlocked.Decrement(ref s_retainedSegments);
            segment.Initialize(capacity, runningIndex);
            return segment;
        }

        private static void ReturnSegment(StagingSegment segment)
        {
            segment.Release();
            if (Interlocked.Increment(ref s_retainedSegments) <= MaxRetainedSegments)
            {
                SegmentPool.Push(segment);
                return;
            }
            Interlocked.Decrement(ref s_retainedSegments);
        }

        private sealed class StagingSegment : ReadOnlySequenceSegment<byte>
        {
            private byte[] _buffer = Array.Empty<byte>();
            private int _written;

            public int AvailableCapacity => _buffer.Length - _written;
            public StagingSegment? NextSegment => (StagingSegment?)Next;

            public void Initialize(int capacity, long runningIndex)
            {
                _buffer = ArrayPool<byte>.Shared.Rent(capacity);
                _written = 0;
                Memory = default;
                RunningIndex = runningIndex;
                Next = null;
            }

            public int Append(ReadOnlySpan<byte> source)
            {
                var count = Math.Min(source.Length, AvailableCapacity);
                source[..count].CopyTo(_buffer.AsSpan(_written));
                _written += count;
                Memory = _buffer.AsMemory(0, _written);
                return count;
            }

            public void SetNext(StagingSegment? next) => Next = next;

            public void Release()
            {
                var buffer = Interlocked.Exchange(ref _buffer, Array.Empty<byte>());
                if (buffer.Length != 0)
                    ArrayPool<byte>.Shared.Return(buffer);
                _written = 0;
                Memory = default;
                RunningIndex = 0;
                Next = null;
            }
        }
    }
}

internal sealed class SharedMemoryPipeWriter : PipeWriter
{
    private readonly SharedMemoryRingDirection _direction;
    private readonly SharedMemoryControlChannel _control;
    private readonly int _spinCount;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private long _publishedWritePosition;
    private long _reservedWritePosition;
    private long _cachedReadPosition;
    private PooledSpillBuffer? _spill;
    private SpillReason _lastSpillReason;
    private int _lastMemoryLength;
    private BufferKind _lastBufferKind;
    private int _peerReaderArmed;
    private int _cancelPending;
    private int _completed;

    public SharedMemoryPipeWriter(
        SharedMemoryRingDirection direction,
        SharedMemoryControlChannel control,
        int spinCount)
    {
        _direction = direction;
        _control = control;
        _spinCount = spinCount;
        _publishedWritePosition = direction.ReadWritePosition();
        _reservedWritePosition = _publishedWritePosition;
        _cachedReadPosition = RefreshReadPosition();
    }

    public override bool CanGetUnflushedBytes => true;

    public override long UnflushedBytes
        => unchecked(_reservedWritePosition - _publishedWritePosition) +
           (_spill?.WrittenCount ?? 0);

    public override void Advance(int bytes)
    {
        if (bytes < 0 || bytes > _lastMemoryLength)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (_lastBufferKind == BufferKind.None)
            throw new InvalidOperationException("GetMemory or GetSpan must be called before Advance.");

        if (_lastBufferKind == BufferKind.Direct)
        {
            _reservedWritePosition = unchecked(_reservedWritePosition + bytes);
            SharpLinkTelemetry.RecordSharedMemoryDirectWriteBytes(bytes);
        }
        else
        {
            _spill!.Advance(bytes);
            SharpLinkTelemetry.RecordSharedMemorySpillBytes(bytes, GetSpillReasonName(_lastSpillReason));
        }
        _lastBufferKind = BufferKind.None;
        _lastMemoryLength = 0;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
        if (_lastBufferKind != BufferKind.None)
            throw new InvalidOperationException("Advance must be called before requesting another buffer.");
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        sizeHint = Math.Max(1, sizeHint);
        if (sizeHint > SharedMemoryTransportOptions.MaxCapacityPerDirectionBytes)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                "Shared-memory spill requests cannot exceed the maximum per-direction capacity.");
        }

        var spillReason = SpillReason.Pending;
        Memory<byte> memory;
        if (_spill is null && TryGetDirectMemory(sizeHint, out memory, out spillReason))
        {
            _lastBufferKind = BufferKind.Direct;
            _lastMemoryLength = memory.Length;
            return memory;
        }

        if (_spill is null)
        {
            _spill = new PooledSpillBuffer();
            _lastSpillReason = spillReason;
        }
        else
        {
            _lastSpillReason = SpillReason.Pending;
        }
        memory = _spill.GetMemory(sizeHint);
        _lastBufferKind = BufferKind.Spill;
        _lastMemoryLength = memory.Length;
        return memory;
    }

    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public override void CancelPendingFlush()
    {
        Volatile.Write(ref _cancelPending, 1);
        _control.PulseSpaceWaiter();
    }

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
        if (_lastBufferKind != BufferKind.None)
            throw new InvalidOperationException("Advance must be called before FlushAsync.");

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _completed) != 0)
                return new FlushResult(isCanceled: false, isCompleted: true);
            if (Interlocked.Exchange(ref _cancelPending, 0) != 0)
                return new FlushResult(isCanceled: true, isCompleted: false);
            if (_direction.IsReaderClosed || _control.IsClosed)
            {
                _control.ThrowIfFaulted();
                return new FlushResult(isCanceled: false, isCompleted: true);
            }

            PublishDirectWrites();
            if (_spill is not null && !await DrainSpillAsync(cancellationToken).ConfigureAwait(false))
            {
                var completed = Volatile.Read(ref _completed) != 0;
                return new FlushResult(isCanceled: !completed, isCompleted: completed);
            }
            if (_direction.IsReaderClosed || _control.IsClosed)
            {
                _control.ThrowIfFaulted();
                return new FlushResult(isCanceled: false, isCompleted: true);
            }
            return new FlushResult(isCanceled: false, isCompleted: false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public override void Complete(Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _direction.CloseWriter();
        if (_direction.TakeReaderWaiting() | TakePeerReaderArmed())
            _control.SignalDataAvailable();
        _control.PulseSpaceWaiter();

        if (_spill is not null)
            CompleteSpill();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CompleteSpill()
    {
        // A flush that has not acquired the gate yet observes _completed before it
        // can touch the spill. Avoid semaphore traffic for an idle spill shutdown.
        if (_flushGate.CurrentCount != 0)
        {
            CleanupSpill();
            return;
        }

        _flushGate.Wait();
        try
        {
            CleanupSpill();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        // Direct reservations already live in the ring and can be published
        // without waiting. Spill may require peer progress, so completion drops
        // it rather than turning shutdown into an unbounded flush.
        if (Volatile.Read(ref _completed) == 0 && _lastBufferKind == BufferKind.None)
            PublishDirectWrites();
        Complete(exception);
        return ValueTask.CompletedTask;
    }

    private bool TryGetDirectMemory(
        int sizeHint,
        out Memory<byte> memory,
        out SpillReason spillReason)
    {
        var free = GetCachedFreeBytes(_reservedWritePosition, sizeHint);
        if (free < sizeHint)
        {
            memory = default;
            spillReason = SpillReason.Backpressure;
            return false;
        }

        var index = (int)(unchecked((ulong)_reservedWritePosition) & (uint)_direction.Mask);
        var contiguous = Math.Min(free, _direction.Capacity - index);
        if (contiguous < sizeHint)
        {
            memory = default;
            spillReason = SpillReason.Wrap;
            return false;
        }

        memory = _direction.Memory.Slice(index, contiguous);
        spillReason = default;
        return true;
    }

    private void PublishDirectWrites()
    {
        if (_reservedWritePosition == _publishedWritePosition)
            return;
        _publishedWritePosition = _reservedWritePosition;
        _direction.PublishWritePosition(_publishedWritePosition);
        if (_direction.TakeReaderWaiting() | TakePeerReaderArmed())
            _control.SignalDataAvailable();
    }

    private void CleanupSpill()
    {
        _spill?.Dispose();
        _spill = null;
        _lastBufferKind = BufferKind.None;
        _lastMemoryLength = 0;
    }

    private async ValueTask<bool> DrainSpillAsync(CancellationToken cancellationToken)
    {
        while (_spill is not null && _spill.WrittenCount != 0)
        {
            if (Volatile.Read(ref _completed) != 0)
                return false;
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _cancelPending, 0) != 0)
                return false;
            if (_direction.IsReaderClosed || _control.IsClosed)
            {
                _control.ThrowIfFaulted();
                throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Shared-memory peer stopped reading.");
            }

            var free = GetCachedFreeBytes(_publishedWritePosition, minimumBytes: 1);
            var readPosition = _cachedReadPosition;
            if (free == 0)
            {
                SpinBriefly();
                readPosition = RefreshReadPosition();
                free = _direction.Capacity -
                    _direction.GetAvailableBytes(_publishedWritePosition, readPosition);
            }
            if (free == 0)
            {
                _direction.SetWriterWaiting();
                readPosition = RefreshReadPosition();
                free = _direction.Capacity -
                    _direction.GetAvailableBytes(_publishedWritePosition, readPosition);
                if (free == 0)
                {
                    SharpLinkTelemetry.RecordSharedMemoryWait("writer");
                    try
                    {
                        _control.SignalSpaceWaiterArmed();
                        await _control.WaitForSpaceAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _direction.ClearWriterWaiting();
                    }
                    continue;
                }
                _direction.ClearWriterWaiting();
            }

            var index = (int)(unchecked((ulong)_publishedWritePosition) & (uint)_direction.Mask);
            var count = Math.Min(
                Math.Min(free, _direction.Capacity - index),
                _spill.FirstMemory.Length);
            _spill.FirstMemory.Span[..count]
                .CopyTo(_direction.Memory.Span.Slice(index, count));
            _spill.Consume(count);
            _publishedWritePosition = unchecked(_publishedWritePosition + count);
            _reservedWritePosition = _publishedWritePosition;
            _direction.PublishWritePosition(_publishedWritePosition);
            if (_direction.TakeReaderWaiting() | TakePeerReaderArmed())
                _control.SignalDataAvailable();
        }

        if (_spill is not null)
        {
            _spill.Dispose();
            _spill = null;
        }
        return true;
    }

    internal void OnPeerReaderArmed()
    {
        if (Volatile.Read(ref _completed) != 0)
            return;

        Interlocked.Exchange(ref _peerReaderArmed, 1);
        if (!_direction.IsReaderWaiting)
        {
            Interlocked.Exchange(ref _peerReaderArmed, 0);
            return;
        }
        // Read the peer-owned cursor first and the published cursor second. A
        // concurrent advance may produce a false-positive wake, which is safe;
        // it must not turn a notification hint into a connection-fatal layout
        // validation failure.
        var readPosition = _direction.ReadReadPosition();
        var writePosition = _direction.ReadWritePosition();
        if (writePosition != readPosition &&
            Interlocked.Exchange(ref _peerReaderArmed, 0) != 0)
        {
            _control.SignalDataAvailable();
        }
    }

    private bool TakePeerReaderArmed()
        => Interlocked.Exchange(ref _peerReaderArmed, 0) != 0;

    private void SpinBriefly()
    {
        for (var index = 0; index < _spinCount; index++)
            Thread.SpinWait(4);
    }

    private long RefreshReadPosition()
    {
        SharpLinkTelemetry.RecordSharedMemoryCursorRefresh("writer_read");
        _cachedReadPosition = _direction.ReadReadPosition();
        return _cachedReadPosition;
    }

    private int GetCachedFreeBytes(long writePosition, int minimumBytes)
    {
        var occupied = unchecked((ulong)(writePosition - _cachedReadPosition));
        if (occupied <= (ulong)_direction.Capacity)
        {
            var cachedFree = _direction.Capacity - (int)occupied;
            if (cachedFree >= minimumBytes)
                return cachedFree;
        }

        var readPosition = RefreshReadPosition();
        return _direction.Capacity - _direction.GetAvailableBytes(writePosition, readPosition);
    }

    private static string GetSpillReasonName(SpillReason reason)
        => reason switch
        {
            SpillReason.Wrap => "wrap",
            SpillReason.Backpressure => "backpressure",
            SpillReason.Pending => "pending",
            _ => "unknown"
        };

    private enum BufferKind
    {
        None,
        Direct,
        Spill
    }

    private enum SpillReason
    {
        None,
        Wrap,
        Backpressure,
        Pending
    }

    private sealed class PooledSpillBuffer : IDisposable
    {
        private const int MaxRetainedSegments = 256;
        private static readonly ConcurrentStack<SpillSegment> SegmentPool = [];
        private static int s_retainedSegments;

        private SpillSegment? _first;
        private SpillSegment? _last;
        private int _firstOffset;
        private int _writtenCount;
        private int _disposed;

        public int WrittenCount => _writtenCount;
        public ReadOnlyMemory<byte> FirstMemory
            => _first!.WrittenMemory[_firstOffset..];

        public Memory<byte> GetMemory(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (WrittenCount > SharedMemoryTransportOptions.MaxCapacityPerDirectionBytes - sizeHint)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    "Shared-memory spill buffer exceeded the maximum per-direction capacity.");
            }
            if (_last is null || _last.AvailableCapacity < sizeHint)
                AppendSegment(Math.Max(256, sizeHint));
            return _last!.AvailableMemory;
        }

        public void Advance(int count)
        {
            if (_last is null || count < 0 || count > _last.AvailableCapacity)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (WrittenCount > SharedMemoryTransportOptions.MaxCapacityPerDirectionBytes - count)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    "Shared-memory spill buffer exceeded the maximum per-direction capacity.");
            }
            _last.Advance(count);
            _writtenCount = checked(_writtenCount + count);
        }

        public void Consume(int count)
        {
            if (count < 0 || count > WrittenCount)
                throw new ArgumentOutOfRangeException(nameof(count));
            _writtenCount -= count;
            while (count != 0)
            {
                var available = _first!.WrittenCount - _firstOffset;
                if (count < available)
                {
                    _firstOffset += count;
                    return;
                }

                count -= available;
                var consumed = _first;
                _first = consumed.Next;
                consumed.Next = null;
                ReturnSegment(consumed);
                _firstOffset = 0;
            }

            if (_first is null)
                _last = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            while (_first is not null)
            {
                var segment = _first;
                _first = segment.Next;
                segment.Next = null;
                ReturnSegment(segment);
            }
            _last = null;
            _firstOffset = 0;
            _writtenCount = 0;
        }

        private void AppendSegment(int capacity)
        {
            var segment = RentSegment(capacity);
            if (_last is null)
                _first = segment;
            else
                _last.Next = segment;
            _last = segment;
        }

        private static SpillSegment RentSegment(int capacity)
        {
            if (!SegmentPool.TryPop(out var segment))
                segment = new SpillSegment();
            else
                Interlocked.Decrement(ref s_retainedSegments);
            segment.Initialize(capacity);
            return segment;
        }

        private static void ReturnSegment(SpillSegment segment)
        {
            segment.Release();
            if (Interlocked.Increment(ref s_retainedSegments) <= MaxRetainedSegments)
            {
                SegmentPool.Push(segment);
                return;
            }
            Interlocked.Decrement(ref s_retainedSegments);
        }

        private sealed class SpillSegment
        {
            private byte[] _buffer = Array.Empty<byte>();

            public int WrittenCount { get; private set; }
            public int AvailableCapacity => _buffer.Length - WrittenCount;
            public Memory<byte> AvailableMemory => _buffer.AsMemory(WrittenCount);
            public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, WrittenCount);
            public SpillSegment? Next { get; set; }

            public void Initialize(int capacity)
            {
                _buffer = ArrayPool<byte>.Shared.Rent(capacity);
                WrittenCount = 0;
                Next = null;
            }

            public void Advance(int count) => WrittenCount += count;

            public void Release()
            {
                var buffer = Interlocked.Exchange(ref _buffer, Array.Empty<byte>());
                if (buffer.Length != 0)
                    ArrayPool<byte>.Shared.Return(buffer);
                WrittenCount = 0;
                Next = null;
            }
        }
    }
}
