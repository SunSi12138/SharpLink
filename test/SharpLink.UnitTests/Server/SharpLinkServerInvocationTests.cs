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
    public async Task FullErrorResponseQueueShouldWaitForCapacityWithoutClosingConnection()
    {
        var output = new BlockingFlushPipeWriter();
        await using var harness = new ServerDispatchHarness(
            new SynchronouslyThrowingStub(), output, maxSendQueueBytes: 1);
        harness.Session.SendHealthCheck(99);
        await output.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var operation = harness.Dispatch(1, ProtocolV2FrameFlags.None);

        Ensure(!operation.IsCompleted,
            "a full response queue must move synchronous error dispatch to the capacity-wait slow path");
        Ensure(harness.Session.IsConnected,
            "response backpressure must not close an otherwise healthy session");
        Ensure(harness.GlobalActiveCalls == 1 && harness.Connection.ActiveCalls == 1,
            "the error response must retain both admission slots while waiting for queue capacity");

        output.ReleaseFlush();
        await operation.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(harness.Session.IsConnected,
            "the session must remain usable after deferred error-response admission");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "deferred error-response completion must release both call counters");
        EnsureResponseFrame(
            output.WrittenMemory,
            harness.Session.RuntimeContext.Protocol,
            requestId: 1,
            expectedError: SharpLinkErrorCode.Internal,
            expectedPayloadByte: null);
    }

    [Test]
    public async Task FullPayloadResponseQueueShouldWaitForCapacityWithoutClosingConnection()
    {
        var output = new BlockingFlushPipeWriter();
        await using var harness = new ServerDispatchHarness(
            new SynchronouslyRespondingStub(), output, maxSendQueueBytes: 1);
        harness.Session.SendHealthCheck(99);
        await output.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var operation = harness.Dispatch(2, ProtocolV2FrameFlags.HasReturn);

        Ensure(!operation.IsCompleted,
            "a full response queue must move synchronous payload dispatch to the capacity-wait slow path");
        Ensure(harness.Session.IsConnected,
            "payload-response backpressure must not close an otherwise healthy session");
        Ensure(harness.GlobalActiveCalls == 1 && harness.Connection.ActiveCalls == 1,
            "the payload response must retain both admission slots while waiting for queue capacity");

        output.ReleaseFlush();
        await operation.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(harness.Session.IsConnected,
            "the session must remain usable after deferred payload-response admission");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "deferred payload-response completion must release both call counters");
        EnsureResponseFrame(
            output.WrittenMemory,
            harness.Session.RuntimeContext.Protocol,
            requestId: 2,
            expectedError: null,
            expectedPayloadByte: SynchronouslyRespondingStub.ResponseByte);
    }

    [Test]
    public async Task AvailableResponseQueueShouldKeepSynchronousDispatchFastPath()
    {
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(
            new SynchronouslyRespondingStub(), output.Writer, maxSendQueueBytes: 1024);

        var operation = harness.Dispatch(3, ProtocolV2FrameFlags.HasReturn);

        Ensure(operation.IsCompletedSuccessfully,
            "an available response queue must preserve synchronous dispatch completion");
        await operation;
        Ensure(harness.Session.IsConnected, "the synchronous fast path must keep the session healthy");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "the synchronous fast path must release both call counters before returning");

        await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        EnsureResponseFrame(
            read.Buffer,
            harness.Session.RuntimeContext.Protocol,
            requestId: 3,
            expectedError: null,
            expectedPayloadByte: SynchronouslyRespondingStub.ResponseByte);
        output.Reader.AdvanceTo(read.Buffer.End);
        await output.Reader.CompleteAsync();
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

    private static void EnsureResponseFrame(
        ReadOnlyMemory<byte> bytes,
        SharpLinkProtocolOptions limits,
        ulong requestId,
        SharpLinkErrorCode? expectedError,
        byte? expectedPayloadByte)
        => EnsureResponseFrame(
            new ReadOnlySequence<byte>(bytes),
            limits,
            requestId,
            expectedError,
            expectedPayloadByte);

    private static void EnsureResponseFrame(
        ReadOnlySequence<byte> bytes,
        SharpLinkProtocolOptions limits,
        ulong requestId,
        SharpLinkErrorCode? expectedError,
        byte? expectedPayloadByte)
    {
        var remaining = bytes;
        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, limits, out var header, out var payload))
        {
            if (header.RequestId != requestId)
                continue;

            Ensure(header.Type == ProtocolV2FrameType.Response, "dispatch must emit a response frame");
            if (expectedError is { } errorCode)
            {
                Ensure((header.Flags & ProtocolV2FrameFlags.Error) != 0,
                    "service failure must emit an error response");
                var error = ProtocolV2PayloadCodec.ReadError(payload, header.Flags, limits.MaxErrorMessageBytes);
                Ensure(error.Code == errorCode, "deferred response must preserve the mapped service error");
            }
            else
            {
                Ensure(header.Flags == ProtocolV2FrameFlags.None,
                    "successful response must not carry error flags");
                Ensure(payload.Length == 1 && payload.FirstSpan[0] == expectedPayloadByte,
                    "successful response must preserve its serialized payload");
            }
            return;
        }

        throw new Exception($"response frame {requestId} was not emitted");
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

    private sealed class SynchronouslyRespondingStub : IRpcStub
    {
        internal const byte ResponseByte = 0x2A;
        public long InterfaceHash => 8;

        public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor descriptor)
        {
            descriptor = new RpcMethodDescriptor(
                InterfaceHash,
                methodHash,
                RpcMethodKind.Unary,
                HasResponsePayload: true,
                HasClientStreams: false,
                HasMethodTimeout: false,
                MethodTimeout: null);
            return true;
        }

        public ValueTask InvokeNoReturnAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output)
        {
            output.Write([ResponseByte]);
            return ValueTask.CompletedTask;
        }

        public ValueTask InvokeCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output,
            CancellationToken cancellationToken)
            => InvokeAsync(service, session, methodHash, requestId, args, output);
    }

    private sealed class ServerDispatchHarness : IAsyncDisposable
    {
        private static readonly MethodInfo DispatchMethod = typeof(SharpLinkServer).GetMethod(
            "DispatchRpcAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server RPC dispatch path");
        private static readonly FieldInfo GlobalActiveCallsField = typeof(SharpLinkServer).GetField(
            "_globalActiveCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find global active-call counter");
        private static readonly FieldInfo ConnectionActiveCallsField = typeof(ServerConnectionState).GetField(
            "_activeCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find connection active-call counter");
        private static readonly Action<SharpLinkServer, int> SetServerState =
            CreateInterlockedInt32Setter<SharpLinkServer>("_state");

        private readonly Pipe _input = new();
        private readonly PipeWriter _output;
        private readonly IRpcStub _stub;

        internal ServerDispatchHarness(IRpcStub stub, PipeWriter output, int maxSendQueueBytes)
        {
            _stub = stub;
            _output = output;
            Server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .DisableAutomaticServiceRegistration()
                .UseRuntime(options => options.FlowControl.MaxSendQueueBytes = maxSendQueueBytes)
                .UseTransport(new IdleListener())
                .Build();
            var runtimeContext = (SharpLinkRuntimeContext)(
                typeof(SharpLinkServer).GetField(
                    "_runtimeContext",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Server)!);
            Session = new RpcSession(
                "response-capacity",
                _input.Reader,
                output,
                static () => { },
                static () => true);
            Session.BindRuntimeContext(runtimeContext);
            Connection = new ServerConnectionState(
                Session,
                new RuntimeConcurrencyOptions(),
                CancellationToken.None);
            Ensure(Connection.MarkReady(null), "connection ready");
            var registration = ServiceRegistration.CreateSingleton(
                typeof(ThrowingService),
                stub,
                new ThrowingService(),
                ownsService: false);
            typeof(SharpLinkServer).GetField(
                    "_services",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Server, new Dictionary<long, ServiceRegistration>
                {
                    [stub.InterfaceHash] = registration
                }.ToFrozenDictionary());
            const int running = 2;
            SetServerState(Server, running);
        }

        internal SharpLinkServer Server { get; }
        internal RpcSession Session { get; }
        internal ServerConnectionState Connection { get; }
        internal int GlobalActiveCalls => (int)GlobalActiveCallsField.GetValue(Server)!;

        internal ValueTask Dispatch(long requestId, ProtocolV2FrameFlags flags)
        {
            var request = new byte[sizeof(long) * 2];
            BinaryPrimitives.WriteInt64LittleEndian(request, _stub.InterfaceHash);
            BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(sizeof(long)), 1);
            return (ValueTask)DispatchMethod.Invoke(Server,
            [
                Connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(request),
                Connection.CallCancellations,
                CancellationToken.None,
                null,
                false
            ])!;
        }

        public async ValueTask DisposeAsync()
        {
            GlobalActiveCallsField.SetValue(Server, 0);
            ConnectionActiveCallsField.SetValue(Connection, 0);
            if (_output is BlockingFlushPipeWriter blocking)
                blocking.ReleaseFlush();
            await Connection.CloseAsync();
            await Server.DisposeAsync();
            await _input.Writer.CompleteAsync();
        }
    }

    private sealed class BlockingFlushPipeWriter : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly TaskCompletionSource<FlushResult> _flush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FlushStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;

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
