namespace SharpLink.IntegrationTests;

public partial class IntegrationBehaviorTests
{






    private static Exception? DeserializeMutatedGeneratedSemantic(
        uint fieldId,
        Action<byte[], int, int> mutate)
    {
        var payload = SerializeGeneratedSemantic();
        var field = FindGeneratedSemanticField(payload, fieldId);
        mutate(payload, field.Offset, field.Length);
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<GeneratedSemanticEnvelope>();
        return CaptureException(() => codec.Deserialize(new ReadOnlySequence<byte>(payload)));
    }

    private static byte[] SerializeGeneratedSemantic()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<GeneratedSemanticEnvelope>();
        using var writer = new PooledByteBufferWriter();
        codec.Serialize(new GeneratedSemanticEnvelope(
            true,
            new System.Text.Rune('A'),
            123.45m,
            new DateOnly(2026, 7, 27),
            new DateTime(2026, 7, 27, 12, 34, 56, DateTimeKind.Utc),
            new TimeOnly(12, 34, 56),
            new DateTimeOffset(2026, 7, 27, 12, 34, 56, TimeSpan.FromHours(8))), writer);
        return writer.WrittenMemory.ToArray();
    }

    private static (int Offset, int Length, RpcGeneratedWireType WireType) FindGeneratedSemanticField(
        byte[] payload,
        uint targetFieldId)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
        Ensure(RpcGeneratedCodecWire.ReadPresence(ref reader), "generated semantic envelope presence");
        while (RpcGeneratedCodecWire.TryReadField(ref reader, out var fieldId, out var wireType))
        {
            var fixedLength = wireType switch
            {
                RpcGeneratedWireType.Fixed1 => 1,
                RpcGeneratedWireType.Fixed2 => 2,
                RpcGeneratedWireType.Fixed4 => 4,
                RpcGeneratedWireType.Fixed8 => 8,
                RpcGeneratedWireType.Fixed16 => 16,
                _ => 0
            };
            if (wireType == RpcGeneratedWireType.LengthDelimited)
            {
                var before = checked((int)reader.Consumed);
                var value = RpcGeneratedCodecWire.ReadLengthDelimited(ref reader);
                if (fieldId == targetFieldId)
                    return (before + sizeof(uint), checked((int)value.Length), wireType);
                continue;
            }
            if (fieldId == targetFieldId)
                return (checked((int)reader.Consumed), fixedLength, wireType);
            RpcGeneratedCodecWire.SkipField(ref reader, wireType);
        }
        throw new Exception($"generated semantic field {targetFieldId} was not found");
    }

























































    private static async Task EnsureThrows<TException>(Task task, string name) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should throw {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task StopHarnessAndAssertResourcesAsync(TestHarness harness, string scenario)
    {
        await harness.DisposeClientOnlyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await harness.WaitForServerExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var client = (SharpLinkClient)harness.Client;
        var server = ServerLifecycleResourceInspector.Capture(harness.Server);
        if (!ServerResourcesAreZero(server))
        {
            try
            {
                await WaitUntilAsync(() =>
                    ServerResourcesAreZero(ServerLifecycleResourceInspector.Capture(harness.Server)));
            }
            catch (OperationCanceledException)
            {
                // Preserve the strict assertion below so a timeout reports the final counters.
            }
            server = ServerLifecycleResourceInspector.Capture(harness.Server);
        }
        Ensure(harness.Client.State == SharpLinkConnectionState.Stopped,
            $"{scenario}: client stopped within the bound");
        Ensure(harness.Server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            $"{scenario}: server stopped within the bound");
        Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 &&
               client.ActiveClientStreamCount == 0,
            $"{scenario}: client pending/call/stream resources are zero");
        Ensure(server is
        {
            ActiveCalls: 0,
            Connections: 0,
            RetiredConnections: 0,
            AdmissionPermits: 0,
            AdmissionQueuedCalls: 0,
            AdmissionQueuedBytes: 0
        },
            $"{scenario}: server connection/call/admission resources are zero; actual {server}");
    }

    private static bool ServerResourcesAreZero(ServerLifecycleResourceSnapshot snapshot)
        => snapshot is
        {
            ActiveCalls: 0,
            Connections: 0,
            RetiredConnections: 0,
            AdmissionPermits: 0,
            AdmissionQueuedCalls: 0,
            AdmissionQueuedBytes: 0
        };

    private static async Task EnsureClientStreamProducerFailure(Task task, string name)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception($"assert failed: {name} should fail");
        }
        catch (InvalidOperationException)
        {
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.Internal)
        {
        }
    }

    private static async Task EnsureThrowsSharpLinkFast(
        Task task,
        string name,
        params SharpLinkErrorCode[] errorCodes)
    {
        Ensure(errorCodes.Length > 0, $"{name} expected error codes");
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception($"assert failed: {name} should throw");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {name} did not fail fast");
        }
        catch (SharpLinkException ex)
        {
            Ensure(
                errorCodes.Contains(ex.Code),
                $"{name} error code: expected {string.Join(" or ", errorCodes)}, actual {ex.Code}");
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var item in stream.WithCancellation(ct))
            list.Add(item);
        return list;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> values, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> YieldOneThenWaitAsync(
        int value,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return value;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<int> YieldThenFailAsync(int value)
    {
        yield return value;
        await Task.Yield();
        throw new InvalidOperationException("injected client stream producer failure");
    }

    private sealed class RecordingServerStreamExceptionMapper : IRpcExceptionMapper
    {
        private readonly Lock _gate = new();
        private readonly List<SharpLinkErrorCode?> _mappedCodes = [];

        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            if (context.Method.Kind == RpcMethodKind.ServerStreaming)
            {
                lock (_gate)
                    _mappedCodes.Add((exception as SharpLinkException)?.Code);
            }

            if (exception is SharpLinkException sharpLinkException)
                return sharpLinkException;
            return exception is OperationCanceledException
                ? new SharpLinkException(SharpLinkErrorCode.Cancelled, "The server call was cancelled.", exception)
                : new SharpLinkException(SharpLinkErrorCode.Internal, "Internal service error.", exception);
        }

        public SharpLinkErrorCode?[] GetMappedCodes()
        {
            lock (_gate)
                return [.. _mappedCodes];
        }
    }

    private sealed class SequencedTenantMetadataInterceptor : ISharpLinkClientInterceptor
    {
        private int _invocationCount;

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            var invocation = Interlocked.Increment(ref _invocationCount);
            var tenant = invocation <= 2 ? "a" : "b";
            context.Metadata = new SharpLinkMetadata(
                new KeyValuePair<string, string>("tenant", tenant));
            return next(context);
        }
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _serverDisposed;
        private bool _clientDisposed;

        public ISharpLinkClient Client { get; }
        internal ISharpLinkServer Server => _server;

        private TestHarness(
            ISharpLinkServer server,
            Task serverTask,
            CancellationTokenSource serverCts,
            ISharpLinkClient client)
        {
            _server = server;
            _serverTask = serverTask;
            _serverCts = serverCts;
            Client = client;
        }

        public static async Task<TestHarness> CreateAsync(
            TimeSpan? requestTimeout = null,
            Func<Type, IRpcCodec?>? codecResolver = null,
            Action<SharpLinkRuntimeOptions>? runtimeConfigure = null,
            Action<SharpLinkConnectionPoolOptions>? poolConfigure = null,
            bool disableRequestTimeout = false,
            bool useSharedMemory = false,
            Action<SharpLinkRuntimeOptions>? serverRuntimeConfigure = null,
            Action<SharpLinkRuntimeOptions>? clientRuntimeConfigure = null,
            Action<SharpLinkServerBuilder>? serverConfigure = null,
            ISharpLinkClientInterceptor? clientInterceptor = null)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (codecResolver is not null)
                serverBuilder.UseSerializer(codecResolver);
            if (runtimeConfigure is not null)
                serverBuilder.UseRuntime(runtimeConfigure);
            if (serverRuntimeConfigure is not null)
                serverBuilder.UseRuntime(serverRuntimeConfigure);
            serverConfigure?.Invoke(serverBuilder);

            var sharedMemoryName = $"sharplink-behavior-{Guid.NewGuid():N}";
            var port = 0;
            if (useSharedMemory)
                serverBuilder.UseSharedMemory(sharedMemoryName);
            else
            {
                serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
                port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            }
            var server = serverBuilder.Build();

            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }, CancellationToken.None);

            var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (codecResolver is not null)
                clientBuilder.UseSerializer(codecResolver);
            if (useSharedMemory)
                clientBuilder.UseSharedMemory(sharedMemoryName);
            else
                clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
            if (runtimeConfigure is not null)
                clientBuilder.UseRuntime(runtimeConfigure);
            if (clientRuntimeConfigure is not null)
                clientBuilder.UseRuntime(clientRuntimeConfigure);
            if (poolConfigure is not null)
                clientBuilder.UseConnectionPool(poolConfigure);
            if (clientInterceptor is not null)
                clientBuilder.AddInterceptor(clientInterceptor);

            if (disableRequestTimeout)
                clientBuilder.DisableRequestTimeout();
            else if (requestTimeout is { } timeout)
                clientBuilder.UseRequestTimeout(timeout);

            var client = clientBuilder.Build();
            await client.ConnectAsync();

            return new TestHarness(server, serverTask, cts, client);
        }

        public async ValueTask DisposeServerOnlyAsync(TimeSpan? gracefulTimeout = null)
        {
            if (_serverDisposed)
                return;

            _serverDisposed = true;
            await _server.StopAsync(gracefulTimeout ?? TimeSpan.Zero);
        }

        public async ValueTask DisposeClientOnlyAsync()
        {
            if (_clientDisposed)
                return;

            _clientDisposed = true;
            await Client.StopAsync();
        }

        internal Task WaitForServerExitAsync() => _serverTask;

        public async ValueTask DisposeAsync()
        {
            await DisposeClientOnlyAsync();
            await _serverCts.CancelAsync();
            await DisposeServerOnlyAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }

    private sealed class CountingCompressionProvider(
        ISharpLinkCompressionProvider inner,
        string? wireProfile = null)
        : ISharpLinkCompressionProvider
    {
        private int _compressCount;
        private int _decompressCount;
        public string WireProfile => wireProfile ?? inner.WireProfile;
        public int CompressCount => Volatile.Read(ref _compressCount);
        public int DecompressCount => Volatile.Read(ref _decompressCount);

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _compressCount);
            return inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decompressCount);
            inner.Decompress(input, output, maxOutputBytes, cancellationToken);
            return;
        }
    }

    private sealed class NoBenefitCompressionProvider : ISharpLinkCompressionProvider
    {
        public string WireProfile => "test.identity/v1";

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            foreach (var segment in input)
                output.Write(segment.Span);
            return true;
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Identity candidates must never be selected.");
    }

    private sealed class ThrowingCompressionProvider(
        ISharpLinkCompressionProvider inner,
        bool throwOnCompress,
        bool throwOnDecompress) : ISharpLinkCompressionProvider
    {
        public string WireProfile => inner.WireProfile;

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throwOnCompress
                ? throw new InvalidOperationException("Injected compression failure.")
                : inner.TryCompress(input, output, maxOutputBytes, cancellationToken);

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            if (throwOnDecompress)
                throw new InvalidOperationException("Injected decompression failure.");
            inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }
}

