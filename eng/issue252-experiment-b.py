from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one match, found {count}")
    return text.replace(old, new, 1)


slot_path = Path("src/SharpLink.Client/SegmentedSlotTable.cs")
slots = slot_path.read_text()
slots = replace_once(
    slots,
    """    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? Read(int index)
    {
        var segment = Volatile.Read(ref _segments[index >> _segmentShift]);
        return segment is null ? null : Volatile.Read(ref segment[index & _segmentMask]);
    }
""",
    """    /// <summary>
    /// Operation-local location for a materialized slot. The segment reference is immutable for the
    /// owning table lifetime, so callers can reuse this value during one register/complete operation
    /// without introducing shared writable cache state.
    /// </summary>
    internal readonly struct SlotLocation
    {
        private readonly T?[] _segment;
        private readonly int _offset;

        internal SlotLocation(T?[] segment, int offset)
        {
            _segment = segment;
            _offset = offset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T? Read()
            => Volatile.Read(ref _segment[_offset]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T? CompareExchange(T? value, T? comparand)
            => Interlocked.CompareExchange(ref _segment[_offset], value, comparand);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetLocation(int index, out SlotLocation location)
    {
        var segment = Volatile.Read(ref _segments[index >> _segmentShift]);
        if (segment is null)
        {
            location = default;
            return false;
        }

        location = new SlotLocation(segment, index & _segmentMask);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SlotLocation GetOrCreateLocation(int index)
    {
        var segmentIndex = index >> _segmentShift;
        var segment = Volatile.Read(ref _segments[segmentIndex]) ?? CreateSegmentSlow(segmentIndex);
        return new SlotLocation(segment, index & _segmentMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? Read(int index)
    {
        var segment = Volatile.Read(ref _segments[index >> _segmentShift]);
        return segment is null ? null : Volatile.Read(ref segment[index & _segmentMask]);
    }
""",
    "SegmentedSlotTable location API",
)
slot_path.write_text(slots)

path = Path("src/SharpLink.Client/PendingRequestTable.cs")
text = path.read_text()

text = replace_once(
    text,
    """    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)
    {
        var index = (int)(id & _indexMask);
        var current = _slots.Read(index);
        if (current is not null && current.Id == id &&
            current.Kind is PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming)
        {
            // A successful Response is only the server's acknowledgement; StreamComplete owns
            // the terminal transition for server and duplex streams. The callback shares the
            // per-call completion gate with terminal removal, so a matching acknowledgement is
            // observed before cancellation, deadline, or disconnect can report the terminal result.
            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(_slots.Read(index), current) ||
                    current.Id != id ||
                    current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
                {
                    return false;
                }

                current.CompletionObserver?.OnResponseObserved();
                return true;
            }
        }

        if (!TryTakeMatchingCall(id, out var call))
            return false;

        CompleteTakenCall(call!, PendingCallCompletionReason.Response, exception: null, ref payload);
        return true;
    }

    public bool DispatchError(long id, Exception exception)
        => TryComplete(id, PendingCallCompletionReason.RemoteError, exception);

    public bool TryComplete(
        long id,
        PendingCallCompletionReason reason,
        Exception? exception = null)
    {
        if (!TryTakeMatchingCall(id, out var call))
            return false;

        var emptyPayload = ReadOnlySequence<byte>.Empty;
        CompleteTakenCall(call!, reason, exception, ref emptyPayload);
        return true;
    }

    public bool Contains(long id)
    {
        var call = _slots.Read((int)(id & _indexMask));
        return call is not null && call.Id == id;
    }

    public CancellationToken GetProducerCancellationToken(long id)
    {
        var call = _slots.Read((int)(id & _indexMask));
        if (call is null || call.Id != id)
            return new CancellationToken(canceled: true);
        return call.ProducerCancellationToken;
    }
""",
    """    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)
    {
        var index = (int)(id & _indexMask);
        if (!_slots.TryGetLocation(index, out var location))
            return false;

        var current = location.Read();
        if (current is not null && current.Id == id &&
            current.Kind is PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming)
        {
            // A successful Response is only the server's acknowledgement; StreamComplete owns
            // the terminal transition for server and duplex streams. The callback shares the
            // per-call completion gate with terminal removal, so a matching acknowledgement is
            // observed before cancellation, deadline, or disconnect can report the terminal result.
            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(location.Read(), current) ||
                    current.Id != id ||
                    current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
                {
                    return false;
                }

                current.CompletionObserver?.OnResponseObserved();
                return true;
            }
        }

        if (!TryTakeMatchingCall(id, location, out var call))
            return false;

        CompleteTakenCall(call!, PendingCallCompletionReason.Response, exception: null, ref payload);
        return true;
    }

    public bool DispatchError(long id, Exception exception)
        => TryComplete(id, PendingCallCompletionReason.RemoteError, exception);

    public bool TryComplete(
        long id,
        PendingCallCompletionReason reason,
        Exception? exception = null)
    {
        var index = (int)(id & _indexMask);
        if (!_slots.TryGetLocation(index, out var location))
            return false;

        if (!TryTakeMatchingCall(id, location, out var call))
            return false;

        var emptyPayload = ReadOnlySequence<byte>.Empty;
        CompleteTakenCall(call!, reason, exception, ref emptyPayload);
        return true;
    }

    public bool Contains(long id)
    {
        var index = (int)(id & _indexMask);
        return _slots.TryGetLocation(index, out var location) &&
            location.Read() is { } call && call.Id == id;
    }

    public CancellationToken GetProducerCancellationToken(long id)
    {
        var index = (int)(id & _indexMask);
        if (!_slots.TryGetLocation(index, out var location))
            return new CancellationToken(canceled: true);

        var call = location.Read();
        if (call is null || call.Id != id)
            return new CancellationToken(canceled: true);
        return call.ProducerCancellationToken;
    }
""",
    "dispatch/complete lookup hoist",
)

