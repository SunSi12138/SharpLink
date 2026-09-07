using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpLink.IntegrationTests;

internal sealed class ExtensionFaultHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _serverCancellation;
    private readonly Task _serverTask;
    private readonly ISharpLinkServer _server;
    private readonly ServiceProvider? _serviceProvider;

    private ExtensionFaultHarness(
        ISharpLinkServer server,
        Task serverTask,
        CancellationTokenSource serverCancellation,
        ISharpLinkClient client,
        ServiceProvider? serviceProvider,
        string initialSessionId)
    {
        _server = server;
        _serverTask = serverTask;
        _serverCancellation = serverCancellation;
        Client = client;
        _serviceProvider = serviceProvider;
        InitialSessionId = initialSessionId;
    }

    internal ISharpLinkClient Client { get; }
    internal IExtensionFaultService Service => Client.Get<IExtensionFaultService>();
    internal ISharpLinkServer Server => _server;
    internal string InitialSessionId { get; }

    internal static async Task<ExtensionFaultHarness> CreateAsync(ExtensionFaultHarnessOptions? options = null)
    {
        options ??= new ExtensionFaultHarnessOptions();
        var cancellation = new CancellationTokenSource();
        ServiceProvider? provider = null;
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        if (options.ServiceFactory is null)
        {
            serverBuilder.ReplaceService<IExtensionFaultService>(
                options.ServiceInstance ?? new ExtensionFaultService());
        }
        else
        {
            provider = new ServiceCollection().BuildServiceProvider();
            serverBuilder
                .UseServiceProvider(provider)
                .ReplaceService<IExtensionFaultService>(options.ServiceFactory, options.ServiceLifetime);
        }

        foreach (var interceptor in options.ServerInterceptors)
            serverBuilder.AddInterceptor(interceptor);
        if (options.EnableAdmissionControl)
        {
            serverBuilder.UseAdmissionControl(admission =>
                admission.Global.UseConcurrency(8));
        }

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        var clientBuilder = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .UseEndpoint(
                new SharpLinkEndpoint
                {
                    Id = "fault-matrix",
                    Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
                },
                SharpLinkTransportFactories.Sockets());
        foreach (var interceptor in options.ClientInterceptors)
            clientBuilder.AddInterceptor(interceptor);
        if (options.EndpointAdmissionPolicy is not null)
            clientBuilder.UseEndpointAdmission(options.EndpointAdmissionPolicy);
        if (options.RetryPolicy is not null)
        {
            clientBuilder.UseRetry(options.RetryPolicy);
            clientBuilder.UseRetry(retry =>
            {
                retry.MaxAttempts = 3;
                retry.InitialBackoff = TimeSpan.Zero;
                retry.MaxBackoff = TimeSpan.Zero;
                retry.JitterRatio = 0;
            });
        }
        if (options.LoggerFactory is not null)
            clientBuilder.UseLoggerFactory(options.LoggerFactory);

        var client = clientBuilder.Build();
        try
        {
            await client.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            var initialSessionId = options.SkipInitialSessionProbe
                ? string.Empty
                : await client.Get<IExtensionFaultService>()
                    .GetSessionIdAsync()
                    .ConfigureAwait(false);
            return new ExtensionFaultHarness(
                server, serverTask, cancellation, client, provider, initialSessionId);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            provider?.Dispose();
            cancellation.Dispose();
            throw;
        }
    }

    internal async Task AssertClientIdleAsync(string scenario)
    {
        var concrete = (SharpLinkClient)Client;
        var started = Stopwatch.GetTimestamp();
        while (concrete.PendingCallCount != 0 ||
               concrete.ActiveClientCallCount != 0 ||
               concrete.ActiveClientStreamCount != 0)
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(3))
            {
                throw new Exception(
                    $"assert failed: {scenario}: client resources did not return to zero; " +
                    $"pending={concrete.PendingCallCount} calls={concrete.ActiveClientCallCount} " +
                    $"streams={concrete.ActiveClientStreamCount}");
            }
            await Task.Yield();
        }
    }

    internal async Task AssertReusableAsync(string scenario, bool requireSameSession = true)
    {
        await AssertClientIdleAsync(scenario).ConfigureAwait(false);
        var service = Service;
        var session = await service.GetSessionIdAsync().ConfigureAwait(false);
        if (requireSameSession && InitialSessionId.Length != 0 &&
            !string.Equals(session, InitialSessionId, StringComparison.Ordinal))
        {
            throw new Exception(
                $"assert failed: {scenario}: connection changed from {InitialSessionId} to {session}");
        }
        var result = await service.EchoAsync(41).ConfigureAwait(false);
        if (result != 42)
            throw new Exception($"assert failed: {scenario}: healthy reuse returned {result}");
        await AssertClientIdleAsync(scenario + " after healthy reuse").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await _serverCancellation.CancelAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
        _serviceProvider?.Dispose();
        _serverCancellation.Dispose();
    }
}

