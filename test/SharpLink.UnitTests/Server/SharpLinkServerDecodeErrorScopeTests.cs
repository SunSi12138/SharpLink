using Microsoft.Extensions.Logging;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class SharpLinkServerDecodeErrorScopeTests
{
    private static readonly MethodInfo ObserveDecodedRequestErrorSendMethod = typeof(SharpLinkServer).GetMethod(
        "ObserveDecodedRequestErrorSend", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("cannot find decoded-request error observer");

    [Test]
    public async Task DecodeFailureAsyncSendFailureKeepsRequestScopeAliveUntilObserverCompletes()
    {
        const long requestId = 401;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseLoggerFactory(loggerFactory)
            .UseTransport(new IdleListener())
            .Build();
        var pendingSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // This is the dedicated observer used by the decode/validation failure branch, which
        // runs before the normal Request dispatch has established its RequestId logging scope.
        var observer = (Task)ObserveDecodedRequestErrorSendMethod.Invoke(
            server,
            [new ValueTask(pendingSend.Task), requestId])!;

        // The observer's async method isolates its ambient scope from the caller, but the scope
        // token itself must remain undisposed until the asynchronous send has been observed.
        await Assert.That(loggerFactory.RequestScopeBeginCount).IsEqualTo(1);
        await Assert.That(loggerFactory.RequestScopeDisposeCount).IsEqualTo(0);
        await Assert.That(loggerFactory.ActiveRequestDepth).IsEqualTo(0);

        pendingSend.SetException(new InvalidOperationException("issue-248-decode-error-send"));
        await observer;

        var log = loggerFactory.Logs.Single(
            static entry => entry.Message == "Unhandled exception in RPC dispatch.");
        await Assert.That(log.RequestIds.Length).IsEqualTo(1);
        await Assert.That(log.RequestIds[0]).IsEqualTo(requestId);
        await Assert.That(loggerFactory.RequestScopeBeginCount).IsEqualTo(1);
        await Assert.That(loggerFactory.RequestScopeDisposeCount).IsEqualTo(1);
        await Assert.That(loggerFactory.ActiveRequestDepth).IsEqualTo(0);
    }

    private readonly record struct CapturedLog(string Message, long[] RequestIds);

    private sealed class ScopeCaptureLoggerFactory : ILoggerFactory
    {
        private readonly AsyncLocal<ScopeNode?> _current = new();
        private readonly ConcurrentQueue<CapturedLog> _logs = new();
        private int _requestScopeBeginCount;
        private int _requestScopeDisposeCount;

        internal int RequestScopeBeginCount => Volatile.Read(ref _requestScopeBeginCount);
        internal int RequestScopeDisposeCount => Volatile.Read(ref _requestScopeDisposeCount);
        internal CapturedLog[] Logs => _logs.ToArray();

        internal int ActiveRequestDepth
        {
            get
            {
                var depth = 0;
                for (var current = _current.Value; current is not null; current = current.Parent)
                {
                    if (!current.IsDisposed && current.RequestId.HasValue)
                        depth++;
                }
                return depth;
            }
        }

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private IDisposable Begin(object? state)
        {
            var requestId = TryGetRequestId(state);
            var node = new ScopeNode(this, _current.Value, requestId);
            _current.Value = node;
            if (requestId.HasValue)
                Interlocked.Increment(ref _requestScopeBeginCount);
            return node;
        }

        private void End(ScopeNode node)
        {
            _current.Value = node.Parent;
            if (node.RequestId.HasValue)
                Interlocked.Increment(ref _requestScopeDisposeCount);
        }

        private void Capture<TState>(TState state, Func<TState, Exception?, string> formatter, Exception? exception)
        {
            var requestIds = new List<long>();
            for (var current = _current.Value; current is not null; current = current.Parent)
            {
                if (!current.IsDisposed && current.RequestId is { } requestId)
                    requestIds.Add(requestId);
            }
            _logs.Enqueue(new CapturedLog(formatter(state, exception), requestIds.ToArray()));
        }

        private static long? TryGetRequestId(object? state)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var pair in values)
                {
                    if (pair.Key == "RequestId" && pair.Value is long requestId)
                        return requestId;
                }
            }
            return null;
        }

        private sealed class ScopeNode(
            ScopeCaptureLoggerFactory owner,
            ScopeNode? parent,
            long? requestId) : IDisposable
        {
            private int _disposed;
            internal ScopeNode? Parent { get; } = parent;
            internal long? RequestId { get; } = requestId;
            internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    owner.End(this);
            }
        }

        private sealed class CaptureLogger(ScopeCaptureLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.Begin(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Capture(state, formatter, exception);
        }
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
