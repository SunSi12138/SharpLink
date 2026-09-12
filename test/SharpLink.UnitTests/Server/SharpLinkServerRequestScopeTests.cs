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

public class SharpLinkServerRequestScopeTests
{
    [Test]
    public async Task SuccessfulRequestsCreateExactlyOneRequestScope()
    {
        var syncUnary = await MeasureUnaryAsync(isAsync: false, requestId: 101);
        var asyncUnary = await MeasureUnaryAsync(isAsync: true, requestId: 102);
        var syncOneWay = await MeasureOneWayAsync(isAsync: false, requestId: 103);
        var asyncOneWay = await MeasureOneWayAsync(isAsync: true, requestId: 104);

        Console.WriteLine($"ISSUE248_SCOPE_COUNTS sync-unary={syncUnary.BeginCount}/{syncUnary.MaxDepth} " +
                          $"async-unary={asyncUnary.BeginCount}/{asyncUnary.MaxDepth} " +
                          $"sync-oneway={syncOneWay.BeginCount}/{syncOneWay.MaxDepth} " +
                          $"async-oneway={asyncOneWay.BeginCount}/{asyncOneWay.MaxDepth}");

        await Assert.That(syncUnary.BeginCount).IsEqualTo(1);
        await Assert.That(syncUnary.DisposeCount).IsEqualTo(syncUnary.BeginCount);
        await Assert.That(syncUnary.MaxDepth).IsEqualTo(1);
        await Assert.That(asyncUnary.BeginCount).IsEqualTo(1);
        await Assert.That(asyncUnary.DisposeCount).IsEqualTo(asyncUnary.BeginCount);
        await Assert.That(asyncUnary.MaxDepth).IsEqualTo(1);
        await Assert.That(syncOneWay.BeginCount).IsEqualTo(1);
        await Assert.That(syncOneWay.DisposeCount).IsEqualTo(syncOneWay.BeginCount);
        await Assert.That(syncOneWay.MaxDepth).IsEqualTo(1);
        await Assert.That(asyncOneWay.BeginCount).IsEqualTo(1);
        await Assert.That(asyncOneWay.DisposeCount).IsEqualTo(asyncOneWay.BeginCount);
        await Assert.That(asyncOneWay.MaxDepth).IsEqualTo(1);
    }

    [Test]
    public async Task AsyncOneWayFailureRetainsExactlyOneRequestIdAfterOuterScopeDisposes()
    {
        const long requestId = 201;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(RpcMethodKind.OneWay, asynchronous: true);
        await using var harness = new DispatchHarness(loggerFactory, stub);

        ValueTask dispatch;
        using (harness.BeginRequestScope(requestId))
            dispatch = harness.DispatchOneWay(requestId);

        stub.Fail(new InvalidOperationException("issue-248-oneway"));
        await dispatch;

        var log = loggerFactory.Logs.Single(entry => entry.Message == "One-way RPC dispatch failed.");
        await Assert.That(log.RequestIds.Length).IsEqualTo(1);
        await Assert.That(log.RequestIds[0]).IsEqualTo(requestId);
    }

    [Test]
    public async Task DispatchObserverFailureRetainsExactlyOneRequestIdAfterOuterScopeDisposes()
    {
        const long requestId = 202;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(RpcMethodKind.Unary, asynchronous: false);
        await using var harness = new DispatchHarness(loggerFactory, stub);

        Task observer;
        using (harness.BeginRequestScope(requestId))
        {
            observer = harness.Observe(
                ValueTask.FromException(new InvalidOperationException("issue-248-observer")),
                requestId);
        }
        await observer;

        var log = loggerFactory.Logs.Single(entry => entry.Message == "Unhandled exception in RPC dispatch.");
        await Assert.That(log.RequestIds.Length).IsEqualTo(1);
        await Assert.That(log.RequestIds[0]).IsEqualTo(requestId);
    }

    [Test]
    public async Task ParallelAsyncFailuresDoNotCrossContaminateRequestIds()
    {
        const long firstRequestId = 301;
        const long secondRequestId = 302;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var firstStub = new ControlledStub(RpcMethodKind.OneWay, asynchronous: true, interfaceHash: 301);
        var secondStub = new ControlledStub(RpcMethodKind.OneWay, asynchronous: true, interfaceHash: 302);
        await using var firstHarness = new DispatchHarness(loggerFactory, firstStub);
        await using var secondHarness = new DispatchHarness(loggerFactory, secondStub);

        ValueTask firstDispatch;
        using (firstHarness.BeginRequestScope(firstRequestId))
            firstDispatch = firstHarness.DispatchOneWay(firstRequestId);
        ValueTask secondDispatch;
        using (secondHarness.BeginRequestScope(secondRequestId))
            secondDispatch = secondHarness.DispatchOneWay(secondRequestId);

        secondStub.Fail(new InvalidOperationException("issue-248-second"));
        firstStub.Fail(new InvalidOperationException("issue-248-first"));
        await firstDispatch;
        await secondDispatch;

        var logs = loggerFactory.Logs
            .Where(static entry => entry.Message == "One-way RPC dispatch failed.")
            .ToArray();
        foreach (var log in logs)
            await Assert.That(log.RequestIds.Length).IsEqualTo(1);
        var observedIds = logs.Select(static entry => entry.RequestIds[0]).Order().ToArray();
        await Assert.That(observedIds[0]).IsEqualTo(firstRequestId);
        await Assert.That(observedIds[1]).IsEqualTo(secondRequestId);
    }