[RpcContract]
public interface ITestService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [Sdk.Timeout]
    ValueTask<int> SlowAddAsync(int left, int right, CancellationToken cancellationToken);
    [NonCancellable]
    ValueTask<int> SlowAddWithoutTimeoutAsync(int left, int right);
    ValueTask<int> BlockingAddAsync(
        int left,
        int right,
        CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask<int> SlowThrowWithoutTimeoutAsync();
    [NonCancellable]
    ValueTask ThrowCancellationAsync();
    [Sdk.Timeout(0.1)]
    ValueTask<int> SlowAddWithMethodTimeoutAsync(
        int left,
        int right,
        CancellationToken cancellationToken);
    [Sdk.Timeout(2)]
    ValueTask<string> DescribeCallAsync(
        int value,
        CancellationToken cancellationToken);
    [NonCancellable]
    ValueTask<Person> EchoAsync(Person person);
    [NonCancellable]
    ValueTask<GeneratedEnvelope> EchoGeneratedAsync(GeneratedEnvelope value);
    ValueTask<int> UploadAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask<int> UploadWithHeaderAsync(MalformedHeader header, IAsyncEnumerable<int> values);
    [NonCancellable]
    IAsyncEnumerable<string> DownloadAsync(int count);
    IAsyncEnumerable<int> SlowDownloadAsync(int count, int delayMs, CancellationToken cancellationToken);
    [Oneway]
    [Sdk.Timeout(0.1)]
    ValueTask WaitForOneWayDeadlineAsync(CancellationToken cancellationToken);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(string message);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyUploadWithHeaderAsync(MalformedHeader header, IAsyncEnumerable<int> values);
}

