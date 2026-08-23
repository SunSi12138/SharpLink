namespace SharpLink.IntegrationTests;

public class PreAdmissionStreamBudgetIntegrationTests
{
    private const int ItemBytes = 4 * 1024;
    private const long StreamBudgetBytes = 12L * 1024;

    [Test]
    [NotInParallel]
    public async Task StreamBudgetShouldBeGlobalAndIndependentFromAdmissionQueuedBytes()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await BudgetHarness.CreateAsync();
        var serviceA = harness.ClientA.Get<ITestService>();
        var serviceB = harness.ClientB.Get<ITestService>();
        var uploadAService = harness.ClientA.Get<ICompressionService>();
        var uploadBService = harness.ClientB.Get<ICompressionService>();
        var producerRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var active = serviceA.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        Task<int>? uploadA = null;
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));

            uploadA = uploadAService.UploadBytesAsync(
                TwoItemsThenWaitAsync(producerRelease.Task)).AsTask();
            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes > ItemBytes * 2,
                "two A stream items retained while admission waits");

            var retainedByA = harness.PreAdmissionStreamBytes;
            var admissionBytesBeforeB = harness.AdmissionQueuedBytes;
            Ensure(retainedByA <= StreamBudgetBytes,
                "A pre-admission stream ownership must remain within the stable global budget");
            Ensure(admissionBytesBeforeB < retainedByA,
                "Dynamic Admission queued bytes must not include retained stream-frame bytes");

            var rejectedB = uploadBService.UploadBytesAsync(
                SingleItemAsync(CreateItem(0x42))).AsTask();
            var failure = await CaptureFailureAsync(rejectedB);
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted } exhausted &&
                   exhausted.Message.Contains(
                       "server_pre_admission_stream_bytes",
                       StringComparison.Ordinal),
                "second connection must receive the stable stream-budget ResourceExhausted reason");

            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes == retainedByA,
                "rejected B reservation leaves A physical ownership unchanged");
            Ensure(harness.AdmissionQueuedBytes == admissionBytesBeforeB,
                "rejected B stream must not leave bytes in Dynamic Admission accounting");

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 2,
                "admission permit owner completes");
            producerRelease.TrySetResult();
            Ensure(await uploadA.WaitAsync(TimeSpan.FromSeconds(5)) == ItemBytes * 2,
                "A buffered stream replays after admission");

            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes == 0 &&
                      harness.AdmissionQueuedBytes == 0,
                "all pre-admission and admission queued byte ownership released");
            Ensure(await serviceB.AddAsync(20, 22) == 42,
                "stream-budget rejection must leave the second connection usable");
        }
        finally
        {
            producerRelease.TrySetResult();
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (uploadA is not null)
                await ObserveTerminalAsync(uploadA);
        }
    }

    [Test]
    [NotInParallel]
    public async Task ForceStopShouldReleaseBufferedStreamBudgetBeforeExit()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await BudgetHarness.CreateAsync();
        var producerRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = harness.ClientA.Get<ITestService>()
            .BlockingAddAsync(3, 4, CancellationToken.None).AsTask();
        Task<int>? queued = null;

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            queued = harness.ClientB.Get<ICompressionService>()
                .UploadBytesAsync(TwoItemsThenWaitAsync(producerRelease.Task)).AsTask();
            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes > ItemBytes * 2 &&
                      harness.AdmissionQueuedBytes > 0,
                "buffered stream ownership exists before force stop");

            await harness.StopServerAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));
            producerRelease.TrySetResult();
            await ObserveTerminalAsync(active);
            await ObserveTerminalAsync(queued);

            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes == 0 &&
                      harness.AdmissionQueuedBytes == 0,
                "force stop releases stable stream budget and admission waiter bytes");
        }
        finally
        {
            producerRelease.TrySetResult();
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (queued is not null)
                await ObserveTerminalAsync(queued);
        }
    }

    private static byte[] CreateItem(byte value)
        => Enumerable.Repeat(value, ItemBytes).ToArray();

    private static async IAsyncEnumerable<byte[]> TwoItemsThenWaitAsync(
        Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return CreateItem(0x31);
        await Task.Yield();
        yield return CreateItem(0x32);
        await release.WaitAsync(cancellationToken);
    }

    private static async IAsyncEnumerable<byte[]> SingleItemAsync(
        byte[] value,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return value;
        await Task.Yield();
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task ObserveTerminalAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    private sealed class BudgetHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _disposed;

        private BudgetHarness(
            CancellationTokenSource serverCancellation,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient clientA,
            ISharpLinkClient clientB)
        {
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
            _server = server;
            ClientA = clientA;
            ClientB = clientB;
        }

        internal ISharpLinkClient ClientA { get; }
        internal ISharpLinkClient ClientB { get; }

        internal long PreAdmissionStreamBytes
            => ReadServerDiagnostic<long>("PreAdmissionStreamBytesForDiagnostics");

        internal long AdmissionQueuedBytes
        {
            get
            {
                var field = _server.GetType().GetField(
                    "_admissionController",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("cannot find server admission controller field");
                var controller = field.GetValue(_server)
                    ?? throw new Exception("server admission controller is unavailable");
                var property = controller.GetType().GetProperty(
                    "QueuedBytes",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("cannot find admission queued-byte diagnostic");
                return (long)property.GetValue(controller)!;
            }
        }

        internal static async Task<BudgetHarness> CreateAsync()
        {
            var serverCancellation = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxPreAdmissionStreamBytesPerServer = StreamBudgetBytes;
                    options.FlowControl.StreamReceiveWindowBytes = 64 * 1024;
                    options.FlowControl.ConnectionReceiveWindowBytes = 256 * 1024;
                })
                .UseAdmissionControl(options =>
                {
                    options.Global.UseConcurrency(1);
                    options.MaxQueuedCalls = 2;
                    options.MaxQueuedBytes = 64 * 1024;
                    options.MaxQueueDelay = TimeSpan.FromSeconds(10);
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = RunServerAsync(server, serverCancellation.Token);

            var clientA = CreateClient(port);
            var clientB = CreateClient(port);
            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            return new BudgetHarness(
                serverCancellation,
                serverTask,
                server,
                clientA,
                clientB);
        }

        internal Task StopServerAsync(TimeSpan gracefulTimeout)
            => _server.StopAsync(gracefulTimeout).AsTask();

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await StopClientAsync(ClientA);
                await StopClientAsync(ClientB);
            }
            finally
            {
                await _serverCancellation.CancelAsync();
                try
                {
                    await _server.StopAsync(TimeSpan.Zero);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
                await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
                _serverCancellation.Dispose();
            }
        }

        private T ReadServerDiagnostic<T>(string name)
        {
            var property = _server.GetType().GetProperty(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception($"cannot find server diagnostic {name}");
            return (T)property.GetValue(_server)!;
        }

        private static ISharpLinkClient CreateClient(int port)
            => SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .Build();

        private static async Task StopClientAsync(ISharpLinkClient client)
        {
            try
            {
                await client.StopAsync();
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException or ObjectDisposedException or SharpLinkException)
            {
            }
        }
    }

    private static Task RunServerAsync(
        ISharpLinkServer server,
        CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cancellationToken);
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
}
