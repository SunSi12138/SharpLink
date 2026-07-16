namespace SharpLink.Hosting;

internal sealed class SharpLinkServerHostedService(
    SharpLinkServerBuilder builder,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    SharpLinkServerReadiness readiness) : IHostedService
{
    private ISharpLinkServer? _server;
    private Task? _runTask;
    private CancellationTokenSource? _runCts;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        builder.UseLoggerFactoryIfUnset(loggerFactory);
        builder.UseServiceProvider(serviceProvider);
        _server = builder.Build();
        readiness.Publish(_server);
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = _server.RunAsync(_runCts.Token).AsTask();
        if (!_runTask.IsCompleted)
            return;

        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch
        {
            var server = Interlocked.Exchange(ref _server, null);
            if (server is not null)
            {
                readiness.Clear(server);
                await server.DisposeAsync().ConfigureAwait(false);
            }
            _runCts.Dispose();
            _runCts = null;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var runCts = Interlocked.Exchange(ref _runCts, null);
        if (runCts is null)
            return;

        OperationCanceledException? cancellationException = null;
        try
        {
            if (_server is not null)
                await _server.StopAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await runCts.CancelAsync();
            if (_runTask is not null)
                await _runTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            cancellationException = exception;
            if (_server is SharpLinkServer sharpLinkServer)
                sharpLinkServer.ForceStop();
        }
        finally
        {
            runCts.Dispose();
            var server = Interlocked.Exchange(ref _server, null);
            if (server is not null)
            {
                readiness.Clear(server);
                await server.DisposeAsync().ConfigureAwait(false);
            }
            _server = null;
            _runTask = null;
        }

        if (cancellationException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationException).Throw();
    }
}