[RpcService]
public class TestService : ITestService
{
    private static TaskCompletionSource s_nonCancellableCompletion = CreateCompletionSource();
    private static TaskCompletionSource s_nonCancellableFailure = CreateCompletionSource();
    private static TaskCompletionSource s_blockingAddStarted = CreateCompletionSource();
    private static TaskCompletionSource s_blockingAddRelease = CreateCompletionSource();
    private static TaskCompletionSource s_downloadDisposed = CreateCompletionSource();
    private static int s_blockingAddExpectedStarts = 1;
    private static int s_blockingAddStartedCount;
    private static int s_activeUploads;
    private static int s_malformedUploadInvocations;
    private static int s_malformedOneWayInvocations;
    private static int s_notifyCount;
    private static TaskCompletionSource s_notify = CreateCompletionSource();
    private static TaskCompletionSource s_oneWayDeadlineCancellation = CreateCompletionSource();

    internal static int ActiveUploads => Volatile.Read(ref s_activeUploads);
    internal static int MalformedUploadInvocations => Volatile.Read(ref s_malformedUploadInvocations);
    internal static int MalformedOneWayInvocations => Volatile.Read(ref s_malformedOneWayInvocations);
    internal static int NotifyCount => Volatile.Read(ref s_notifyCount);

    internal static void ResetNotify()
    {
        Volatile.Write(ref s_notifyCount, 0);
        Interlocked.Exchange(ref s_notify, CreateCompletionSource());
    }

