namespace SharpLink.Hosting;

internal sealed class SharpLinkServerHostedService(
    SharpLinkServerBuilder builder,
    ILoggerFactory loggerFactory) : IHostedService
{
    private ISharpLinkServer? _server;
    private Task? _runTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        builder.UseLoggerFactoryIfUnset(loggerFactory);
        _server = builder.Build();
        _runTask = _server.Start(cancellationToken);
        return _runTask.IsCompleted ? _runTask : Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
