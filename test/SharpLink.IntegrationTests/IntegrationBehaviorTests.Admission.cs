namespace SharpLink.IntegrationTests;

public partial class IntegrationBehaviorTests
{
    [Test]
    [NotInParallel]
    public async Task ServerConcurrencyExhaustionShouldRejectOverflowAndRecoverWithoutClosingConnection()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();
        var calls = new Task<int>[1025];

        for (var index = 0; index < calls.Length; index++)
            calls[index] = svc.SlowAddAsync(index, 1, CancellationToken.None).AsTask();

        var completed = 0;
        var exhausted = 0;
        foreach (var call in calls)
        {
            try
            {
                _ = await call.WaitAsync(TimeSpan.FromSeconds(10));
                completed++;
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                exhausted++;
            }
        }

        Ensure(completed == 1024, "server should admit exactly the per-connection limit");
        Ensure(exhausted == 1, "server should reject the overflow call as ResourceExhausted");
        Ensure(await svc.AddAsync(20, 22) == 42, "connection should recover after call capacity is released");
    }

    [Test]
    [NotInParallel]
    public async Task AdmissionQueueShouldRemainBoundedAndRecoverOnSameConnection()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var service = harness.Client.Get<ITestService>();

        TestService.ResetBlockingAdd();
        var active = service.BlockingAddAsync(20, 1).AsTask();
        const int contenderCount = 8;
        var contenders = new List<Task<int>>(capacity: contenderCount);
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            for (var index = 0; index < contenderCount; index++)
                contenders.Add(service.AddAsync(20, index).AsTask());

            await WaitUntilAsync(() => contenders.Count(static task => task.IsCompleted) >= contenders.Count - 1);
            Ensure(contenders.Count(static task => task.IsCompleted) == contenders.Count - 1,
                "admission queue must retain exactly one call before permit release");
            var queuedIndex = -1;
            for (var index = 0; index < contenders.Count; index++)
            {
                if (contenders[index].IsCompleted)
                {
                    await EnsureThrowsSharpLinkFast(
                        contenders[index],
                        "admission queue count",
                        SharpLinkErrorCode.ResourceExhausted);
                }
                else
                {
                    Ensure(queuedIndex < 0, "admission queue must retain exactly one call");
                    queuedIndex = index;
                }
            }

            Ensure(queuedIndex >= 0, "admission queue must retain one call");
            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 21, "active admitted call");
            Ensure(await contenders[queuedIndex].WaitAsync(TimeSpan.FromSeconds(2)) == 20 + queuedIndex,
                "queued admitted call");
            Ensure(await service.AddAsync(20, 4) == 24, "connection recovers after overload");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            try
            {
                await Task.WhenAll(contenders).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                _ = contenders.Count(static task => task.Exception is not null);
            }
        }
    }

    [Test]
    [NotInParallel]
    public async Task QueuedClientStreamShouldSpoolUntilAdmissionAndPreserveOrder()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();

        var active = service.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(75);
        var queuedStream = service.UploadAsync(
            ToAsyncEnumerable([1, 2, 3, 4], CancellationToken.None)).AsTask();

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 2, "active call before stream");
        Ensure(await queuedStream.WaitAsync(TimeSpan.FromSeconds(2)) == 10,
            "pre-admission stream spool order");
        Ensure(TestService.ActiveUploads == 0, "queued stream permit and dispatcher released");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedDuplexStreamShouldSpoolAndHoldPermitForBothDirections()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(75);
        var payloads = new[]
        {
            Enumerable.Repeat((byte)0x11, 256).ToArray(),
            Enumerable.Repeat((byte)0x22, 512).ToArray()
        };
        var duplex = CollectAsync(
            harness.Client.Get<ICompressionService>().DuplexBytesAsync(
                ToAsyncEnumerable(payloads, CancellationToken.None)),
            CancellationToken.None);

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 2,
            "active call before queued duplex");
        var received = await duplex.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(received.Count == 2 && received[0].SequenceEqual(payloads[0]) &&
            received[1].SequenceEqual(payloads[1]), "queued duplex preserves both directions");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "duplex releases admission permit");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedCompressedRequestAndClientStreamShouldDecodeAfterAdmission()
    {
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                new TestCompressionProvider()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(
                new TestCompressionProvider()),
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));

        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(20, 22).AsTask();
        await Task.Delay(75);
        var item = Enumerable.Repeat((byte)0x2a, 8192).ToArray();
        var upload = harness.Client.Get<ICompressionService>().UploadBytesAsync(
            ToAsyncEnumerable([item, item, item], CancellationToken.None)).AsTask();

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 42,
            "active call before compressed stream");
        Ensure(await upload.WaitAsync(TimeSpan.FromSeconds(2)) == item.Length * 3,
            "queued compressed request and stream items");
        Ensure((await harness.Client.Get<ICompressionService>().EchoBytesAsync([1, 2, 3]))
            .SequenceEqual(new byte[] { 1, 2, 3 }), "combined overload connection recovery");
    }

    [Test]
    public async Task MethodRateLimitShouldNotThrottleOtherMethodsAndShouldRecover()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
                options.AddMethod<ITestService>(nameof(ITestService.AddAsync), rule =>
                    rule.UseFixedWindow(rate =>
                    {
                        rate.PermitLimit = 1;
                        rate.Window = TimeSpan.FromMilliseconds(100);
                    }))));
        var service = harness.Client.Get<ITestService>();

        Ensure(await service.AddAsync(1, 1) == 2, "first method-rate permit");
        await EnsureThrowsSharpLinkFast(
            service.AddAsync(2, 2).AsTask(),
            "method rate rejection",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure((await service.EchoAsync(new Person { Name = "other", Age = 1 })).Age == 2,
            "unlimited method remains healthy");
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                try
                {
                    var result = await service.AddAsync(3, 4).AsTask()
                        .WaitAsync(recoveryTimeout.Token);
                    Ensure(result == 7, "method rate replenishment");
                    break;
                }
                catch (SharpLinkException exception) when (
                    exception.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    await Task.Delay(20, recoveryTimeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (recoveryTimeout.IsCancellationRequested)
        {
            throw new Exception("assert failed: method rate permit did not replenish within 3 seconds");
        }
    }

    [Test]
    public async Task ContractLimitShouldNotThrottleAnotherContract()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
                options.AddContract<ITestService>(rule => rule.UseConcurrency(1))));
        var testService = harness.Client.Get<ITestService>();
        var active = testService.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await Task.Delay(75);

        await EnsureThrowsSharpLinkFast(
            testService.AddAsync(1, 1).AsTask(),
            "contract concurrency rejection",
            SharpLinkErrorCode.ResourceExhausted);
        var other = await harness.Client.Get<ICompressionService>().EchoBytesAsync([7, 8, 9]);
        Ensure(other.SequenceEqual(new byte[] { 7, 8, 9 }), "other contract remains admitted");
        Ensure(await active == 21, "contract permit owner completes");
    }

    [Test]
    public async Task PartitionSelectorShouldIsolateMetadataKeys()
    {
        var metadataInterceptor = new SequencedTenantMetadataInterceptor();
        await using var harness = await TestHarness.CreateAsync(
            serverConfigure: builder => builder.UseAdmissionControl(options => options.UsePartition(
                context => context.Metadata is { Count: > 0 } metadata ? metadata[0].Value : null,
                partition =>
                {
                    partition.MaxPartitions = 8;
                    partition.UseConcurrency(1);
                })),
            clientInterceptor: metadataInterceptor);
        var service = harness.Client.Get<ITestService>();
        TestService.ResetBlockingAdd();
        var active = service.BlockingAddAsync(1, 2, CancellationToken.None).AsTask();
        await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await EnsureThrowsSharpLinkFast(
            service.AddAsync(1, 1).AsTask(),
            "same partition concurrency",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure(await service.AddAsync(2, 2) == 4, "independent metadata partition permit");

        TestService.ReleaseBlockingAdd();
        Ensure(await active == 3, "partition active call completion");
    }

    [Test]
    public async Task AdmissionQueueByteLimitShouldRejectBeforeServiceCreation()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 2;
                options.MaxQueuedBytes = 64;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(10, 1).AsTask();
        await Task.Delay(75);

        await EnsureThrowsSharpLinkFast(
            service.EchoAsync(new Person
            {
                Name = new string('x', 2048),
                Age = 1,
                Tags = ["queue-bytes"]
            }).AsTask(),
            "admission queue bytes",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure(await active == 11, "queue-byte permit owner");
        Ensure(await service.AddAsync(20, 22) == 42, "queue-byte rejection connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task PreAdmissionStreamSpoolShouldRejectWhenStreamBudgetOverflows()
    {
        await using var harness = await TestHarness.CreateAsync(
            serverRuntimeConfigure: options =>
                options.FlowControl.MaxPreAdmissionStreamBytesPerServer = 128,
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(10, 1).AsTask();
        await Task.Delay(75);
        var oversized = service.UploadAsync(ToAsyncEnumerable(
            Enumerable.Range(1, 100), CancellationToken.None)).AsTask();

        // The initial request fits, then pre-admission stream frames exhaust the
        // independent server stream-buffer budget without consuming admission queue bytes.
        await EnsureThrowsSharpLinkFast(
            oversized,
            "pre-admission stream budget",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure(TestService.ActiveUploads == 0, "overflowed stream service did not execute");
        Ensure(await active == 11, "spool overflow permit owner");
        Ensure(await service.AddAsync(20, 22) == 42, "spool overflow connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedClientStreamCallerCancellationShouldReleaseAdmissionAndStreamResources()
    {
        using var metrics = new LifecycleMetricProbe(
            LifecycleMetricProbe.AdmissionPermits,
            LifecycleMetricProbe.AdmissionQueuedCalls,
            LifecycleMetricProbe.ActiveStreams);
        TestService.ResetActiveUploads();
        TestService.ResetBlockingAdd();
        await using var harness = await TestHarness.CreateAsync(
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var client = (SharpLinkClient)harness.Client;
        var service = harness.Client.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionPermits, 1, "P2-T04 active permit");
            var queued = service.UploadAsync(
                YieldOneThenWaitAsync(2, cancellation.Token),
                cancellation.Token).AsTask();
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 1, "P2-T04 queued waiter");
            await metrics.WaitForAtLeastAsync(
                LifecycleMetricProbe.ActiveStreams, 1, "P2-T04 pre-admission stream reservation");

            await cancellation.CancelAsync();
            Ensure(await CaptureExceptionAsync(queued) is OperationCanceledException,
                "P2-T04 queued client stream observes caller cancellation");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 0, "P2-T04 waiter release");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.ActiveStreams, 0, "P2-T04 stream reservation release");
            var queuedReleased = ServerLifecycleResourceInspector.Capture(harness.Server);
            Ensure(queuedReleased is
            {
                AdmissionQueuedCalls: 0,
                AdmissionQueuedBytes: 0,
                AdmissionPermits: 1
            },
                "P2-T04 waiter/retained payload/stream reservation release while owner retains one permit");
            Ensure(TestService.ActiveUploads == 0,
                "P2-T04 canceled queued stream never reaches the service");

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(10)) == 2,
                "P2-T04 active permit owner completes");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionPermits, 0, "P2-T04 permit release");
            Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 &&
                   client.ActiveClientStreamCount == 0,
                "P2-T04 client pending/call/stream resources return to zero");
            Ensure(await service.AddAsync(3, 4) == 7,
                "P2-T04 released admission capacity is reusable");
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T04");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await cancellation.CancelAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task QueuedDeadlineShouldNotLeakPermits()
    {
        TestService.ResetNonCancellableCompletion();
        await using var deadlineHarness = await TestHarness.CreateAsync(
            requestTimeout: TimeSpan.FromMilliseconds(100),
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var deadlineService = deadlineHarness.Client.Get<ITestService>();
        var deadlineActive = deadlineService.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(25);
        await EnsureThrowsSharpLinkFast(
            deadlineService.AddAsync(2, 2).AsTask(),
            "queued deadline",
            SharpLinkErrorCode.DeadlineExceeded);
        await EnsureThrowsSharpLinkFast(
            deadlineActive,
            "active default deadline",
            SharpLinkErrorCode.DeadlineExceeded);
        await TestService.WaitForNonCancellableCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    [NotInParallel]
    public async Task RejectedAndQueuedOneWayCallsShouldFollowConfiguredPolicy()
    {
        TestService.ResetNotify();
        await using (var rejectHarness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options => options.Global.UseConcurrency(1))))
        {
            var service = rejectHarness.Client.Get<ITestService>();
            var active = service.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
            await Task.Delay(75);
            await service.NotifyAsync("drop");
            await Task.Delay(75);
            Ensure(TestService.NotifyCount == 0, "rejected OneWay service must not execute");
            Ensure(await active == 2, "OneWay rejection permit owner");
        }

        TestService.ResetNotify();
        await using var queueHarness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                options.QueueOneWayCalls = true;
            }));
        var queuedService = queueHarness.Client.Get<ITestService>();
        var queuedActive = queuedService.SlowAddWithoutTimeoutAsync(2, 2).AsTask();
        await Task.Delay(75);
        await queuedService.NotifyAsync("queue");
        Ensure(await queuedActive == 4, "queued OneWay permit owner");
        await TestService.WaitForNotifyAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(TestService.NotifyCount == 1, "explicitly queued OneWay executes once");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayClientStreamShouldSpoolUntilAdmission()
    {
        CompressionService.ResetOneWay();
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                options.QueueOneWayCalls = true;
            }));
        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(4, 5).AsTask();
        await Task.Delay(75);
        var payloads = new[]
        {
            Enumerable.Repeat((byte)0x31, 128).ToArray(),
            Enumerable.Repeat((byte)0x32, 256).ToArray()
        };

        await harness.Client.Get<ICompressionService>().NotifyStreamBytesAsync(
            ToAsyncEnumerable(payloads, CancellationToken.None));

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 9,
            "queued OneWay client-stream permit owner");
        Ensure(await CompressionService.WaitForOneWayAsync().WaitAsync(TimeSpan.FromSeconds(2)) == 384,
            "queued OneWay client-stream items preserved");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "queued OneWay client-stream connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task RejectedOneWayClientStreamShouldDrainWithoutServiceExecution()
    {
        CompressionService.ResetOneWay();
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            serverConfigure: builder => builder.UseAdmissionControl(options =>
                options.Global.UseConcurrency(1)));
        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(6, 7).AsTask();
        await Task.Delay(75);
        var payloads = Enumerable.Range(0, 256)
            .Select(static index => Enumerable.Repeat((byte)index, 128).ToArray());

        await harness.Client.Get<ICompressionService>()
            .NotifyStreamBytesAsync(ToAsyncEnumerable(payloads, CancellationToken.None))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 13,
            "rejected OneWay stream permit owner");
        await Task.Delay(100);
        Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
            "rejected OneWay stream service must not execute");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "rejected OneWay stream connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task PostAdmissionArgumentDecodeFailureShouldReleaseReservedStreams()
    {
        TestService.ResetMalformedUploadInvocations();
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(8, 9).AsTask();
        await Task.Delay(75);
        var failed = service.UploadWithHeaderAsync(
            new MalformedHeader(1),
            ToAsyncEnumerable(Enumerable.Range(1, 256), CancellationToken.None)).AsTask();

        await EnsureThrowsSharpLinkFast(
            failed,
            "post-admission argument decode failure",
            SharpLinkErrorCode.Internal);

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 17,
            "post-admission decode permit owner");
        Ensure(TestService.MalformedUploadInvocations == 0,
            "malformed request service must not execute");
        Ensure(await service.AddAsync(20, 22) == 42,
            "post-admission decode failure connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayRequestDecompressionFailureShouldDrainReservedStreams()
    {
        using var metrics = new LifecycleMetricProbe(
            LifecycleMetricProbe.AdmissionQueuedCalls,
            LifecycleMetricProbe.ActiveStreams);
        CompressionService.ResetOneWay();
        TestService.ResetBlockingAdd();
        var serverProvider = new ThrowingCompressionProvider(
            new TestCompressionProvider(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                new TestCompressionProvider()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider),
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                options.QueueOneWayCalls = true;
            }));
        var permitOwner = harness.Client.Get<ITestService>()
            .BlockingAddAsync(9, 10, CancellationToken.None).AsTask();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync();
            var payloads = Enumerable.Range(0, 256)
                .Select(static index => Enumerable.Repeat((byte)index, 128).ToArray());
            var failedOneWay = harness.Client.Get<ICompressionService>()
                .NotifyStreamWithHeaderAsync(
                    Enumerable.Repeat((byte)0x41, 4096).ToArray(),
                    ToAsyncEnumerable(payloads, CancellationToken.None))
                .AsTask();
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 1,
                "queued compressed OneWay reaches admission queue");
            await metrics.WaitForAtLeastAsync(
                LifecycleMetricProbe.ActiveStreams, 1,
                "queued compressed OneWay reserves its pre-admission stream");

            TestService.ReleaseBlockingAdd();
            Ensure(await permitOwner.WaitAsync(TimeSpan.FromSeconds(2)) == 19,
                "queued compressed OneWay permit owner");
            await failedOneWay.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
                "failed compressed OneWay request must not execute the service");
            Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
                "compressed OneWay decode failure connection recovery");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    [Test]
    [NotInParallel]
    public async Task OneWayRequestDecompressionFailureWithoutAdmissionShouldDrainClientStreams()
    {
        CompressionService.ResetOneWay();
        var serverProvider = new ThrowingCompressionProvider(
            new TestCompressionProvider(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                new TestCompressionProvider()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var payloads = Enumerable.Range(0, 256)
            .Select(static index => Enumerable.Repeat((byte)index, 128).ToArray());

        await harness.Client.Get<ICompressionService>()
            .NotifyStreamWithHeaderAsync(
                Enumerable.Repeat((byte)0x41, 4096).ToArray(),
                ToAsyncEnumerable(payloads, CancellationToken.None))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        await Task.Delay(100);
        Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
            "failed compressed OneWay request must not execute without admission");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "non-admission compressed OneWay failure connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayStubFailureShouldDrainReservedStreams()
    {
        using var metrics = new LifecycleMetricProbe(
            LifecycleMetricProbe.AdmissionQueuedCalls,
            LifecycleMetricProbe.ActiveStreams);
        TestService.ResetMalformedOneWayInvocations();
        TestService.ResetBlockingAdd();
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            serverConfigure: builder =>
            {
                builder.UseAdmissionControl(options =>
                {
                    options.Global.UseConcurrency(1);
                    options.MaxQueuedCalls = 1;
                    options.MaxQueuedBytes = 64 * 1024;
                    options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                    options.QueueOneWayCalls = true;
                });
            });
        var service = harness.Client.Get<ITestService>();
        var permitOwner = service.BlockingAddAsync(10, 11, CancellationToken.None).AsTask();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync();
            var failedOneWay = service.NotifyUploadWithHeaderAsync(
                new MalformedHeader(2),
                ToAsyncEnumerable(Enumerable.Range(1, 256), CancellationToken.None)).AsTask();
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 1,
                "queued malformed OneWay reaches admission queue");
            await metrics.WaitForAtLeastAsync(
                LifecycleMetricProbe.ActiveStreams, 1,
                "queued malformed OneWay reserves its pre-admission stream");

            TestService.ReleaseBlockingAdd();
            Ensure(await permitOwner.WaitAsync(TimeSpan.FromSeconds(2)) == 21,
                "queued malformed OneWay permit owner");
            await failedOneWay.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Ensure(TestService.MalformedOneWayInvocations == 0,
                "malformed OneWay request must not execute the service");
            Ensure(await service.AddAsync(20, 22) == 42,
                "malformed OneWay stub failure connection recovery");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    [Test]
    [NotInParallel]
    public async Task ServerStreamConsumerExitShouldReleaseCallStreamAndAdmissionResources()
    {
        using var metrics = new LifecycleMetricProbe(
            LifecycleMetricProbe.ActiveCalls,
            LifecycleMetricProbe.ActiveStreams,
            LifecycleMetricProbe.AdmissionPermits);
        TestService.ResetDownloadDisposed();
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options => options.Global.UseConcurrency(1)));
        var client = (SharpLinkClient)harness.Client;
        var service = harness.Client.Get<ITestService>();
        await using (var enumerator = service.SlowDownloadAsync(
            1_000, 10, CancellationToken.None).GetAsyncEnumerator())
        {
            Ensure(await enumerator.MoveNextAsync(), "admitted server stream first item");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionPermits, 1, "P2-T06 admitted stream permit");
            await EnsureThrowsSharpLinkFast(
                service.AddAsync(1, 1).AsTask(),
                "permit held for server stream",
                SharpLinkErrorCode.ResourceExhausted);
        }

        await TestService.WaitForDownloadDisposedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await metrics.WaitForValueAsync(
            LifecycleMetricProbe.AdmissionPermits, 0, "P2-T06 permit release");
        await metrics.WaitForValueAsync(
            LifecycleMetricProbe.ActiveStreams, 0, "P2-T06 stream release");
        await metrics.WaitForValueAsync(
            LifecycleMetricProbe.ActiveCalls, 0, "P2-T06 call release");
        Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 &&
               client.ActiveClientStreamCount == 0,
            "P2-T06 client pending/call/stream resources return to zero");
        Ensure(await service.AddAsync(20, 22) == 42,
            "P2-T06 permit is reusable immediately after the disposal gate");
        await StopHarnessAndAssertResourcesAsync(harness, "P2-T06 static");
    }

    [Test]
    [NotInParallel]
    public async Task ServerStopShouldCancelAdmissionWaitersWithoutUnboundedDelay()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var service = harness.Client.Get<ITestService>();
        TestService.ResetBlockingAdd();
        var active = service.BlockingAddAsync(1, 1).AsTask();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var queued = service.SlowAddWithoutTimeoutAsync(2, 2).AsTask();
            await Task.Delay(50);
            Ensure(!queued.IsCompleted, "queued call must await admission before stop");

            var started = Stopwatch.GetTimestamp();
            await harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2), "bounded stop with waiter");
            await EnsureThrows<SharpLinkException>(queued, "queued call stopped before execution");
            await EnsureThrows<SharpLinkException>(active, "active call disconnected by forced stop");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    [Test]
    [NotInParallel]
    public async Task ClientDisconnectWhileAdmissionQueuedShouldReleaseWaiterAndAllConnections()
    {
        using var metrics = new LifecycleMetricProbe(
            LifecycleMetricProbe.ActiveConnections,
            LifecycleMetricProbe.AdmissionPermits,
            LifecycleMetricProbe.AdmissionQueuedCalls);
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var service = harness.Client.Get<ITestService>();
        TestService.ResetBlockingAdd();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var queued = service.SlowAddWithoutTimeoutAsync(2, 2).AsTask();
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 1, "P2-T05 queued waiter");
            Ensure(!queued.IsCompleted, "queued call must await admission before disconnect");

            await harness.DisposeClientOnlyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(await CaptureExceptionAsync(queued) is SharpLinkException,
                "P2-T05 disconnected admission waiter has one terminal error");
            Ensure(await CaptureExceptionAsync(active) is SharpLinkException,
                "P2-T05 disconnected active call has one terminal error");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 0, "P2-T05 waiter release");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionPermits, 0, "P2-T05 permit release");
            await harness.DisposeServerOnlyAsync(TimeSpan.FromSeconds(1))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.ActiveConnections, 0, "P2-T05 connection release");
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T05");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }
}
