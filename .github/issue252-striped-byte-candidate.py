from pathlib import Path

path = Path("src/SharpLink.Client/PendingRequestTable.cs")
text = path.read_text(encoding="utf-8")


def replace_once(old: str, new: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"expected exactly one match, got {count}: {old[:120]!r}")
    text = text.replace(old, new, 1)


replace_once(
    """internal sealed class PendingRequestTable : IDisposable
{
    private readonly int _indexMask;
    private readonly int _capacity;
    private readonly object _slotsInitializationGate = new();
    private PendingCall?[]? _slots;
""",
    """internal sealed class PendingRequestTable : IDisposable
{
    private const int DeadlinePageShift = 8;
    private const int DeadlinePageSize = 1 << DeadlinePageShift;
    private const int DeadlineRegistrationStripeCount = 16;
    private const int DeadlineRegistrationStripeMask = DeadlineRegistrationStripeCount - 1;
    private const int DeadlineRetentionStripe = DeadlineRegistrationStripeCount;
    private const int DeadlineMarkerStripeCount = DeadlineRegistrationStripeCount + 1;
    private const int DeadlineMarkerCacheLineBytes = 64;

    private readonly int _indexMask;
    private readonly int _capacity;
    private readonly object _slotsInitializationGate = new();
    private readonly int _deadlinePageCount;
    private readonly int _deadlineMarkerStripeStride;
    private byte[]? _deadlinePageMarks;
    private PendingCall?[]? _slots;
""",
)

replace_once(
    """        _capacity = capacity;
        _indexMask = capacity - 1;
""",
    """        _capacity = capacity;
        _deadlinePageCount = (capacity + DeadlinePageSize - 1) >> DeadlinePageShift;
        _deadlineMarkerStripeStride =
            (_deadlinePageCount + DeadlineMarkerCacheLineBytes - 1) & ~(DeadlineMarkerCacheLineBytes - 1);
        _indexMask = capacity - 1;
""",
)

replace_once(
    """    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PendingCall?[] GetOrCreateSlots()
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is not null)
            return slots;

        lock (_slotsInitializationGate)
        {
            slots = Volatile.Read(ref _slots);
            if (slots is null)
            {
                slots = new PendingCall?[_capacity];
                Volatile.Write(ref _slots, slots);
            }

            return slots;
        }
    }

""",
    """    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PendingCall?[] GetOrCreateSlots()
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is not null)
            return slots;

        lock (_slotsInitializationGate)
        {
            slots = Volatile.Read(ref _slots);
            if (slots is null)
            {
                slots = new PendingCall?[_capacity];
                Volatile.Write(ref _slots, slots);
            }

            return slots;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkDeadlinePage(long id)
    {
        var marks = _deadlinePageMarks;
        if (marks is null)
            marks = GetOrCreateDeadlinePageMarks();

        var page = ((int)(id & _indexMask)) >> DeadlinePageShift;
        var stripe = Environment.CurrentManagedThreadId & DeadlineRegistrationStripeMask;
        Volatile.Write(
            ref marks[stripe * _deadlineMarkerStripeStride + page],
            (byte)1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[] GetOrCreateDeadlinePageMarks()
    {
        lock (_slotsInitializationGate)
        {
            var marks = _deadlinePageMarks;
            if (marks is null)
            {
                marks = new byte[_deadlineMarkerStripeStride * DeadlineMarkerStripeCount];
                Volatile.Write(ref _deadlinePageMarks, marks);
            }

            return marks;
        }
    }

""",
)

replace_once(
    """        call.MarkRegistered();
        if (call.Deadline.HasValue)
            UpdateEarliestDeadline(call.Deadline.Timestamp);
        if (call.CancellationToken.IsCancellationRequested)
""",
    """        call.MarkRegistered();
        if (call.Deadline.HasValue)
        {
            MarkDeadlinePage(call.Id);
            UpdateEarliestDeadline(call.Deadline.Timestamp);
        }
        if (call.CancellationToken.IsCancellationRequested)
""",
)

replace_once(
    """    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _deadlineScanRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
            var slots = Volatile.Read(ref _slots);
            if (slots is null)
                return;

            var now = _timeProvider.GetTimestamp();
            for (var index = 0; index < slots.Length; index++)
            {
                var call = Volatile.Read(ref slots[index]);
                if (call is null || !call.Deadline.HasValue)
                    continue;
                if (call.Deadline.Timestamp <= now)
                {
                    TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
                }
                else
                {
                    UpdateEarliestDeadline(call.Deadline.Timestamp);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _deadlineScanRunning, 0);
            var next = Volatile.Read(ref _approximateEarliestDeadline);
            if (next != long.MaxValue)
                ArmDeadlineTimer(next);
        }
    }

""",
    """    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _deadlineScanRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
            var slots = Volatile.Read(ref _slots);
            if (slots is null)
                return;
            var marks = Volatile.Read(ref _deadlinePageMarks);
            if (marks is null)
                return;

            var now = _timeProvider.GetTimestamp();
            for (var page = 0; page < _deadlinePageCount; page++)
            {
                var marked = false;
                for (var stripe = 0; stripe < DeadlineMarkerStripeCount; stripe++)
                {
                    ref var mark = ref marks[stripe * _deadlineMarkerStripeStride + page];
                    if (Volatile.Read(ref mark) == 0)
                        continue;

                    Volatile.Write(ref mark, (byte)0);
                    marked = true;
                }

                if (!marked)
                    continue;

                var start = page << DeadlinePageShift;
                var end = Math.Min(start + DeadlinePageSize, slots.Length);
                var hasFutureDeadline = false;
                for (var index = start; index < end; index++)
                {
                    var call = Volatile.Read(ref slots[index]);
                    if (call is null || !call.Deadline.HasValue)
                        continue;
                    if (call.Deadline.Timestamp <= now)
                    {
                        TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
                    }
                    else
                    {
                        hasFutureDeadline = true;
                        UpdateEarliestDeadline(call.Deadline.Timestamp);
                    }
                }

                if (hasFutureDeadline)
                {
                    Volatile.Write(
                        ref marks[DeadlineRetentionStripe * _deadlineMarkerStripeStride + page],
                        (byte)1);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _deadlineScanRunning, 0);
            var next = Volatile.Read(ref _approximateEarliestDeadline);
            if (next != long.MaxValue)
                ArmDeadlineTimer(next);
        }
    }

""",
)

path.write_text(text, encoding="utf-8")