internal sealed class ExtensionFaultHarnessOptions
{
    internal IExtensionFaultService? ServiceInstance { get; init; }
    internal Func<IServiceProvider, IExtensionFaultService>? ServiceFactory { get; init; }
    internal SharpLinkServiceLifetime ServiceLifetime { get; init; } = SharpLinkServiceLifetime.Call;
    internal IReadOnlyList<ISharpLinkClientInterceptor> ClientInterceptors { get; init; } = [];
    internal IReadOnlyList<ISharpLinkServerInterceptor> ServerInterceptors { get; init; } = [];
    internal ISharpLinkEndpointAdmissionPolicy? EndpointAdmissionPolicy { get; init; }
    internal ISharpLinkRetryPolicy? RetryPolicy { get; init; }
    internal ILoggerFactory? LoggerFactory { get; init; }
    internal bool EnableAdmissionControl { get; init; }
    internal bool SkipInitialSessionProbe { get; init; }
}

[RpcContract]
public interface IExtensionFaultService : IService
{
    [Idempotent]
    [NonCancellable]
    ValueTask<int> EchoAsync(int value);

    [Idempotent]
    [NonCancellable]
    ValueTask<int> FailOnceAsync();

    [NonCancellable]
    ValueTask<string> GetSessionIdAsync();

    [NonCancellable]
    ValueTask<int> ConsumeClientSerializeFaultAsync(ClientSerializeFaultPayload value);

    [NonCancellable]
    ValueTask<int> ConsumeServerDeserializeFaultAsync(ServerDeserializeFaultPayload value);

    [NonCancellable]
    ValueTask<ServerSerializeFaultPayload> ProduceServerSerializeFaultAsync(int value);

    [NonCancellable]
    ValueTask<ClientDeserializeFaultPayload> ProduceClientDeserializeFaultAsync(int value);

    ValueTask<int> UploadAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken = default);

    [NonCancellable]
    IAsyncEnumerable<int> StreamAsync(int count);
}

[RpcService]
public sealed class ExtensionFaultService : IExtensionFaultService, IAsyncDisposable
{
    private readonly bool _throwOnDispose;
    private readonly bool _failStreamAfterFirst;
    private int _failOnce = 1;
    private int _invocations;

    public ExtensionFaultService()
    {
    }

    internal ExtensionFaultService(bool throwOnDispose, bool failStreamAfterFirst = false)
    {
        _throwOnDispose = throwOnDispose;
        _failStreamAfterFirst = failStreamAfterFirst;
    }

    internal int InvocationCount => Volatile.Read(ref _invocations);

    public ValueTask<int> EchoAsync(int value)
    {
        Interlocked.Increment(ref _invocations);
        return ValueTask.FromResult(value + 1);
    }

