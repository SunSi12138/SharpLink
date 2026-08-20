from pathlib import Path

path = Path("src/SharpLink.Client/PendingRequestTable.cs")
text = path.read_text()


def replace_once(old: str, new: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"expected one match, found {count}: {old[:100]!r}")
    text = text.replace(old, new, 1)


replace_once(
    """    private readonly int _indexMask;
    private readonly SegmentedSlotTable<PendingCall> _slots;
""",
    """    private const int SlotSegmentShift = 8;
    private const int SlotSegmentSize = 1 << SlotSegmentShift;
    private const int SlotSegmentMask = SlotSegmentSize - 1;

    private readonly int _capacity;
    private readonly int _indexMask;
    private readonly PendingCall?[]?[] _segments;
""",
)

replace_once(
    """        _slots = new SegmentedSlotTable<PendingCall>(capacity);
        _indexMask = capacity - 1;
""",
    """        _capacity = capacity;
        _indexMask = capacity - 1;
        _segments = new PendingCall?[]?[(capacity + SlotSegmentMask) >> SlotSegmentShift];
""",
)

replace_once(
    """    public int Capacity => _slots.Length;

    internal int ActiveCount => Volatile.Read(ref _activeSlots);

    internal int MaterializedSegmentCount => _slots.MaterializedSegmentCount;

    internal int SegmentSize => _slots.SegmentSize;
""",
    """    public int Capacity => _capacity;

    internal int ActiveCount => Volatile.Read(ref _activeSlots);

    internal int MaterializedSegmentCount
    {
        get
        {
            var count = 0;
            for (var index = 0; index < _segments.Length; index++)
            {
                if (Volatile.Read(ref _segments[index]) is not null)
                    count++;
            }
            return count;
        }
    }

    internal int SegmentSize => Math.Min(_capacity, SlotSegmentSize);
""",
)

replace_once(
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
        var segment = Volatile.Read(ref _segments[index >> SlotSegmentShift]);
        if (segment is null)
            return false;

        ref var slot = ref segment[index & SlotSegmentMask];
        var current = Volatile.Read(ref slot);
        if (current is not null && current.Id == id &&
            current.Kind is PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming)
        {
            // A successful Response is only the server's acknowledgement; StreamComplete owns
            // the terminal transition for server and duplex streams. The callback shares the
            // per-call completion gate with terminal removal, so a matching acknowledgement is
            // observed before cancellation, deadline, or disconnect can report the terminal result.
            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slot), current) ||
                    current.Id != id ||
                    current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
                {
                    return false;
                }

                current.CompletionObserver?.OnResponseObserved();
                return true;
            }
        }

        if (!TryTakeMatchingCall(id, ref slot, out var call))
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
        var segment = Volatile.Read(ref _segments[index >> SlotSegmentShift]);
        if (segment is null)
            return false;

        ref var slot = ref segment[index & SlotSegmentMask];
        if (!TryTakeMatchingCall(id, ref slot, out var call))
            return false;

        var emptyPayload = ReadOnlySequence<byte>.Empty;
        CompleteTakenCall(call!, reason, exception, ref emptyPayload);
        return true;
    }

    public bool Contains(long id)
    {
        var index = (int)(id & _indexMask);
        var segment = Volatile.Read(ref _segments[index >> SlotSegmentShift]);
        if (segment is null)
            return false;

        var call = Volatile.Read(ref segment[index & SlotSegmentMask]);
        return call is not null && call.Id == id;
    }

    public CancellationToken GetProducerCancellationToken(long id)
    {
        var index = (int)(id & _indexMask);
        var segment = Volatile.Read(ref _segments[index >> SlotSegmentShift]);
        if (segment is null)
            return new CancellationToken(canceled: true);

        var call = Volatile.Read(ref segment[index & SlotSegmentMask]);
        if (call is null || call.Id != id)
            return new CancellationToken(canceled: true);
        return call.ProducerCancellationToken;
    }
""",
)

replace_once(
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
                    // Resolve the segment once for this registration attempt and retain the array-element
                    // ref locally. Segments are lifetime-retained, so this ref cannot race segment teardown.
                    var segment = GetOrCreateSegment(index);
                    ref var slot = ref segment[index & SlotSegmentMask];
                    if (Volatile.Read(ref slot) is not null)
                        continue;
""",
)

