namespace SharpLink.Hosting;

internal sealed class SharpLinkServerHostedService(
    SharpLinkServerBuilder builder,
    ILoggerFactory loggerFactory) : IHostedService
{
    private ISharpLinkServer? _server;
    private Task? _runTask;
    private CancellationTokenSource? _runCts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        builder.UseLoggerFactoryIfUnset(loggerFactory);
        _server = builder.Build();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = _server.RunAsync(_runCts.Token).AsTask();
        return _runTask.IsCompleted ? _runTask : Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var runCts = Interlocked.Exchange(ref _runCts, null);
        if (runCts is null)
            return;

        try
        {
            if (_server is not null)
                await _server.StopAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await runCts.CancelAsync();
            if (_runTask is not null)
                await _runTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            runCts.Dispose();
            var server = Interlocked.Exchange(ref _server, null);
            if (server is not null)
                await server.DisposeAsync().AsTask().WaitAsync(cancellationToken);
            _server = null;
            _runTask = null;
        }
    }
}
