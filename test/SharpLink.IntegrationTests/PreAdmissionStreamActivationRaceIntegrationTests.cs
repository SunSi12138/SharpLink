namespace SharpLink.IntegrationTests;

public class PreAdmissionStreamActivationRaceIntegrationTests
{
    private const int FirstItemBytes = 4 * 1024;
    private const int OverflowItemBytes = 16 * 1024;
    private const long StreamBudgetBytes = 12L * 1024;

    [Test]
    [NotInParallel]
    public async Task OneWayStreamBudgetCancellationAfterAdmissionShouldPreventInvocation()
    {
        PreAdmissionStreamActivationRaceService.Reset();
        TestService.ResetBlockingAdd();
        await using var harness = await RaceHarness.CreateAsync();
        var unary = await harness.ClientA.PrepareTestServiceAsync<ITestService>();
        var recovery = await harness.ClientB.PrepareTestServiceAsync<ITestService>();
        var stream = await harness.ClientB.PrepareTestServiceAsync<IPreAdmissionStreamActivationRaceService>();
        var overflowRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activationEntered = new TaskCompletionSource<ServerCallCancellationState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var activationRelease = new ManualResetEventSlim();
        Task? target = null;
        var active = unary.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            target = stream.NotifyAsync(TwoItemsAsync(overflowRelease.Task)).AsTask();
            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes > 0,
                "one-way stream is physically buffered while admission waits");

