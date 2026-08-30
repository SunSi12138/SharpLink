namespace SharpLink.IntegrationTests;

public sealed class DynamicAdmissionStateKernelIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task StalePublicationReadShouldRetryAndNeverAttachRetiredGeneration()
    {
        await using var harness = await Harness.CreateAsync(
            options => options.Global.UseConcurrency(1));
        var original = harness.Server.OwnedAdmissionProgramForTests!;
        var replacement = harness.Server.CreateAdmissionProgramForTests(
            options => options.Global.UseConcurrency(2));
        AdmissionProgram? captured = null;
        var readHookCount = 0;

        try
        {
            SharpLinkServer.AfterAdmissionPublicationReadForTests = (server, _, observed) =>
            {
                if (!ReferenceEquals(server, harness.Server) ||
                    !ReferenceEquals(observed, original) ||
                    Interlocked.Exchange(ref readHookCount, 1) != 0)
                {
                    return;
                }
                server.PublishAdmissionProgramForTests(replacement);
            };
            SharpLinkServer.AfterAdmissionCaptureForTests = (server, _, observed) =>
            {
                if (ReferenceEquals(server, harness.Server))
                    captured = observed;
            };

            Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
                "request must retry to N+1 after its stale N read loses the retire/use CAS race");
            await WaitUntilAsync(() => original.IsReclaimed && replacement.ActiveUses == 0,
                "stale N publication reclaims and N+1 request releases its use");
            Ensure(ReferenceEquals(captured, replacement),
                "stale read must never attach a new use to retired generation N");
            Ensure(original.ActiveUses == 0 && original.ReclaimCount == 1,
                "retired stale generation must have no post-retire users and reclaim exactly once");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionPublicationReadForTests = null;
            SharpLinkServer.AfterAdmissionCaptureForTests = null;
            TryDisableAdmission(harness.Server);
        }
    }

    [Test]
    [NotInParallel]
    public async Task DisabledCaptureShouldRemainAllocationAndRefcountFree()
    {
        await using var harness = await Harness.CreateAsync();
        var kernel = harness.Server.AdmissionStateKernelForTests!;
        Ensure(harness.Server.CaptureAdmissionProgramForTests() is null,
            "disabled capture warmup must return the disabled sentinel without a generation use");

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 4096; index++)
        {
            if (harness.Server.CaptureAdmissionProgramForTests(index) is not null)
                throw new Exception("assert failed: disabled capture unexpectedly produced a program");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Ensure(allocated == 0,
            $"disabled capture fast path must allocate zero bytes; observed {allocated}");
        Ensure(kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
               kernel.RuleStateCount == 0 && kernel.PartitionStateCount == 0,
            "disabled capture must not create generation refcounts or mutable admission state");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "disabled request path remains functional");
        Ensure(kernel.LiveProgramCount == 0 && kernel.QueuedCalls == 0 &&
               kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            "disabled request path must leave admission accounting untouched");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayShouldUseCapturedPolicyAcrossCompatiblePublication()
    {
        TestService.ResetBlockingAdd();
        TestService.ResetNotify();
        await using var harness = await Harness.CreateAsync(ConfigureQueuedOneWay);
        var original = harness.Server.OwnedAdmissionProgramForTests!;
        var replacement = harness.Server.CreateAdmissionProgramForTests(options =>
        {
            ConfigureQueuedOneWay(options);
            options.QueueOneWayCalls = false;
        });
        var service = harness.Client.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        var hookCount = 0;

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            SharpLinkServer.AfterAdmissionCaptureForTests = (server, _, observed) =>
            {
                if (!ReferenceEquals(server, harness.Server) ||
                    !ReferenceEquals(observed, original) ||
                    Interlocked.Exchange(ref hookCount, 1) != 0)
                {
                    return;
                }
                server.PublishAdmissionProgramForTests(replacement);
            };

            await service.NotifyAsync("captured-queue-one-way");
            await WaitUntilAsync(() => original.Kernel.QueuedCalls == 1,
                "one-way request captured under N must queue under N policy after N+1 publication");
            Ensure(TestService.NotifyCount == 0,
                "captured queue-one-way request must not be reinterpreted by N+1 QueueOneWayCalls=false");
            Ensure(ReferenceEquals(
                    original.Controller.GlobalStateForTests,
                    replacement.Controller.GlobalStateForTests),
                "policy-only QueueOneWay change must still share compatible limiter state");

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 2,
                "active owner completes and releases shared permit");
            await TestService.WaitForNotifyAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => original.Kernel.QueuedCalls == 0 && original.Kernel.QueuedBytes == 0 &&
                      original.Kernel.ActivePermits == 0,
                "captured one-way queue accounting drains through stable kernel");
            Ensure(TestService.NotifyCount == 1,
                "captured N one-way request executes exactly once");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = null;
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            TryDisableAdmission(harness.Server);
        }
    }

    [Test]
    [NotInParallel]
    public async Task StopRacingStaleCaptureShouldNotAttachRetiredProgram()
    {
        await using var harness = await Harness.CreateAsync(
            options => options.Global.UseConcurrency(2));
        var original = harness.Server.OwnedAdmissionProgramForTests!;
        var kernel = original.Kernel;
        Task? stopTask = null;
        var hookCount = 0;

        try
        {
            SharpLinkServer.AfterAdmissionPublicationReadForTests = (server, _, observed) =>
            {
                if (!ReferenceEquals(server, harness.Server) ||
                    !ReferenceEquals(observed, original) ||
                    Interlocked.Exchange(ref hookCount, 1) != 0)
                {
                    return;
                }
                stopTask = server.StopAsync(TimeSpan.Zero).AsTask();
                Ensure(kernel.IsDraining, "Stop must seal and cancel admission before stale capture resumes");
            };

            var failure = await CaptureFailureAsync(
                harness.Client.Get<ITestService>().AddAsync(20, 22).AsTask());
            Ensure(failure is not ObjectDisposedException,
                "Stop-vs-capture must terminate through controlled shutdown, never disposed limiter state");
            Ensure(stopTask is not null, "deterministic capture hook must start Stop");
            await stopTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(original.IsRetired && original.IsReclaimed && original.ActiveUses == 0,
                "stale capture must not add a use after Stop retires the publication");
            AssertKernelDrained(kernel, "Stop-vs-capture");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionPublicationReadForTests = null;
        }
    }

    [Test]
    [NotInParallel]
    public async Task StopSealShouldRejectPublicationAndRetireUnpublishedCandidate()
    {
        await using var harness = await Harness.CreateAsync(
            options => options.Global.UseConcurrency(1));
        var original = harness.Server.OwnedAdmissionProgramForTests!;
        var candidate = harness.Server.CreateAdmissionProgramForTests(
            options => options.Global.UseConcurrency(2));
        var kernel = original.Kernel;

        var stopTask = harness.Server.StopAsync(TimeSpan.Zero).AsTask();
        await WaitUntilAsync(() => kernel.IsDraining,
            "Stop seals admission publication/control plane");

        var publicationFailure = CaptureSynchronousFailure(
            () => harness.Server.PublishAdmissionProgramForTests(candidate));
        Ensure(publicationFailure is InvalidOperationException,
            "no admission publication may succeed after Stop seals the control plane");
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Ensure(original.IsRetired && original.ReclaimCount == 1,
            "Stop must retire and reclaim the current program exactly once");
        Ensure(candidate.IsRetired && candidate.ReclaimCount == 1,
            "Stop must also retire an already-built live candidate exactly once");
        AssertKernelDrained(kernel, "Stop-vs-publication");
    }

    [Test]
    [NotInParallel]
    public async Task StopShouldWaitForActiveUseOfAlreadyRetiredGeneration()
    {
        await using var harness = await Harness.CreateAsync(
            options => options.Global.UseConcurrency(1));
        var original = harness.Server.OwnedAdmissionProgramForTests!;
        var replacement = harness.Server.CreateAdmissionProgramForTests(
            options => options.Global.UseConcurrency(2));
        var kernel = original.Kernel;

        Ensure(original.TryAcquireUse(), "test must hold one pre-retire generation use");
        harness.Server.PublishAdmissionProgramForTests(replacement);
        Ensure(original.IsRetired && !original.IsReclaimed && original.ActiveUses == 1,
            "ordinary replacement retires N without invalidating its active use");

        var stopTask = harness.Server.StopAsync(TimeSpan.Zero).AsTask();
        await WaitUntilAsync(() => kernel.IsDraining && replacement.IsRetired,
            "Stop retires the current replacement while old use remains live");
        Ensure(!original.IsReclaimed,
            "retired N must remain alive until its pre-retire use reaches terminal release");

        original.ReleaseUse();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(original.IsReclaimed && original.ReclaimCount == 1,
            "last old-generation use release must unblock exact-once reclamation");
        Ensure(replacement.IsReclaimed && replacement.ReclaimCount == 1,
            "current generation also reclaims exactly once during Stop");
        AssertKernelDrained(kernel, "Stop with active retired generation use");
    }

    [Test]
    [NotInParallel]
    public async Task StopShouldDrainQueuedOldGenerationWithoutDisposedState()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.Global.UseConcurrency(1);
            options.MaxQueuedCalls = 1;
            options.MaxQueuedBytes = 64 * 1024;
            options.MaxQueueDelay = TimeSpan.FromSeconds(10);
        });
        var original = harness.Server.OwnedAdmissionProgramForTests!;
        var replacement = harness.Server.CreateAdmissionProgramForTests(options =>
        {
            options.Global.UseConcurrency(1);
            options.MaxQueuedCalls = 1;
            options.MaxQueuedBytes = 64 * 1024;
            options.MaxQueueDelay = TimeSpan.FromSeconds(10);
        });
        var kernel = original.Kernel;
        var service = harness.Client.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        Task<int>? queued = null;

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            queued = service.AddAsync(20, 22).AsTask();
            await WaitUntilAsync(() => kernel.QueuedCalls == 1 && original.ActiveUses == 2,
                "generation N owns both active and queued requests before replacement");
            harness.Server.PublishAdmissionProgramForTests(replacement);
            Ensure(original.IsRetired && !original.IsReclaimed,
                "queued/active N requests must retain retired program ownership");

            var stopTask = harness.Server.StopAsync(TimeSpan.Zero).AsTask();
            var queuedFailure = await CaptureFailureAsync(queued);
            Ensure(queuedFailure is not ObjectDisposedException,
                "Stop must cancel old-generation queue work without disposing state underneath it");
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

            Ensure(original.IsReclaimed && original.ReclaimCount == 1,
                "old queued/active generation reclaims once after both requests terminate");
            Ensure(replacement.IsReclaimed && replacement.ReclaimCount == 1,
                "replacement generation reclaims once during Stop");
            AssertKernelDrained(kernel, "Stop with queued old generation");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (queued is not null)
                await ObserveTerminalAsync(queued);
        }
    }

    private static void ConfigureQueuedOneWay(SharpLinkAdmissionControlOptions options)
    {
        options.Global.UseConcurrency(1);
        options.QueueOneWayCalls = true;
        options.MaxQueuedCalls = 1;
        options.MaxQueuedBytes = 64 * 1024;
        options.MaxQueueDelay = TimeSpan.FromSeconds(10);
    }

    private static void AssertKernelDrained(AdmissionStateKernel kernel, string scenario)
        => Ensure(
            kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && kernel.ActivePermits == 0 &&
            kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
            kernel.RuleStateCount == 0 && kernel.PartitionStateCount == 0,
            $"{scenario}: Stop must drain all admission diagnostics and registries to zero");

    private static void TryDisableAdmission(SharpLinkServer server)
    {
        try
        {
            server.PublishAdmissionProgramForTests(null);
        }
        catch (InvalidOperationException) when (server.AdmissionStateKernelForTests?.IsDraining == true)
        {
        }
    }

    private static Exception? CaptureSynchronousFailure(Action action)
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

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private bool _disposed;

        private Harness(
            CancellationTokenSource serverCancellation,
            Task serverTask,
            SharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
            Server = server;
            Client = client;
        }

        internal SharpLinkServer Server { get; }
        internal ISharpLinkClient Client { get; }

        internal static async Task<Harness> CreateAsync(
            Action<SharpLinkAdmissionControlOptions>? admissionConfigure = null)
        {
            var serverCancellation = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (admissionConfigure is not null)
                serverBuilder.UseAdmissionControl(admissionConfigure);
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = (SharpLinkServer)serverBuilder.Build();
            var serverTask = RunServerAsync(server, serverCancellation.Token);
            var client = SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .Build();
            await client.ConnectAsync();
            return new Harness(serverCancellation, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await StopClientAsync(Client);
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
