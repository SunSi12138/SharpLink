namespace SharpLink.Hosting;

internal sealed class SharpLinkMultiClusterClientHostedService(
    SharpLinkMultiClusterClientBuilder builder,
    SharpLinkMultiClusterClientAccessor accessor,
    ILoggerFactory loggerFactory) : IHostedService, IAsyncDisposable
{
    private ISharpLinkMultiClusterClient? _client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            builder.UseLoggerFactoryIfUnset(loggerFactory);
            _client = builder.Build();
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            accessor.SetClient(_client);
        }
        catch (Exception exception)
        {
            accessor.Fail(exception);
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        accessor.Stop();
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
            await client.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
            await client.DisposeAsync().ConfigureAwait(false);
    }
}