            ServerCallCancellationState.BeforeRequestActivationForTests = state =>
            {
                if (activationEntered.TrySetResult(state))
                    activationRelease.Wait(TimeSpan.FromSeconds(5));
            };

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 2,
                "active admission owner completes before target activation");
            var callState = await activationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(callState.Reason == ServerCallCancellationReason.None,
                "admission must be acquired before the forced stream-budget race");

            overflowRelease.TrySetResult();
            await WaitUntilAsync(
                () => callState.Reason ==
                    ServerCallCancellationReason.PreAdmissionStreamResourceExhausted,
                "stream-budget cancellation wins before one-way activation");

            activationRelease.Set();
            await ObserveTerminalAsync(target);
            await WaitUntilAsync(
                () => harness.PreAdmissionStreamBytes == 0,
                "one-way race releases stream-buffer ownership");
            await Task.Delay(50);
            Ensure(PreAdmissionStreamActivationRaceService.OneWayInvocations == 0,
                "one-way user code must not run after stream-budget terminal wins");
            Ensure(await recovery.AddAsync(20, 22) == 42,
                "one-way race leaves the connection usable");
        }
        finally
        {
            ServerCallCancellationState.BeforeRequestActivationForTests = null;
            activationRelease.Set();
            overflowRelease.TrySetResult();
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (target is not null)
                await ObserveTerminalAsync(target);
        }
    }

    [Test]
    [NotInParallel]
    public async Task TwoWayProducerShouldWaitForActivationAndRequestAccepted()
    {
        PreAdmissionStreamActivationRaceService.Reset();
        TestService.ResetBlockingAdd();
        await using var harness = await RaceHarness.CreateAsync();
        var unary = await harness.ClientA.PrepareTestServiceAsync<ITestService>();
        var recovery = await harness.ClientB.PrepareTestServiceAsync<ITestService>();
        var stream = await harness.ClientB.PrepareTestServiceAsync<IPreAdmissionStreamActivationRaceService>();
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationEntered = new TaskCompletionSource<ServerCallCancellationState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var activationRelease = new ManualResetEventSlim();
        Task<int>? target = null;
        var active = unary.BlockingAddAsync(3, 4, CancellationToken.None).AsTask();

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            target = stream.UploadAsync(TwoItemsAsync(Task.CompletedTask, () => producerStarted.TrySetResult())).AsTask();
            await WaitUntilAsync(() => harness.QueuedCalls == 1, "the two-way head is actually queued");
            Ensure(!producerStarted.Task.IsCompleted && harness.PreAdmissionStreamBytes == 0,
                "waiting for admission does not start the producer or buffer data");

            ServerCallCancellationState.BeforeRequestActivationForTests = state =>
            {
                if (activationEntered.TrySetResult(state))
                    activationRelease.Wait(TimeSpan.FromSeconds(5));
            };

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 7,
                "active admission owner completes before target activation");
            var callState = await activationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(callState.Reason == ServerCallCancellationReason.None && !target.IsCompleted &&
                   !producerStarted.Task.IsCompleted && harness.PreAdmissionStreamBytes == 0 &&
                   PreAdmissionStreamActivationRaceService.TwoWayInvocations == 0,
                "a permit alone cannot start a producer while activation has not installed the routes");

            activationRelease.Set();
            Ensure(await target.WaitAsync(TimeSpan.FromSeconds(5)) == FirstItemBytes + OverflowItemBytes &&
                   producerStarted.Task.IsCompleted && PreAdmissionStreamActivationRaceService.TwoWayInvocations == 1,
                "the accepted two-way upload receives both items, including one larger than the pre-admission cap");
            Ensure(harness.PreAdmissionStreamBytes == 0 && await recovery.AddAsync(20, 22) == 42,
                "accepted traffic leaves no pre-admission bytes and does not interrupt the connection");
        }
        finally
        {
            ServerCallCancellationState.BeforeRequestActivationForTests = null;
            activationRelease.Set();
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (target is not null)
                await ObserveTerminalAsync(target);
        }
    }

    private static async IAsyncEnumerable<byte[]> TwoItemsAsync(
        Task releaseSecond, Action? onStarted = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        onStarted?.Invoke();
        yield return CreateItem(0x51, FirstItemBytes);
        await releaseSecond.WaitAsync(cancellationToken);
        yield return CreateItem(0x52, OverflowItemBytes);
    }

    private static byte[] CreateItem(byte value, int length)
        => Enumerable.Repeat(value, length).ToArray();

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

    private sealed class RaceHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _disposed;

        private RaceHarness(
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

        internal long PreAdmissionStreamBytes => ((SharpLinkServer)_server).PreAdmissionStreamBytesForDiagnostics;
        internal int QueuedCalls => ((SharpLinkServer)_server).AdmissionStateKernelForTests!.QueuedCalls;

        internal static async Task<RaceHarness> CreateAsync()
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
                    options.QueueOneWayCalls = true;
                    options.MaxQueuedCalls = 2;
                    options.MaxQueuedBytes = 64 * 1024;
                    options.MaxQueueDelay = TimeSpan.FromSeconds(10);
                })
                .UseTcp(0, IPAddress.Loopback);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = RunServerAsync(server, serverCancellation.Token);

            var clientA = CreateClient(port);
            var clientB = CreateClient(port);
            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            return new RaceHarness(
                serverCancellation,
                serverTask,
                server,
                clientA,
                clientB);
        }

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

[RpcContract]
public interface IPreAdmissionStreamActivationRaceService : IService
{
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(IAsyncEnumerable<byte[]> values);

    [NonCancellable]
    ValueTask<int> UploadAsync(IAsyncEnumerable<byte[]> values);
}

[RpcService]
public sealed class PreAdmissionStreamActivationRaceService : IPreAdmissionStreamActivationRaceService
{
    private static int s_oneWayInvocations;
    private static int s_twoWayInvocations;

    internal static int OneWayInvocations => Volatile.Read(ref s_oneWayInvocations);
    internal static int TwoWayInvocations => Volatile.Read(ref s_twoWayInvocations);

    internal static void Reset()
    {
        Volatile.Write(ref s_oneWayInvocations, 0);
        Volatile.Write(ref s_twoWayInvocations, 0);
    }

    public async ValueTask NotifyAsync(IAsyncEnumerable<byte[]> values)
    {
        Interlocked.Increment(ref s_oneWayInvocations);
        await foreach (var _ in values)
        {
        }
    }

    public async ValueTask<int> UploadAsync(IAsyncEnumerable<byte[]> values)
    {
        Interlocked.Increment(ref s_twoWayInvocations);
        var total = 0;
        await foreach (var value in values)
            total += value.Length;
        return total;
    }
}
