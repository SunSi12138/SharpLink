using System.Collections;
using System.Reflection;

namespace SharpLink.IntegrationTests;

public class CompressionRemoteCancelDuringDecodeTests
{
    [Test]
    [NotInParallel]
    public async Task RemoteCancelShouldReachPostCapacityCompressedDecode()
    {
        CompressionMergeGateProbeService.Reset();
        var serverProvider = new WaitForRemoteCancellationProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RemoteCancelHarness.CreateAsync(serverProvider);
        var payload = Enumerable.Repeat((byte)0x61, 32 * 1024).ToArray();
        using var callCts = new CancellationTokenSource();
        var call = harness.Client.Get<ICompressionMergeGateProbeService>()
            .CancellableEchoAsync(payload, callCts.Token)
            .AsTask();

        await AssertRemoteCancellationDuringDecodeAsync(
            harness,
            serverProvider,
            callCts,
            call,
            "unary");
        Ensure(CompressionMergeGateProbeService.CancellableEchoInvocations == 0,
            "remote-cancelled compressed decode must not invoke the unary service");

        var response = await harness.Client.Get<ICompressionMergeGateProbeService>()
            .EchoAsync(payload)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(response.SequenceEqual(payload),
            "connection should remain reusable after remote cancellation during compressed decode");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0,
            "reusable control call should release call capacity");
    }

    [Test]
    [NotInParallel]
    public async Task RemoteCancelShouldReachClientStreamingCompressedHeaderDecode()
    {
        CompressionRemoteCancelStreamingService.Reset();
        var serverProvider = new WaitForRemoteCancellationProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RemoteCancelHarness.CreateAsync(serverProvider);
        var payload = Enumerable.Repeat((byte)0x62, 32 * 1024).ToArray();
        using var callCts = new CancellationTokenSource();
        var call = harness.Client.Get<ICompressionRemoteCancelStreamingService>()
            .UploadAsync(payload, ToStream([1, 2, 3, 4]), callCts.Token)
            .AsTask();

        await AssertRemoteCancellationDuringDecodeAsync(
            harness,
            serverProvider,
            callCts,
            call,
            "client-streaming");
        await WaitUntilAsync(
            () => harness.ActiveStreams == 0,
            "remote-cancelled client-streaming handoff should release stream state");
        Ensure(CompressionRemoteCancelStreamingService.UploadInvocations == 0,
            "remote-cancelled compressed header must not invoke the client-streaming service");
    }

    [Test]
    [NotInParallel]
    public async Task RemoteCancelShouldReachDuplexCompressedHeaderDecode()
    {
        CompressionRemoteCancelStreamingService.Reset();
        var serverProvider = new WaitForRemoteCancellationProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RemoteCancelHarness.CreateAsync(serverProvider);
        var payload = Enumerable.Repeat((byte)0x63, 32 * 1024).ToArray();
        using var callCts = new CancellationTokenSource();
        await using var enumerator = harness.Client.Get<ICompressionRemoteCancelStreamingService>()
            .DuplexAsync(payload, ToStream([5, 6, 7]), callCts.Token)
            .GetAsyncEnumerator();
        var move = enumerator.MoveNextAsync().AsTask();

        await AssertRemoteCancellationDuringDecodeAsync(
            harness,
            serverProvider,
            callCts,
            move,
            "duplex");
        await WaitUntilAsync(
            () => harness.ActiveStreams == 0,
            "remote-cancelled duplex handoff should release stream state");
        Ensure(CompressionRemoteCancelStreamingService.DuplexInvocations == 0,
            "remote-cancelled compressed header must not invoke the duplex service");
    }

    [Test]
    [NotInParallel]
    public async Task RemoteCancelShouldReachSynchronousAdvancedAdmissionCompressedDecode()
    {
        CompressionMergeGateProbeService.Reset();
        var serverProvider = new WaitForRemoteCancellationProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RemoteCancelHarness.CreateAsync(
            serverProvider,
            useAdmissionControl: true);
        var payload = Enumerable.Repeat((byte)0x64, 32 * 1024).ToArray();
        using var callCts = new CancellationTokenSource();
        var call = harness.Client.Get<ICompressionMergeGateProbeService>()
            .CancellableEchoAsync(payload, callCts.Token)
            .AsTask();

        await AssertRemoteCancellationDuringDecodeAsync(
            harness,
            serverProvider,
            callCts,
            call,
            "advanced-admission fast path");
        Ensure(CompressionMergeGateProbeService.CancellableEchoInvocations == 0,
            "advanced-admission remote cancel must prevent service invocation");
    }

    [Test]
    [NotInParallel]
    public async Task CapacityRejectedCancellableCompressedRequestShouldNotRetainOrDecode()
    {
        CompressionMergeGateProbeService.Reset();
        CompressionOwnershipProbeService.Reset();
        var serverProvider = new CountingDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RemoteCancelHarness.CreateAsync(serverProvider);
        var blocker = harness.Client.Get<ICompressionMergeGateProbeService>().BlockAsync().AsTask();
        var service = harness.Client.Get<ICompressionOwnershipProbeService>();
        var payload = Enumerable.Repeat((byte)0x65, 32 * 1024).ToArray();

        try
        {
            await CompressionMergeGateProbeService.WaitForBlockStartedAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => harness.ActiveCalls == 1,
                "capacity blocker should own the only call slot");
            var rejectedBefore = harness.RejectedOneWayCalls;
            using var callCts = new CancellationTokenSource();

            using (harness.DisableServerBufferRents())
            {
                await service.CancellableNotifyAsync(payload, callCts.Token)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => harness.RejectedOneWayCalls == rejectedBefore + 1,
                    "capacity-full cancellable one-way should be rejected without renting a retained copy");
            }

            Ensure(serverProvider.DecompressCount == 0,
                "capacity-rejected cancellable compressed request must not decompress");
            Ensure(CompressionOwnershipProbeService.NotifyInvocations == 0,
                "capacity-rejected cancellable request must not invoke the service");
        }
        finally
        {
            CompressionMergeGateProbeService.ReleaseBlock();
        }

        Ensure(await blocker.WaitAsync(TimeSpan.FromSeconds(2)) == 1,
            "capacity blocker should complete after release");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0,
            "capacity blocker should release the call slot");
    }

    [Test]
    [NotInParallel]
    public async Task RetainedCompressedFrameShouldReleaseAfterDecodeBeforeAsyncHandlerCompletes()
    {
        CompressionOwnershipProbeService.Reset();
        var serverProvider = new CountingDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RemoteCancelHarness.CreateAsync(serverProvider);
        var service = harness.Client.Get<ICompressionOwnershipProbeService>();
        var payload = Enumerable.Repeat((byte)0x66, 32 * 1024).ToArray();
        var pooledBefore = harness.ServerPooledWriterCount;
        using var callCts = new CancellationTokenSource();
        var call = service.BlockAfterDecodeAsync(payload, callCts.Token).AsTask();

        try
        {
            await CompressionOwnershipProbeService.WaitForBlockStartedAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => harness.ActiveCalls == 1,
                "blocked ownership probe should keep its call slot");

            var expectedPooledWhileBlocked = Math.Max(pooledBefore - 3, 0) + 1;
            await WaitUntilAsync(
                () => harness.ServerPooledWriterCount == expectedPooledWhileBlocked,
                "retained compressed request copy should return after decode while handler remains blocked");
            Ensure(serverProvider.DecompressCount == 1,
                "blocked ownership probe should decode its compressed request exactly once");
        }
        finally
        {
            CompressionOwnershipProbeService.ReleaseBlock();
        }

        Ensure(await call.WaitAsync(TimeSpan.FromSeconds(2)) == payload.Length,
            "blocked ownership probe result");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0,
            "blocked ownership probe should release its call slot");
    }

    private static async Task AssertRemoteCancellationDuringDecodeAsync(
        RemoteCancelHarness harness,
        WaitForRemoteCancellationProvider serverProvider,
        CancellationTokenSource callCts,
        Task call,
        string scenario)
    {
        await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => harness.ActiveCalls == 1,
            $"{scenario} compressed request should own call capacity while decode is running");

        callCts.Cancel();

        await serverProvider.WaitForCancellationAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await ObserveTerminalCallAsync(call);
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0,
            $"{scenario} remote-cancelled compressed decode should release call capacity");
    }

    private static async IAsyncEnumerable<int> ToStream(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.CompletedTask;
        }
    }

    private static async Task ObserveTerminalCallAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.Cancelled or SharpLinkErrorCode.ConnectionClosed)
        {
        }
        catch (TimeoutException)
        {
            throw new Exception("assert failed: remote-cancelled call did not terminate");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
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

    private sealed class WaitForRemoteCancellationProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly TaskCompletionSource _decompressionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempt;

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
            if (Interlocked.Increment(ref _attempt) != 1)
                return inner.Decompress(input, output, maxOutputBytes, cancellationToken);

            _decompressionStarted.TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
            {
                throw new InvalidOperationException(
                    "The server-side compressed decode token was not cancelled by the remote caller.");
            }

            _cancellationObserved.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class RemoteCancelHarness : IAsyncDisposable
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;

        public ISharpLinkClient Client { get; }
        public int ActiveCalls
            => (int)(GetRequiredField(_server, "_globalActiveCalls").GetValue(_server)
                ?? throw new Exception("server active call count is null"));
        public long RejectedOneWayCalls
            => (long)(GetRequiredField(_server, "_rejectedOneWayCalls").GetValue(_server)
                ?? throw new Exception("server rejected one-way count is null"));
        public int ServerPooledWriterCount
        {
            get
            {
                var buffers = GetServerBufferPool();
                return (int)(GetRequiredField(buffers, "_pooledCount").GetValue(buffers)
                    ?? throw new Exception("server pooled writer count is null"));
            }
        }

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

        private RemoteCancelHarness(
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

        public static async Task<RemoteCancelHarness> CreateAsync(
            ISharpLinkCompressionProvider serverProvider,
            bool useAdmissionControl = false)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                    options.FlowControl.MaxConcurrentCallsPerServer = 1;
                    options.Compression.Providers.Add(serverProvider);
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            if (useAdmissionControl)
            {
                serverBuilder.UseAdmissionControl(options =>
                {
                    options.Global.UseConcurrency(8);
                    options.MaxQueuedCalls = 0;
                    options.MaxQueuedBytes = 0;
                    options.MaxQueueDelay = TimeSpan.Zero;
                });
            }
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

            var client = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
                .UseConnectionPool(options =>
                {
                    options.MinConnections = 1;
                    options.MaxConnections = 1;
                })
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    SharpLinkCompressionProviders.CreateBrotli()))
                .Build();
            await client.ConnectAsync();

            return new RemoteCancelHarness(serverCts, serverTask, server, client);
        }

        public IDisposable DisableServerBufferRents()
        {
            var buffers = GetServerBufferPool();
            var poolField = GetRequiredField(buffers, "_pool");
            var pool = poolField.GetValue(buffers)
                ?? throw new Exception("server buffer pool is already disabled");
            poolField.SetValue(buffers, null);
            return new RestoreAction(() => poolField.SetValue(buffers, pool));
        }

        private object GetServerBufferPool()
        {
            var runtimeContext = GetRequiredField(_server, "_runtimeContext").GetValue(_server)
                ?? throw new Exception("server runtime context is null");
            return GetRequiredProperty(runtimeContext, "Buffers").GetValue(runtimeContext)
                ?? throw new Exception("server buffer pool is null");
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
            CompressionOwnershipProbeService.ReleaseBlock();
            await Client.StopAsync();
            await _serverCts.CancelAsync();
            await _server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }

        private sealed class RestoreAction(Action restore) : IDisposable
        {
            private Action? _restore = restore;

            public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
        }
    }
}