    [Test]
    public async Task OneHundredThousandOneWayRequestsDoNotLeakRequestScopes()
    {
        const int requestCount = 100_000;
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(RpcMethodKind.OneWay, asynchronous: false);
        await using var harness = new DispatchHarness(loggerFactory, stub);

        for (var i = 0; i < requestCount; i++)
        {
            using (harness.BeginRequestScope(1_000L + i))
                await harness.DispatchOneWay(1_000L + i);
        }

        var snapshot = loggerFactory.Snapshot();
        await Assert.That(snapshot.BeginCount).IsEqualTo(requestCount);
        await Assert.That(snapshot.DisposeCount).IsEqualTo(requestCount);
        await Assert.That(snapshot.MaxDepth).IsEqualTo(1);
        await Assert.That(harness.Connection.ActiveCalls).IsEqualTo(0);
    }

    private static async Task<ScopeSnapshot> MeasureUnaryAsync(bool isAsync, long requestId)
    {
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(RpcMethodKind.Unary, isAsync);
        await using var harness = new DispatchHarness(loggerFactory, stub);

        Task? observer = null;
        using (harness.BeginRequestScope(requestId))
        {
            var dispatch = harness.DispatchUnary(requestId);
            if (!dispatch.IsCompletedSuccessfully)
                observer = harness.Observe(dispatch, requestId);
        }

        if (isAsync)
            stub.Complete();
        if (observer is not null)
            await observer;

        return loggerFactory.Snapshot();
    }

    private static async Task<ScopeSnapshot> MeasureOneWayAsync(bool isAsync, long requestId)
    {
        var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(RpcMethodKind.OneWay, isAsync);
        await using var harness = new DispatchHarness(loggerFactory, stub);

        ValueTask dispatch;
        using (harness.BeginRequestScope(requestId))
            dispatch = harness.DispatchOneWay(requestId);

        if (isAsync)
            stub.Complete();
        await dispatch;

        await Assert.That(harness.Connection.ActiveCalls).IsEqualTo(0);
        return loggerFactory.Snapshot();
    }

    private readonly record struct ScopeSnapshot(int BeginCount, int DisposeCount, int MaxDepth);
    private readonly record struct CapturedLog(string Message, Exception? Exception, long[] RequestIds);

    private sealed class ScopeCaptureLoggerFactory : ILoggerFactory
    {
        private readonly AsyncLocal<ScopeNode?> _current = new();
        private readonly ConcurrentQueue<CapturedLog> _logs = new();
        private int _requestScopeBeginCount;
        private int _requestScopeDisposeCount;
        private int _maxRequestDepth;

        internal int RequestScopeBeginCount => Volatile.Read(ref _requestScopeBeginCount);
        internal int RequestScopeDisposeCount => Volatile.Read(ref _requestScopeDisposeCount);
        internal CapturedLog[] Logs => _logs.ToArray();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        internal ScopeSnapshot Snapshot()
            => new(RequestScopeBeginCount, RequestScopeDisposeCount, Volatile.Read(ref _maxRequestDepth));

        private IDisposable Begin(object? state)
        {
            var requestId = TryGetRequestId(state);
            var node = new ScopeNode(this, _current.Value, requestId);
            _current.Value = node;
            if (requestId.HasValue)
            {
                Interlocked.Increment(ref _requestScopeBeginCount);
                var depth = 0;
                for (var current = node; current is not null; current = current.Parent)
                {
                    if (current.RequestId.HasValue)
                        depth++;
                }
                UpdateMax(ref _maxRequestDepth, depth);
            }
            return node;
        }

        private void End(ScopeNode node)
        {
            _current.Value = node.Parent;
            if (node.RequestId.HasValue)
                Interlocked.Increment(ref _requestScopeDisposeCount);
        }

