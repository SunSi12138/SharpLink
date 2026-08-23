using Microsoft.Extensions.Logging;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class SharpLinkServerRequestScopeReviewTests
{
    [Test]
    public async Task UnaryServiceThrowBeforeAwaitUsesRequestScopeThroughRealDispatch()
    {
        const long requestId = 401;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(StubBehavior.ThrowSynchronously, loggerFactory.CreateLogger("EvidenceService"));
        await using var harness = new DispatchHarness(loggerFactory, stub, useThrowingExceptionMapper: true);

        await harness.DispatchRequest(requestId);

        var log = loggerFactory.Logs.Single(static entry => entry.Message == "Unhandled exception in RPC dispatch.");
        await AssertSingleRequestIdAsync(log, requestId);
        await Assert.That(stub.InvocationCount).IsEqualTo(1);
    }

    [Test]
    public async Task UnaryServiceThrowAfterAwaitKeepsRequestScopeAliveThroughRealDispatch()
    {
        const long requestId = 402;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(StubBehavior.ThrowAfterSignal, loggerFactory.CreateLogger("EvidenceService"));
        await using var harness = new DispatchHarness(loggerFactory, stub, useThrowingExceptionMapper: true);

        var dispatch = harness.DispatchRequest(requestId);
        var inFlight = loggerFactory.Snapshot();
        await Assert.That(inFlight.BeginCount).IsEqualTo(1);
        await Assert.That(inFlight.DisposeCount).IsEqualTo(0);
        await Assert.That(inFlight.MaxDepth).IsEqualTo(1);

        stub.Signal();
        await dispatch;

        var log = loggerFactory.Logs.Single(static entry => entry.Message == "Unhandled exception in RPC dispatch.");
        await AssertSingleRequestIdAsync(log, requestId);
        await Assert.That(stub.InvocationCount).IsEqualTo(1);
        var completed = loggerFactory.Snapshot();
        await Assert.That(completed.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task AsyncServiceLogPreservesSessionThenRequestScopeNesting()
    {
        const long requestId = 403;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(StubBehavior.LogAfterSignal, loggerFactory.CreateLogger("EvidenceService"));
        await using var harness = new DispatchHarness(loggerFactory, stub);

        using (harness.BeginSessionScope(harness.Session.Id))
        {
            var dispatch = harness.DispatchRequest(requestId);
            stub.Signal();
            await dispatch;
        }

        var log = loggerFactory.Logs.Single(static entry => entry.Message == "Evidence service log after await.");
        await Assert.That(log.Scopes.Length).IsEqualTo(2);
        await Assert.That(log.Scopes[0]).IsEqualTo($"RequestId:{requestId}");
        await Assert.That(log.Scopes[1]).IsEqualTo($"SessionId:{harness.Session.Id}");
    }

    [Test]
    public async Task AsyncOneWayServiceLogKeepsRequestScopeAliveUntilCompletion()
    {
        const long requestId = 404;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(
            StubBehavior.LogAfterSignal,
            loggerFactory.CreateLogger("EvidenceService"),
            RpcMethodKind.OneWay);
        await using var harness = new DispatchHarness(loggerFactory, stub);

        var dispatch = harness.DispatchRequest(requestId, ProtocolV2FrameFlags.OneWay);
        var inFlight = loggerFactory.Snapshot();
        await Assert.That(inFlight.BeginCount).IsEqualTo(1);
        await Assert.That(inFlight.DisposeCount).IsEqualTo(0);
        await Assert.That(inFlight.MaxDepth).IsEqualTo(1);

        stub.Signal();
        await dispatch;

        var log = loggerFactory.Logs.Single(static entry => entry.Message == "Evidence service log after await.");
        await AssertSingleRequestIdAsync(log, requestId);
        var completed = loggerFactory.Snapshot();
        await Assert.That(completed.DisposeCount).IsEqualTo(1);
        await Assert.That(harness.Connection.ActiveCalls).IsEqualTo(0);
    }

    [Test]
    public async Task AsyncCancellationTerminalServiceLogRetainsRequestScope()
    {
        const long requestId = 405;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(StubBehavior.LogOnCancellation, loggerFactory.CreateLogger("EvidenceService"));
        await using var harness = new DispatchHarness(loggerFactory, stub);
        using var serverLoopCts = new CancellationTokenSource();

        var dispatch = harness.DispatchRequest(
            requestId,
            ProtocolV2FrameFlags.Cancellable,
            serverLoopCts.Token);

        serverLoopCts.Cancel();
        await dispatch;

        var log = loggerFactory.Logs.Single(static entry => entry.Message == "Evidence service cancellation observed.");
        await AssertSingleRequestIdAsync(log, requestId);
        await YieldUntilAsync(() => harness.Connection.ActiveCalls == 0, "cancelled call did not release admission state");
    }

    [Test]
    public async Task ExpiredDeadlineUsesSingleRequestScopeWithoutInvokingService()
    {
        const long requestId = 406;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(StubBehavior.CompleteSynchronously, loggerFactory.CreateLogger("EvidenceService"));
        await using var harness = new DispatchHarness(loggerFactory, stub);

        await harness.DispatchRequest(
            requestId,
            ProtocolV2FrameFlags.HasDeadline,
            CancellationToken.None,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var snapshot = loggerFactory.Snapshot();
        await Assert.That(stub.InvocationCount).IsEqualTo(0);
        await Assert.That(snapshot.BeginCount).IsEqualTo(1);
        await Assert.That(snapshot.DisposeCount).IsEqualTo(1);
        await Assert.That(snapshot.MaxDepth).IsEqualTo(1);
    }

    private static async Task AssertSingleRequestIdAsync(CapturedLog log, long requestId)
    {
        var requestScopes = log.Scopes.Where(static scope => scope.StartsWith("RequestId:", StringComparison.Ordinal)).ToArray();
        await Assert.That(requestScopes.Length).IsEqualTo(1);
        await Assert.That(requestScopes[0]).IsEqualTo($"RequestId:{requestId}");
    }

    private static async Task YieldUntilAsync(Func<bool> condition, string failureMessage)
    {
        for (var attempt = 0; attempt < 1024 && !condition(); attempt++)
            await Task.Yield();
        if (!condition())
            throw new Exception(failureMessage);
    }

    private readonly record struct ScopeSnapshot(int BeginCount, int DisposeCount, int MaxDepth);
    private readonly record struct CapturedLog(string Message, string[] Scopes);

    private sealed class ScopeCaptureLoggerFactory : ILoggerFactory
    {
        private readonly AsyncLocal<ScopeNode?> _current = new();
        private readonly ConcurrentQueue<CapturedLog> _logs = new();
        private int _requestScopeBeginCount;
        private int _requestScopeDisposeCount;
        private int _maxRequestDepth;

        internal CapturedLog[] Logs => _logs.ToArray();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        internal ScopeSnapshot Snapshot()
            => new(
                Volatile.Read(ref _requestScopeBeginCount),
                Volatile.Read(ref _requestScopeDisposeCount),
                Volatile.Read(ref _maxRequestDepth));

        private IDisposable Begin(object? state)
        {
            var label = TryGetScopeLabel(state);
            var isRequest = label?.StartsWith("RequestId:", StringComparison.Ordinal) == true;
            var node = new ScopeNode(this, _current.Value, label, isRequest);
            _current.Value = node;
            if (isRequest)
            {
                Interlocked.Increment(ref _requestScopeBeginCount);
                var depth = 0;
                for (var current = node; current is not null; current = current.Parent)
                {
                    if (!current.IsDisposed && current.IsRequest)
                        depth++;
                }
                UpdateMax(ref _maxRequestDepth, depth);
            }
            return node;
        }

        private void End(ScopeNode node)
        {
            _current.Value = node.Parent;
            if (node.IsRequest)
                Interlocked.Increment(ref _requestScopeDisposeCount);
        }

        private void Capture<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            for (var current = _current.Value; current is not null; current = current.Parent)
            {
                // A conforming provider is allowed to treat Dispose as globally ending the scope,
                // even in ExecutionContexts that captured the same scope object earlier.
                if (!current.IsDisposed && current.Label is { } label)
                    scopes.Add(label);
            }
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

        private static void UpdateMax(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }

        private sealed class ScopeNode(
            ScopeCaptureLoggerFactory owner,
            ScopeNode? parent,
            string? label,
            bool isRequest) : IDisposable
        {
            private int _disposed;
            internal ScopeNode? Parent { get; } = parent;
            internal string? Label { get; } = label;
            internal bool IsRequest { get; } = isRequest;
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
                => owner.Capture(state, exception, formatter);
        }
    }

    private sealed class DispatchHarness : IAsyncDisposable
    {
        private static readonly MethodInfo BeginSessionScopeMethod = typeof(SharpLinkServer).GetMethod(
            "BeginSessionLogScope", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find SessionId logging scope helper");
        private static readonly MethodInfo DispatchRequestMethod = typeof(SharpLinkServer).GetMethod(
            "DispatchRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find scoped Request dispatch path");
        private static readonly FieldInfo LoggerField = typeof(SharpLinkServer).GetField(
            "_logger", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server logger");
        private static readonly FieldInfo GlobalActiveCallsField = typeof(SharpLinkServer).GetField(
            "_globalActiveCalls", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find global active-call counter");
        private static readonly FieldInfo ConnectionActiveCallsField = typeof(ServerConnectionState).GetField(
            "_activeCalls", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find connection active-call counter");

        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly ControlledStub _stub;
        private readonly ILogger _serverLogger;

        internal DispatchHarness(
            ILoggerFactory loggerFactory,
            ControlledStub stub,
            bool useThrowingExceptionMapper = false)
        {
            _stub = stub;
            var builder = SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration()
                .UseLoggerFactory(loggerFactory)
                .UseTransport(new IdleListener());
            if (useThrowingExceptionMapper)
                builder.UseExceptionMapper(new ThrowingExceptionMapper());
            Server = (SharpLinkServer)builder.Build();
            _serverLogger = (ILogger)LoggerField.GetValue(Server)!;

            var runtimeContext = (SharpLinkRuntimeContext)(
                typeof(SharpLinkServer).GetField(
                    "_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Server)!);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                "request-scope-review",
                _input.Reader,
                _output.Writer,
                RpcSessionTestFixture.ServerOptions(runtimeContext));
            Connection = new ServerConnectionState(
                Session,
                new RpcSessionGeneratedServerBridge(Session),
                new StripedLongMap<ServerCallCancellationState>(runtimeContext.Concurrency),
                CancellationToken.None,
                runtimeContext.TimeProvider);
            if (!Connection.MarkReady(null))
                throw new Exception("connection must become ready");

            var registration = ServiceRegistration.CreateSingleton(
                typeof(EvidenceService), stub, new EvidenceService(), ownsService: false);
            typeof(SharpLinkServer).GetField(
                    "_services", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Server, new Dictionary<long, ServiceRegistration>
                {
                    [stub.InterfaceHash] = registration
                }.ToFrozenDictionary());
            typeof(SharpLinkServer).GetField(
                    "_state", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Server, 2);
        }

        internal SharpLinkServer Server { get; }
        internal RpcSession Session { get; }
        internal ServerConnectionState Connection { get; }

        internal IDisposable? BeginSessionScope(string sessionId)
            => (IDisposable?)BeginSessionScopeMethod.Invoke(null, [_serverLogger, sessionId]);

        internal Task DispatchRequest(
            long requestId,
            ProtocolV2FrameFlags flags = ProtocolV2FrameFlags.None,
            CancellationToken serverLoopToken = default,
            DateTimeOffset? deadline = null)
            => (Task)DispatchRequestMethod.Invoke(Server,
            [
                Connection,
                requestId,
                flags,
                CreateRequestPayload(deadline),
                Connection.CallCancellations,
                serverLoopToken
            ])!;

        private ReadOnlySequence<byte> CreateRequestPayload(DateTimeOffset? deadline)
        {
            var request = new byte[sizeof(long) * (deadline.HasValue ? 3 : 2)];
            BinaryPrimitives.WriteInt64LittleEndian(request, _stub.InterfaceHash);
            BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(sizeof(long)), ControlledStub.MethodHash);
            if (deadline is { } value)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    request.AsSpan(sizeof(long) * 2),
                    value.ToUnixTimeMilliseconds());
            }
            return new ReadOnlySequence<byte>(request);
        }

        public async ValueTask DisposeAsync()
        {
            GlobalActiveCallsField.SetValue(Server, 0);
            ConnectionActiveCallsField.SetValue(Connection, 0);
            await Connection.CloseAsync();
            await Server.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
        }
    }

    private enum StubBehavior
    {
        CompleteSynchronously,
        ThrowSynchronously,
        ThrowAfterSignal,
        LogAfterSignal,
        LogOnCancellation
    }

    private sealed class ControlledStub(
        StubBehavior behavior,
        ILogger serviceLogger,
        RpcMethodKind kind = RpcMethodKind.Unary) : IRpcStub
    {
        internal const long MethodHash = 1;
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        public long InterfaceHash => 2480;
        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor descriptor)
        {
            descriptor = new RpcMethodDescriptor(
                InterfaceHash,
                methodHash,
                kind,
                HasResponsePayload: false,
                HasClientStreams: false,
                HasMethodTimeout: false,
                MethodTimeout: null);
            return methodHash == MethodHash;
        }

        public bool SupportsCancellation(long methodHash) => true;

        public ValueTask InvokeNoReturnAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args)
            => InvokeCore(CancellationToken.None);

        public ValueTask InvokeNoReturnCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            CancellationToken cancellationToken)
            => InvokeCore(cancellationToken);

        public ValueTask InvokeAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output)
            => InvokeCore(CancellationToken.None);

        public ValueTask InvokeCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output,
            CancellationToken cancellationToken)
            => InvokeCore(cancellationToken);

        private ValueTask InvokeCore(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            if (behavior == StubBehavior.ThrowSynchronously)
                throw new InvalidOperationException("issue-248-sync-service");
            return behavior switch
            {
                StubBehavior.CompleteSynchronously => ValueTask.CompletedTask,
                StubBehavior.ThrowAfterSignal => new ValueTask(ThrowAfterSignalAsync()),
                StubBehavior.LogAfterSignal => new ValueTask(LogAfterSignalAsync()),
                StubBehavior.LogOnCancellation => new ValueTask(LogOnCancellationAsync(cancellationToken)),
                _ => ValueTask.CompletedTask
            };
        }

        private async Task ThrowAfterSignalAsync()
        {
            await _signal.Task.ConfigureAwait(false);
            throw new InvalidOperationException("issue-248-async-service");
        }

        private async Task LogAfterSignalAsync()
        {
            await _signal.Task.ConfigureAwait(false);
            serviceLogger.LogInformation("Evidence service log after await.");
        }

        private async Task LogOnCancellationAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                serviceLogger.LogWarning("Evidence service cancellation observed.");
                throw;
            }
        }

        internal void Signal() => _signal.TrySetResult();
    }

    private sealed class ThrowingExceptionMapper : IRpcExceptionMapper
    {
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
            => throw new InvalidOperationException("issue-248-mapper", exception);
    }

    private sealed class EvidenceService
    {
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