[RpcContract]
public interface ICompressionRemoteCancelStreamingService : IService
{
    ValueTask<int> UploadAsync(
        byte[] headerPayload,
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);

    IAsyncEnumerable<int> DuplexAsync(
        byte[] headerPayload,
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);
}

[RpcService]
public sealed class CompressionRemoteCancelStreamingService : ICompressionRemoteCancelStreamingService
{
    private static int s_uploadInvocations;
    private static int s_duplexInvocations;

    internal static int UploadInvocations => Volatile.Read(ref s_uploadInvocations);
    internal static int DuplexInvocations => Volatile.Read(ref s_duplexInvocations);

    internal static void Reset()
    {
        Volatile.Write(ref s_uploadInvocations, 0);
        Volatile.Write(ref s_duplexInvocations, 0);
    }

    public async ValueTask<int> UploadAsync(
        byte[] headerPayload,
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref s_uploadInvocations);
        var result = headerPayload.Length;
        await foreach (var value in values.WithCancellation(cancellationToken))
            result += value;
        return result;
    }

    public async IAsyncEnumerable<int> DuplexAsync(
        byte[] headerPayload,
        IAsyncEnumerable<int> values,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref s_duplexInvocations);
        await foreach (var value in values.WithCancellation(cancellationToken))
            yield return headerPayload.Length + value;
    }
}

