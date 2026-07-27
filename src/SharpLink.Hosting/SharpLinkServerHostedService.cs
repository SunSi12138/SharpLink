using System.Collections.Generic;

namespace SharpLink.Hosting;

internal sealed class SharpLinkServerHostedService(
    SharpLinkServerBuilder builder,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    SharpLinkServerReadiness readiness,
    IHostApplicationLifetime applicationLifetime) : IHostedService
{
    private ISharpLinkServer? _server;
    private Task? _runTask;
    private CancellationTokenSource? _runCts;
    private readonly Lock _stopGate = new();
    private Task? _stopTask;
    private int _stopRequested;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        builder.UseLoggerFactoryIfUnset(loggerFactory);
        builder.UseServiceProvider(serviceProvider);
        _server = builder.Build();
        readiness.Publish(_server);
        _runCts = new CancellationTokenSource();
        _runTask = _server.RunAsync(_runCts.Token).AsTask();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_runTask.IsCompleted)
            {
                _ = ObserveRunTaskAsync(_runTask);
                return;
            }

            await _runTask.ConfigureAwait(false);
            throw new InvalidOperationException("SharpLink server RunAsync completed during startup.");
        }
        catch (Exception runException)
        {
            var failures = new System.Collections.Generic.List<Exception> { runException };
            var server = Interlocked.Exchange(ref _server, null);
            if (server is not null)
            {
                readiness.Clear(server);
                try { await server.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupException) { failures.Add(cleanupException); }
            }
            try { _runCts.Dispose(); }
            catch (Exception cleanupException) { failures.Add(cleanupException); }
            _runCts = null;
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(runException).Throw();
            throw new AggregateException(failures);
        }
    }

    private async Task ObserveRunTaskAsync(Task runTask)
    {
        try
        {
            await runTask.ConfigureAwait(false);
            if (Volatile.Read(ref _stopRequested) == 0 &&
                !applicationLifetime.ApplicationStopping.IsCancellationRequested)
            {
                loggerFactory.CreateLogger<SharpLinkServerHostedService>().LogCritical(
                    "SharpLink server run loop completed unexpectedly.");
                applicationLifetime.StopApplication();
            }
        }
        catch (Exception exception)
        {
            if (applicationLifetime.ApplicationStopping.IsCancellationRequested)
                return;
            loggerFactory.CreateLogger<SharpLinkServerHostedService>().LogCritical(
                exception,
                "SharpLink server run loop terminated unexpectedly.");
            applicationLifetime.StopApplication();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopRequested, 1);
        lock (_stopGate)
            return _stopTask ??= StopCoreAsync(cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var runCts = Interlocked.Exchange(ref _runCts, null);
        if (runCts is null)
            return;

        List<Exception>? failures = null;
        try
        {
            if (_server is not null)
                await _server.StopAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await runCts.CancelAsync();
            if (_runTask is not null)
                await _runTask.WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested &&
                _server is SharpLinkServer sharpLinkServer)
            {
                sharpLinkServer.ForceStop();
            }
        }

        try { runCts.Dispose(); }
        catch (Exception exception) { AddFailure(ref failures, exception); }
        var server = Interlocked.Exchange(ref _server, null);
        if (server is not null)
        {
            try { readiness.Clear(server); }
            catch (Exception exception) { AddFailure(ref failures, exception); }
            try { await server.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { AddFailure(ref failures, exception); }
        }
        _server = null;
        _runTask = null;

        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException(failures);
    }

    private static void AddFailure(ref List<Exception>? failures, Exception exception)
    {
        failures ??= [];
        for (var index = 0; index < failures.Count; index++)
        {
            if (ReferenceEquals(failures[index], exception))
                return;
        }
        failures.Add(exception);
    }
}
