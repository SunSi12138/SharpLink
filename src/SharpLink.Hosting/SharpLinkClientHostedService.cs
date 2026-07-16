namespace SharpLink.Hosting;

internal sealed class SharpLinkClientHostedService(
    SharpClientBuilder builder,
    SharpLinkClientAccessor accessor,
    ILoggerFactory loggerFactory) : IHostedService, IAsyncDisposable
{
    private ISharpLinkClient? _client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            builder.UseLoggerFactoryIfUnset(loggerFactory);
            _client = builder.Build();
            await _client.ConnectAsync(cancellationToken);
            accessor.SetClient(_client);
        }
        catch (Exception ex)
        {
            accessor.Fail(ex);
            await DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        accessor.Stop();
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
            await client.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
            await client.DisposeAsync();
    }
}
