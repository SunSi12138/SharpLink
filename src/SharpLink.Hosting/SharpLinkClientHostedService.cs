namespace SharpLink.Hosting;

internal sealed class SharpLinkClientHostedService(
    SharpClientBuilder builder,
    SharpLinkClientAccessor accessor,
    ILoggerFactory loggerFactory) : IHostedService, IDisposable
{
    private ISharpLinkClient? _client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            builder.UseLoggerFactoryIfUnset(loggerFactory);
            _client = builder.Build();
            await _client.ConnectOrThrowAsync(cancellationToken);
            accessor.SetClient(_client);
        }
        catch (Exception ex)
        {
            accessor.Fail(ex);
            Dispose();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        accessor.Stop();
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_client is IDisposable disposable)
            disposable.Dispose();
    }
}
