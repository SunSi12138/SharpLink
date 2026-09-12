using System.Collections.Generic;

namespace SharpLink.Hosting;

internal sealed class SharpLinkServerHostedService(
    SharpLinkServerBuilder builder,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    SharpLinkServerReadiness readiness,
    IHostApplicationLifetime applicationLifetime) : IHostedService
{
    private readonly Lock _stopGate = new();
    private ISharpLinkServer? _server;
    private Task? _terminalObserver;
    private Task? _stopTask;
    private int _stopRequested;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ISharpLinkServer server;
        lock (_stopGate)
        {
            if (Volatile.Read(ref _stopRequested) != 0 || _stopTask is not null)
                throw new InvalidOperationException("The SharpLink server host has already stopped.");
            if (_server is not null)
                throw new InvalidOperationException("The SharpLink server host has already started.");

            builder.UseLoggerFactoryIfUnset(loggerFactory);
            builder.UseServiceProvider(serviceProvider);
            server = builder.Build();
            _server = server;
        }

        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            readiness.Publish(server);
            Volatile.Write(ref _terminalObserver, ObserveTerminalAsync(server));
        }
        catch (Exception startException)
        {
            var failures = new List<Exception> { startException };
            var owned = Interlocked.Exchange(ref _server, null);
            if (owned is not null)
            {
                try { readiness.Clear(owned); }
                catch (Exception cleanupException) { AddFailure(ref failures, cleanupException); }
                try { await owned.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupException) { AddFailure(ref failures, cleanupException); }
            }
            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startException).Throw();
            throw new AggregateException(failures ?? []);
        }
    }

    private async Task ObserveTerminalAsync(ISharpLinkServer server)
    {
        try
        {
            await server.WaitForShutdownAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _stopRequested) == 0 &&
                !applicationLifetime.ApplicationStopping.IsCancellationRequested)
            {
                loggerFactory.CreateLogger<SharpLinkServerHostedService>().LogCritical(
                    "SharpLink server terminated unexpectedly.");
                applicationLifetime.StopApplication();
            }
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _stopRequested) != 0 ||
                applicationLifetime.ApplicationStopping.IsCancellationRequested)
            {
                return;
            }
            loggerFactory.CreateLogger<SharpLinkServerHostedService>().LogCritical(
                exception,
                "SharpLink server terminated because of an unrecoverable runtime failure.");
            applicationLifetime.StopApplication();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopRequested, 1);
        Task stopTask;
        lock (_stopGate)
            stopTask = _stopTask ??= StopCoreAsync();

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    private async Task StopCoreAsync()
    {
        var server = Interlocked.Exchange(ref _server, null);
        if (server is null)
            return;

        List<Exception>? failures = null;
        try
        {
            await server.StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
        }

        try { readiness.Clear(server); }
        catch (Exception exception) { AddFailure(ref failures, exception); }

        var terminalObserver = Volatile.Read(ref _terminalObserver);
        if (terminalObserver is not null)
            await terminalObserver.ConfigureAwait(false);
        Volatile.Write(ref _terminalObserver, null);

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
