using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpLink.IntegrationTests;

public sealed class DynamicAdmissionUpdateResourceRegressionTests
{
    private const int StreamItemBytes = 4 * 1024;
    private const long StreamBudgetBytes = 12L * 1024;

    [Test]
    [NotInParallel]
    public async Task RuntimeUpdateShouldPreserveCapacityRejectionBeforeDecompression()
    {
        TestService.ResetBlockingAdd();
        var serverProvider = new CountingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RunningHarness.CreateAsync(
            serverRuntimeConfigure: options =>
            {
                options.FlowControl.MaxConcurrentCallsPerServer = 1;
                options.Compression.Providers.Add(serverProvider);
            },
            admissionConfigure: options => options.Global.UseConcurrency(2),
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()));
        var publicServer = (ISharpLinkServer)harness.Server;
        var source = harness.Server.CurrentAdmissionProgramForTests
            ?? throw new Exception("resource update regression requires enabled Admission");
        var blocker = harness.ClientA.Get<ITestService>()
            .BlockingAddAsync(1, 2, CancellationToken.None).AsTask();

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            publicServer.UpdateAdmissionControl(options =>
            {
                options.Global.UseConcurrency(3);
                options.MaxQueuedCalls = 2;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(5);
            });
            var replacement = harness.Server.CurrentAdmissionProgramForTests
                ?? throw new Exception("runtime update must publish N+1");
            Ensure(!ReferenceEquals(source, replacement) && source.IsRetired,
                "runtime update must publish N+1 while retiring the source generation");
            Ensure(replacement.Controller.GlobalConcurrencyStateForTests?.PermitLimit == 3,
                "test must exercise a committed Admission concurrency resize");

