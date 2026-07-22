namespace SharpLink.Hosting;

internal sealed class SharpLinkMultiClusterClientAccessor : ISharpLinkMultiClusterClientAccessor
{
    private readonly TaskCompletionSource<ISharpLinkMultiClusterClient> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ISharpLinkMultiClusterClient? _client;
    private volatile bool _stopped;

    public ValueTask<ISharpLinkMultiClusterClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        var client = Volatile.Read(ref _client);
        if (client is not null)
            return ValueTask.FromResult(client);
        if (_stopped)
            return ValueTask.FromException<ISharpLinkMultiClusterClient>(Unavailable());

        var task = _ready.Task;
        return cancellationToken.CanBeCanceled
            ? new ValueTask<ISharpLinkMultiClusterClient>(task.WaitAsync(cancellationToken))
            : new ValueTask<ISharpLinkMultiClusterClient>(task);
    }

    internal void SetClient(ISharpLinkMultiClusterClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_stopped)
            throw Unavailable();
        if (Interlocked.CompareExchange(ref _client, client, null) is not null)
            throw new InvalidOperationException("SharpLink multi-cluster client has already been published.");
        _ready.TrySetResult(client);
    }

    internal void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _stopped = true;
        Volatile.Write(ref _client, null);
        _ready.TrySetException(exception);
    }

    internal void Stop()
    {
        _stopped = true;
        Volatile.Write(ref _client, null);
        _ready.TrySetException(Unavailable());
    }

    private static InvalidOperationException Unavailable()
        => new("SharpLink multi-cluster client is not available because the host has already stopped.");
}
