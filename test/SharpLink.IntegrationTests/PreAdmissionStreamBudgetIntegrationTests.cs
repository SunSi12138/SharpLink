using System.Diagnostics.Metrics;

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
        CompressionService.ResetOneWay();
        await using var harness = await BudgetHarness.CreateAsync();
        var serviceA = await harness.ClientA.PrepareTestServiceAsync<ITestService>();
        var serviceB = await harness.ClientB.PrepareTestServiceAsync<ITestService>();
        var oneWayA = await harness.ClientA.PrepareTestServiceAsync<ICompressionService>();
        var oneWayB = await harness.ClientB.PrepareTestServiceAsync<ICompressionService>();
        Ensure(harness.ActiveConnections == 2, "the test has two actual data connections, not two proxies on one connection");

        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dropListener = new MeterListener();
        dropListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SharpLinkTelemetry.Meter.Name &&
                instrument.Name == "sharplink.admission.oneway.dropped")
                listener.EnableMeasurementEvents(instrument);
        };
        dropListener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (value == 1 && tag.Key == "sharplink.admission.reason" &&
                    tag.Value is "pre_admission_stream_bytes")
                    dropped.TrySetResult();
        });
        dropListener.Start();
        var producerRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var active = serviceA.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        Task? sendA = null;
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));

            sendA = oneWayA.NotifyStreamBytesAsync(TwoItemsThenWaitAsync(producerRelease.Task)).AsTask();
            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes > ItemBytes * 2 &&
                      harness.Kernel.QueuedCalls == 1 && harness.AdmissionQueuedBytes > 0,
                "A owns both a queued head and two separately charged stream items");

            var retainedByA = harness.PreAdmissionStreamBytes;
            var admissionBytesBeforeB = harness.AdmissionQueuedBytes;
            Ensure(retainedByA <= StreamBudgetBytes,
                "A pre-admission stream ownership must remain within the stable global budget");
            Ensure(admissionBytesBeforeB < retainedByA,
                "Dynamic Admission queued bytes must not include retained stream-frame bytes");

            // Both sends use the stock typed OneWay path, which has no per-request response.
            await oneWayB.NotifyStreamBytesAsync(SingleItemAsync(CreateItem(0x42))).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes == retainedByA &&
                      harness.Kernel.QueuedCalls == 1 && harness.AdmissionQueuedBytes == admissionBytesBeforeB,
                "B's exact stream-budget drop leaves A's buffer and queued-head ownership unchanged");
            Ensure(!active.IsCompleted && !CompressionService.WaitForOneWayAsync().IsCompleted,
                "B cannot enter the handler or release the independent permit owner");

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 2,
                "admission permit owner completes");
            producerRelease.TrySetResult();
            await sendA.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(await CompressionService.WaitForOneWayAsync().WaitAsync(TimeSpan.FromSeconds(5)) == ItemBytes * 2,
                "only A's buffered OneWay stream really replays after admission");

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
            if (sendA is not null)
                await ObserveTerminalAsync(sendA);
        }
    }

    [Test]
    [NotInParallel]
    public async Task ForceStopShouldReleaseBufferedStreamBudgetBeforeExit()
    {
        TestService.ResetBlockingAdd();
        CompressionService.ResetOneWay();
        await using var harness = await BudgetHarness.CreateAsync();
        var unary = await harness.ClientA.PrepareTestServiceAsync<ITestService>();
        var oneWay = await harness.ClientB.PrepareTestServiceAsync<ICompressionService>();
        var producerRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = unary.BlockingAddAsync(3, 4, CancellationToken.None).AsTask();
        Task? queued = null;

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            queued = oneWay.NotifyStreamBytesAsync(TwoItemsThenWaitAsync(producerRelease.Task)).AsTask();
            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes > ItemBytes * 2 &&
                      harness.AdmissionQueuedBytes > 0,
                "buffered stream ownership exists before force stop");

            await harness.StopServerAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(harness.PreAdmissionStreamBytes == 0 && harness.AdmissionQueuedBytes == 0 &&
                   harness.Kernel.QueuedCalls == 0 && !CompressionService.WaitForOneWayAsync().IsCompleted,
                "force Stop returns the server budgets without starting the queued handler or waiting for the producer gate");
            producerRelease.TrySetResult();
            await ObserveTerminalAsync(active);
            await ObserveTerminalAsync(queued);
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
        private readonly SharpLinkServer _server;
        private bool _disposed;

        private BudgetHarness(
            CancellationTokenSource serverCancellation,
            Task serverTask,
            SharpLinkServer server,
            ISharpLinkClient clientA,
            ISharpLinkClient clientB)
        {
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
            _server = server;
            Kernel = server.AdmissionStateKernelForTests ?? throw new InvalidOperationException("Expected admission state.");
            ClientA = clientA;
            ClientB = clientB;
        }

        internal ISharpLinkClient ClientA { get; }
        internal ISharpLinkClient ClientB { get; }

        internal AdmissionStateKernel Kernel { get; }
        internal int ActiveConnections => ServerRegistryTestAccessor.ActiveConnectionCount(_server);
        internal long PreAdmissionStreamBytes => _server.PreAdmissionStreamBytesForDiagnostics;
        internal long AdmissionQueuedBytes => Kernel.QueuedBytes;

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
                    options.QueueOneWayCalls = true;
                })
                .UseTcp(0, IPAddress.Loopback);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = (SharpLinkServer)serverBuilder.Build();
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

        private static ISharpLinkClient CreateClient(int port)
            => SharpClientBuilder.Create().DisableRequestTimeout()
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
                await server.RunUntilStoppedAsync(cancellationToken);
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