[RpcContract]
public interface ICompressionOwnershipProbeService : IService
{
    [Oneway]
    ValueTask CancellableNotifyAsync(byte[] payload, CancellationToken cancellationToken);

    ValueTask<int> BlockAfterDecodeAsync(byte[] payload, CancellationToken cancellationToken);
}

[RpcService]
public sealed class CompressionOwnershipProbeService : ICompressionOwnershipProbeService
{
    private static TaskCompletionSource s_blockStarted = CreateSignal();
    private static TaskCompletionSource s_blockRelease = CreateSignal();
    private static int s_notifyInvocations;

    internal static int NotifyInvocations => Volatile.Read(ref s_notifyInvocations);

    internal static void Reset()
    {
        s_blockStarted = CreateSignal();
        s_blockRelease = CreateSignal();
        Volatile.Write(ref s_notifyInvocations, 0);
    }

    internal static Task WaitForBlockStartedAsync() => s_blockStarted.Task;
    internal static void ReleaseBlock() => s_blockRelease.TrySetResult();

    public ValueTask CancellableNotifyAsync(byte[] payload, CancellationToken cancellationToken)
    {
        _ = payload;
        _ = cancellationToken;
        Interlocked.Increment(ref s_notifyInvocations);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> BlockAfterDecodeAsync(
        byte[] payload,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        s_blockStarted.TrySetResult();
        await s_blockRelease.Task.ConfigureAwait(false);
        return payload.Length;
    }

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