            var decompressionsBeforeRejectedRequest = serverProvider.DecompressCount;
            var payload = Enumerable.Repeat((byte)0x51, 32 * 1024).ToArray();
            var failure = await CaptureFailureAsync(
                harness.ClientB.Get<ICompressionService>().EchoBytesAsync(payload).AsTask());

            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                "Admission update must not replace or bypass ServerResourceGovernor call capacity");
            Ensure(serverProvider.DecompressCount == decompressionsBeforeRejectedRequest,
                "capacity-rejected compressed Request after update must perform zero decompression");
            await WaitUntilAsync(
                () => harness.Server.ActiveDecodeCountForDiagnostics == 0 &&
                      harness.Server.DecodedBytesInFlightForDiagnostics == 0,
                "capacity rejection after update must leave zero decoded execution/rent accounting");

            TestService.ReleaseBlockingAdd();
            Ensure(await blocker.WaitAsync(TimeSpan.FromSeconds(5)) == 3,
                "capacity owner must complete normally after Admission update");

            var response = await harness.ClientB.Get<ICompressionService>()
                .EchoBytesAsync(payload).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(response.SequenceEqual(payload),
                "controlled capacity rejection after update must leave the connection reusable");
            Ensure(serverProvider.DecompressCount == decompressionsBeforeRejectedRequest + 1,
                "accepted compressed Request must decompress exactly once after capacity returns");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(blocker);
        }
    }

    [Test]
    [NotInParallel]
    public async Task PreAdmissionStreamAccountingShouldRemainExactAcrossRuntimeUpdate()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await RunningHarness.CreateAsync(
            serverRuntimeConfigure: options =>
            {
                options.FlowControl.MaxPreAdmissionStreamBytesPerServer = StreamBudgetBytes;
                options.FlowControl.StreamReceiveWindowBytes = 64 * 1024;
                options.FlowControl.ConnectionReceiveWindowBytes = 256 * 1024;
            },
            admissionConfigure: ConfigureInitialQueue);
        var publicServer = (ISharpLinkServer)harness.Server;
        var original = harness.Server.CurrentAdmissionProgramForTests
            ?? throw new Exception("stream update regression requires enabled Admission");
        var kernel = original.Kernel;
        var concurrency = original.Controller.GlobalConcurrencyStateForTests
            ?? throw new Exception("stream update regression requires global concurrency state");
        var producerRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = harness.ClientA.Get<ITestService>()
            .BlockingAddAsync(3, 4, CancellationToken.None).AsTask();
        Task<int>? queued = null;
        AdmissionProgram? replacement = null;

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            queued = harness.ClientB.Get<ICompressionService>()
                .UploadBytesAsync(TwoStreamItemsThenWaitAsync(producerRelease.Task)).AsTask();
            await WaitUntilAsync(
                () => harness.Server.PreAdmissionStreamBytesForDiagnostics > StreamItemBytes * 2 &&
                      kernel.QueuedCalls == 1 && kernel.QueuedBytes > 0 &&
                      concurrency.WaitingCount == 1,
                "queued generation N Request must own stream bytes, Admission bytes, and one inner waiter");

            var retainedStreamBytes = harness.Server.PreAdmissionStreamBytesForDiagnostics;
            var retainedAdmissionBytes = kernel.QueuedBytes;
            publicServer.UpdateAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 32 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(5);
            });
            replacement = harness.Server.CurrentAdmissionProgramForTests
                ?? throw new Exception("stream update regression requires N+1");

            Ensure(original.IsRetired && !original.IsReclaimed,
                "runtime update must retain generation N while its queued stream Request is captured");
            Ensure(ReferenceEquals(kernel, replacement.Kernel) &&
                   ReferenceEquals(concurrency, replacement.Controller.GlobalConcurrencyStateForTests),
                "queue-only update must preserve the stable kernel and concurrency state");
            Ensure(harness.Server.PreAdmissionStreamBytesForDiagnostics == retainedStreamBytes,
                "runtime update must not transfer, duplicate, or release physical pre-admission stream bytes");
            Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == retainedAdmissionBytes &&
                   concurrency.WaitingCount == 1,
                "runtime update must preserve exactly one old queue reservation and one underlying waiter");

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 7,
                "old active Request must complete after N+1 publication");
            producerRelease.TrySetResult();
            Ensure(await queued.WaitAsync(TimeSpan.FromSeconds(5)) == StreamItemBytes * 2,
                "queued generation N stream must replay and complete after runtime update");

            await WaitUntilAsync(
                () => harness.Server.PreAdmissionStreamBytesForDiagnostics == 0 &&
                      kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 &&
                      concurrency.WaitingCount == 0 && original.IsReclaimed,
                "stream bytes, Admission reservation, waiter, and retired generation must all drain exactly");
            Ensure(original.ReclaimCount == 1 && original.DuplicateReleaseAttempts == 0,
                "generation N stream path must reclaim exactly once without duplicate release");
            Ensure(await harness.ClientB.Get<ITestService>().AddAsync(20, 22) == 42,
                "queued-stream update must leave the connection reusable");

            publicServer.DisableAdmissionControl();
            await WaitUntilAsync(() => replacement.IsReclaimed,
                "N+1 must reclaim after final disable");
            AssertAdmissionKernelEmpty(kernel, "runtime-updated queued stream");
        }
        finally
        {
            producerRelease.TrySetResult();
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (queued is not null)
                await ObserveTerminalAsync(queued);
            if (harness.Server.CurrentAdmissionProgramForTests is not null)
            {
                try
                {
                    publicServer.DisableAdmissionControl();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    private static void ConfigureInitialQueue(SharpLinkAdmissionControlOptions options)
    {
        options.Global.UseConcurrency(1);
        options.MaxQueuedCalls = 2;
        options.MaxQueuedBytes = 64 * 1024;
        options.MaxQueueDelay = TimeSpan.FromSeconds(10);
    }

    private static byte[] CreateStreamItem(byte value)
        => Enumerable.Repeat(value, StreamItemBytes).ToArray();

    private static async IAsyncEnumerable<byte[]> TwoStreamItemsThenWaitAsync(
        Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return CreateStreamItem(0x61);
        await Task.Yield();
        yield return CreateStreamItem(0x62);
        await release.WaitAsync(cancellationToken);
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

    private static void AssertAdmissionKernelEmpty(AdmissionStateKernel kernel, string scenario)
        => Ensure(
            kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
            kernel.ConcurrencyStateCount == 0 && kernel.RateStateCount == 0 &&
            kernel.PartitionStateCount == 0 && kernel.QueuedCalls == 0 &&
            kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            $"{scenario}: Admission lifecycle diagnostics must return to zero");

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class CountingCompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private int _decompressCount;

        public string WireProfile => inner.WireProfile;
        internal int DecompressCount => Volatile.Read(ref _decompressCount);

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

    private sealed class RunningHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private bool _disposed;

        private RunningHarness(
            CancellationTokenSource serverCancellation,
            Task serverTask,
            SharpLinkServer server,
            ISharpLinkClient clientA,
            ISharpLinkClient clientB)
        {
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
            Server = server;
            ClientA = clientA;
            ClientB = clientB;
        }

        internal SharpLinkServer Server { get; }
        internal ISharpLinkClient ClientA { get; }
        internal ISharpLinkClient ClientB { get; }

        internal static async Task<RunningHarness> CreateAsync(
            Action<SharpLinkRuntimeOptions>? serverRuntimeConfigure = null,
            Action<SharpLinkAdmissionControlOptions>? admissionConfigure = null,
            Action<SharpLinkRuntimeOptions>? clientRuntimeConfigure = null)
        {
            var serverCancellation = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (serverRuntimeConfigure is not null)
                serverBuilder.UseRuntime(serverRuntimeConfigure);
            if (admissionConfigure is not null)
                serverBuilder.UseAdmissionControl(admissionConfigure);
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = (SharpLinkServer)serverBuilder.Build();
            var serverTask = RunServerAsync(server, serverCancellation.Token);

            var clientA = CreateClient(port, clientRuntimeConfigure);
            var clientB = CreateClient(port, clientRuntimeConfigure);
            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            return new RunningHarness(serverCancellation, serverTask, server, clientA, clientB);
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
                    await Server.StopAsync(TimeSpan.Zero);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
                await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
                _serverCancellation.Dispose();
            }
        }

        private static ISharpLinkClient CreateClient(
            int port,
            Action<SharpLinkRuntimeOptions>? runtimeConfigure)
        {
            var builder = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (runtimeConfigure is not null)
                builder.UseRuntime(runtimeConfigure);
            return builder.UseTcp(IPAddress.Loopback.ToString(), port).Build();
        }

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
}
