namespace SharpLink.Hosting;

internal sealed class SharpLinkMultiClusterClientAccessor : ISharpLinkMultiClusterClientAccessor
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<ISharpLinkMultiClusterClient> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ISharpLinkMultiClusterClient? _client;
    private bool _stopped;

    public ValueTask<ISharpLinkMultiClusterClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        Task<ISharpLinkMultiClusterClient> task;
        lock (_gate)
        {
            if (_stopped)
                return ValueTask.FromException<ISharpLinkMultiClusterClient>(Unavailable());

            if (_client is { } client)
                return ValueTask.FromResult(client);

            task = _ready.Task;
        }
        return cancellationToken.CanBeCanceled
            ? new ValueTask<ISharpLinkMultiClusterClient>(task.WaitAsync(cancellationToken))
            : new ValueTask<ISharpLinkMultiClusterClient>(task);
    }

    internal void SetClient(ISharpLinkMultiClusterClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            if (_stopped)
                throw Unavailable();
            if (_client is not null)
                throw new InvalidOperationException("SharpLink multi-cluster client has already been published.");

            _client = client;
            _ready.TrySetResult(client);
        }
    }

    internal void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _stopped = true;
            _client = null;
            _ready.TrySetException(exception);
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _client = null;
            _ready.TrySetException(Unavailable());
        }
    }

    private static InvalidOperationException Unavailable()
        => new("SharpLink multi-cluster client is not available because the host has already stopped.");
}
