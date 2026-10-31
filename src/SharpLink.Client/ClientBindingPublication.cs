namespace SharpLink.Client;

/// <summary>
/// Physical-session cold publication gate. No collectible payload, assembly, or codec is retained.
/// The single bounded floor array handles invalidation before a result and reordered successful results.
/// </summary>
internal sealed class ClientBindingPublication
{
    private readonly Lock _gate = new();
    private readonly long[] _retiredIncarnations;
    private bool _closed;

    internal ClientBindingPublication(uint slots)
    {
        if (slots is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(slots));
        _retiredIncarnations = new long[checked((int)slots)];
    }

    internal int SlotCount => _retiredIncarnations.Length;

    // Call only AFTER full structural, operation-correlation, receipt and physical-authority checks.
    // Cleanup-only successes still advance the floor: a newer occupant disproves all older occupants.
    internal bool ObserveValidatedSuccess(ulong token)
    {
        ValidateToken(token);
        lock (_gate)
        {
            if (_closed)
                return false;
            var slot = Protocol3BindingToken.Slot(token);
            var incarnation = checked((long)Protocol3BindingToken.Incarnation(token));
            var floor = _retiredIncarnations[slot];
            if (incarnation - 1 > floor)
                Volatile.Write(ref _retiredIncarnations[slot], incarnation - 1);
            return incarnation > _retiredIncarnations[slot];
        }
    }

    // The callback must only publish an internal immutable route snapshot; no user code or async work.
    // Invalidation and this final recheck share the gate, including early results buffered in Publishing.
    internal bool TryCommit(ulong token, Action publishSnapshot)
    {
        ArgumentNullException.ThrowIfNull(publishSnapshot);
        ValidateToken(token);
        lock (_gate)
        {
            if (_closed || !IsEligibleCore(token))
                return false;
            publishSnapshot();
            return true;
        }
    }

    internal void Invalidate(ulong token)
    {
        ValidateToken(token);
        lock (_gate)
        {
            var slot = Protocol3BindingToken.Slot(token);
            var incarnation = checked((long)Protocol3BindingToken.Incarnation(token));
            if (incarnation > _retiredIncarnations[slot])
                Volatile.Write(ref _retiredIncarnations[slot], incarnation);
        }
    }

    internal bool IsEligible(ulong token)
        => !Volatile.Read(ref _closed) &&
           Protocol3BindingToken.IsValid(token, (uint)_retiredIncarnations.Length) && IsEligibleCore(token);

    private bool IsEligibleCore(ulong token)
        => Protocol3BindingToken.Incarnation(token) >
           (ulong)Volatile.Read(ref _retiredIncarnations[Protocol3BindingToken.Slot(token)]);

    internal void Close()
    {
        lock (_gate)
            Volatile.Write(ref _closed, true);
    }

    private void ValidateToken(ulong token)
    {
        if (!Protocol3BindingToken.IsValid(token, (uint)_retiredIncarnations.Length))
            throw new SharpLinkProtocolViolationException(ProtocolViolationReason.MalformedFrame,
                "Binding token is outside this physical session's negotiated slot domain.");
    }
}
