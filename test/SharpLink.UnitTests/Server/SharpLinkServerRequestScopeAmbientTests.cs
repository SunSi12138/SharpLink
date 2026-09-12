using Microsoft.Extensions.Logging;
using SharpLink.Server;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class SharpLinkServerRequestScopeAmbientTests
{
    private const long FirstRequestId = 409;
    private const long SecondRequestId = 410;
    private static readonly Type ReviewTestsType = typeof(SharpLinkServerRequestScopeReviewTests);
    private static readonly Type StubBehaviorType = GetNestedType("StubBehavior");
    private static readonly Type ControlledStubType = GetNestedType("ControlledStub");
    private static readonly Type DispatchHarnessType = GetNestedType("DispatchHarness");
    private static readonly MethodInfo DispatchRequestMethod = typeof(SharpLinkServer).GetMethod(
        "DispatchRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("cannot find scoped Request dispatch path");

    [Test]
    public async Task PendingRequestsDoNotLeakRequestScopeIntoCallerOrEachOther()
    {
        using var loggerFactory = new DefaultExternalScopeLoggerFactory();
        var behavior = Enum.Parse(StubBehaviorType, "LogAfterSignal");
        var stub = CreateInstance(
            ControlledStubType,
            behavior,
            loggerFactory.CreateLogger("AmbientIsolationService"),
            RpcMethodKind.Unary);
        var harnessObject = CreateInstance(DispatchHarnessType, loggerFactory, stub, false);
        var harness = (IAsyncDisposable)harnessObject;

        try
        {
            var server = (SharpLinkServer)GetProperty(harnessObject, "Server");
            var connection = (ServerConnectionState)GetProperty(harnessObject, "Connection");
            var rpcStub = (IRpcStub)stub;

            var first = Dispatch(server, connection, rpcStub, FirstRequestId);
            var second = Dispatch(server, connection, rpcStub, SecondRequestId);

            await Assert.That(first.IsCompleted).IsFalse();
            await Assert.That(second.IsCompleted).IsFalse();

            loggerFactory.CreateLogger("RequestLoopCaller")
                .LogInformation("Caller log after pending requests.");
            var callerLog = loggerFactory.Logs.Single(
                static entry => entry.Message == "Caller log after pending requests.");
            await Assert.That(RequestIds(callerLog).Length).IsEqualTo(0);

            ControlledStubType.GetMethod("Signal", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(stub, null);
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
            await harness.DisposeAsync();
        }
    }

    private static Task Dispatch(
        SharpLinkServer server,
        ServerConnectionState connection,
        IRpcStub stub,
        long requestId)
    {
        var payload = new byte[sizeof(long) * 2];
        BinaryPrimitives.WriteInt64LittleEndian(payload, stub.InterfaceHash);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(long)), 1);
        return (Task)DispatchRequestMethod.Invoke(
            server,
            [
                connection,
                requestId,
                ProtocolV2FrameFlags.None,
                new ReadOnlySequence<byte>(payload),
                connection.CallCancellations,
                CancellationToken.None
            ])!;
    }

    private static long[] RequestIds(CapturedLog log)
        => log.Scopes
            .Where(static scope => scope.StartsWith("RequestId:", StringComparison.Ordinal))
            .Select(static scope => long.Parse(scope.AsSpan("RequestId:".Length)))
            .ToArray();

    private static Type GetNestedType(string name)
        => ReviewTestsType.GetNestedType(name, BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find request-scope test fixture type {name}");

    private static object CreateInstance(Type type, params object?[] arguments)
        => Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null)
            ?? throw new Exception($"cannot create {type.Name}");

    private static object GetProperty(object instance, string name)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;

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
