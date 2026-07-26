namespace SharpLink.Hosting;

internal sealed class SharpLinkMultiClusterClientHostedService(
    SharpLinkMultiClusterClientBuilder builder,
    SharpLinkMultiClusterClientAccessor accessor,
    ILoggerFactory loggerFactory) : IHostedService, IAsyncDisposable
{
    private readonly Lock _lifecycleGate = new();
    private ISharpLinkMultiClusterClient? _client;
    private Task? _stopTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            ISharpLinkMultiClusterClient client;
            lock (_lifecycleGate)
            {
                if (_stopTask is not null)
                    throw new InvalidOperationException("The SharpLink multi-cluster client host has already stopped.");
                builder.UseLoggerFactoryIfUnset(loggerFactory);
                client = builder.Build();
                _client = client;
            }
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            accessor.SetClient(client);
        }
        catch (Exception exception)
        {
            accessor.Fail(exception);
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(exception, cleanupException);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
            throw new System.Diagnostics.UnreachableException();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        accessor.Stop();
        lock (_lifecycleGate)
            return _stopTask ??= StopCoreAsync(cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
            await client.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        accessor.Stop();
        lock (_lifecycleGate)
            return new ValueTask(_stopTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
            await client.DisposeAsync().ConfigureAwait(false);
    }
}
