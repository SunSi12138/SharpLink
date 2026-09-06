namespace SharpLink.IntegrationTests;

public class DynamicAdmissionGenerationTests
{
    [Test]
    [NotInParallel]
    [Arguments(true)]
    [Arguments(false)]
    public async Task EnabledCaptureShouldRemainEnabledWhenCurrentBecomesDisabled(bool oneWay)
    {
        TestService.ResetNotify();
        await using var harness = await Harness.CreateAsync(
            admissionConfigure: options => options.Global.UseConcurrency(1));
        var program = harness.OwnedProgram
            ?? throw new Exception("enabled server must expose its initial admission program");
        var held = await program.Controller.AcquireAsync(
            CreateAdmissionContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired, "test must occupy the captured generation before the request");
        AdmissionProgram? captured = null;
        var hookCount = 0;
        var captureCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = (server, _, observed) =>
            {
                if (!ReferenceEquals(server, harness.Server) ||
                    Interlocked.Exchange(ref hookCount, 1) != 0)
                    return;
                captured = observed;
                server.PublishAdmissionProgramForTests(null);
                captureCompleted.TrySetResult();
            };

            var service = harness.ClientA.Get<ITestService>();
            if (oneWay)
            {
                await service.NotifyAsync("captured-enabled");
                await captureCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SharpLinkServer.AfterAdmissionCaptureForTests = null;
                Ensure(await service.AddAsync(20, 22) == 42,
                    "the new disabled publication must be usable by the next request");
                Ensure(TestService.NotifyCount == 0,
                    "the already-captured enabled one-way request must still be rejected");
            }
            else
            {
                var failure = await CaptureFailureAsync(service.AddAsync(20, 22).AsTask());
                Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                    "the already-captured enabled two-way request must still be rejected");
            }

