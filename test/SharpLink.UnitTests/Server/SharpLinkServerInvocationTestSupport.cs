using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
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

    private static Task InvokeAwaitDispatchAsync(
        MethodInfo awaitDispatch,
        SharpLinkServer server,
        Exception exception,
        long requestId)
        => (Task)awaitDispatch.Invoke(
            server,
            [ValueTask.FromException(exception), requestId])!;

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

    private static ServerConnectionState CreateConnection(RpcSession session)
        => new(
            session,
            new RpcSessionGeneratedServerBridge(session),
            CreateCallCancellations(),
            CancellationToken.None,
            RpcSessionTestFixture.RuntimeContext.TimeProvider);

    private static StripedLongMap<ServerCallCancellationState> CreateCallCancellations(
        SharpLinkRuntimeContext? runtimeContext = null)
        => new((runtimeContext ?? RpcSessionTestFixture.RuntimeContext).Concurrency);

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

    private static async Task YieldUntilAsync(Func<bool> condition, string failureMessage)
    {
        for (var attempt = 0; attempt < 128 && !condition(); attempt++)
            await Task.Yield();
        Ensure(condition(), failureMessage);
    }

    private static Task GetConnectionCompletionTask(ServerConnectionState connection)
        => connection.SessionTask;

    private sealed class CaptureLoggerFactory : ILoggerFactory
    {
        private readonly Lock _gate = new();

        internal List<LogEntry> ErrorEntries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CaptureLogger(CaptureLoggerFactory owner) : ILogger
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
                if (logLevel != LogLevel.Error)
                    return;
                lock (owner._gate)
                    owner.ErrorEntries.Add(new LogEntry(eventId, exception));
            }
        }
    }

    private readonly record struct LogEntry(EventId EventId, Exception? Exception);

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingListener : IServerTransportListener
    {
        internal TaskCompletionSource AcceptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            AcceptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled accept must not continue.");
        }

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

        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => Fail();

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Fail();

        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => Fail();

        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
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

        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => Throw();

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Throw();

        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => Throw();

        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
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

        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
        {
            output.Write([ResponseByte]);
            return ValueTask.CompletedTask;
        }

        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken)
            => InvokeAsync(service, bridge, methodHash, requestId, args, output);
    }

    private sealed class CancelThenRecoverStub : IRpcStub
    {
        private int _invocationCount;

        internal TaskCompletionSource FirstInvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        public long InterfaceHash => 9;

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

        public ValueTask InvokeNoReturnAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args)
            => throw new InvalidOperationException("The test method must use cooperative cancellation.");

        public ValueTask InvokeNoReturnCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _invocationCount) != 1)
                return ValueTask.CompletedTask;

            FirstInvocationStarted.TrySetResult();
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }

        public ValueTask InvokeAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output)
            => throw new NotSupportedException();

        public ValueTask InvokeCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ServerDispatchHarness : IAsyncDisposable
    {
        private static readonly MethodInfo DispatchMethod = typeof(SharpLinkServer).GetMethod(
            "DispatchRpcAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server RPC dispatch path");
        private static readonly FieldInfo CallAdmissionField = typeof(SharpLinkServer).GetField(
            "_callAdmission",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server call-admission owner");
        private static readonly FieldInfo GlobalActiveCallsField = typeof(ServerCallAdmission).GetField(
            "_globalActiveCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find admission active-call counter");
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
            Server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration()
                .UseRuntime(options => options.FlowControl.MaxSendQueueBytes = maxSendQueueBytes)
                .UseTransport(new IdleListener())
                .Build();
            var runtimeContext = (SharpLinkRuntimeContext)(
                typeof(SharpLinkServer).GetField(
                    "_runtimeContext",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Server)!);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                "response-capacity",
                _input.Reader,
                output,
                RpcSessionTestFixture.ServerOptions(runtimeContext));
            Connection = new ServerConnectionState(
                Session,
                new RpcSessionGeneratedServerBridge(Session),
                CreateCallCancellations(runtimeContext),
                CancellationToken.None,
                runtimeContext.TimeProvider);
            Ensure(Connection.MarkReady(null), "connection ready");
            var registration = ServiceRegistration.CreateSingleton(
                typeof(ThrowingService),
                stub,
                new ThrowingService(),
                ownsService: false);
            var serviceRegistry = (ServerServiceModuleRegistry)typeof(SharpLinkServer).GetField(
                    "_serviceModuleRegistry",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Server)!;
            serviceRegistry.PublishServices(new Dictionary<long, ServiceRegistration>
            {
                [stub.InterfaceHash] = registration
            }.ToFrozenDictionary());
            const int running = 2;
            SetServerState(Server, running);
        }

        internal SharpLinkServer Server { get; }
        internal RpcSession Session { get; }
        internal ServerConnectionState Connection { get; }
        internal int GlobalActiveCalls
            => (int)GlobalActiveCallsField.GetValue(CallAdmissionField.GetValue(Server))!;

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
                null,
                (flags & ProtocolV2FrameFlags.Cancellable) != 0,
                null
            ])!;
        }

        public async ValueTask DisposeAsync()
        {
            GlobalActiveCallsField.SetValue(CallAdmissionField.GetValue(Server), 0);
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
