namespace SharpLink.Client;

partial class PendingRequestTable
{
    internal bool SlotsMaterialized => Volatile.Read(ref _transitionSlots) is not null;
    internal int DeadlineSlotSpan =>
        Volatile.Read(ref _slots)?.Length ?? 1 + (Volatile.Read(ref _transitionSlots)?.Length ?? 0);

    public int Count
    {
        get
        {
            var slots = Volatile.Read(ref _slots);
            var count = 0;
            if (slots is null)
            {
                count = Volatile.Read(ref _inlineSlot) is null ? 0 : 1;
                slots = Volatile.Read(ref _transitionSlots);
            }
            if (slots is null)
                return count;

            for (var index = 0; index < slots.Length; index++)
                if (Volatile.Read(ref slots[index]) is not null)
                    count++;
            return count;
        }
    }

    private bool TryInstallInline(PendingCall call, long id)
    {
        // Only pre-promotion writers take this lock. Completion may remove
        // the call in parallel, but publication cannot change the ID past promotion.
        lock (_slotsInitializationGate)
        {
            if (_transitionSlots is not null ||
                Interlocked.CompareExchange(ref _inlineSlot, call, null) is not null)
                return false;
            _inlineRequestId = id;
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref PendingCall? ReadSlot(long id)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is not null)
            return ref slots[(int)(id & _indexMask)];
        return ref ReadUnsettledSlot(id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref PendingCall? ReadUnsettledSlot(long id)
    {
        // Keep the original reference even after settling. A reader which
        // already saw _slots == null must resolve the same dense array.
        var slots = Volatile.Read(ref _transitionSlots);
        if (slots is null ||
            (id == Volatile.Read(ref _inlineRequestId) && Volatile.Read(ref _inlineSlot) is not null))
            return ref _inlineSlot;
        return ref slots[(int)(id & _indexMask)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private PendingCall?[] PrepareFlatSlots(out int reservedIndex)
    {
        lock (_slotsInitializationGate)
        {
            var slots = _transitionSlots;
            if (slots is null)
            {
                slots = new PendingCall?[_capacity];
                Volatile.Write(ref _transitionSlots, slots);
            }

            if (Volatile.Read(ref _inlineSlot) is null)
            {
                // Inline publishes under the same gate and is disabled once
                // _transitionSlots exists. The empty cell is now permanent.
                reservedIndex = -1;
                if (_slots is null)
                    Volatile.Write(ref _slots, slots);
            }
            else
            {
                // While the inline call remains live, keep its original ring index reserved.
                reservedIndex = (int)(_inlineRequestId & _indexMask);
            }

            return slots;
        }
    }
}