    public ValueTask<int> FailOnceAsync()
    {
        Interlocked.Increment(ref _invocations);
        return Interlocked.Exchange(ref _failOnce, 0) != 0
            ? ValueTask.FromException<int>(new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "injected retryable service failure"))
            : ValueTask.FromResult(42);
    }

    public ValueTask<string> GetSessionIdAsync()
    {
        Interlocked.Increment(ref _invocations);
        return ValueTask.FromResult(
            SharpLinkCallContext.Current?.SessionId ?? "missing-session");
    }

    public ValueTask<int> ConsumeClientSerializeFaultAsync(ClientSerializeFaultPayload value)
    {
        Interlocked.Increment(ref _invocations);
        return ValueTask.FromResult(value.Value + 1);
    }

    public ValueTask<int> ConsumeServerDeserializeFaultAsync(ServerDeserializeFaultPayload value)
    {
        Interlocked.Increment(ref _invocations);
        return ValueTask.FromResult(value.Value + 1);
    }

    public ValueTask<ServerSerializeFaultPayload> ProduceServerSerializeFaultAsync(int value)
    {
        Interlocked.Increment(ref _invocations);
        return ValueTask.FromResult(new ServerSerializeFaultPayload { Value = value + 1 });
    }

    public ValueTask<ClientDeserializeFaultPayload> ProduceClientDeserializeFaultAsync(int value)
    {
        Interlocked.Increment(ref _invocations);
        return ValueTask.FromResult(new ClientDeserializeFaultPayload { Value = value + 1 });
    }

    public async ValueTask<int> UploadAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocations);
        var sum = 0;
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            sum += value;
        return sum;
    }

    public async IAsyncEnumerable<int> StreamAsync(int count)
    {
        Interlocked.Increment(ref _invocations);
        for (var index = 0; index < count; index++)
        {
            yield return index;
            if (_failStreamAfterFirst && index == 0)
                throw new InvalidOperationException("injected server stream producer failure");
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync()
        => _throwOnDispose
            ? ValueTask.FromException(new InvalidOperationException("injected service disposal failure"))
            : ValueTask.CompletedTask;
}

[RpcCodec(typeof(ClientSerializeFaultPayloadCodec))]
public sealed class ClientSerializeFaultPayload
{
    public int Value { get; set; }
}

[RpcCodec(typeof(ServerDeserializeFaultPayloadCodec))]
public sealed class ServerDeserializeFaultPayload
{
    public int Value { get; set; }
}

[RpcCodec(typeof(ServerSerializeFaultPayloadCodec))]
public sealed class ServerSerializeFaultPayload
{
    public int Value { get; set; }
}

[RpcCodec(typeof(ClientDeserializeFaultPayloadCodec))]
public sealed class ClientDeserializeFaultPayload
{
    public int Value { get; set; }
}

[RpcCodecSemanticIdentity(0x5760000000000001UL, 0xA11CE00000000001UL)]
public sealed class ClientSerializeFaultPayloadCodec : IRpcCodec<ClientSerializeFaultPayload>
{
    private int _remaining = 1;

    public void Serialize(in ClientSerializeFaultPayload value, IBufferWriter<byte> writer)
    {
        if (Interlocked.Exchange(ref _remaining, 0) != 0)
            throw new InvalidOperationException("injected client request serialization failure");
        FaultCodecWire.Write(value.Value, writer);
    }

    public ClientSerializeFaultPayload Deserialize(in ReadOnlySequence<byte> buffer)
        => new() { Value = FaultCodecWire.Read(buffer) };
}

[RpcCodecSemanticIdentity(0x5760000000000002UL, 0xA11CE00000000002UL)]
public sealed class ServerDeserializeFaultPayloadCodec : IRpcCodec<ServerDeserializeFaultPayload>
{
    private int _remaining = 1;

    public void Serialize(in ServerDeserializeFaultPayload value, IBufferWriter<byte> writer)
        => FaultCodecWire.Write(value.Value, writer);

    public ServerDeserializeFaultPayload Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (Interlocked.Exchange(ref _remaining, 0) != 0)
            throw new InvalidOperationException("injected server request deserialization failure");
        return new ServerDeserializeFaultPayload { Value = FaultCodecWire.Read(buffer) };
    }
}

