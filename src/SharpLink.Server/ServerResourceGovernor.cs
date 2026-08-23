namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ServerResourceGovernor? _resourceGovernor;

    private ServerResourceGovernor ResourceGovernor
    {
        get
        {
            var existing = Volatile.Read(ref _resourceGovernor);
            if (existing is not null)
                return existing;

            var flowControl = _runtimeContext.FlowControl;
            var created = new ServerResourceGovernor(
                flowControl.MaxConcurrentDecodesPerServer,
                flowControl.MaxRetainedCompressedBytesPerServer,
                flowControl.MaxDecodedBytesInFlightPerServer);
            return Interlocked.CompareExchange(ref _resourceGovernor, created, null) ?? created;
        }
    }

    internal int ActiveDecodeCountForDiagnostics => ResourceGovernor.ActiveDecodeCount;

    internal long RetainedCompressedBytesForDiagnostics => ResourceGovernor.RetainedCompressedBytes;

    internal long DecodedBytesInFlightForDiagnostics => ResourceGovernor.DecodedBytesInFlight;
}

/// <summary>
/// Stable server-owned accounting for resources consumed before call activation.
/// This kernel is independent from optional admission-policy generations.
/// </summary>
internal sealed class ServerResourceGovernor
{
    private readonly int _maxConcurrentDecodes;
    private readonly long _maxRetainedCompressedBytes;
    private readonly long _maxDecodedBytesInFlight;
    private int _activeDecodes;
    private long _retainedCompressedBytes;
    private long _decodedBytesInFlight;

    internal ServerResourceGovernor(
        int maxConcurrentDecodes,
        long maxRetainedCompressedBytes,
        long maxDecodedBytesInFlight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentDecodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetainedCompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytesInFlight);
        _maxConcurrentDecodes = maxConcurrentDecodes;
        _maxRetainedCompressedBytes = maxRetainedCompressedBytes;
        _maxDecodedBytesInFlight = maxDecodedBytesInFlight;
    }

    internal int ActiveDecodeCount => Volatile.Read(ref _activeDecodes);

    internal long RetainedCompressedBytes => Volatile.Read(ref _retainedCompressedBytes);

    internal long DecodedBytesInFlight => Volatile.Read(ref _decodedBytesInFlight);

    internal bool TryAcquireRetained(
        long retainedCompressedBytes,
        out ServerRetainedCompressedPermit? permit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retainedCompressedBytes);

        if (!TryAddBounded(
                ref _retainedCompressedBytes,
                retainedCompressedBytes,
                _maxRetainedCompressedBytes))
        {
            permit = null;
            return false;
        }

        try
        {
            permit = new ServerRetainedCompressedPermit(this, retainedCompressedBytes);
            return true;
        }
        catch
        {
            ReleaseRetained(retainedCompressedBytes);
            throw;
        }
    }

    internal bool TryAcquireDecode(
        long retainedCompressedBytes,
        out ServerDecodePermit? permit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retainedCompressedBytes);

        if (!TryIncrementBounded(ref _activeDecodes, _maxConcurrentDecodes))
        {
            permit = null;
            return false;
        }

        if (!TryAddBounded(
                ref _retainedCompressedBytes,
                retainedCompressedBytes,
                _maxRetainedCompressedBytes))
        {
            ReleaseDecodeSlot();
            permit = null;
            return false;
        }

        try
        {
            permit = new ServerDecodePermit(this, retainedCompressedBytes);
            return true;
        }
        catch
        {
            ReleaseDecodeAndRetained(retainedCompressedBytes);
            throw;
        }
    }

    internal bool TryAcquireDecode(
        ServerRetainedCompressedPermit retainedPermit,
        out ServerDecodePermit? permit)
    {
        ArgumentNullException.ThrowIfNull(retainedPermit);

        if (!TryIncrementBounded(ref _activeDecodes, _maxConcurrentDecodes))
        {
            permit = null;
            return false;
        }

        if (!retainedPermit.TryTransferToDecode(this, out var retainedCompressedBytes))
        {
            ReleaseDecodeSlot();
            permit = null;
            return false;
        }

        try
        {
            permit = new ServerDecodePermit(this, retainedCompressedBytes);
            return true;
        }
        catch
        {
            ReleaseDecodeAndRetained(retainedCompressedBytes);
            throw;
        }
    }

    internal bool TryReserveDecodedBytes(long decodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decodedBytes);
        return TryAddBounded(ref _decodedBytesInFlight, decodedBytes, _maxDecodedBytesInFlight);
    }

    internal void ReleaseRetained(long retainedCompressedBytes)
        => ReleaseBytes(
            ref _retainedCompressedBytes,
            retainedCompressedBytes,
            "retained compressed bytes");

    internal void ReleaseDecodeAndRetained(long retainedCompressedBytes)
    {
        try
        {
            ReleaseRetained(retainedCompressedBytes);
        }
        finally
        {
            ReleaseDecodeSlot();
        }
    }

    internal void ReleaseDecodedBytes(long decodedBytes)
        => ReleaseBytes(ref _decodedBytesInFlight, decodedBytes, "decoded bytes");

    private void ReleaseDecodeSlot()
    {
        var remaining = Interlocked.Decrement(ref _activeDecodes);
        if (remaining >= 0)
            return;

        Interlocked.Increment(ref _activeDecodes);
        throw new InvalidOperationException("Server decode concurrency accounting underflowed.");
    }

    private static bool TryIncrementBounded(ref int counter, int limit)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current >= limit)
                return false;
            if (Interlocked.CompareExchange(ref counter, current + 1, current) == current)
                return true;
        }
    }

    private static bool TryAddBounded(ref long counter, long amount, long limit)
    {
        if (amount == 0)
            return true;
        if (amount > limit)
            return false;

        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current > limit - amount)
                return false;
            if (Interlocked.CompareExchange(ref counter, current + amount, current) == current)
                return true;
        }
    }

    private static void ReleaseBytes(ref long counter, long amount, string resourceName)
    {
        if (amount == 0)
            return;

        var remaining = Interlocked.Add(ref counter, -amount);
        if (remaining >= 0)
            return;

        Interlocked.Add(ref counter, amount);
        throw new InvalidOperationException($"Server {resourceName} accounting underflowed.");
    }
}

