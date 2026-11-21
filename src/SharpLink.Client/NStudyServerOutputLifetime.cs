namespace SharpLink.Client;

// The enumeration can report a deadline before the runtime dispatcher has
// physically left its decoder/cleanup. Both share the one original module lease.
internal sealed class NStudyServerOutputLifetime(
    SharpLinkDynamicModuleLease lease, CancellationTokenSource? linked) : IDisposable
{
    private readonly Lock _gate = new();
    private int _owners = 1;
    private bool _consumerClosed;
    private bool _dispatcherClaimed;
    private Func<ValueTask>? _closeDispatcher;

    internal void ClaimDispatcher()
    {
        lock (_gate)
        {
            if (_consumerClosed)
                throw new OperationCanceledException("The prepared output consumer has already exited.");
            if (_dispatcherClaimed)
                throw new InvalidOperationException("The prepared output already owns a dispatcher.");
            _dispatcherClaimed = true;
            _owners++;
        }
    }

    internal void AttachDispatcher(Func<ValueTask> close)
    {
        lock (_gate)
        {
            if (!_consumerClosed)
            {
                _closeDispatcher = close;
                return;
            }
        }
        _ = ObserveLateCloseAsync(close);
    }

    internal void CloseConsumer()
    {
        Func<ValueTask>? close;
        lock (_gate)
        {
            if (_consumerClosed)
                return;
            _consumerClosed = true;
            close = _closeDispatcher;
            _closeDispatcher = null;
        }
        if (close is not null)
            _ = ObserveLateCloseAsync(close);
        Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (--_owners != 0)
                return;
        }
        try { linked?.Dispose(); }
        finally { lease.Dispose(); }
    }

    private static async Task ObserveLateCloseAsync(Func<ValueTask> close)
    {
        // Normal disposal is awaited by the consumer. Only an abandoned
        // dispatcher remains for this fallback to finish and observe.
        try { await close().ConfigureAwait(false); }
        catch { }
    }
}
