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
        _runTask = _server.Start(_runCts.Token);
        return _runTask.IsCompleted ? _runTask : Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var runCts = Interlocked.Exchange(ref _runCts, null);
        if (runCts is null)
            return;

        try
        {
            await runCts.CancelAsync();
            if (_runTask is not null)
                await _runTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            runCts.Dispose();
            (_server as IDisposable)?.Dispose();
            _server = null;
            _runTask = null;
        }
    }
}
