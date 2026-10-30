using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SharpLink.IntegrationTests;

public sealed partial class DynamicEndpointIntegrationTests
{
    // Validation-only overlay shared byte-for-byte by both causal arms. No retries,
    // swallowed exceptions, timing changes, or production instrumentation.
    private sealed class RuntimeSelectionDiagnosticCapture(RuntimeSelectionConnectCounter counter)
    {
        private readonly Lock _gate = new();
        private readonly List<ServerEvent> _events = [];
        private readonly long _started = Stopwatch.GetTimestamp();
        public bool InitialCountsVerified { get; set; }
        public bool PolicyOnlyCountsVerified { get; set; }

        public ILoggerFactory ForEndpoint(string endpoint) => new EndpointLoggerFactory(this, endpoint);

        private void Record(string endpoint, string category, EventId eventId, string message)
        {
            lock (_gate)
                _events.Add(new ServerEvent(endpoint, category, eventId.Id, message,
                    counter.Phase, Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
        }

        public void WriteRecord(int streamDelayMs, bool bodyCompleted, Exception? error)
        {
            ServerEvent[] events;
            lock (_gate)
                events = _events.ToArray();
            var attempts = counter.CaptureAttempts();
            Console.WriteLine("DYNAMIC_ROUTE_CAUSAL " + JsonSerializer.Serialize(new
            {
                schema = 1,
                streamDelayMs,
                bodyCompleted,
                initialCountsVerified = InitialCountsVerified,
                policyOnlyCountsVerified = PolicyOnlyCountsVerified,
                phase = counter.Phase,
                elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                total = attempts.Length,
                first = attempts.Count(attempt => attempt.Endpoint == "first"),
                second = attempts.Count(attempt => attempt.Endpoint == "second"),
                third = attempts.Count(attempt => attempt.Endpoint == "third"),
                attempts = attempts.Select(attempt => new { endpoint = attempt.Endpoint, phase = attempt.Phase }).ToArray(),
                serverEvents = events,
                error = error is null ? null : new
                {
                    type = error.GetType().FullName,
                    message = error.Message,
                    code = (error as SharpLinkException)?.Code.ToString()
                }
            }));
        }

        private sealed record ServerEvent(string Endpoint, string Category, int EventId, string Message,
            string Phase, double ElapsedMs);

        private sealed class EndpointLoggerFactory(RuntimeSelectionDiagnosticCapture capture, string endpoint)
            : ILoggerFactory
        {
            public ILogger CreateLogger(string categoryName) => new EndpointLogger(capture, endpoint, categoryName);
            public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
            public void Dispose() { }
        }

        private sealed class EndpointLogger(RuntimeSelectionDiagnosticCapture capture, string endpoint, string category)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => capture.Record(endpoint, category, eventId, formatter(state, exception));
        }
    }
}
