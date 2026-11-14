namespace SharpLink.Runtime;

// Internal handoff between the protocol pump and its existing invocation owner.
// Add/Seal have one writer: the pump; Join begins only after that pump returns.
internal interface IGeneratedOutputPhysicalLifetime
{
    GeneratedOutputPhysicalTail GetPhysicalTail();
}

internal sealed class GeneratedOutputPhysicalTail
{
    private Task? _work;
    private Task? _completion;

    internal void Add(Task work)
    {
        if (_completion is not null)
            throw new InvalidOperationException("The physical output lifetime is already sealed.");
        _work = _work is null ? work : Task.WhenAll(_work, work);
    }

    internal void Seal(CancellationTokenSource lifetimeCancellation, IAsyncDisposable? disposeAfterWork = null)
    {
        if (_completion is not null)
            throw new InvalidOperationException("The physical output lifetime is already sealed.");
        _completion = CompleteAsync(_work, lifetimeCancellation, disposeAfterWork);
        _work = null;
    }

    internal ValueTask JoinAsync()
        => new(_completion ?? throw new InvalidOperationException("The physical output lifetime is not sealed."));

    private static async Task CompleteAsync(Task? work, CancellationTokenSource lifetimeCancellation, IAsyncDisposable? disposeAfterWork)
    {
        // Even if work just finished, synchronous user disposal cannot run inline
        // on the logical pump or the connection's request-reading prefix.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (work is not null)
            {
                try { await work.ConfigureAwait(false); }
                catch { } // The logical pump already selected the terminal; observe late faults.
            }
            if (disposeAfterWork is not null)
            {
                try { await disposeAfterWork.DisposeAsync().ConfigureAwait(false); }
                catch { } // Finish cleanup without replacing the selected terminal.
            }
        }
        finally
        {
            lifetimeCancellation.Dispose();
        }
    }
}

// Standalone Runtime callers have no service/module owner to join. They still retain
// the linked CTS until every physical tail exits. This adapter exists only on abandon.
internal sealed class GeneratedOutputPhysicalLifetimeStream<T>(IAsyncEnumerable<T> stream)
    : IAsyncEnumerable<T>, IGeneratedOutputPhysicalLifetime
{
    private readonly GeneratedOutputPhysicalTail _tail = new();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => stream.GetAsyncEnumerator(cancellationToken);

    public GeneratedOutputPhysicalTail GetPhysicalTail() => _tail;
}
