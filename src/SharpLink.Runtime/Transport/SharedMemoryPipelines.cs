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
    private int _currentRingLength;
    private bool _currentIsStaging;
    private bool _stagingExaminedAll;
    private bool _hasOutstandingRead;
    private CancellationToken _registeredReadCancellation;
    private CancellationTokenRegistration _readCancellationRegistration;
    private int _cancelPending;
    private int _completed;

    public SharedMemoryPipeReader(
        SharedMemoryRingDirection direction,
        SharedMemoryControlChannel control,
        int spinCount)
    {
        _direction = direction;
        _control = control;
        _spinCount = spinCount;
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
            _staging?.Dispose();
            _staging = null;
            _stagingExaminedAll = false;
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
            var readPosition = _direction.ReadReadPosition();
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
                _direction.PublishReadPosition(unchecked(readPosition + _currentRingLength));
                if (_direction.TakeWriterWaiting() || _currentRingLength == _direction.Capacity)
                    _control.SignalSpaceAvailable();
            }
            else
            {
                _direction.PublishReadPosition(unchecked(readPosition + consumedBytes));
                if (consumedBytes != 0 &&
                    (_direction.TakeWriterWaiting() || _currentRingLength == _direction.Capacity))
                    _control.SignalSpaceAvailable();
            }
        }
        Volatile.Write(ref _hasOutstandingRead, false);
        _currentBuffer = default;
        _currentRingLength = 0;
        _currentIsStaging = false;
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
        if (_direction.TakeWriterWaiting())
            _control.SignalSpaceAvailable();
        _control.PulseDataWaiter();
        _readCancellationRegistration.Dispose();
        if (!Volatile.Read(ref _hasOutstandingRead))
        {
            _staging?.Dispose();
            _staging = null;
        }
    }

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        Complete(exception);
        while (Volatile.Read(ref _hasOutstandingRead))
            await Task.Yield();
        _staging?.Dispose();
        _staging = null;
    }

    public override bool TryRead(out ReadResult result)
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

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _hasOutstandingRead))
            throw new InvalidOperationException("AdvanceTo must be called before reading again.");

        RegisterReadCancellation(cancellationToken);
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
                return new ReadResult(default, isCanceled: false, isCompleted: true);
            }

            try
            {
                SharpLinkTelemetry.RecordSharedMemoryWait("reader");
                await _control.WaitForDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SharpLinkException) when (_control.IsClosed)
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

    private bool TryCreateReadResult(out ReadResult result)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            result = new ReadResult(default, isCanceled: false, isCompleted: true);
            return true;
        }

        var readPosition = _direction.ReadReadPosition();
        var writePosition = _direction.ReadWritePosition();
        var available = _direction.GetAvailableBytes(writePosition, readPosition);
        if (available == 0)
        {
            if (_direction.IsWriterClosed || _control.IsClosed)
            {
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
        _currentBuffer = new ReadOnlySequence<byte>(_staging.WrittenMemory);
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
        var readPosition = _direction.ReadReadPosition();
        var writePosition = _direction.ReadWritePosition();
        var available = _direction.GetAvailableBytes(writePosition, readPosition);
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
        _direction.PublishReadPosition(unchecked(readPosition + available));
        if (_direction.TakeWriterWaiting() || available == _direction.Capacity)
            _control.SignalSpaceAvailable();
        return available;
    }

    private void SpinBriefly()
    {
        for (var index = 0; index < _spinCount; index++)
            Thread.SpinWait(4);
    }

    private void RegisterReadCancellation(CancellationToken cancellationToken)
    {
        if (cancellationToken == _registeredReadCancellation)
            return;
        _readCancellationRegistration.Dispose();
        _registeredReadCancellation = cancellationToken;
        _readCancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(
                static state => ((SharedMemoryPipeReader)state!)._control.PulseDataWaiter(),
                this)
            : default;
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
        private byte[] _buffer;
        private int _start;
        private int _end;

        public PooledReadBuffer(int initialCapacity)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));
        }

        public int WrittenCount => _end - _start;
        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(_start, WrittenCount);

        public void Append(ReadOnlySequence<byte> sequence)
        {
            foreach (var segment in sequence)
                Append(segment.Span);
        }

        public void Append(ReadOnlySpan<byte> source)
        {
            EnsureCapacity(source.Length);
            source.CopyTo(_buffer.AsSpan(_end));
            _end += source.Length;
        }

        public void Consume(int count)
        {
            if (count < 0 || count > WrittenCount)
                throw new ArgumentOutOfRangeException(nameof(count));
            _start += count;
            if (_start == _end)
                _start = _end = 0;
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes <= _buffer.Length - _end)
                return;
            var written = WrittenCount;
            if (additionalBytes <= _buffer.Length - written)
            {
                _buffer.AsSpan(_start, written).CopyTo(_buffer);
                _start = 0;
                _end = written;
                return;
            }

            var required = checked(written + additionalBytes);
            var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(required, checked(_buffer.Length * 2)));
            _buffer.AsSpan(_start, written).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = replacement;
            _start = 0;
            _end = written;
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, Array.Empty<byte>());
            if (buffer.Length != 0)
                ArrayPool<byte>.Shared.Return(buffer);
            _start = _end = 0;
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
    private PooledSpillBuffer? _spill;
    private int _spillOffset;
    private int _lastMemoryLength;
    private BufferKind _lastBufferKind;
    private CancellationToken _registeredFlushCancellation;
    private CancellationTokenRegistration _flushCancellationRegistration;
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
    }

    public override bool CanGetUnflushedBytes => true;

    public override long UnflushedBytes
        => unchecked(_reservedWritePosition - _publishedWritePosition) +
           (_spill is null ? 0 : _spill.WrittenCount - _spillOffset);

    public override void Advance(int bytes)
    {
        if (bytes < 0 || bytes > _lastMemoryLength)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (_lastBufferKind == BufferKind.None)
            throw new InvalidOperationException("GetMemory or GetSpan must be called before Advance.");

        if (_lastBufferKind == BufferKind.Direct)
            _reservedWritePosition = unchecked(_reservedWritePosition + bytes);
        else
        {
            _spill!.Advance(bytes);
            SharpLinkTelemetry.RecordSharedMemorySpillBytes(bytes);
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

        if (_spill is null && TryGetDirectMemory(sizeHint, out var memory))
        {
            _lastBufferKind = BufferKind.Direct;
            _lastMemoryLength = memory.Length;
            return memory;
        }

        _spill ??= new PooledSpillBuffer(sizeHint);
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

        RegisterFlushCancellation(cancellationToken);
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _cancelPending, 0) != 0)
                return new FlushResult(isCanceled: true, isCompleted: false);
            if (_direction.IsReaderClosed || _control.IsClosed)
                return new FlushResult(isCanceled: false, isCompleted: true);

            PublishDirectWrites();
            if (_spill is not null && !await DrainSpillAsync(cancellationToken).ConfigureAwait(false))
                return new FlushResult(isCanceled: true, isCompleted: false);
            return new FlushResult(isCanceled: false, isCompleted: _direction.IsReaderClosed || _control.IsClosed);
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
        if (_direction.TakeReaderWaiting())
            _control.SignalDataAvailable();
        _control.PulseSpaceWaiter();
        _flushCancellationRegistration.Dispose();
        _spill?.Dispose();
        _spill = null;
    }

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        if (Volatile.Read(ref _completed) != 0)
            return;
        try
        {
            _ = await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SharpLinkException)
        {
        }
        Complete(exception);
    }

    private bool TryGetDirectMemory(int sizeHint, out Memory<byte> memory)
    {
        var readPosition = _direction.ReadReadPosition();
        var reserved = _direction.GetAvailableBytes(_reservedWritePosition, readPosition);
        var free = _direction.Capacity - reserved;
        if (free < sizeHint)
        {
            memory = default;
            return false;
        }

        var index = (int)(unchecked((ulong)_reservedWritePosition) & (uint)_direction.Mask);
        var contiguous = Math.Min(free, _direction.Capacity - index);
        if (contiguous < sizeHint)
        {
            memory = default;
            return false;
        }

        memory = _direction.Memory.Slice(index, contiguous);
        return true;
    }

    private void PublishDirectWrites()
    {
        if (_reservedWritePosition == _publishedWritePosition)
            return;
        var wasEmpty = _publishedWritePosition == _direction.ReadReadPosition();
        _publishedWritePosition = _reservedWritePosition;
        _direction.PublishWritePosition(_publishedWritePosition);
        if (_direction.TakeReaderWaiting() || wasEmpty)
            _control.SignalDataAvailable();
    }

    private async ValueTask<bool> DrainSpillAsync(CancellationToken cancellationToken)
    {
        while (_spill is not null && _spillOffset < _spill.WrittenCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _cancelPending, 0) != 0)
                return false;
            if (_direction.IsReaderClosed || _control.IsClosed)
                throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Shared-memory peer stopped reading.");

            var readPosition = _direction.ReadReadPosition();
            var occupied = _direction.GetAvailableBytes(_publishedWritePosition, readPosition);
            var free = _direction.Capacity - occupied;
            if (free == 0)
            {
                SpinBriefly();
                readPosition = _direction.ReadReadPosition();
                occupied = _direction.GetAvailableBytes(_publishedWritePosition, readPosition);
                free = _direction.Capacity - occupied;
            }
            if (free == 0)
            {
                _direction.SetWriterWaiting();
                readPosition = _direction.ReadReadPosition();
                occupied = _direction.GetAvailableBytes(_publishedWritePosition, readPosition);
                free = _direction.Capacity - occupied;
                if (free == 0)
                {
                    SharpLinkTelemetry.RecordSharedMemoryWait("writer");
                    try
                    {
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
                _spill.WrittenCount - _spillOffset);
            _spill.WrittenMemory.Span.Slice(_spillOffset, count)
                .CopyTo(_direction.Memory.Span.Slice(index, count));
            _spillOffset += count;
            var wasEmpty = _publishedWritePosition == readPosition;
            _publishedWritePosition = unchecked(_publishedWritePosition + count);
            _reservedWritePosition = _publishedWritePosition;
            _direction.PublishWritePosition(_publishedWritePosition);
            if (_direction.TakeReaderWaiting() || wasEmpty)
                _control.SignalDataAvailable();
        }

        if (_spill is not null)
        {
            _spill.Dispose();
            _spill = null;
            _spillOffset = 0;
        }
        return true;
    }

    private void SpinBriefly()
    {
        for (var index = 0; index < _spinCount; index++)
            Thread.SpinWait(4);
    }

    private void RegisterFlushCancellation(CancellationToken cancellationToken)
    {
        if (cancellationToken == _registeredFlushCancellation)
            return;
        _flushCancellationRegistration.Dispose();
        _registeredFlushCancellation = cancellationToken;
        _flushCancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(
                static state => ((SharedMemoryPipeWriter)state!)._control.PulseSpaceWaiter(),
                this)
            : default;
    }

    private enum BufferKind
    {
        None,
        Direct,
        Spill
    }

    private sealed class PooledSpillBuffer : IDisposable
    {
        private byte[] _buffer;

        public PooledSpillBuffer(int initialCapacity)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));
        }

        public int WrittenCount { get; private set; }
        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, WrittenCount);

        public Memory<byte> GetMemory(int sizeHint)
        {
            EnsureCapacity(checked(WrittenCount + sizeHint));
            return _buffer.AsMemory(WrittenCount);
        }

        public void Advance(int count)
        {
            if (count < 0 || WrittenCount > _buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            WrittenCount += count;
        }

        private void EnsureCapacity(int capacity)
        {
            if (capacity <= _buffer.Length)
                return;
            var newBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, checked(_buffer.Length * 2)));
            _buffer.AsSpan(0, WrittenCount).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, Array.Empty<byte>());
            if (buffer.Length != 0)
                ArrayPool<byte>.Shared.Return(buffer);
            WrittenCount = 0;
        }
    }
}
