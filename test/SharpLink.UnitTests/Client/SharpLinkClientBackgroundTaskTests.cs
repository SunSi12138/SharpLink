using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientBackgroundTaskTests
{
    [Test]
    public async Task FaultedTrackedTaskShouldBeLoggedAfterItCompletes()
    {
        var loggerFactory = new CaptureLoggerFactory();
        var client = new SharpLinkClient(
            new TestClientTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            loggerFactory,
            new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false));

        client.TrackFrameworkTask(
            Task.FromException(new InvalidOperationException("tracked cleanup failed")),
            "TrackedCleanup");

        Ensure(loggerFactory.Entries.Any(static entry =>
                entry.Level == LogLevel.Error &&
                entry.Exception is InvalidOperationException { Message: "tracked cleanup failed" }),
            "a completed faulted background task must remain observable through logging");
        try
        {
            await client.StopAsync();
        }
        catch (InvalidOperationException exception) when (exception.Message == "tracked cleanup failed")
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class CaptureLoggerFactory : ILoggerFactory
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CaptureLogger(List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries)
                entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }

    private readonly record struct LogEntry(LogLevel Level, Exception? Exception, string Message);
}