            Ensure(ReferenceEquals(captured, program),
                "request must retain the exact enabled generation captured before publication change");
            await WaitUntilAsync(() => program.ActiveUses == 0,
                "enabled capture use returns to zero after rejection");
            Ensure(program.DuplicateReleaseAttempts == 0,
                "enabled capture must not be released twice");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = null;
            held.Lease?.Dispose();
        }
    }

    [Test]
    [NotInParallel]
    public async Task EnabledCaptureShouldRemainOnGenerationNWhenCurrentBecomesNPlusOne()
    {
        await using var harness = await Harness.CreateAsync(
            admissionConfigure: options => options.Global.UseConcurrency(1));
        var original = harness.OwnedProgram
            ?? throw new Exception("enabled server must expose its initial admission program");
        var held = await original.Controller.AcquireAsync(
            CreateAdmissionContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired, "test must occupy generation N");
        var replacement = harness.Server.CreateAdmissionProgramForTests(
            options => options.Global.UseConcurrency(2));
        AdmissionProgram? captured = null;
        var hookCount = 0;

        try
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = (server, _, observed) =>
            {
                if (!ReferenceEquals(server, harness.Server) ||
                    Interlocked.Exchange(ref hookCount, 1) != 0)
                    return;
                captured = observed;
                server.PublishAdmissionProgramForTests(replacement);
            };

            var service = harness.ClientA.Get<ITestService>();
            var failure = await CaptureFailureAsync(service.AddAsync(1, 2).AsTask());
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                "request captured from N must not switch to the available N+1 generation");
            SharpLinkServer.AfterAdmissionCaptureForTests = null;
            Ensure(await service.AddAsync(20, 22) == 42,
                "the next request must observe the replacement generation");
            Ensure(ReferenceEquals(captured, original) &&
                   captured!.GenerationId != replacement.GenerationId,
                "the in-flight request must retain generation N identity");
            await WaitUntilAsync(() => original.ActiveUses == 0 && replacement.ActiveUses == 0,
                "both generations return to zero after their requests finish");
            Ensure(original.DuplicateReleaseAttempts == 0 &&
                   replacement.DuplicateReleaseAttempts == 0,
                "generation replacement must not double-release either generation");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = null;
            harness.Server.PublishAdmissionProgramForTests(null);
            held.Lease?.Dispose();
        }
    }

    [Test]
    [NotInParallel]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DisabledCaptureShouldRemainDisabledWhenCurrentBecomesEnabled(bool oneWay)
    {
        TestService.ResetNotify();
        await using var harness = await Harness.CreateAsync();
        var replacement = harness.Server.CreateAdmissionProgramForTests(
            options => options.Global.UseConcurrency(1));
        var held = await replacement.Controller.AcquireAsync(
            CreateAdmissionContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired, "test must occupy the replacement enabled generation");
        AdmissionProgram? captured = replacement;
        var hookCount = 0;

        try
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = (server, _, observed) =>
            {
                if (!ReferenceEquals(server, harness.Server) ||
                    Interlocked.Exchange(ref hookCount, 1) != 0)
                    return;
                captured = observed;
                server.PublishAdmissionProgramForTests(replacement);
            };

            var service = harness.ClientA.Get<ITestService>();
            if (oneWay)
            {
                await service.NotifyAsync("captured-disabled");
                await TestService.WaitForNotifyAsync().WaitAsync(TimeSpan.FromSeconds(2));
                Ensure(TestService.NotifyCount == 1,
                    "one-way request captured while disabled must bypass the later enabled publication");
            }
            else
            {
                Ensure(await service.AddAsync(20, 22) == 42,
                    "two-way request captured while disabled must bypass the later enabled publication");
            }

            Ensure(captured is null, "disabled capture must remain represented as disabled");
            SharpLinkServer.AfterAdmissionCaptureForTests = null;
            var nextFailure = await CaptureFailureAsync(service.AddAsync(1, 2).AsTask());
            Ensure(nextFailure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                "the next request must observe the new enabled publication");
            await WaitUntilAsync(() => replacement.ActiveUses == 0,
                "replacement generation use returns to zero after rejection");
            Ensure(replacement.DuplicateReleaseAttempts == 0,
                "replacement generation must not be released twice");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = null;
            harness.Server.PublishAdmissionProgramForTests(null);
            held.Lease?.Dispose();
        }
    }

    [Test]
    [NotInParallel]
    [Arguments(true)]
    [Arguments(false)]
    public async Task QueuedRequestShouldRetainCapturedGenerationAcrossAwait(bool oneWay)
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
        var program = harness.OwnedProgram
            ?? throw new Exception("enabled server must expose its initial admission program");
        var service = harness.ClientA.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        Task<int>? queuedTwoWay = null;
        var hookCount = 0;

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            SharpLinkServer.AfterAdmissionCaptureForTests = (server, _, observed) =>
            {
                if (!ReferenceEquals(server, harness.Server) ||
                    Interlocked.Exchange(ref hookCount, 1) != 0)
                    return;
                Ensure(ReferenceEquals(observed, program),
                    "queued request must capture the original generation before publication change");
                server.PublishAdmissionProgramForTests(null);
            };

            if (oneWay)
                await service.NotifyAsync("queued-generation");
            else
                queuedTwoWay = service.AddAsync(20, 22).AsTask();

            await WaitUntilAsync(() => program.Controller.QueuedCalls == 1,
                "target request reaches the captured generation queue");
            Ensure(program.ActiveUses == 2,
                "active owner and queued target must each retain one generation use");
            if (oneWay)
                Ensure(TestService.NotifyCount == 0,
                    "queued one-way request must not bypass after current publication becomes disabled");
            else
                Ensure(!queuedTwoWay!.IsCompleted,
                    "queued two-way request must remain queued on its captured generation");

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(5)) == 2,
                "active admission owner completes");
            if (oneWay)
                await TestService.WaitForNotifyAsync().WaitAsync(TimeSpan.FromSeconds(5));
            else
                Ensure(await queuedTwoWay!.WaitAsync(TimeSpan.FromSeconds(5)) == 42,
                    "queued two-way request executes after the captured generation releases a permit");

            await WaitUntilAsync(
                () => program.Controller.QueuedCalls == 0 && program.ActiveUses == 0,
                "queued generation accounting returns to zero");
            Ensure(program.DuplicateReleaseAttempts == 0,
                "queued request generation must release exactly once");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCaptureForTests = null;
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (queuedTwoWay is not null)
                await ObserveTerminalAsync(queuedTwoWay);
        }
    }

    [Test]
    [NotInParallel]
    public async Task AdmissionRejectShouldReleaseGenerationExactlyOnce()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await Harness.CreateAsync(
            admissionConfigure: options => options.Global.UseConcurrency(1));
        var program = harness.OwnedProgram!;
        var service = harness.ClientA.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1).AsTask();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var failure = await CaptureFailureAsync(service.AddAsync(2, 2).AsTask());
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                "contender must be rejected by admission");
            await WaitUntilAsync(() => program.ActiveUses == 1,
                "admission reject releases only the rejected request generation use");
            Ensure(program.DuplicateReleaseAttempts == 0,
                "admission reject must not double-release generation use");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
        }
        await AssertProgramReleasedAsync(program, "admission reject terminal cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task CallCapacityRejectShouldReleaseGenerationExactlyOnce()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await Harness.CreateAsync(
            serverRuntimeConfigure: options => options.FlowControl.MaxConcurrentCallsPerServer = 1,
            admissionConfigure: options => options.Global.UseConcurrency(2));
        var program = harness.OwnedProgram!;
        var service = harness.ClientA.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1).AsTask();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var failure = await CaptureFailureAsync(service.AddAsync(2, 2).AsTask());
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
                "contender must be rejected by server call capacity after admission succeeds");
            await WaitUntilAsync(() => program.ActiveUses == 1,
                "call-capacity rejection releases the target generation use");
            Ensure(program.DuplicateReleaseAttempts == 0,
                "call-capacity rejection must not double-release generation use");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
        }
        await AssertProgramReleasedAsync(program, "call-capacity rejection terminal cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task RetainedRequestBudgetRejectShouldReleaseGenerationExactlyOnce()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await Harness.CreateAsync(
            serverRuntimeConfigure: options =>
            {
                options.FlowControl.MaxRetainedCompressedBytesPerServer = 1;
                options.Compression.Providers.Add(new TestCompressionProvider());
            },
            clientRuntimeConfigure: options =>
                options.Compression.Providers.Add(new TestCompressionProvider()),
            admissionConfigure: options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            });
        var program = harness.OwnedProgram!;
        var active = harness.ClientA.Get<ITestService>()
            .BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        Task<byte[]>? target = null;
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var payload = Enumerable.Repeat((byte)0x2a, 16 * 1024).ToArray();
            target = harness.ClientA.Get<ICompressionService>().EchoBytesAsync(payload).AsTask();
            var failure = await CaptureFailureAsync(target);
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted } exhausted &&
                   exhausted.Message.Contains(
                       SharpLinkResourceExhaustion.ServerRetainedCompressedBytes,
                       StringComparison.Ordinal),
                "retained compressed request budget must reject with its stable reason");
            await WaitUntilAsync(
                () => program.Controller.QueuedCalls == 0 && program.ActiveUses == 1,
                "retained-budget rejection releases only the rejected generation use and queue accounting");
            Ensure(program.DuplicateReleaseAttempts == 0,
                "retained-budget rejection must not double-release generation use");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (target is not null)
                await ObserveTerminalAsync(target);
        }
        await AssertProgramReleasedAsync(program, "retained request budget rejection cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedCancellationShouldReleaseGenerationExactlyOnce()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await Harness.CreateAsync(
            admissionConfigure: options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            });
        var program = harness.OwnedProgram!;
        var service = harness.ClientA.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        using var cancellation = new CancellationTokenSource();
        Task<int>? target = null;
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            target = service.BlockingAddAsync(2, 2, cancellation.Token).AsTask();
            await WaitUntilAsync(() => program.Controller.QueuedCalls == 1,
                "cancellable request enters admission queue");
            cancellation.Cancel();
            await CaptureFailureAsync(target);
            await WaitUntilAsync(
                () => program.Controller.QueuedCalls == 0 && program.ActiveUses == 1,
                "queued cancellation releases only the cancelled generation use");
            Ensure(program.DuplicateReleaseAttempts == 0,
                "queued cancellation must not double-release generation use");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (target is not null)
                await ObserveTerminalAsync(target);
        }
        await AssertProgramReleasedAsync(program, "queued cancellation terminal cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedConnectionCloseShouldReleaseGenerationExactlyOnce()
    {
        TestService.ResetBlockingAdd();
        await using var harness = await Harness.CreateAsync(
            admissionConfigure: options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            });
        var program = harness.OwnedProgram!;
        var active = harness.ClientA.Get<ITestService>()
            .BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        Task<int>? target = null;
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            target = harness.ClientB.Get<ITestService>().AddAsync(2, 2).AsTask();
            await WaitUntilAsync(() => program.Controller.QueuedCalls == 1,
                "second-connection request enters admission queue");
            await harness.StopClientBAsync();
            await CaptureFailureAsync(target);
            await WaitUntilAsync(
                () => program.Controller.QueuedCalls == 0 && program.ActiveUses == 1,
                "connection close releases the queued request generation use");
            Ensure(program.DuplicateReleaseAttempts == 0,
                "connection close must not double-release generation use");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await ObserveTerminalAsync(active);
            if (target is not null)
                await ObserveTerminalAsync(target);
        }
        await AssertProgramReleasedAsync(program, "connection-close terminal cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task DecodeFailureShouldReleaseGenerationAndKeepConnectionReusable()
    {
        var throwingProvider = new ThrowingDecompressionProvider(
            new TestCompressionProvider());
        await using var harness = await Harness.CreateAsync(
            serverRuntimeConfigure: options =>
                options.Compression.Providers.Add(throwingProvider),
            clientRuntimeConfigure: options =>
                options.Compression.Providers.Add(new TestCompressionProvider()),
            admissionConfigure: options => options.Global.UseConcurrency(2));
        var program = harness.OwnedProgram!;
        var payload = Enumerable.Repeat((byte)0x35, 16 * 1024).ToArray();

        var failure = await CaptureFailureAsync(
            harness.ClientA.Get<ICompressionService>().EchoBytesAsync(payload).AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
            "provider decode failure must remain call-scoped");
        await AssertProgramReleasedAsync(program, "decode failure cleanup");
        Ensure(await harness.ClientA.Get<ITestService>().AddAsync(20, 22) == 42,
            "connection must remain reusable after controlled decode failure");
        await AssertProgramReleasedAsync(program, "post-decode-failure connection reuse");
    }

    [Test]
    [NotInParallel]
    public async Task ActivationFailureShouldReleaseGenerationAndKeepConnectionReusable()
    {
        await using var harness = await Harness.CreateAsync(
            admissionConfigure: options => options.Global.UseConcurrency(2));
        var program = harness.OwnedProgram!;
        try
        {
            ServerCallCancellationState.BeforeRequestActivationForTests = state =>
                state.TryCancel(ServerCallCancellationReason.RemoteCancel);
            var failure = await CaptureFailureAsync(
                harness.ClientA.Get<ITestService>().AddAsync(1, 2).AsTask());
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Cancelled },
                "activation terminal winner must prevent invocation and surface cancellation");
        }
        finally
        {
            ServerCallCancellationState.BeforeRequestActivationForTests = null;
        }

        await AssertProgramReleasedAsync(program, "activation failure cleanup");
        Ensure(await harness.ClientA.Get<ITestService>().AddAsync(20, 22) == 42,
            "connection must remain reusable after controlled activation failure");
        await AssertProgramReleasedAsync(program, "post-activation-failure connection reuse");
    }

    [Test]
    [NotInParallel]
    public async Task SuccessfulTerminalCompletionShouldReleaseGenerationExactlyOnce()
    {
        await using var harness = await Harness.CreateAsync(
            admissionConfigure: options => options.Global.UseConcurrency(2));
        var program = harness.OwnedProgram!;
        Ensure(await harness.ClientA.Get<ITestService>().AddAsync(20, 22) == 42,
            "successful admitted request result");
        await AssertProgramReleasedAsync(program, "successful request terminal cleanup");
    }

    private static SharpLinkAdmissionContext CreateAdmissionContext()
        => new(1, 2, RpcMethodKind.Unary, "generation-test", null, null);

    private static async Task AssertProgramReleasedAsync(
        AdmissionProgram program,
        string scenario)
    {
        await WaitUntilAsync(() => program.ActiveUses == 0, scenario);
        Ensure(program.DuplicateReleaseAttempts == 0,
            $"{scenario}: generation use must be released exactly once");
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

    private sealed class ThrowingDecompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        public string WireProfile => inner.WireProfile;

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => inner.TryCompress(input, output, maxOutputBytes, cancellationToken);

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("forced generation-test decompression failure");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private bool _clientBStopped;
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
        internal AdmissionProgram? OwnedProgram => Server.OwnedAdmissionProgramForTests;

        internal static async Task<Harness> CreateAsync(
            Action<SharpLinkRuntimeOptions>? serverRuntimeConfigure = null,
            Action<SharpLinkRuntimeOptions>? clientRuntimeConfigure = null,
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

            var clientA = CreateClient(port, clientRuntimeConfigure);
            var clientB = CreateClient(port, clientRuntimeConfigure);
            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            return new Harness(serverCancellation, serverTask, server, clientA, clientB);
        }

        internal async Task StopClientBAsync()
        {
            if (_clientBStopped)
                return;
            _clientBStopped = true;
            await StopClientAsync(ClientB);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await StopClientAsync(ClientA);
                if (!_clientBStopped)
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
            var builder = SharpClientBuilder.Create().DisableRequestTimeout()
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
