namespace SharpLink.IntegrationTests;

public partial class IntegrationBehaviorTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OneByteFlowWindowsShouldResumeBothStreamDirections(bool useSharedMemory)
    {
        await using var harness = await TestHarness.CreateAsync(runtimeConfigure: options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 1;
            options.FlowControl.ConnectionReceiveWindowBytes = 1;
        }, useSharedMemory: useSharedMemory);
        var svc = harness.Client.Get<ITestService>();

        var upload = await svc.UploadAsync(
            ToAsyncEnumerable(Enumerable.Range(1, 64), CancellationToken.None));
        Ensure(upload == 2080, "one-byte client stream flow control");

        var download = await CollectAsync(svc.DownloadAsync(64), CancellationToken.None);
        Ensure(download.Count == 64 && download[63] == "v-63", "one-byte server stream flow control");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectionPoolShouldExpandOnceUnderConcurrentPressure(bool useSharedMemory)
    {
        await using var harness = await TestHarness.CreateAsync(poolConfigure: options =>
        {
            options.MinConnections = 1;
            options.MaxConnections = 2;
        }, useSharedMemory: useSharedMemory);
        var client = (SharpLinkClient)harness.Client;
        var svc = harness.Client.Get<ITestService>();

        var first = svc.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await Task.Delay(20);
        var second = svc.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await WaitUntilAsync(() => client.ReadyConnectionCount == 2);

        Ensure(await first == 21 && await second == 21, "concurrent calls should complete across the pool");
        Ensure(client.ReadyConnectionCount == 2, "pressure should create one bounded expansion connection");
    }

    [Test]
    public async Task OneWayMethodTimeoutShouldCancelServerInvocationCooperatively()
    {
        TestService.ResetOneWayDeadlineCancellation();
        await using var harness = await TestHarness.CreateAsync();
        var service = harness.Client.Get<ITestService>();

        await service.WaitForOneWayDeadlineAsync(CancellationToken.None);
        await TestService.WaitForOneWayDeadlineCancellationAsync()
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task UserCancellationShouldPropagateOperationCanceledException()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await EnsureThrows<OperationCanceledException>(
            svc.SlowAddAsync(1, 2, cts.Token).AsTask(),
            "SlowAddAsync user cancellation");
    }

    [Test]
    [NotInParallel]
    public async Task UnaryResponseAndCallerCancellationRaceShouldHaveOneTerminalOutcomeAndNoPendingLeaks()
    {
        const int callCount = 100;
        TestService.ResetBlockingAdd(callCount);
        await using var harness = await TestHarness.CreateAsync(disableRequestTimeout: true);
        var client = (SharpLinkClient)harness.Client;
        var service = harness.Client.Get<ITestService>();
        var cancellations = Enumerable.Range(0, callCount)
            .Select(static _ => new CancellationTokenSource())
            .ToArray();
        try
        {
            var calls = cancellations.Select((cancellation, iteration) =>
                    service.BlockingAddAsync(iteration, 1, cancellation.Token).AsTask())
                .ToArray();
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));

            using var ready = new CountdownEvent(2);
            using var start = new ManualResetEventSlim(initialState: false);
            var response = Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                TestService.ReleaseBlockingAdd();
            });
            var callerCancel = Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                foreach (var cancellation in cancellations)
                    cancellation.Cancel();
            });
            Ensure(ready.Wait(TimeSpan.FromSeconds(10)), "P2-T01 workers reached the response/cancel gate");
            start.Set();
            await Task.WhenAll(response, callerCancel).WaitAsync(TimeSpan.FromSeconds(10));

            for (var iteration = 0; iteration < calls.Length; iteration++)
            {
                var exception = await CaptureExceptionAsync(calls[iteration]);
                Ensure(exception is null or OperationCanceledException,
                    $"P2-T01 iteration {iteration}: terminal is success or caller cancellation");
                if (exception is null)
                {
                    Ensure(calls[iteration].Result == iteration + 1,
                        $"P2-T01 iteration {iteration}: successful response value");
                }
            }

            Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 &&
                   client.ActiveClientStreamCount == 0,
                "P2-T01: every racing invocation releases pending/call/stream state");
            Ensure(await service.AddAsync(20, 22) == 42,
                "P2-T01: the connection remains reusable after all terminal races");
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T01");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            foreach (var cancellation in cancellations)
                cancellation.Dispose();
        }
    }

    [Test]
    public async Task DefaultRequestTimeoutShouldThrowDeadlineExceeded()
    {
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask(),
            "SlowAddAsync request timeout",
            SharpLinkErrorCode.DeadlineExceeded);
    }

    [Test]
    [NotInParallel]
    public async Task UnaryWithoutTimeoutAttributeShouldUseClientDefaultTimeout()
    {
        TestService.ResetNonCancellableCompletion();
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddWithoutTimeoutAsync(1, 2).AsTask(),
            "SlowAddWithoutTimeoutAsync default timeout",
            SharpLinkErrorCode.DeadlineExceeded);

        await TestService.WaitForNonCancellableCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(await svc.AddAsync(20, 22) == 42,
            "a late non-cancellable result must be suppressed without damaging the connection");
    }

    [Test]
    [NotInParallel]
    public async Task NonCancellableFailureAfterTimeoutShouldBeObservedAndSuppressed()
    {
        TestService.ResetNonCancellableFailure();
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowThrowWithoutTimeoutAsync().AsTask(),
            "SlowThrowWithoutTimeoutAsync default timeout",
            SharpLinkErrorCode.DeadlineExceeded);

        await TestService.WaitForNonCancellableFailureAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(await svc.AddAsync(20, 22) == 42,
            "a late non-cancellable exception must be observed without damaging the connection");
    }

    [Test]
    [NotInParallel]
    public async Task DisableRequestTimeoutShouldAllowNonCancellableUnaryToFinish()
    {
        TestService.ResetNonCancellableCompletion();
        await using var harness = await TestHarness.CreateAsync(disableRequestTimeout: true);
        var svc = harness.Client.Get<ITestService>();

        Ensure(await svc.SlowAddWithoutTimeoutAsync(20, 22) == 42,
            "disabled default timeout should wait for the service result");
        await TestService.WaitForNonCancellableCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task NonCancellableOperationCanceledExceptionShouldNotBeMisreportedAsDeadline()
    {
        await using var harness = await TestHarness.CreateAsync();
        await EnsureThrowsSharpLinkFast(
            harness.Client.Get<ITestService>().ThrowCancellationAsync().AsTask(),
            "non-cancellable service cancellation classification",
            SharpLinkErrorCode.Cancelled);
    }

    [Test]
    public async Task EarlyServerStreamDisposalShouldCancelAndReleaseConnectionState()
    {
        await using var harness = await TestHarness.CreateAsync();
        var client = (SharpLinkClient)harness.Client;
        var service = harness.Client.Get<ITestService>();

        for (var iteration = 0; iteration < 100; iteration++)
        {
            await using var enumerator = service
                .SlowDownloadAsync(1_000, 10, CancellationToken.None)
                .GetAsyncEnumerator();
            Ensure(await enumerator.MoveNextAsync(), "stream should produce its first item");
        }

        await WaitUntilAsync(() =>
            client.PendingCallCount == 0 &&
            client.ActiveClientCallCount == 0 &&
            client.ActiveClientStreamCount == 0);
        Ensure(await service.AddAsync(20, 22) == 42, "connection should remain healthy after early disposal");
    }

    [Test]
    [NotInParallel]
    public async Task NonCancellableServerStreamEarlyBreakShouldStopFrameworkPump()
    {
        TestService.ResetDownloadDisposed();
        await using var harness = await TestHarness.CreateAsync();
        var service = harness.Client.Get<ITestService>();

        await using (var enumerator = service.DownloadAsync(int.MaxValue).GetAsyncEnumerator())
            Ensure(await enumerator.MoveNextAsync(), "stream should produce its first item");

        await TestService.WaitForDownloadDisposedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(await service.AddAsync(20, 22) == 42,
            "framework stream cancellation must leave the connection healthy");
    }

    [Test]
    [NotInParallel]
    public async Task FastEarlyBreakShouldReturnFlowCreditAndNotLeakCompletedSendStates()
    {
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: static options =>
                // Amplify completed-state pressure while leaving enough room for a canceled
                // producer to observe its terminal token and retire its final in-flight frame.
                options.Protocol.MaxConcurrentStreamsPerConnection = 8);
        var service = harness.Client.Get<ITestService>();

        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var enumerator = service.DownloadAsync(32).GetAsyncEnumerator();
            try
            {
                bool hasItem;
                try
                {
                    hasItem = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        $"Fast stream {iteration}/10,000 did not produce its first item within 5 seconds.",
                        exception);
                }
                Ensure(hasItem, "fast stream should produce its first item");
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        $"Fast stream {iteration}/10,000 did not dispose within 5 seconds.",
                        exception);
                }
            }
        }

        Ensure(await service.AddAsync(20, 22) == 42,
            "connection should remain healthy after 10,000 fast early-break streams");
    }

    [Test]
    public async Task MethodTimeoutShouldExpireWithoutPublicCallContextDeadline()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var summary = await svc.DescribeCallAsync(42, CancellationToken.None);
        Ensure(summary.StartsWith("42:missing:no-deadline", StringComparison.Ordinal),
            "method timeout should not recreate a public absolute call-context deadline");

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddWithMethodTimeoutAsync(1, 2, CancellationToken.None).AsTask(),
            "method timeout",
            SharpLinkErrorCode.DeadlineExceeded);
    }

    [Test]
    public async Task CallerSelectedMetadataShouldVaryPerInvocation()
    {
        await using var harness = await TestHarness.CreateAsync();
        var tenantA = harness.Client.GetWithMetadata<ITestService>(new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "a")));
        var tenantB = harness.Client.GetWithMetadata<ITestService>(new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "b")));

        var results = await Task.WhenAll(
            tenantA.DescribeCallAsync(1, CancellationToken.None).AsTask(),
            tenantB.DescribeCallAsync(2, CancellationToken.None).AsTask());

        Ensure(results[0].StartsWith("1:a:", StringComparison.Ordinal),
            "caller-selected metadata A should stay bound to its invocation");
        Ensure(results[1].StartsWith("2:b:", StringComparison.Ordinal),
            "caller-selected metadata B should stay bound to its invocation");
    }

    [Test]
    [NotInParallel]
    public async Task ServerStopShouldPreservePendingCallCancellationReasonsWithoutReenteringMapper()
    {
        var exceptionMapper = new RecordingServerStreamExceptionMapper();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            await using var harness = await TestHarness.CreateAsync(
                serverConfigure: builder => builder.UseExceptionMapper(exceptionMapper));
            var svc = harness.Client.Get<ITestService>();

            var unaryTask = svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask();
            var streamTask = CollectAsync(
                svc.SlowDownloadAsync(100, 200, CancellationToken.None),
                CancellationToken.None);

            await Task.Delay(100);
            await harness.DisposeServerOnlyAsync();

            await EnsureThrowsSharpLinkFast(
                unaryTask,
                $"unary fail-fast iteration {iteration}",
                SharpLinkErrorCode.Unavailable,
                SharpLinkErrorCode.ConnectionClosed);
            await EnsureThrowsSharpLinkFast(
                streamTask,
                $"stream fail-fast iteration {iteration}",
                SharpLinkErrorCode.Unavailable,
                SharpLinkErrorCode.ConnectionClosed);
        }

        var mappedStreamErrors = exceptionMapper.GetMappedCodes();
        Ensure(mappedStreamErrors.Length == 0,
            "a framework-selected server-stop terminal must not re-enter the application stream exception mapper");
    }

    [Test]
    public async Task ClientDisposeShouldFailFastPendingUnaryAndStream()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var unaryTask = svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask();
        var streamTask = CollectAsync(svc.SlowDownloadAsync(100, 200, CancellationToken.None), CancellationToken.None);

        await Task.Delay(100);
        await harness.DisposeClientOnlyAsync();

        await EnsureThrowsSharpLinkFast(unaryTask, "unary fail-fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
        await EnsureThrowsSharpLinkFast(streamTask, "stream fail-fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    [NotInParallel]
    public async Task ClientStreamingStopRaceShouldReleaseEveryServerInvocation()
    {
        const int callCount = 128;
        TestService.ResetActiveUploads();
        await using var harness = await TestHarness.CreateAsync(poolConfigure: options =>
        {
            options.MinConnections = 1;
            options.MaxConnections = 4;
        });
        var service = harness.Client.Get<ITestService>();
        using var producerCancellation = new CancellationTokenSource();
        var calls = new Task<int>[callCount];
        for (var index = 0; index < calls.Length; index++)
        {
            calls[index] = service.UploadAsync(
                YieldOneThenWaitAsync(index, producerCancellation.Token)).AsTask();
        }

        await WaitUntilAsync(() => TestService.ActiveUploads == callCount);
        var stopServer = harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask();
        var stopClient = harness.DisposeClientOnlyAsync().AsTask();
        await producerCancellation.CancelAsync();
        await Task.WhenAll(stopServer, stopClient).WaitAsync(TimeSpan.FromSeconds(10));

        foreach (var call in calls)
        {
            try
            {
                _ = await call.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is OperationCanceledException or SharpLinkException)
            {
            }
        }
        await WaitUntilAsync(() => TestService.ActiveUploads == 0);
    }

    [Test]
    [NotInParallel]
    public async Task ClientStreamProducerFailureShouldTerminateRemoteInvocationAndKeepConnectionHealthy()
    {
        TestService.ResetActiveUploads();
        await using var harness = await TestHarness.CreateAsync();
        var service = harness.Client.Get<ITestService>();

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            await EnsureClientStreamProducerFailure(
                service.UploadAsync(YieldThenFailAsync(iteration)).AsTask(),
                "client stream producer failure");
        }

        await WaitUntilAsync(() => TestService.ActiveUploads == 0);
        Ensure(await service.AddAsync(20, 22) == 42,
            "connection should remain healthy after client stream producer failures");
    }

    [Test]
    [NotInParallel]
    public async Task GracefulStopShouldDrainAcceptedCallAndRejectNewCallsAfterGoAway()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var acceptedCall = svc.SlowAddWithoutTimeoutAsync(20, 22).AsTask();
        await Task.Delay(50);
        var stopTask = harness.DisposeServerOnlyAsync(TimeSpan.FromSeconds(2)).AsTask();
        await WaitUntilAsync(() => harness.Client.State == SharpLinkConnectionState.Draining);

        await EnsureThrowsSharpLinkFast(
            svc.AddAsync(1, 1).AsTask(),
            "new call after GoAway",
            SharpLinkErrorCode.Unavailable);
        Ensure(await acceptedCall == 42, "accepted call should complete during grace period");
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    [NotInParallel]
    public async Task GracefulStopShouldDrainOneHundredAcceptedCallsAndReleaseResources()
    {
        const int callCount = 100;
        TestService.ResetBlockingAdd(callCount);
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();
        var acceptedCalls = Enumerable.Range(0, callCount)
            .Select(iteration => svc.BlockingAddAsync(iteration, 1, CancellationToken.None).AsTask())
            .ToArray();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var stopTask = harness.DisposeServerOnlyAsync(TimeSpan.FromSeconds(2)).AsTask();
            TestService.ReleaseBlockingAdd();

            var results = await Task.WhenAll(acceptedCalls).WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(results.Where((result, iteration) => result != iteration + 1).Any() is false,
                "P2-T03 grace: all 100 accepted calls complete on their original terminal path");
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T03 grace");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    [Test]
    [NotInParallel]
    public async Task GraceTimeoutShouldSelectOneForcedTerminalForOneHundredCallsAndReleaseResources()
    {
        const int callCount = 100;
        TestService.ResetBlockingAdd(callCount);
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();
        var pending = Enumerable.Range(0, callCount)
            .Select(iteration => svc.BlockingAddAsync(iteration, 1, CancellationToken.None).AsTask())
            .ToArray();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var started = Stopwatch.GetTimestamp();
            await harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10),
                "P2-T03 force: zero grace stops within the lifecycle bound");

            for (var iteration = 0; iteration < pending.Length; iteration++)
            {
                var exception = await CaptureExceptionAsync(pending[iteration]);
                Ensure(exception is SharpLinkException
                { Code: SharpLinkErrorCode.ConnectionClosed },
                    $"P2-T03 force iteration {iteration}: ConnectionClosed is the unique wire terminal");
            }
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T03 force");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }
}
