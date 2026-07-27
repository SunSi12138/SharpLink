namespace SharpLink.Hosting;

internal sealed class SharpLinkClientAccessor : ISharpLinkClientAccessor
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<ISharpLinkClient> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ISharpLinkClient? _client;
    private volatile bool _stopped;

    public ValueTask<ISharpLinkClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped)
            return ValueTask.FromException<ISharpLinkClient>(CreateUnavailableException());

        var client = Volatile.Read(ref _client);
        if (client is not null)
        {
            if (_stopped)
                return ValueTask.FromException<ISharpLinkClient>(CreateUnavailableException());
            return ValueTask.FromResult(client);
        }

        if (_stopped)
            return ValueTask.FromException<ISharpLinkClient>(CreateUnavailableException());
        var task = _ready.Task;
        return cancellationToken.CanBeCanceled
            ? new ValueTask<ISharpLinkClient>(task.WaitAsync(cancellationToken))
            : new ValueTask<ISharpLinkClient>(task);
    }

    public void SetClient(ISharpLinkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        lock (_gate)
        {
            if (_stopped)
                throw CreateUnavailableException();
            if (_client is not null)
                throw new InvalidOperationException("SharpLink client has already been published.");
            _client = client;
        }

        _ready.TrySetResult(client);
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _stopped = true;
            _client = null;
        }
        _ready.TrySetException(exception);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _client = null;
        }
        _ready.TrySetException(CreateUnavailableException());
    }

    private static InvalidOperationException CreateUnavailableException()
        => new("SharpLink client is not available because the host has already stopped.");
}