replace_once(
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
                    var segment = GetOrCreateSegment(index);
                    ref var slot = ref segment[index & SlotSegmentMask];
                    if (Volatile.Read(ref slot) is not null)
                        continue;
""",
)

if text.count("if (_slots.CompareExchange(index, call, null) is null)") != 2:
    raise SystemExit("expected two registration CompareExchange sites")
text = text.replace(
    "if (_slots.CompareExchange(index, call, null) is null)",
    "if (Interlocked.CompareExchange(ref slot, call, null) is null)",
)

replace_once(
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
    """    private static bool TryTakeMatchingCall(long id, ref PendingCall? slot, out PendingCall? call)
    {
        while (true)
        {
            var current = Volatile.Read(ref slot);
            if (current is null || current.Id != id)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slot), current) || current.Id != id)
                    continue;

                if (!ReferenceEquals(Interlocked.CompareExchange(ref slot, null, current), current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }

    private bool TryTakeCallAtIndex(int index, out PendingCall? call)
    {
        var segment = Volatile.Read(ref _segments[index >> SlotSegmentShift]);
        if (segment is null)
        {
            call = null;
            return false;
        }

        ref var slot = ref segment[index & SlotSegmentMask];
        while (true)
        {
            var current = Volatile.Read(ref slot);
            if (current is null)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slot), current))
                    continue;

                if (!ReferenceEquals(Interlocked.CompareExchange(ref slot, null, current), current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }
""",
)

text = text.replace("_slots.SegmentCount", "_segments.Length")
text = text.replace("_slots.GetMaterializedSegment(segmentIndex)", "Volatile.Read(ref _segments[segmentIndex])")
text = text.replace("segmentIndex * _slots.SegmentSize", "segmentIndex << SlotSegmentShift")
text = text.replace("_slots.Length", "_capacity")

replace_once(
    """    private long NextRequestId()
    {
""",
    """    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PendingCall?[] GetOrCreateSegment(int index)
    {
        var segmentIndex = index >> SlotSegmentShift;
        var segment = Volatile.Read(ref _segments[segmentIndex]);
        return segment ?? CreateSegmentSlow(segmentIndex);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private PendingCall?[] CreateSegmentSlow(int segmentIndex)
    {
        var segment = Volatile.Read(ref _segments[segmentIndex]);
        if (segment is not null)
            return segment;

        var firstIndex = segmentIndex << SlotSegmentShift;
        var created = new PendingCall?[Math.Min(SlotSegmentSize, _capacity - firstIndex)];
        return Interlocked.CompareExchange(ref _segments[segmentIndex], created, null) ?? created;
    }

    private long NextRequestId()
    {
""",
)

if "_slots" in text or "SegmentedSlotTable<" in text:
    raise SystemExit("Experiment A left the generic segmented helper on the production path")
path.write_text(text)

test_path = Path("test/SharpLink.UnitTests/Runtime/PendingRequestTableSegmentationTests.cs")
tests = test_path.read_text()
old_test = """    [Test]
    public void MaximumCapacityShouldStartWithOnlySegmentDirectoryStorage()
    {
        var slots = new SegmentedSlotTable<object>(1024 * 1024);

        Ensure(slots.Length == 1024 * 1024, "logical capacity must remain unchanged");
        Ensure(slots.SegmentSize == 256, "large pending tables should use 256-slot segments");
        Ensure(slots.SegmentCount == 4096, "the hard maximum should require only 4096 root references");
        Ensure(slots.MaterializedSegmentCount == 0, "construction must not materialize a slot segment");

        _ = slots.Read(700_000);
        Ensure(slots.MaterializedSegmentCount == 0,
            "a lookup into an untouched segment must not materialize storage");
    }
"""
new_test = """    [Test]
    public void MaximumCapacityShouldStartWithOnlySegmentDirectoryStorage()
    {
        using var manager = PendingRequestTableTestFixture.Create(1024 * 1024);

        Ensure(manager.Capacity == 1024 * 1024, "logical capacity must remain unchanged");
        Ensure(manager.SegmentSize == 256, "large pending tables should use 256-slot segments");
        Ensure(manager.MaterializedSegmentCount == 0, "construction must not materialize a slot segment");

        Ensure(!manager.Contains(700_000), "a lookup into an untouched segment must return no match");
        Ensure(manager.MaterializedSegmentCount == 0,
            "a lookup into an untouched segment must not materialize storage");
    }
"""
if tests.count(old_test) != 1:
    raise SystemExit("failed to find direct SegmentedSlotTable unit test")
test_path.write_text(tests.replace(old_test, new_test, 1))

Path("src/SharpLink.Client/SegmentedSlotTable.cs").unlink()
