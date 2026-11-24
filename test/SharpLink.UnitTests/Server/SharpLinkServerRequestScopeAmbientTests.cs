using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static SharpLink.UnitTests.Server.SharpLinkServerRequestScopeReviewTests;

namespace SharpLink.UnitTests.Server;

public class SharpLinkServerRequestScopeAmbientTests
{
    private const long FirstRequestId = 409;
    private const long SecondRequestId = 410;

    [Test]
    public async Task PendingRequestsDoNotLeakRequestScopeIntoCallerOrEachOther()
    {
        using var loggerFactory = new DefaultExternalScopeLoggerFactory();
        var stub = new ControlledStub(StubBehavior.LogAfterSignal,
            loggerFactory.CreateLogger("AmbientIsolationService"));
        await using var harness = new DispatchHarness(loggerFactory, stub);

        try
        {
            var first = harness.DispatchRequest(FirstRequestId);
            var second = harness.DispatchRequest(SecondRequestId);

            await Assert.That(first.IsCompleted).IsFalse();
            await Assert.That(second.IsCompleted).IsFalse();

            loggerFactory.CreateLogger("RequestLoopCaller")
                .LogInformation("Caller log after pending requests.");
            var callerLog = loggerFactory.Logs.Single(
                static entry => entry.Message == "Caller log after pending requests.");
            await Assert.That(RequestIds(callerLog).Length).IsEqualTo(0);

            stub.Signal();
            await Task.WhenAll(first, second);

            var serviceLogs = loggerFactory.Logs
                .Where(static entry => entry.Message == "Evidence service log after await.")
                .ToArray();
            await Assert.That(serviceLogs.Length).IsEqualTo(2);
            foreach (var log in serviceLogs)
                await Assert.That(RequestIds(log).Length).IsEqualTo(1);

            var observed = serviceLogs
                .Select(static log => RequestIds(log).Single())
                .OrderBy(static requestId => requestId)
                .ToArray();
            await Assert.That(observed).IsEquivalentTo(new[] { FirstRequestId, SecondRequestId });
        }
        finally
        {
            stub.Signal();
        }
    }

    private static long[] RequestIds(CapturedLog log)
        => log.Scopes
            .Where(static scope => scope.StartsWith("RequestId:", StringComparison.Ordinal))
            .Select(static scope => long.Parse(scope.AsSpan("RequestId:".Length)))
            .ToArray();

    private readonly record struct CapturedLog(string Message, string[] Scopes);

    private sealed class DefaultExternalScopeLoggerFactory : ILoggerFactory
    {
        private readonly LoggerExternalScopeProvider _scopeProvider = new();
        private readonly ConcurrentQueue<CapturedLog> _logs = new();

        internal CapturedLog[] Logs => _logs.ToArray();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private IDisposable? Begin<TState>(TState state) where TState : notnull
            => _scopeProvider.Push(state);

        private void Capture<TState>(
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            _scopeProvider.ForEachScope(
                static (scope, capturedScopes) =>
                {
                    if (TryGetScopeLabel(scope) is { } label)
                        capturedScopes.Add(label);
                },
                scopes);
            _logs.Enqueue(new CapturedLog(formatter(state, exception), scopes.ToArray()));
        }

        private static string? TryGetScopeLabel(object? state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values)
                return null;
            foreach (var pair in values)
            {
                if (pair.Key == "RequestId" && pair.Value is long requestId)
                    return $"RequestId:{requestId}";
                if (pair.Key == "SessionId" && pair.Value is string sessionId)
                    return $"SessionId:{sessionId}";
            }
            return null;
        }

        private sealed class CaptureLogger(DefaultExternalScopeLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.Begin(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Capture(state, exception, formatter);
        }
    }
}