    internal static void ResetMalformedOneWayInvocations()
        => Volatile.Write(ref s_malformedOneWayInvocations, 0);

    internal static Task WaitForNotifyAsync() => Volatile.Read(ref s_notify).Task;

    internal static void ResetOneWayDeadlineCancellation()
        => Interlocked.Exchange(ref s_oneWayDeadlineCancellation, CreateCompletionSource());

    internal static Task WaitForOneWayDeadlineCancellationAsync()
        => Volatile.Read(ref s_oneWayDeadlineCancellation).Task;

    internal static void ResetActiveUploads() => Volatile.Write(ref s_activeUploads, 0);
    internal static void ResetMalformedUploadInvocations()
        => Volatile.Write(ref s_malformedUploadInvocations, 0);

    internal static void ResetNonCancellableCompletion()
        => Interlocked.Exchange(ref s_nonCancellableCompletion, CreateCompletionSource());

    internal static Task WaitForNonCancellableCompletionAsync()
        => Volatile.Read(ref s_nonCancellableCompletion).Task;

    internal static void ResetNonCancellableFailure()
        => Interlocked.Exchange(ref s_nonCancellableFailure, CreateCompletionSource());

    internal static Task WaitForNonCancellableFailureAsync()
        => Volatile.Read(ref s_nonCancellableFailure).Task;