        private void Capture<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var requestIds = new List<long>();
            for (var current = _current.Value; current is not null; current = current.Parent)
            {
                if (current.RequestId is { } requestId)
                    requestIds.Add(requestId);
            }
            _logs.Enqueue(new CapturedLog(formatter(state, exception), exception, requestIds.ToArray()));
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
            long? requestId) : IDisposable
        {
            private int _disposed;
            internal ScopeNode? Parent { get; } = parent;
            internal long? RequestId { get; } = requestId;

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
        private static readonly MethodInfo BeginRequestScopeMethod = typeof(SharpLinkServer).GetMethod(
            "BeginRequestLogScope", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find RequestId logging scope helper");
        private static readonly MethodInfo DispatchRpcMethod = typeof(SharpLinkServer).GetMethod(
            "DispatchRpcAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find RPC dispatch path");
        private static readonly MethodInfo DispatchOneWayMethod = typeof(SharpLinkServer).GetMethod(
            "DispatchOneWayRpc", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find one-way dispatch path");
        private static readonly MethodInfo AwaitDispatchMethod = typeof(SharpLinkServer).GetMethod(
            "AwaitDispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find dispatch observer");
        private static readonly FieldInfo LoggerField = typeof(SharpLinkServer).GetField(
            "_logger", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server logger");
        private static readonly FieldInfo CallAdmissionField = typeof(SharpLinkServer).GetField(
            "_callAdmission", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server call-admission owner");
        private static readonly FieldInfo GlobalActiveCallsField = typeof(ServerCallAdmission).GetField(
            "_globalActiveCalls", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find admission active-call counter");
        private static readonly FieldInfo ConnectionActiveCallsField = typeof(ServerConnectionState).GetField(
            "_activeCalls", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find connection active-call counter");

        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly ControlledStub _stub;
        private readonly ILogger _logger;

        internal DispatchHarness(ILoggerFactory loggerFactory, ControlledStub stub)
        {
            _stub = stub;
            Server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration()
                .UseLoggerFactory(loggerFactory)
                .UseTransport(new IdleListener())
                .Build();
            _logger = (ILogger)LoggerField.GetValue(Server)!;
            var runtimeContext = (SharpLinkRuntimeContext)(
                typeof(SharpLinkServer).GetField(
                    "_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Server)!);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                "request-scope-evidence",
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
            ((ServerServiceModuleRegistry)typeof(SharpLinkServer).GetField(
                    "_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Server)!).PublishServices(new Dictionary<long, ServiceRegistration>
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

        internal IDisposable? BeginRequestScope(long requestId)
            => (IDisposable?)BeginRequestScopeMethod.Invoke(null, [_logger, requestId]);

        internal ValueTask DispatchUnary(long requestId)
            => (ValueTask)DispatchRpcMethod.Invoke(Server,
            [
                Connection,
                requestId,
                ProtocolV2FrameFlags.None,
                CreateRequestPayload(),
                Connection.CallCancellations,
                CancellationToken.None,
                null,
                null,
                false,
                null
            ])!;

        internal ValueTask DispatchOneWay(long requestId)
            => (ValueTask)DispatchOneWayMethod.Invoke(Server,
            [
                Connection,
                requestId,
                ProtocolV2FrameFlags.OneWay,
                CreateRequestPayload(),
                Connection.CallCancellations,
                CancellationToken.None,
                null,
                null,
                false,
                0,
                null
            ])!;

        internal Task Observe(ValueTask dispatchTask, long requestId)
            => (Task)AwaitDispatchMethod.Invoke(Server, [dispatchTask, requestId])!;

        private ReadOnlySequence<byte> CreateRequestPayload()
        {
            var request = new byte[sizeof(long) * 2];
            BinaryPrimitives.WriteInt64LittleEndian(request, _stub.InterfaceHash);
            BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(sizeof(long)), ControlledStub.MethodHash);
            return new ReadOnlySequence<byte>(request);
        }

        public async ValueTask DisposeAsync()
        {
            GlobalActiveCallsField.SetValue(CallAdmissionField.GetValue(Server), 0);
            ConnectionActiveCallsField.SetValue(Connection, 0);
            await Connection.CloseAsync();
            await Server.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
        }
    }

    private sealed class ControlledStub(
        RpcMethodKind kind,
        bool asynchronous,
        long interfaceHash = 248) : IRpcStub
    {
        internal const long MethodHash = 1;
        private readonly TaskCompletionSource? _completion = asynchronous
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        public long InterfaceHash { get; } = interfaceHash;

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

        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args)
            => _completion is null ? ValueTask.CompletedTask : new ValueTask(_completion.Task);

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);

        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
            => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);

        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken)
            => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);

        internal void Complete() => _completion?.TrySetResult();
        internal void Fail(Exception exception) => _completion?.TrySetException(exception);
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
