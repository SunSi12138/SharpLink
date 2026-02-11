namespace SharpLink.Hosting;

internal sealed class SharpLinkClientHostedService(
    SharpClientBuilder builder,
    SharpLinkClientAccessor accessor,
    ILoggerFactory loggerFactory) : IHostedService, IDisposable
{
    private ISharpLinkClient? _client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        builder.UseLoggerFactoryIfUnset(loggerFactory);
        _client = builder.Build();
        var connected = await _client.ConnectAsync(cancellationToken);
        if (!connected)
            throw new InvalidOperationException("Failed to connect SharpLink client during host startup.");
        accessor.Client = _client;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        accessor.Client = null;
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_client is IDisposable disposable)
            disposable.Dispose();
    }
}
