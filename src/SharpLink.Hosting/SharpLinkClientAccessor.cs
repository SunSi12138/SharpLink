namespace SharpLink.Hosting;

internal sealed class SharpLinkClientAccessor : ISharpLinkClientAccessor
{
    private readonly TaskCompletionSource<ISharpLinkClient> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ISharpLinkClient? _client;
    private volatile bool _stopped;

    public ValueTask<ISharpLinkClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        var client = Volatile.Read(ref _client);
        if (client is not null)
            return ValueTask.FromResult(client);

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

        if (_stopped)
            throw CreateUnavailableException();

        if (Interlocked.CompareExchange(ref _client, client, null) is not null)
            throw new InvalidOperationException("SharpLink client has already been published.");

        _ready.TrySetResult(client);
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _stopped = true;
        Volatile.Write(ref _client, null);
        _ready.TrySetException(exception);
    }

    public void Stop()
    {
        _stopped = true;
        Volatile.Write(ref _client, null);
        _ready.TrySetException(CreateUnavailableException());
    }

    private static InvalidOperationException CreateUnavailableException()
        => new("SharpLink client is not available because the host has already stopped.");
}