/// <summary>
/// Owns compressed request bytes that outlive the reader-loop frame before a call has acquired its
/// decode credit. Ownership may move exactly once into a <see cref="ServerDecodePermit"/>.
/// </summary>
internal sealed class ServerRetainedCompressedPermit : IDisposable
{
    private const int Owned = 0;
    private const int Transferred = 1;
    private const int Disposed = 2;

    private readonly ServerResourceGovernor _governor;
    private readonly long _retainedCompressedBytes;
    private int _state = Owned;

    internal ServerRetainedCompressedPermit(
        ServerResourceGovernor governor,
        long retainedCompressedBytes)
    {
        _governor = governor;
        _retainedCompressedBytes = retainedCompressedBytes;
    }

    internal long RetainedCompressedBytes => _retainedCompressedBytes;

    internal bool TryTransferToDecode(
        ServerResourceGovernor governor,
        out long retainedCompressedBytes)
    {
        if (!ReferenceEquals(_governor, governor))
            throw new InvalidOperationException("A retained-byte permit cannot move between resource governors.");

        if (Interlocked.CompareExchange(ref _state, Transferred, Owned) != Owned)
        {
            retainedCompressedBytes = 0;
            return false;
        }

        retainedCompressedBytes = _retainedCompressedBytes;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, Disposed, Owned) != Owned)
            return;
        _governor.ReleaseRetained(_retainedCompressedBytes);
    }
}

/// <summary>
/// Request-owned decode resource permit. While decoding it owns one decode-concurrency credit and
/// any retained compressed bytes. <see cref="CompleteDecode"/> releases those resources while
/// decoded-byte ownership remains attached until final disposal.
/// </summary>
internal sealed class ServerDecodePermit : IDisposable
{
    private readonly ServerResourceGovernor _governor;
    private readonly long _retainedCompressedBytes;
    private readonly Lock _gate = new();
    private long _decodedBytes;
    private bool _decodeCompleted;
    private bool _disposed;

    internal ServerDecodePermit(
        ServerResourceGovernor governor,
        long retainedCompressedBytes)
    {
        _governor = governor;
        _retainedCompressedBytes = retainedCompressedBytes;
    }

    internal bool IsDecodeCompleted
    {
        get
        {
            lock (_gate)
                return _decodeCompleted;
        }
    }

    internal long DecodedBytesOwned
    {
        get
        {
            lock (_gate)
                return _decodedBytes;
        }
    }

    internal bool TryReserveDecodedBytes(long additionalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalBytes);

        lock (_gate)
        {
            if (_disposed || _decodeCompleted)
                return false;
            if (additionalBytes == 0)
                return true;
            if (!_governor.TryReserveDecodedBytes(additionalBytes))
                return false;
            _decodedBytes += additionalBytes;
            return true;
        }
    }

    internal void CompleteDecode()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_decodeCompleted)
                return;

            _governor.ReleaseDecodeAndRetained(_retainedCompressedBytes);
            _decodeCompleted = true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                try
                {
                    if (!_decodeCompleted)
                        _governor.ReleaseDecodeAndRetained(_retainedCompressedBytes);
                }
                finally
                {
                    _governor.ReleaseDecodedBytes(_decodedBytes);
                }
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
