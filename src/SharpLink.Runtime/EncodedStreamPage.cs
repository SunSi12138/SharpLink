namespace SharpLink.Runtime;

/// <summary>
/// One immutable-identity page lease. The producer owns its initial reference; every
/// published slice owns one more until its physical output operation has terminated.
/// Owner objects are deliberately not pooled: a stale owner can never affect a new
/// lease of the same underlying array.
/// </summary>
internal sealed class EncodedStreamPage(byte[] buffer, Action<byte[]> returnBuffer)
{
    private int _references = 1;

    internal byte[] Buffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _references) == 0, this);
            return buffer;
        }
    }

    internal int Capacity => buffer.Length;

    internal void AddRef()
    {
        while (true)
        {
            var current = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(current == 0, this);
            if (current == int.MaxValue)
                throw new InvalidOperationException("Encoded page reference count overflow.");
            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                return;
        }
    }

    internal void Release()
    {
        while (true)
        {
            var current = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(current == 0, this);
            if (Interlocked.CompareExchange(ref _references, current - 1, current) != current)
                continue;
            if (current == 1)
                returnBuffer(buffer);
            return;
        }
    }
}

/// <summary>
/// A page-only, nonwaiting admission budget. It counts actual admitted array capacity,
/// not wire bytes. Renting is serialized, so at most one not-yet-admitted candidate
/// exists in addition to this bound. No codec, credit wait or I/O runs under this gate.
/// </summary>
internal sealed class EncodedStreamPageBudget
{
    internal const int PageBytes = 4 * 1024;
    private readonly Lock _gate = new();
    private readonly int _maxBytes;
    private readonly Func<int, byte[]> _rent;
    private readonly Action<byte[]> _return;
    private readonly Action<byte[]> _releasePage;
    private int _bytesOwned;

    internal EncodedStreamPageBudget(
        int maxBytes,
        Func<int, byte[]>? rent = null,
        Action<byte[]>? returnBuffer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        _maxBytes = maxBytes;
        _rent = rent ?? (static size => ArrayPool<byte>.Shared.Rent(size));
        _return = returnBuffer ?? (static buffer => ArrayPool<byte>.Shared.Return(buffer));
        _releasePage = ReleasePage;
    }

    internal long BytesOwned => Volatile.Read(ref _bytesOwned);

    internal EncodedStreamPage? TryRent()
    {
        lock (_gate)
        {
            if (_maxBytes - _bytesOwned < PageBytes)
                return null;
            var buffer = _rent(PageBytes);
            if (buffer.Length < PageBytes || buffer.Length > _maxBytes - _bytesOwned)
            {
                _return(buffer);
                return null;
            }
            var page = new EncodedStreamPage(buffer, _releasePage);
            _bytesOwned += buffer.Length;
            return page;
        }
    }

    private void ReleasePage(byte[] buffer)
    {
        // Return before freeing the capacity: a new admitted lease cannot overlap
        // this array's old physical ownership.
        lock (_gate)
        {
            try { _return(buffer); }
            finally { _bytesOwned -= buffer.Length; }
        }
    }
}

/// <summary>
/// Single-stream append-only producer. Only trusted fixed-width built-in codecs get
/// this reusable adapter. Published slices never refer to the adapter's mutable cursor.
/// </summary>
internal sealed class EncodedStreamPageWriter(EncodedStreamPageBudget budget) : IRpcByteBufferWriter
{
    private EncodedStreamPage? _page;
    private int _cursor;
    private int _written;
    private int _limit;
    private bool _frameActive;
    private bool _disposed;

    internal bool TryBeginFrame(int bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameActive)
            throw new InvalidOperationException("A stream page frame is already being encoded.");
        if (bytes <= 0 || bytes > EncodedStreamPageBudget.PageBytes)
            return false;
        if (_page is null || _page.Capacity - _cursor < bytes)
        {
            var previous = _page;
            _page = null;
            _cursor = 0;
            previous?.Release();
            _page = budget.TryRent();
            if (_page is null)
                return false;
        }
        _written = 0;
        _limit = bytes;
        _frameActive = true;
        return true;
    }

    internal OwnedFrame CommitFrame()
    {
        EnsureFrame();
        if (_written != _limit)
            throw new InvalidOperationException("Built-in stream item size differed after credit was acquired.");
        var page = _page!;
        page.AddRef();
        OwnedFrame frame;
        try { frame = new OwnedFrame(page, _cursor, _written); }
        catch { page.Release(); throw; }
        _cursor += _written;
        _frameActive = false;
        return frame;
    }

    internal void AbandonFrame()
    {
        _frameActive = false;
        _written = 0;
    }

    public int WrittenCount { get { EnsureFrame(); return _written; } }
    public int Capacity { get { EnsureFrame(); return _limit; } }
    public ReadOnlyMemory<byte> WrittenMemory { get { EnsureFrame(); return _page!.Buffer.AsMemory(_cursor, _written); } }
    public Span<byte> WrittenSpan { get { EnsureFrame(); return _page!.Buffer.AsSpan(_cursor, _written); } }
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _page!.Buffer.AsMemory(_cursor + _written, _limit - _written);
    }
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _page!.Buffer.AsSpan(_cursor + _written, _limit - _written);
    }
    public void Advance(int count)
    {
        EnsureFrame();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _limit - _written)
            throw new InvalidOperationException("Built-in stream codec exceeded its fixed-width reservation.");
        _written += count;
    }
    public void Clear() { EnsureFrame(); _written = 0; }

    private void EnsureCapacity(int sizeHint)
    {
        EnsureFrame();
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        if (Math.Max(1, sizeHint) > _limit - _written)
            throw new InvalidOperationException("Built-in stream codec exceeded its fixed-width reservation.");
    }

    private void EnsureFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_frameActive)
            throw new InvalidOperationException("No stream page frame is being encoded.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var page = _page;
        _page = null;
        page?.Release();
    }
}
