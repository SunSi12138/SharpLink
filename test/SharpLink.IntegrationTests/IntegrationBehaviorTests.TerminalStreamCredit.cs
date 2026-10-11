using System.Reflection;

namespace SharpLink.IntegrationTests;

public partial class IntegrationBehaviorTests
{
    [Test]
    [NotInParallel]
    public async Task QueuedOneWayStubFailureShouldPreserveCreditWhileExceptionMapperIsBlocked()
    {
        const int windowBytes = 64;
        using var metrics = new LifecycleMetricProbe(
            LifecycleMetricProbe.AdmissionQueuedCalls,
            LifecycleMetricProbe.ActiveStreams);
        using var producerCancellation = new CancellationTokenSource();
        var mapper = new GatedOneWayExceptionMapper();
        var windowPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemaining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestService.ResetMalformedOneWayInvocations();
        TestService.ResetBlockingAdd();
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = windowBytes;
                options.FlowControl.ConnectionReceiveWindowBytes = windowBytes;
            },
            poolConfigure: options =>
            {
                options.MinConnections = 1;
                options.MaxConnections = 1;
            },
            serverConfigure: builder =>
            {
                builder.UseExceptionMapper(mapper);
                builder.UseAdmissionControl(options =>
                {
                    options.Global.UseConcurrency(1);
                    options.MaxQueuedCalls = 1;
                    options.MaxQueuedBytes = 64 * 1024;
                    options.MaxQueueDelay = TimeSpan.FromSeconds(10);
                    options.QueueOneWayCalls = true;
                });
            });
        var client = (SharpLinkClient)harness.Client;
        var session = GetSingleCreditTestSession(client);
        var service = harness.Client.Get<ITestService>();
        var permitOwner = service.BlockingAddAsync(10, 11, CancellationToken.None).AsTask();
        Task? failedOneWay = null;
        var creditPreserved = false;
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            failedOneWay = service.NotifyUploadWithHeaderAsync(
                new MalformedHeader(2),
                PublishOneWindowThenWaitAsync(
                    mapper.Entered, windowPublished, releaseRemaining.Task, producerCancellation.Token)).AsTask();
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 1,
                "gated malformed OneWay reaches admission queue");
            await metrics.WaitForAtLeastAsync(
                LifecycleMetricProbe.ActiveStreams, 1,
                "gated malformed OneWay reserves its stream before publishing data");

            // Admission resumes independently of the connection read loop. The mapper holds
            // the existing window after drain/completion and before cancellation-state removal.
            TestService.ReleaseBlockingAdd();
            await mapper.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(await permitOwner.WaitAsync(TimeSpan.FromSeconds(5)) == 21,
                "gated malformed OneWay permit owner");
            await windowPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // All 16 StreamData frames have been published, and item 17 remains gated.
            // HealthCheck uses the same normal-priority FIFO and is answered inline by the
            // server read loop, so its response fences parsing of those 64 payload bytes.
            var health = await client.CheckHealthAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(health.Outcome == SharpLinkHealthProbeOutcome.Success,
                "health probe fences the discarded stream frames");
            Ensure(ReferenceEquals(session, GetSingleCreditTestSession(client)),
                "stream data and health fence must stay on the original connection");

            // WindowUpdate has its own send-pump priority queue. Allow its delivery after
            // the parsing fence, but keep the completed call and item 17 gated throughout.
            var credit = await WaitForConnectionCreditAsync(session, windowBytes);
            Ensure(!mapper.GateTimedOut, "the exception mapper must remain blocked through the credit assertion");
            Ensure(credit == windowBytes,
                $"discarded terminal-call stream bytes must preserve connection credit: expected {windowBytes}, actual {credit}");
            creditPreserved = true;
        }
        finally
        {
            mapper.Release();
            TestService.ReleaseBlockingAdd();
            if (!creditPreserved)
                producerCancellation.Cancel();
            releaseRemaining.TrySetResult();
            if (failedOneWay is not null)
            {
                try
                {
                    await failedOneWay.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception) when (!creditPreserved)
                {
                    // Preserve the credit assertion or setup failure while observing cleanup.
                }
            }
        }

        Ensure(TestService.MalformedOneWayInvocations == 0,
            "malformed OneWay request must not execute the service");
        Ensure(await service.AddAsync(20, 22).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 42,
            "connection remains usable after the gated OneWay stream drains");
    }

    private static async IAsyncEnumerable<int> PublishOneWindowThenWaitAsync(
        Task mapperEntered,
        TaskCompletionSource windowPublished,
        Task releaseRemaining,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await mapperEntered.WaitAsync(cancellationToken);
        for (var value = 1; value <= 16; value++)
            yield return value;

        // Reaching this continuation requires the sender to await publication of item 16.
        // Signalling immediately before its yield would leave that frame unfenced.
        windowPublished.TrySetResult();
        await releaseRemaining.WaitAsync(cancellationToken);
        for (var value = 17; value <= 256; value++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    private static RpcSession GetSingleCreditTestSession(SharpLinkClient client)
    {
        var connections = ReadCreditTestField<ClientConnection[]>(client, "_readyConnections");
        Ensure(connections.Length == 1, "terminal stream credit test requires exactly one ready connection");
        return connections[0].Session;
    }

    private static async Task<long> WaitForConnectionCreditAsync(RpcSession session, long expectedCredit)
    {
        var state = ReadCreditTestField<RpcSessionProtocolState>(session, "_protocolState");
        var controller = state.FlowController
            ?? throw new InvalidOperationException("The terminal stream credit test requires negotiated flow control.");
        var gate = ReadCreditTestField<Lock>(controller, "_gate");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            long credit;
            lock (gate)
                credit = ReadCreditTestField<long>(controller, "_sendConnectionCredit");
            if (credit == expectedCredit || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(2))
                return credit;
            await Task.Delay(10);
        }
    }

    private static T ReadCreditTestField<T>(object owner, string name)
        => (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{owner.GetType().Name}.{name} field was not found."))
            .GetValue(owner)!;

    private sealed class GatedOneWayExceptionMapper : IRpcExceptionMapper
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _gateTimedOut;

        internal Task Entered => _entered.Task;
        internal bool GateTimedOut => Volatile.Read(ref _gateTimedOut) != 0;
        internal void Release() => _release.TrySetResult();

        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            if (context.Method.Kind == RpcMethodKind.OneWay && exception is InvalidDataException)
            {
                _entered.TrySetResult();
                if (!_release.Task.Wait(TimeSpan.FromSeconds(30)))
                    Volatile.Write(ref _gateTimedOut, 1);
            }
            return exception as SharpLinkException
                ?? new SharpLinkException(SharpLinkErrorCode.Internal, "Injected malformed OneWay request.", exception);
        }
    }
}