[RpcCodecSemanticIdentity(0x5760000000000003UL, 0xA11CE00000000003UL)]
public sealed class ServerSerializeFaultPayloadCodec : IRpcCodec<ServerSerializeFaultPayload>
{
    private int _remaining = 1;

    public void Serialize(in ServerSerializeFaultPayload value, IBufferWriter<byte> writer)
    {
        if (Interlocked.Exchange(ref _remaining, 0) != 0)
            throw new InvalidOperationException("injected server response serialization failure");
        FaultCodecWire.Write(value.Value, writer);
    }

    public ServerSerializeFaultPayload Deserialize(in ReadOnlySequence<byte> buffer)
        => new() { Value = FaultCodecWire.Read(buffer) };
}

[RpcCodecSemanticIdentity(0x5760000000000004UL, 0xA11CE00000000004UL)]
public sealed class ClientDeserializeFaultPayloadCodec : IRpcCodec<ClientDeserializeFaultPayload>
{
    private int _remaining = 1;

    public void Serialize(in ClientDeserializeFaultPayload value, IBufferWriter<byte> writer)
        => FaultCodecWire.Write(value.Value, writer);

    public ClientDeserializeFaultPayload Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (Interlocked.Exchange(ref _remaining, 0) != 0)
            throw new InvalidOperationException("injected client response deserialization failure");
        return new ClientDeserializeFaultPayload { Value = FaultCodecWire.Read(buffer) };
    }
}

internal static class FaultCodecWire
{
    internal static void Write(int value, IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(sizeof(int));
    }

    internal static int Read(in ReadOnlySequence<byte> buffer)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        buffer.CopyTo(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
}

internal sealed class OneShotClientInterceptorFault(bool afterNext) : ISharpLinkClientInterceptor
{
    private int _remaining = 1;
    internal SharpLinkClientInvocationContext? FailedContext { get; private set; }

    public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
        SharpLinkClientInvocationContext context,
        SharpLinkClientInvocationDelegate next)
    {
        if (Interlocked.Exchange(ref _remaining, 0) == 0)
            return await next(context).ConfigureAwait(false);
        FailedContext = context;
        if (afterNext)
            _ = await next(context).ConfigureAwait(false);
        throw new InvalidOperationException(afterNext
            ? "injected client interceptor failure after next"
            : "injected client interceptor failure before next");
    }
}

internal sealed class RecordingClientInterceptor : ISharpLinkClientInterceptor
{
    internal SharpLinkClientInvocationContext? Context { get; private set; }
    internal int Calls { get; private set; }

    public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
        SharpLinkClientInvocationContext context,
        SharpLinkClientInvocationDelegate next)
    {
        Context = context;
        Calls++;
        return await next(context).ConfigureAwait(false);
    }
}

internal sealed class OneShotServerInterceptorFault(bool afterNext) : ISharpLinkServerInterceptor
{
    private int _remaining = 1;
    internal SharpLinkServerInvocationContext? FailedContext { get; private set; }

