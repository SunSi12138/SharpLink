#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Client;

// One linked source is shared by the original call and its independent physical producer.
// A logical result can release its reference without disposing the producer's source.
internal sealed class SharpLinkClientModuleCancellation(CancellationToken token, CancellationTokenSource? source) : IDisposable
{
    private int _references = 1;
    internal CancellationToken Token => source?.Token ?? token;
    internal bool IsDisposed { get; private set; }
    internal SharpLinkClientModuleCancellation Retain() { Interlocked.Increment(ref _references); return this; }
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0) { source?.Dispose(); IsDisposed = true; }
    }
}

internal readonly struct SharpLinkClientStreamPhysicalLease(SharpLinkDynamicModuleLease lease, IDisposable? cancellation) : IDisposable
{
    public void Dispose() { try { cancellation?.Dispose(); } finally { lease.Dispose(); } }
}

// Shares the already acquired consumer protection with queued head-registration work.
// This is a retain of one pre-cut owner, not a second acquire on a possibly retired gate.
internal sealed class SharpLinkClientModuleCallOwner(SharpLinkDynamicModuleLease lease, SharpLinkClientModuleCancellation cancellation) : IDisposable
{
    private int _references = 1;
    internal SharpLinkClientModuleCancellation Cancellation => cancellation;
    internal SharpLinkClientModuleCallOwner Retain() { Interlocked.Increment(ref _references); return this; }
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        { try { cancellation.Dispose(); } finally { lease.Dispose(); } }
    }
}

internal static class SharpLinkClientModuleCallContext
{
    private static readonly AsyncLocal<SharpLinkClientModuleCallOwner?> Owner = new();
    internal static SharpLinkClientModuleCallOwner? Current => Owner.Value;
    internal static Scope Push(SharpLinkClientModuleCallOwner owner)
    { var previous = Owner.Value; Owner.Value = owner; return new(previous); }
    internal readonly struct Scope(SharpLinkClientModuleCallOwner? previous) : IDisposable
    { public void Dispose() => Owner.Value = previous; }
}
#endif
