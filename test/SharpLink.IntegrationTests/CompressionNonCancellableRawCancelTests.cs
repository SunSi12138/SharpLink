using System.Collections;
using System.Reflection;

namespace SharpLink.IntegrationTests;

public class CompressionNonCancellableRawCancelTests
{
    [Test]
    [NotInParallel]
    public async Task RawCancelShouldNotCancelCompressedNonCancellableUnaryDecode()
    {
        var serverProvider = new BlockingCancellationProbeProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RawCancelHarness.CreateAsync(serverProvider);
        var payload = Enumerable.Repeat((byte)0x71, 32 * 1024).ToArray();
        var call = harness.Client.Get<ICompressionService>()
            .EchoBytesAsync(payload)
            .AsTask();

        try
        {
            await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var requestId = harness.GetPendingRequestId();
            harness.SendRawCancel(requestId);

            await Task.Delay(150);
            Ensure(!serverProvider.CancellationObserved,
                "raw Cancel must not cancel decode for a Request without the Cancellable flag");
        }
        finally
        {
            serverProvider.Release();
        }

        var response = await call.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(response.SequenceEqual(payload),
            "compressed non-cancellable unary should complete normally after a raw Cancel");
    }

    [Test]
    [NotInParallel]
    public async Task RawCancelShouldNotDropCompressedNonCancellableOneWayDecode()
    {
        CompressionService.ResetOneWay();
        var serverProvider = new BlockingCancellationProbeProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RawCancelHarness.CreateAsync(serverProvider);
        var payload = Enumerable.Repeat((byte)0x72, 32 * 1024).ToArray();
        var requestIdBefore = harness.GetLatestAllocatedRequestId();

        await harness.Client.Get<ICompressionService>()
            .NotifyBytesAsync(payload)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var requestId = harness.GetLatestAllocatedRequestId();
            Ensure(requestId > requestIdBefore,
                "one-way request should allocate a new request ID");
            harness.SendRawCancel(requestId);

            await Task.Delay(150);
            Ensure(!serverProvider.CancellationObserved,
                "raw Cancel must not cancel one-way decode for a Request without the Cancellable flag");
        }
        finally
        {
            serverProvider.Release();
        }

        Ensure(await CompressionService.WaitForOneWayAsync().WaitAsync(TimeSpan.FromSeconds(2)) == payload.Length,
            "compressed non-cancellable one-way should still execute after a raw Cancel");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class BlockingCancellationProbeProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly TaskCompletionSource _decompressionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public string WireProfile => inner.WireProfile;
        public bool CancellationObserved => _cancellationObserved.Task.IsCompleted;

        public Task WaitForDecompressionAsync() => _decompressionStarted.Task;

        public void Release() => _release.Set();

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
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _cancellationObserved);
            _decompressionStarted.TrySetResult();
            _release.Wait();

            // Decode after the probe releases even if the observed token was cancelled. This
            // makes the test verify both token visibility and post-decode terminal semantics.
            return inner.Decompress(input, output, maxOutputBytes, CancellationToken.None);
        }
    }

    private sealed class RawCancelHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private readonly object _connection;
        private readonly object _pendingCalls;
        private readonly object _session;

        public ISharpLinkClient Client { get; }

        private RawCancelHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client,
            object connection,
            object pendingCalls,
            object session)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            _server = server;
            Client = client;
            _connection = connection;
            _pendingCalls = pendingCalls;
            _session = session;
        }

        public static async Task<RawCancelHarness> CreateAsync(
            ISharpLinkCompressionProvider serverProvider)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
                .UseRuntime(options => options.Compression.Providers.Add(serverProvider))
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(serverCts.Token);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
                {
                }
            }, CancellationToken.None);

            var client = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
                .DisableRequestTimeout()
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

            var readyConnectionsField = client.GetType().GetField(
                "_readyConnections",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("cannot find client ready-connections field");
            var readyConnections = (Array)readyConnectionsField.GetValue(client)!;
            Ensure(readyConnections.Length == 1, "raw-cancel harness should have one ready connection");
            var connection = readyConnections.GetValue(0)
                ?? throw new Exception("ready connection was null");
            var pendingCalls = connection.GetType().GetProperty(
                "PendingCalls",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(connection)
                ?? throw new Exception("cannot find pending-call table");
            var session = connection.GetType().GetProperty(
                "Session",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(connection)
                ?? throw new Exception("cannot find RPC session");

            return new RawCancelHarness(
                serverCts,
                serverTask,
                server,
                client,
                connection,
                pendingCalls,
                session);
        }

        public long GetPendingRequestId()
        {
            var slotsField = _pendingCalls.GetType().GetField(
                "_slots",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("cannot find pending-call slots");
            var slots = (IEnumerable)slotsField.GetValue(_pendingCalls)!;
            long? requestId = null;
            foreach (var slot in slots)
            {
                if (slot is null)
                    continue;
                var id = (long)(slot.GetType().GetProperty(
                    "Id",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(slot)
                    ?? throw new Exception("cannot read pending request ID"));
                if (requestId.HasValue)
                    throw new Exception("expected exactly one pending request");
                requestId = id;
            }
            return requestId ?? throw new Exception("no pending request was found");
        }

        public long GetLatestAllocatedRequestId()
        {
            var nextIdField = _pendingCalls.GetType().GetField(
                "_nextId",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("cannot find pending request ID counter");
            return (long)nextIdField.GetValue(_pendingCalls)!;
        }

        public void SendRawCancel(long requestId)
        {
            var extensions = _session.GetType().Assembly.GetType("SharpLink.Runtime.RpcSessionExtensions")
                ?? throw new Exception("cannot find RpcSessionExtensions");
            var sendCancel = extensions.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .SingleOrDefault(method =>
                    method.Name == "SendCancelAsync" &&
                    method.GetParameters().Length == 3)
                ?? throw new Exception("cannot find raw Cancel sender");
            sendCancel.Invoke(null, [_session, requestId, ProtocolV2CancelReason.UserCancellation]);
        }

        public async ValueTask DisposeAsync()
        {
            _ = _connection;
            await Client.StopAsync();
            await _serverCts.CancelAsync();
            await _server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}
