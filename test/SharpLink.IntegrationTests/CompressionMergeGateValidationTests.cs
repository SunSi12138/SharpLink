using System.Collections;
using System.Reflection;

namespace SharpLink.IntegrationTests;

public class CompressionMergeGateValidationTests
{
    [Test]
    [NotInParallel]
    public async Task DeadlineCancellationShouldWinWhenProviderReturnsSuccessfully()
    {
        CompressionMergeGateProbeService.Reset();
        var serverProvider = new ReturnAfterCancellationProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        var timeout = TimeSpan.FromMilliseconds(100);
        await using var harness = await ValidationHarness.CreateAsync(
            serverProvider,
            requestTimeout: timeout);
        var payload = CreateCompressiblePayload(32 * 1024, 0x51);
        var call = harness.Client.Get<ICompressionMergeGateProbeService>()
            .EchoAsync(payload)
            .AsTask();

        await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await serverProvider.WaitForCancellationAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await EnsureErrorCodeAsync(
            call,
            SharpLinkErrorCode.DeadlineExceeded,
            "provider-returned deadline cancellation");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0,
            "provider-returned deadline call release");

        Ensure(CompressionMergeGateProbeService.EchoInvocations == 0,
            "deadline-abandoned request must not invoke service when provider returns normally");
    }

    [Test]
    [NotInParallel]
    public async Task DecodeFailureRacingRemoteCancelShouldReleaseResourcesAndKeepConnectionReusable()
    {
        CompressionMergeGateProbeService.Reset();
        var serverProvider = new FailFirstOnReleaseProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await ValidationHarness.CreateAsync(serverProvider);
        var payload = CreateCompressiblePayload(32 * 1024, 0x52);
        using var callCts = new CancellationTokenSource();
        var call = harness.Client.Get<ICompressionMergeGateProbeService>()
            .CancellableEchoAsync(payload, callCts.Token)
            .AsTask();

        await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        callCts.Cancel();
        serverProvider.ReleaseFailure();
        await ObserveTerminalCallAsync(call, "decode failure versus remote cancel");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0,
            "decode failure remote-cancel call release");

        Ensure(serverProvider.CapturedOutputReturned,
            "decode failure racing remote cancel must return the decoded writer");
        Ensure(CompressionMergeGateProbeService.CancellableEchoInvocations == 0,
            "decode-failed cancellable request must not invoke service");

        var response = await harness.Client.Get<ICompressionMergeGateProbeService>()
            .EchoAsync(payload)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(response.SequenceEqual(payload),
            "connection should remain reusable after decode failure and late remote cancel");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0,
            "post-race reusable call release");
    }

    [Test]
    [NotInParallel]
    public async Task CompressedClientStreamingRequestShouldSetupAndCleanupStreamState()
    {
        CompressionMergeGateProbeService.Reset();
        var serverProvider = new CountingDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await ValidationHarness.CreateAsync(serverProvider);
        var headerPayload = CreateCompressiblePayload(32 * 1024, 0x53);

        var result = await harness.Client.Get<ICompressionMergeGateProbeService>()
            .UploadAsync(headerPayload, ToStream([1, 2, 3, 4]))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(result == headerPayload.Length + 10,
            "compressed client-streaming request result");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0 && harness.ActiveStreams == 0,
            "compressed client-streaming cleanup");
        Ensure(serverProvider.DecompressCount == 1,
            "client-streaming request header should be actually compressed and decompressed exactly once");
        Ensure(CompressionMergeGateProbeService.UploadInvocations == 1,
            "compressed client-streaming service invocation count");
    }

    [Test]
    [NotInParallel]
    public async Task CompressedDuplexRequestShouldSetupAndCleanupStreamState()
    {
        CompressionMergeGateProbeService.Reset();
        var serverProvider = new CountingDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await ValidationHarness.CreateAsync(serverProvider);
        var headerPayload = CreateCompressiblePayload(32 * 1024, 0x54);
        var received = new List<int>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await foreach (var value in harness.Client.Get<ICompressionMergeGateProbeService>()
                           .DuplexAsync(headerPayload, ToStream([5, 6, 7]))
                           .WithCancellation(timeout.Token))
        {
            received.Add(value);
        }

        Ensure(received.SequenceEqual([
                headerPayload.Length + 5,
                headerPayload.Length + 6,
                headerPayload.Length + 7]),
            "compressed duplex response values");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0 && harness.ActiveStreams == 0,
            "compressed duplex cleanup");
        Ensure(serverProvider.DecompressCount == 1,
            "duplex request header should be actually compressed and decompressed exactly once");
        Ensure(CompressionMergeGateProbeService.DuplexInvocations == 1,
            "compressed duplex service invocation count");
    }

    [Test]
    [NotInParallel]
    public async Task HundredThousandCapacityRejectedCompressedRequestsShouldNotDecodeOrLeakAccounting()
    {
        CompressionMergeGateProbeService.Reset();
        const int rejectedRequests = 100_000;
        var serverProvider = new CountingDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await ValidationHarness.CreateAsync(serverProvider);
        var service = harness.Client.Get<ICompressionMergeGateProbeService>();
        var blocker = service.BlockAsync().AsTask();
        var payload = CreateCompressiblePayload(1024, 0x55);

        try
        {
            await CompressionMergeGateProbeService.WaitForBlockStartedAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));
            var rejectedBefore = harness.RejectedOneWayCalls;

            for (var index = 0; index < rejectedRequests; index++)
                await service.NotifyAsync(payload).AsTask();

            await WaitUntilAsync(
                () => harness.RejectedOneWayCalls - rejectedBefore == rejectedRequests,
                "100k compressed capacity rejection accounting",
                TimeSpan.FromSeconds(30));

            Ensure(serverProvider.DecompressCount == 0,
                "100k capacity-rejected compressed requests must never enter DecodeInboundPayload/provider decompression");
            Ensure(CompressionMergeGateProbeService.NotifyInvocations == 0,
                "100k capacity-rejected one-way requests must not invoke service");
            Ensure(harness.ActiveCalls == 1,
                "only the capacity-owning blocker should remain active during the stress window");

            CompressionMergeGateProbeService.ReleaseBlock();
            Ensure(await blocker.WaitAsync(TimeSpan.FromSeconds(2)) == 1,
                "capacity blocker should complete after release");
            await WaitUntilAsync(
                () => harness.ActiveCalls == 0,
                "100k stress blocker call release");

            await service.NotifyAsync(payload).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => CompressionMergeGateProbeService.NotifyInvocations == 1,
                "post-stress accepted compressed one-way invocation");
            await WaitUntilAsync(
                () => harness.ActiveCalls == 0,
                "post-stress accepted call release");
            Ensure(serverProvider.DecompressCount == 1,
                "accepted control request proves the stress payload is actually compressed");
        }
        finally
        {
            CompressionMergeGateProbeService.ReleaseBlock();
        }
    }

    private static byte[] CreateCompressiblePayload(int length, byte value)
        => Enumerable.Repeat(value, length).ToArray();

    private static async IAsyncEnumerable<int> ToStream(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.CompletedTask;
        }
    }

    private static async Task EnsureErrorCodeAsync(
        Task task,
        SharpLinkErrorCode expected,
        string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception($"assert failed: {scenario} should fail");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == expected,
                $"{scenario} should return {expected}, actual {exception.Code}");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {scenario} did not terminate");
        }
    }

    private static async Task ObserveTerminalCallAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (SharpLinkException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {scenario} did not terminate");
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string scenario,
        TimeSpan? timeout = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            while (!condition())
                await Task.Delay(10, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario} was not observed");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class CountingDecompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private int _decompressCount;

        public string WireProfile => inner.WireProfile;
        public int DecompressCount => Volatile.Read(ref _decompressCount);

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => inner.Compress(input, output, maxOutputBytes, cancellationToken);

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decompressCount);
            return inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }

    private sealed class ReturnAfterCancellationProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly TaskCompletionSource _decompressionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string WireProfile => inner.WireProfile;
        public Task WaitForDecompressionAsync() => _decompressionStarted.Task;
        public Task WaitForCancellationAsync() => _cancellationObserved.Task;

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => inner.Compress(input, output, maxOutputBytes, cancellationToken);

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            _decompressionStarted.TrySetResult();
            cancellationToken.WaitHandle.WaitOne();
            _cancellationObserved.TrySetResult();
            return inner.Decompress(input, output, maxOutputBytes, CancellationToken.None);
        }
    }

    private sealed class FailFirstOnReleaseProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly TaskCompletionSource _decompressionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private IBufferWriter<byte>? _capturedOutput;
        private int _attempt;

        public string WireProfile => inner.WireProfile;
        public Task WaitForDecompressionAsync() => _decompressionStarted.Task;

        public bool CapturedOutputReturned
        {
            get
            {
                if (Volatile.Read(ref _capturedOutput) is not IRpcByteBufferWriter writer)
                    return false;
                try
                {
                    _ = writer.WrittenCount;
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }
        }

        public void ReleaseFailure() => _release.Set();

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => inner.Compress(input, output, maxOutputBytes, cancellationToken);

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempt) != 1)
                return inner.Decompress(input, output, maxOutputBytes, cancellationToken);

            Volatile.Write(ref _capturedOutput, output);
            _decompressionStarted.TrySetResult();
            _release.Wait();
            throw new InvalidOperationException("merge-gate provider failure");
        }
    }

    private sealed class ValidationHarness : IAsyncDisposable
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;

        public ISharpLinkClient Client { get; }

        public int ActiveCalls
            => (int)GetRequiredField(_server, "_globalActiveCalls").GetValue(_server)!;

        public long RejectedOneWayCalls
            => (long)GetRequiredField(_server, "_rejectedOneWayCalls").GetValue(_server)!;

        public int ActiveStreams
        {
            get
            {
                var connection = GetSingleConnection();
                var session = GetRequiredProperty(connection, "Session").GetValue(connection)
                    ?? throw new Exception("server connection session is null");
                var streamManager = GetRequiredProperty(session, "StreamManager").GetValue(session)
                    ?? throw new Exception("server stream manager is null");
                return (int)(GetRequiredProperty(streamManager, "ActiveStreamCount").GetValue(streamManager)
                    ?? throw new Exception("server active stream count is null"));
            }
        }

        private ValidationHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            _server = server;
            Client = client;
        }

        public static async Task<ValidationHarness> CreateAsync(
            ISharpLinkCompressionProvider serverProvider,
            TimeSpan? requestTimeout = null)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                    options.FlowControl.MaxConcurrentCallsPerServer = 1;
                    options.Compression.Providers.Add(serverProvider);
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(serverCts.Token);
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

            var clientBuilder = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    SharpLinkCompressionProviders.CreateBrotli()));
            if (requestTimeout is { } timeout)
                clientBuilder.UseRequestTimeout(timeout);
            var client = clientBuilder.Build();
            await client.ConnectAsync();

            return new ValidationHarness(serverCts, serverTask, server, client);
        }

        private object GetSingleConnection()
        {
            var connections = GetRequiredField(_server, "_connections").GetValue(_server)
                ?? throw new Exception("server connection table is null");
            var values = (IEnumerable)(GetRequiredProperty(connections, "Values").GetValue(connections)
                ?? throw new Exception("server connection values are null"));
            var enumerator = values.GetEnumerator();
            try
            {
                if (!enumerator.MoveNext())
                    throw new Exception("no server connection is available");
                var connection = enumerator.Current
                    ?? throw new Exception("server connection entry is null");
                if (enumerator.MoveNext())
                    throw new Exception("expected exactly one server connection");
                return connection;
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        private static FieldInfo GetRequiredField(object instance, string name)
            => instance.GetType().GetField(name, InstanceFlags)
               ?? throw new Exception($"cannot find field {name}");

        private static PropertyInfo GetRequiredProperty(object instance, string name)
            => instance.GetType().GetProperty(name, InstanceFlags)
               ?? throw new Exception($"cannot find property {name}");

        public async ValueTask DisposeAsync()
        {
            CompressionMergeGateProbeService.ReleaseBlock();
            await Client.StopAsync();
            await _serverCts.CancelAsync();
            await _server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}

[RpcContract]
public interface ICompressionMergeGateProbeService : IService
{
    [NonCancellable]
    ValueTask<int> BlockAsync();

    [NonCancellable]
    ValueTask<byte[]> EchoAsync(byte[] payload);

    ValueTask<byte[]> CancellableEchoAsync(byte[] payload, CancellationToken cancellationToken);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(byte[] payload);

    [NonCancellable]
    ValueTask<int> UploadAsync(byte[] headerPayload, IAsyncEnumerable<int> values);

    [NonCancellable]
    IAsyncEnumerable<int> DuplexAsync(byte[] headerPayload, IAsyncEnumerable<int> values);
}

[RpcService]
public sealed class CompressionMergeGateProbeService : ICompressionMergeGateProbeService
{
    private static TaskCompletionSource s_blockStarted = CreateSignal();
    private static TaskCompletionSource s_blockRelease = CreateSignal();
    private static int s_echoInvocations;
    private static int s_cancellableEchoInvocations;
    private static int s_notifyInvocations;
    private static int s_uploadInvocations;
    private static int s_duplexInvocations;

    internal static int EchoInvocations => Volatile.Read(ref s_echoInvocations);
    internal static int CancellableEchoInvocations => Volatile.Read(ref s_cancellableEchoInvocations);
    internal static int NotifyInvocations => Volatile.Read(ref s_notifyInvocations);
    internal static int UploadInvocations => Volatile.Read(ref s_uploadInvocations);
    internal static int DuplexInvocations => Volatile.Read(ref s_duplexInvocations);

    internal static void Reset()
    {
        s_blockStarted = CreateSignal();
        s_blockRelease = CreateSignal();
        Volatile.Write(ref s_echoInvocations, 0);
        Volatile.Write(ref s_cancellableEchoInvocations, 0);
        Volatile.Write(ref s_notifyInvocations, 0);
        Volatile.Write(ref s_uploadInvocations, 0);
        Volatile.Write(ref s_duplexInvocations, 0);
    }

    internal static Task WaitForBlockStartedAsync() => s_blockStarted.Task;
    internal static void ReleaseBlock() => s_blockRelease.TrySetResult();

    public async ValueTask<int> BlockAsync()
    {
        s_blockStarted.TrySetResult();
        await s_blockRelease.Task.ConfigureAwait(false);
        return 1;
    }

    public ValueTask<byte[]> EchoAsync(byte[] payload)
    {
        Interlocked.Increment(ref s_echoInvocations);
        return ValueTask.FromResult(payload);
    }

    public ValueTask<byte[]> CancellableEchoAsync(
        byte[] payload,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Interlocked.Increment(ref s_cancellableEchoInvocations);
        return ValueTask.FromResult(payload);
    }

    public ValueTask NotifyAsync(byte[] payload)
    {
        _ = payload;
        Interlocked.Increment(ref s_notifyInvocations);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> UploadAsync(
        byte[] headerPayload,
        IAsyncEnumerable<int> values)
    {
        Interlocked.Increment(ref s_uploadInvocations);
        var sum = headerPayload.Length;
        await foreach (var value in values)
            sum += value;
        return sum;
    }

    public async IAsyncEnumerable<int> DuplexAsync(
        byte[] headerPayload,
        IAsyncEnumerable<int> values)
    {
        Interlocked.Increment(ref s_duplexInvocations);
        await foreach (var value in values)
            yield return headerPayload.Length + value;
    }

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