    public async ValueTask InvokeAsync(
        SharpLinkServerInvocationContext context,
        SharpLinkServerInvocationDelegate next)
    {
        if (Interlocked.Exchange(ref _remaining, 0) == 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        FailedContext = context;
        if (afterNext)
            await next(context).ConfigureAwait(false);
        throw new SharpLinkException(
            SharpLinkErrorCode.FailedPrecondition,
            afterNext
                ? "injected server interceptor failure after next"
                : "injected server interceptor failure before next");
    }
}

internal sealed class FaultingEndpointAdmissionPolicy(
    bool throwAcquireOnce = false,
    bool throwReport = false) : ISharpLinkEndpointAdmissionPolicy
{
    private int _acquireFault = throwAcquireOnce ? 1 : 0;
    private int _acquireCount;
    private int _reportCount;
    private long _token;

    internal int AcquireCount => Volatile.Read(ref _acquireCount);
    internal int ReportCount => Volatile.Read(ref _reportCount);

    public SharpLinkEndpointAdmissionDecision TryAcquire(
        in SharpLinkEndpointCandidate endpoint,
        in RpcMethodDescriptor method)
    {
        _ = endpoint;
        _ = method;
        Interlocked.Increment(ref _acquireCount);
        if (Interlocked.Exchange(ref _acquireFault, 0) != 0)
            throw new InvalidOperationException("injected admission acquire failure");
        return new SharpLinkEndpointAdmissionDecision(
            true,
            Interlocked.Increment(ref _token),
            RetryAfter: null);
    }

    public void Report(in SharpLinkEndpointOutcome outcome, long token)
    {
        _ = outcome;
        _ = token;
        Interlocked.Increment(ref _reportCount);
        if (throwReport)
            throw new InvalidOperationException("injected admission report failure");
    }
}

internal sealed class ThrowingRetryPolicy : ISharpLinkRetryPolicy
{
    private int _calls;
    internal int Calls => Volatile.Read(ref _calls);

    public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
    {
        _ = context;
        Interlocked.Increment(ref _calls);
        throw new InvalidOperationException("injected retry policy failure");
    }
}

internal sealed class ThrowingLoggerFactory : ILoggerFactory
{
    private sealed class ThrowingLogger : ILogger
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
            _ = logLevel;
            _ = eventId;
            var message = formatter(state, exception);
            if (message.Contains(
                    "endpoint admission policy report failed",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("injected logger failure");
            }
        }
    }

    private static readonly ILogger Logger = new ThrowingLogger();
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => Logger;
    public void Dispose() { }
}

internal sealed class OneShotThrowingAsyncEnumerable(
    bool throwMoveNext,
    bool throwDispose) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
{
    private int _move;
    public int Current { get; private set; }

    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return this;
    }

    public ValueTask<bool> MoveNextAsync()
    {
        var move = Interlocked.Increment(ref _move);
        if (move == 1)
        {
            Current = 7;
            return ValueTask.FromResult(true);
        }
        if (throwMoveNext && move == 2)
            return ValueTask.FromException<bool>(new InvalidOperationException("injected MoveNextAsync failure"));
        return ValueTask.FromResult(false);
    }

    public ValueTask DisposeAsync()
        => throwDispose
            ? ValueTask.FromException(new InvalidOperationException("injected DisposeAsync failure"))
            : ValueTask.CompletedTask;
}

internal sealed class ThrowingMeterScope : IDisposable
{
    private readonly MeterListener _listener = new();
    private int _remaining = 1;

    internal ThrowingMeterScope(string instrumentName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, SharpLinkTelemetry.Meter) &&
                string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (Interlocked.Exchange(ref _remaining, 0) != 0)
                throw new InvalidOperationException("injected MeterListener failure");
        });
        _listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            if (Interlocked.Exchange(ref _remaining, 0) != 0)
                throw new InvalidOperationException("injected MeterListener failure");
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();
}

internal sealed class ThrowingActivityScope : IDisposable
{
    private readonly ActivityListener _listener;
    private int _remaining = 1;

    internal ThrowingActivityScope()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, SharpLinkTelemetry.ClientActivitySource),
            Sample = Sample,
            SampleUsingParentId = SampleUsingParentId
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
    {
        _ = options;
        if (Interlocked.Exchange(ref _remaining, 0) != 0)
            throw new InvalidOperationException("injected ActivityListener sampler failure");
        return ActivitySamplingResult.PropagationData;
    }

    private ActivitySamplingResult SampleUsingParentId(ref ActivityCreationOptions<string> options)
    {
        _ = options;
        if (Interlocked.Exchange(ref _remaining, 0) != 0)
            throw new InvalidOperationException("injected ActivityListener sampler failure");
        return ActivitySamplingResult.PropagationData;
    }

    public void Dispose() => _listener.Dispose();
}
