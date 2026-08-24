namespace SharpLink.IntegrationTests;

public sealed class DynamicAdmissionRuntimeControlTests
{
    [Test]
    [NotInParallel]
    public async Task InitiallyDisabledPublicEnableShouldGovernNextRequest()
    {
        await using var harness = await Harness.CreateAsync();
        var publicServer = (ISharpLinkServer)harness.Server;
        publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(1));
        var program = harness.Server.CurrentAdmissionProgramForTests
            ?? throw new Exception("public enable must publish an admission program");
        var held = await program.Controller.AcquireAsync(
            CreateAdmissionContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired, "test must occupy the newly enabled global permit");

        try
        {
            var failure = await CaptureFailureAsync(
                harness.ClientA.Get<ITestService>().AddAsync(20, 22).AsTask());
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                "request captured after public enable returns must be governed by the new program");
        }
        finally
        {
            held.Lease!.Dispose();
        }

        Ensure(await harness.ClientA.Get<ITestService>().AddAsync(20, 22) == 42,
            "connection must remain reusable after controlled admission rejection");
        publicServer.DisableAdmissionControl();
        await WaitUntilAsync(() => program.IsReclaimed,
            "disabled public generation reclaims after its final request releases");
        AssertKernelEmpty(harness.Server.AdmissionStateKernelForTests!, "enable/disable request path");
    }

    [Test]
    [NotInParallel]
    public async Task RequestCapturedDisabledShouldRemainBypassWhenPublicEnablePublishes()
    {
        await using var harness = await Harness.CreateAsync();
        var publicServer = (ISharpLinkServer)harness.Server;
        AdmissionDecision held = default;
        AdmissionProgram? replacement = null;
        var hookCount = 0;

        try
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = (owner, _, observed) =>
            {
                if (!ReferenceEquals(owner, harness.Server) || observed is not null ||
                    Interlocked.Exchange(ref hookCount, 1) != 0)
                    return;

                publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(1));
                replacement = owner.CurrentAdmissionProgramForTests
                    ?? throw new Exception("public enable must publish inside the capture seam");
                held = replacement.Controller.AcquireAsync(
                    CreateAdmissionContext(), 1, allowQueue: false, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Ensure(held.IsAcquired, "test must occupy the newly published permit");
            };

            Ensure(await harness.ClientA.Get<ITestService>().AddAsync(20, 22) == 42,
                "request that captured disabled must bypass the later public enable");
            Ensure(replacement is not null,
                "capture seam must have published the public enabled generation");
            SharpLinkServer.AfterAdmissionCaptureForTests = null;

            var nextFailure = await CaptureFailureAsync(
                harness.ClientA.Get<ITestService>().AddAsync(20, 22).AsTask());
            Ensure(nextFailure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                "next request must observe the public enabled publication");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = null;
            held.Lease?.Dispose();
            publicServer.DisableAdmissionControl();
        }

        if (replacement is not null)
        {
            await WaitUntilAsync(() => replacement.IsReclaimed,
                "public replacement must reclaim after disable and final use release");
        }
        AssertKernelEmpty(harness.Server.AdmissionStateKernelForTests!, "disabled-capture transition");
    }

    [Test]
    [NotInParallel]
    public async Task PublicDisableShouldBypassNextRequestWhileCapturedActiveRequestCompletes()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await Harness.CreateAsync(
            admissionConfigure: options => options.Global.UseConcurrency(1));
        var publicServer = (ISharpLinkServer)harness.Server;
        var original = harness.Server.CurrentAdmissionProgramForTests!;
        var service = harness.ClientA.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            publicServer.DisableAdmissionControl();
            publicServer.DisableAdmissionControl();
            Ensure(original.IsRetired && !original.IsReclaimed && original.ActiveUses == 1,
                "disable must retire the current program without cancelling its captured active request");
            Ensure(await harness.ClientB.Get<ITestService>().AddAsync(20, 22) == 42,
                "request captured after disable returns must bypass admission immediately");
            Ensure(!active.IsCompleted,
                "public disable must not cancel the already-admitted active request");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 2,
            "old-generation active request must complete normally after disable");
        await WaitUntilAsync(() => original.IsReclaimed,
            "old active generation reclaims on terminal release");
        Ensure(original.ReclaimCount == 1 && original.DuplicateReleaseAttempts == 0,
            "old active generation must reclaim and release exactly once");
        AssertKernelEmpty(harness.Server.AdmissionStateKernelForTests!, "active disable");
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedOldGenerationShouldContinueAfterPublicDisable(bool oneWay)
    {
        TestService.ResetBlockingAdd();
        TestService.ResetNotify();
        await using var harness = await Harness.CreateAsync(
            admissionConfigure: options =>
            {
                options.Global.UseConcurrency(1);
                options.QueueOneWayCalls = true;
                options.MaxQueuedCalls = 2;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            });
        var publicServer = (ISharpLinkServer)harness.Server;
        var original = harness.Server.CurrentAdmissionProgramForTests!;
        var service = harness.ClientA.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        Task<int>? queuedTwoWay = null;

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (oneWay)
                await service.NotifyAsync("runtime-disable-queued");
            else
                queuedTwoWay = service.AddAsync(20, 22).AsTask();

            await WaitUntilAsync(() => original.Controller.QueuedCalls == 1,
                "target request must enter the enabled generation queue before disable");
            publicServer.DisableAdmissionControl();
            Ensure(original.IsRetired && !original.IsReclaimed && original.ActiveUses == 2,
                "active and queued captures must keep the retired generation alive");
            Ensure(await harness.ClientB.Get<ITestService>().AddAsync(3, 4) == 7,
                "new request must bypass admission while old queued work remains retained");

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 2,
                "old-generation active owner completes");
            if (oneWay)
            {
                await TestService.WaitForNotifyAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Ensure(TestService.NotifyCount == 1,
                    "queued one-way capture must execute after its old permit becomes available");
            }
            else
            {
                Ensure(await queuedTwoWay!.WaitAsync(TimeSpan.FromSeconds(5)) == 42,
                    "queued two-way capture must execute after its old permit becomes available");
            }

            await WaitUntilAsync(() => original.IsReclaimed,
                "retired queued generation must reclaim after final queued completion");
            Ensure(original.ReclaimCount == 1 && original.DuplicateReleaseAttempts == 0,
                "queued retirement must reclaim and release exactly once");
            AssertKernelEmpty(harness.Server.AdmissionStateKernelForTests!, "queued disable");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (queuedTwoWay is not null)
                await ObserveTerminalAsync(queuedTwoWay);
        }
    }

    [Test]
    [NotInParallel]
    public async Task ServerCallCapacityShouldRemainEnforcedAfterRuntimeAdmissionDisable()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await Harness.CreateAsync(
            serverRuntimeConfigure: options => options.FlowControl.MaxConcurrentCallsPerServer = 1,
            admissionConfigure: options => options.Global.UseConcurrency(2));
        var publicServer = (ISharpLinkServer)harness.Server;
        var service = harness.ClientA.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            publicServer.DisableAdmissionControl();
            var failure = await CaptureFailureAsync(
                harness.ClientB.Get<ITestService>().AddAsync(20, 22).AsTask());
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                "ServerResourceGovernor call capacity must remain enforced while admission is runtime-disabled");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 2,
            "resource-governor owner must complete normally");
        Ensure(await harness.ClientB.Get<ITestService>().AddAsync(20, 22) == 42,
            "controlled call-capacity rejection must keep the connection reusable");
        AssertKernelEmpty(harness.Server.AdmissionStateKernelForTests!, "runtime-disabled resource governor");
    }

    private static SharpLinkAdmissionContext CreateAdmissionContext()
        => new(1, 2, RpcMethodKind.Unary, "runtime-control-integration", null, null, null);

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

    private static void AssertKernelEmpty(AdmissionStateKernel kernel, string scenario)
        => Ensure(
            kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
            kernel.RuleStateCount == 0 && kernel.PartitionStateCount == 0 &&
            kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            $"{scenario}: admission lifecycle diagnostics must return to zero");

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private bool _disposed;

        private Harness(
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

        internal static async Task<Harness> CreateAsync(
            Action<SharpLinkRuntimeOptions>? serverRuntimeConfigure = null,
            Action<SharpLinkAdmissionControlOptions>? admissionConfigure = null)
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

            var clientA = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .Build();
            var clientB = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .Build();
            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            return new Harness(serverCancellation, serverTask, server, clientA, clientB);
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