    internal static void ResetBlockingAdd(int expectedStarts = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedStarts);
        Volatile.Write(ref s_blockingAddExpectedStarts, expectedStarts);
        Volatile.Write(ref s_blockingAddStartedCount, 0);
        Interlocked.Exchange(ref s_blockingAddStarted, CreateCompletionSource());
        Interlocked.Exchange(ref s_blockingAddRelease, CreateCompletionSource());
    }

    internal static Task WaitForBlockingAddStartedAsync()
        => Volatile.Read(ref s_blockingAddStarted).Task;

    internal static void ReleaseBlockingAdd()
        => Volatile.Read(ref s_blockingAddRelease).TrySetResult();

    internal static void ResetDownloadDisposed()
        => Interlocked.Exchange(ref s_downloadDisposed, CreateCompletionSource());

    internal static Task WaitForDownloadDisposedAsync()
        => Volatile.Read(ref s_downloadDisposed).Task;

    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask<int> SlowAddAsync(int left, int right, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return left + right;
    }

    public async ValueTask<int> SlowAddWithoutTimeoutAsync(int left, int right)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref s_nonCancellableCompletion).TrySetResult();
        return left + right;
    }

    public async ValueTask<int> BlockingAddAsync(
        int left,
        int right,
        CancellationToken cancellationToken = default)
    {
        var release = Volatile.Read(ref s_blockingAddRelease);
        if (Interlocked.Increment(ref s_blockingAddStartedCount) ==
            Volatile.Read(ref s_blockingAddExpectedStarts))
        {
            Volatile.Read(ref s_blockingAddStarted).TrySetResult();
        }
        await release.Task.WaitAsync(cancellationToken);
        return left + right;
    }

    public async ValueTask<int> SlowThrowWithoutTimeoutAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref s_nonCancellableFailure).TrySetResult();
        throw new InvalidOperationException("late non-cancellable failure");
    }

    public ValueTask ThrowCancellationAsync()
        => ValueTask.FromException(new OperationCanceledException("service-specific cancellation"));

    public async ValueTask<int> SlowAddWithMethodTimeoutAsync(
        int left,
        int right,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return left + right;
    }

    public ValueTask<string> DescribeCallAsync(
        int value,
        CancellationToken cancellationToken)
    {
        var context = SharpLinkCallContext.Current;
        var tenant = context?.Metadata is { Count: > 0 } metadata
            ? metadata[0].Value
            : "missing";
        const string deadline = "no-deadline";
        return ValueTask.FromResult($"{value}:{tenant}:{deadline}");
    }

    public ValueTask<Person> EchoAsync(Person person)
    {
        person.Name += "-r";
        person.Age += 1;
        return ValueTask.FromResult(person);
    }

    public ValueTask<GeneratedEnvelope> EchoGeneratedAsync(GeneratedEnvelope value)
        => ValueTask.FromResult(value with { Name = value.Name + "-r", Age = value.Age + 1 });

    public async ValueTask<int> UploadAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref s_activeUploads);
        try
        {
            var sum = 0;
            await foreach (var i in values.WithCancellation(cancellationToken))
                sum += i;
            return sum;
        }
        finally
        {
            Interlocked.Decrement(ref s_activeUploads);
        }
    }

    public async ValueTask<int> UploadWithHeaderAsync(
        MalformedHeader header,
        IAsyncEnumerable<int> values)
    {
        _ = header;
        Interlocked.Increment(ref s_malformedUploadInvocations);
        var sum = 0;
        await foreach (var value in values)
            sum += value;
        return sum;
    }

    public async ValueTask NotifyUploadWithHeaderAsync(
        MalformedHeader header,
        IAsyncEnumerable<int> values)
    {
        _ = header;
        Interlocked.Increment(ref s_malformedOneWayInvocations);
        await foreach (var value in values)
            _ = value;
    }

    public async IAsyncEnumerable<string> DownloadAsync(int count)
    {
        try
        {
            for (var i = 0; i < count; i++)
            {
                yield return $"v-{i}";
                await Task.Yield();
            }
        }
        finally
        {
            Volatile.Read(ref s_downloadDisposed).TrySetResult();
        }
    }

    public async IAsyncEnumerable<int> SlowDownloadAsync(int count, int delayMs, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return i;
                await Task.Delay(delayMs, cancellationToken);
            }
        }
        finally
        {
            Volatile.Read(ref s_downloadDisposed).TrySetResult();
        }
    }

    public async ValueTask WaitForOneWayDeadlineAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Read(ref s_oneWayDeadlineCancellation).TrySetResult();
        }
    }

    public ValueTask NotifyAsync(string message)
    {
        _ = message;
        Interlocked.Increment(ref s_notifyCount);
        Volatile.Read(ref s_notify).TrySetResult();
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[RpcContract]
public interface ICompressionService : IService
{
    [NonCancellable]
    ValueTask<byte[]> EchoBytesAsync(byte[] value);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyBytesAsync(byte[] value);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyStreamBytesAsync(IAsyncEnumerable<byte[]> values);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyStreamWithHeaderAsync(byte[] header, IAsyncEnumerable<byte[]> values);

    [NonCancellable]
    ValueTask<int> UploadBytesAsync(IAsyncEnumerable<byte[]> values);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DownloadBytesAsync(int count, int size);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DuplexBytesAsync(IAsyncEnumerable<byte[]> values);
}

[RpcService]
public sealed class CompressionService : ICompressionService
{
    private static TaskCompletionSource<int> s_oneWay = CreateOneWayCompletion();

    internal static void ResetOneWay()
        => Interlocked.Exchange(ref s_oneWay, CreateOneWayCompletion());

    internal static Task<int> WaitForOneWayAsync() => Volatile.Read(ref s_oneWay).Task;

    public ValueTask<byte[]> EchoBytesAsync(byte[] value) => ValueTask.FromResult(value);

    public ValueTask NotifyBytesAsync(byte[] value)
    {
        Volatile.Read(ref s_oneWay).TrySetResult(value.Length);
        return ValueTask.CompletedTask;
    }

    public async ValueTask NotifyStreamBytesAsync(IAsyncEnumerable<byte[]> values)
    {
        var total = 0;
        await foreach (var value in values)
            total += value.Length;
        Volatile.Read(ref s_oneWay).TrySetResult(total);
    }

    public async ValueTask NotifyStreamWithHeaderAsync(
        byte[] header,
        IAsyncEnumerable<byte[]> values)
    {
        var total = header.Length;
        await foreach (var value in values)
            total += value.Length;
        Volatile.Read(ref s_oneWay).TrySetResult(total);
    }

    public async ValueTask<int> UploadBytesAsync(IAsyncEnumerable<byte[]> values)
    {
        var total = 0;
        await foreach (var value in values)
            total += value.Length;
        return total;
    }

    public async IAsyncEnumerable<byte[]> DownloadBytesAsync(int count, int size)
    {
        var payload = Enumerable.Repeat((byte)0x2a, size).ToArray();
        for (var index = 0; index < count; index++)
        {
            yield return payload;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<byte[]> DuplexBytesAsync(IAsyncEnumerable<byte[]> values)
    {
        await foreach (var value in values)
            yield return value;
    }

    private static TaskCompletionSource<int> CreateOneWayCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[SharpPackable]
public partial class Person
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public List<string> Tags { get; set; } = [];
}

public sealed record GeneratedAddress(
    [property: RpcMember(1)] string City);

public sealed record GeneratedEnvelope(
    [property: RpcRequired] string Name,
    int Age,
    GeneratedAddress Address,
    List<string> Tags);

[RpcContract]
public interface IGeneratedSemanticContract : IService
{
    [NonCancellable]
    ValueTask<GeneratedSemanticEnvelope> EchoAsync(GeneratedSemanticEnvelope value);
}

public sealed record GeneratedSemanticEnvelope(
    [property: RpcMember(1)] bool Boolean,
    [property: RpcMember(2)] System.Text.Rune Rune,
    [property: RpcMember(3)] decimal Decimal,
    [property: RpcMember(4)] DateOnly DateOnly,
    [property: RpcMember(5)] DateTime DateTime,
    [property: RpcMember(6)] TimeOnly TimeOnly,
    [property: RpcMember(7)] DateTimeOffset DateTimeOffset);
