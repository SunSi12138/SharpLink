using Microsoft.Extensions.DependencyInjection;
using SharpLink.Server;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class SharpLinkServerInvocationTests
{
    [Test]
    [NotInParallel]
    public async Task CallAdmissionShouldNotCrossTheServerDrainBoundary()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var input = new System.IO.Pipelines.Pipe();
        var output = new System.IO.Pipelines.Pipe();
        await using var session = new RpcSession(
            "admission-drain-race",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true);
        var connection = new ServerConnectionState(
            session,
            new RuntimeConcurrencyOptions(),
            CancellationToken.None);
        Ensure(connection.MarkReady(null), "connection ready");

        var tryAcquire = CreatePrivateCall<Func<SharpLinkServer, ServerConnectionState, int>>(
            typeof(SharpLinkServer).GetMethod(
                "TryAcquireCall",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server call admission path"));
        var setState = CreateInterlockedInt32Setter<SharpLinkServer>("_state");
        var globalActiveCalls = typeof(SharpLinkServer).GetField(
            "_globalActiveCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find global active-call counter");
        var connectionActiveCalls = typeof(ServerConnectionState).GetField(
            "_activeCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find connection active-call counter");

        const int running = 2;
        const int draining = 3;
        const int acquired = 0;
        const int delayVariants = 96;
        const int iterationsPerDelay = 2_000;
        using var phase = new Barrier(2);
        var admissionResult = -1;
        var witnessedLateAdmission = false;
        var worker = new Thread(() =>
        {
            for (var delay = 0; delay < delayVariants; delay++)
            {
                for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
                {
                    phase.SignalAndWait();
                    admissionResult = tryAcquire(server, connection);
                    phase.SignalAndWait();
                }
            }
        })
        {
            IsBackground = true,
            Name = "SharpLink admission/drain race probe"
        };
        worker.Start();

        for (var delay = 0; delay < delayVariants; delay++)
        {
            for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
            {
                setState(server, running);
                globalActiveCalls.SetValue(server, 0);
                connectionActiveCalls.SetValue(connection, 0);
                admissionResult = -1;
                phase.SignalAndWait();
                Thread.SpinWait(delay);
                setState(server, draining);
                var drainObservedZeroCalls = (int)globalActiveCalls.GetValue(server)! == 0;
                phase.SignalAndWait();
                if (drainObservedZeroCalls && admissionResult == acquired)
                    witnessedLateAdmission = true;
            }
        }
        worker.Join();

        globalActiveCalls.SetValue(server, 0);
        connectionActiveCalls.SetValue(connection, 0);
        setState(server, draining);
        Ensure(!witnessedLateAdmission,
            "Stop observed zero active calls but a racing request was still admitted after the drain boundary");
        Ensure((int)globalActiveCalls.GetValue(server)! == 0, "global active-call counter rollback");
        Ensure(connection.ActiveCalls == 0, "connection active-call counter rollback");
        await connection.CloseAsync();
    }

    [Test]
    public async Task FailedInvocationShouldPreserveLeaseCleanupFailure()
    {
        await using var server = SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        await using var session = new RpcSession(new TestTransportConnection());
        var lease = new ServiceLease(
            new ThrowingService(),
            new ThrowingScope(),
            disposeService: true);
        var method = typeof(SharpLinkServer).GetMethod(
            "InvokeServiceWithLeaseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find leased invocation path");

        Exception failure;
        try
        {
            var invocation = (ValueTask)method.Invoke(server,
            [
                new ThrowingStub(),
                lease,
                session,
                1L,
                1L,
                ReadOnlySequence<byte>.Empty,
                null,
                CancellationToken.None,
                new SharpLinkCallContextSnapshot(session.Id, authentication: null),
                false
            ])!;
            await invocation;
            throw new Exception("expected leased invocation failure");
        }
        catch (Exception exception)
        {
            failure = exception is TargetInvocationException { InnerException: { } inner }
                ? inner
                : exception;
        }

        Ensure(ContainsMessage(failure, "handler failed"),
            "leased invocation must retain the handler failure");
        Ensure(ContainsMessage(failure, "lease cleanup failed"),
            "leased invocation must retain the lease cleanup failure");
    }

    [Test]
    public async Task SessionShutdownShouldNotHideAnUnexpectedSiblingCleanupFailure()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var connections = (ConcurrentDictionary<string, ServerConnectionState>)(
            typeof(SharpLinkServer).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server)!);
        var unexpectedTransport = new ThrowingTransportConnection(
            "unexpected",
            new InvalidOperationException("unexpected sibling session cleanup failed"));
        var unexpected = new ServerConnectionState(
            new RpcSession(unexpectedTransport),
            new RuntimeConcurrencyOptions(),
            CancellationToken.None);
        connections.TryAdd(unexpected.Session.Id, unexpected);

        var expectedTransports = new List<ThrowingTransportConnection>();
        for (var index = 0; index < 64 && ReferenceEquals(connections.Values.First(), unexpected); index++)
        {
            var transport = new ThrowingTransportConnection(
                $"expected-{index}",
                new IOException("expected session transport closure"));
            expectedTransports.Add(transport);
            var connection = new ServerConnectionState(
                new RpcSession(transport),
                new RuntimeConcurrencyOptions(),
                CancellationToken.None);
            connections.TryAdd(connection.Session.Id, connection);
        }
        Ensure(!ReferenceEquals(connections.Values.First(), unexpected),
            "the expected close must be first in the deterministic shutdown snapshot");

        var disposeSessions = CreatePrivateCall<Func<SharpLinkServer, Task>>(
            typeof(SharpLinkServer).GetMethod(
                "DisposeAllSessionsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server session shutdown path"));
        Exception? failure = null;
        try
        {
            await disposeSessions(server);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is not null && ContainsMessage(failure, "unexpected sibling session cleanup failed"),
            "an expected sibling close must not hide an unexpected session cleanup failure");
        Ensure(unexpectedTransport.DisposeCount == 1 &&
               expectedTransports.All(static transport => transport.DisposeCount == 1),
            "parallel session shutdown must still dispose every transport");
    }

    [Test]
    public async Task FailedErrorResponseEnqueueShouldStillReleaseTheServerCall()
    {
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration()
            .UseRuntime(static options => options.FlowControl.MaxSendQueueBytes = 1)
            .UseTransport(new IdleListener())
            .Build();
        var runtimeContext = (SharpLinkRuntimeContext)(
            typeof(SharpLinkServer).GetField("_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server)!);
        var input = new Pipe();
        var output = new BlockingFlushPipeWriter();
        await using var session = new RpcSession(
            "error-response-capacity",
            input.Reader,
            output,
            static () => { },
            static () => true);
        session.BindRuntimeContext(runtimeContext);
        var connection = new ServerConnectionState(
            session,
            new RuntimeConcurrencyOptions(),
            CancellationToken.None);
        Ensure(connection.MarkReady(null), "connection ready");
        var stub = new SynchronouslyThrowingStub();
        var registration = ServiceRegistration.CreateSingleton(
            typeof(ThrowingService),
            stub,
            new ThrowingService(),
            ownsService: false);
        typeof(SharpLinkServer).GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, new Dictionary<long, ServiceRegistration> { [stub.InterfaceHash] = registration }
                .ToFrozenDictionary());
        var setState = CreateInterlockedInt32Setter<SharpLinkServer>("_state");
        const int running = 2;
        setState(server, running);
        session.SendHealthCheck(99);
        await output.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var request = new byte[sizeof(long) * 2];
        BinaryPrimitives.WriteInt64LittleEndian(request, stub.InterfaceHash);
        BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(sizeof(long)), 1);
        var dispatch = typeof(SharpLinkServer).GetMethod(
            "DispatchRpcAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server RPC dispatch path");
        var globalActiveCalls = typeof(SharpLinkServer).GetField(
            "_globalActiveCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find global active-call counter");
        var connectionActiveCalls = typeof(ServerConnectionState).GetField(
            "_activeCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find connection active-call counter");

        Exception? failure = null;
        try
        {
            try
            {
                var operation = (ValueTask)dispatch.Invoke(server,
                [
                    connection,
                    1L,
                    ProtocolV2FrameFlags.None,
                    new ReadOnlySequence<byte>(request),
                    connection.CallCancellations,
                    CancellationToken.None,
                    null,
                    false
                ])!;
                await operation;
            }
            catch (Exception exception)
            {
                failure = exception is TargetInvocationException { InnerException: { } inner }
                    ? inner
                    : exception;
            }

            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                $"the bounded send queue should reject the error response, actual: {failure}");
            Ensure((int)globalActiveCalls.GetValue(server)! == 0 && connection.ActiveCalls == 0,
                "a failed error-response enqueue must not leak the Server call admission counters");
        }
        finally
        {
            globalActiveCalls.SetValue(server, 0);
            connectionActiveCalls.SetValue(connection, 0);
            output.ReleaseFlush();
            await connection.CloseAsync();
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task FrameworkJoinShouldNotHideAnUnexpectedSiblingFailure()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var expected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mixed = Task.WhenAll(expected.Task, unexpected.Task);
        var track = typeof(SharpLinkServer).GetMethod(
            "TrackFrameworkTask",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server framework task tracker");
        track.Invoke(server, [mixed]);
        var wait = CreatePrivateCall<Func<SharpLinkServer, Task>>(
            typeof(SharpLinkServer).GetMethod(
                "WaitForFrameworkTasksAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server framework task join"));
        var joined = wait(server);
        await Task.Yield();
        expected.TrySetException(new IOException("expected framework transport closure"));
        unexpected.TrySetException(new InvalidOperationException("unexpected framework sibling failure"));

        Exception? failure = null;
        try
        {
            await joined;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is not null && ContainsMessage(failure, "unexpected framework sibling failure"),
            "an expected framework close must not hide an unexpected sibling task failure");
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (ContainsMessage(inner, message))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private static TDelegate CreatePrivateCall<TDelegate>(MethodInfo method)
        where TDelegate : Delegate
    {
        var invoke = typeof(TDelegate).GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(static parameter => parameter.ParameterType).ToArray();
        var dynamicMethod = new DynamicMethod(
            $"Call_{method.Name}",
            invoke.ReturnType,
            parameters,
            typeof(SharpLinkServerInvocationTests).Module,
            skipVisibility: true);
        var generator = dynamicMethod.GetILGenerator();
        for (var index = 0; index < parameters.Length; index++)
            generator.Emit(OpCodes.Ldarg, index);
        generator.Emit(OpCodes.Call, method);
        generator.Emit(OpCodes.Ret);
        return dynamicMethod.CreateDelegate<TDelegate>();
    }

    private static Action<TTarget, int> CreateInterlockedInt32Setter<TTarget>(string fieldName)
    {
        var field = typeof(TTarget).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find field {fieldName}");
        var dynamicMethod = new DynamicMethod(
            $"Set_{fieldName}",
            typeof(void),
            [typeof(TTarget), typeof(int)],
            typeof(SharpLinkServerInvocationTests).Module,
            skipVisibility: true);
        var generator = dynamicMethod.GetILGenerator();
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldflda, field);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Call, typeof(Interlocked).GetMethod(
            nameof(Interlocked.Exchange),
            [typeof(int).MakeByRefType(), typeof(int)])!);
        generator.Emit(OpCodes.Pop);
        generator.Emit(OpCodes.Ret);
        return dynamicMethod.CreateDelegate<Action<TTarget, int>>();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingTransportConnection(string id, Exception failure) : ITransportConnection
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private int _disposeCount;

        public string Id { get; } = id;
        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _output.Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.FromException(failure);
        }
    }

    private sealed class ThrowingService : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
            => ValueTask.FromException(new InvalidOperationException("lease cleanup failed"));
    }

    private sealed class ThrowingScope : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new EmptyServiceProvider();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class ThrowingStub : IRpcStub
    {
        public long InterfaceHash => 1;

        public ValueTask InvokeNoReturnAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => Fail();

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Fail();

        public ValueTask InvokeAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output) => Fail();

        public ValueTask InvokeCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output,
            CancellationToken cancellationToken) => Fail();

        private static ValueTask Fail()
            => ValueTask.FromException(new InvalidOperationException("handler failed"));
    }

    private sealed class SynchronouslyThrowingStub : IRpcStub
    {
        public long InterfaceHash => 7;

        public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor descriptor)
        {
            descriptor = new RpcMethodDescriptor(
                InterfaceHash,
                methodHash,
                RpcMethodKind.Unary,
                HasResponsePayload: false,
                HasClientStreams: false,
                HasMethodTimeout: false,
                MethodTimeout: null);
            return true;
        }

        public ValueTask InvokeNoReturnAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => Throw();

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Throw();

        public ValueTask InvokeAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output) => Throw();

        public ValueTask InvokeCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output,
            CancellationToken cancellationToken) => Throw();

        private static ValueTask Throw()
            => throw new InvalidOperationException("handler failed synchronously");
    }

    private sealed class BlockingFlushPipeWriter : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly TaskCompletionSource<FlushResult> _flush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FlushStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override void CancelPendingFlush() => _flush.TrySetResult(new FlushResult(true, false));
        public override void Complete(Exception? exception = null) => ReleaseFlush();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushStarted.TrySetResult();
            return new ValueTask<FlushResult>(_flush.Task.WaitAsync(cancellationToken));
        }
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);

        internal void ReleaseFlush()
            => _flush.TrySetResult(new FlushResult(isCanceled: false, isCompleted: false));
    }
}