text = replace_once(
    text,
    """                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    if (_slots.Read(index) is not null)
                        continue;

                    // Materialize storage before operation/PendingCall ownership is acquired so an
                    // allocation failure can refund the capacity reservation without leaking pooled state.
                    _slots.EnsureSegment(index);
                    if (_slots.Read(index) is not null)
                        continue;
""",
    """                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    // Resolve storage before operation/PendingCall ownership is acquired. Segments are
                    // lifetime-retained, so this operation-local location stays valid until publication.
                    var location = _slots.GetOrCreateLocation(index);
                    if (location.Read() is not null)
                        continue;
""",
    "generic registration lookup hoist",
)

text = replace_once(
    text,
    """                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    if (_slots.Read(index) is not null)
                        continue;

                    _slots.EnsureSegment(index);
                    if (_slots.Read(index) is not null)
                        continue;
""",
    """                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    var location = _slots.GetOrCreateLocation(index);
                    if (location.Read() is not null)
                        continue;
""",
    "stream registration lookup hoist",
)

count = text.count("if (_slots.CompareExchange(index, call, null) is null)")
if count != 2:
    raise SystemExit(f"registration CAS: expected two matches, found {count}")
text = text.replace(
    "if (_slots.CompareExchange(index, call, null) is null)",
    "if (location.CompareExchange(call, null) is null)",
)

text = replace_once(
    text,
    """    private bool TryTakeMatchingCall(long id, out PendingCall? call)
    {
        var index = (int)(id & _indexMask);
        while (true)
        {
            var current = _slots.Read(index);
            if (current is null || current.Id != id)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(_slots.Read(index), current) || current.Id != id)
                    continue;

                var exchanged = _slots.CompareExchange(index, null, current);
                if (!ReferenceEquals(exchanged, current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }

    private bool TryTakeCallAtIndex(int index, out PendingCall? call)
    {
        while (true)
        {
            var current = _slots.Read(index);
            if (current is null)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(_slots.Read(index), current))
                    continue;

                if (!ReferenceEquals(_slots.CompareExchange(index, null, current), current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }
""",
    """    private static bool TryTakeMatchingCall(
        long id,
        SegmentedSlotTable<PendingCall>.SlotLocation location,
        out PendingCall? call)
    {
        while (true)
        {
            var current = location.Read();
            if (current is null || current.Id != id)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(location.Read(), current) || current.Id != id)
                    continue;

                var exchanged = location.CompareExchange(null, current);
                if (!ReferenceEquals(exchanged, current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }

    private bool TryTakeCallAtIndex(int index, out PendingCall? call)
    {
        if (!_slots.TryGetLocation(index, out var location))
        {
            call = null;
            return false;
        }

        while (true)
        {
            var current = location.Read();
            if (current is null)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(location.Read(), current))
                    continue;

                if (!ReferenceEquals(location.CompareExchange(null, current), current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }
""",
    "terminal slot lookup hoist",
)

path.write_text(text)
