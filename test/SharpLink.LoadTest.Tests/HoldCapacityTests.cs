namespace SharpLink.LoadTest.Tests;

public class HoldCapacityTests
{
    [Test]
    public async Task HoldCapacityProbeShouldReportExactPeakAndReturnToZeroAfterRelease()
    {
        var probe = new HoldCapacityProbe();
        var generation = probe.Reset();
        const int expectedAcceptedCalls = 3;
        var calls = new Task[expectedAcceptedCalls];
        for (var index = 0; index < calls.Length; index++)
        {
            calls[index] = probe
                .HoldAsync(generation, expectedAcceptedCalls, holdDurationMilliseconds: 50)
                .AsTask();
        }

        await WaitUntilAsync(
            () => probe.ActiveCalls == expectedAcceptedCalls,
            TimeSpan.FromSeconds(2));
        Ensure(probe.PeakActiveCalls == expectedAcceptedCalls,
            "the Singleton probe must observe the exact simultaneous-call peak");

        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(probe.ActiveCalls == 0,
            "every held call must decrement the active count in its finally block");
        Ensure(probe.PeakActiveCalls == expectedAcceptedCalls,
            "release must not erase the completed generation's peak");
    }

    [Test]
    public async Task HoldCapacityProbeShouldRejectResetWhileActiveAndStaleGenerations()
    {
        var probe = new HoldCapacityProbe();
        var firstGeneration = probe.Reset();
        var first = probe.HoldAsync(
            firstGeneration,
            expectedAcceptedCalls: 2,
            holdDurationMilliseconds: 25).AsTask();
        await WaitUntilAsync(() => probe.ActiveCalls == 1, TimeSpan.FromSeconds(2));

        var activeResetFailure = CaptureFailure(() => probe.Reset());
        Ensure(activeResetFailure is InvalidOperationException,
            "reset must not replace a release gate still owned by an active call");

        var second = probe.HoldAsync(
            firstGeneration,
            expectedAcceptedCalls: 2,
            holdDurationMilliseconds: 25).AsTask();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(probe.ActiveCalls == 0, "first generation drains before reset");

        var secondGeneration = probe.Reset();
        Ensure(secondGeneration == firstGeneration + 1,
            "a completed reset must publish a distinct generation");
        Ensure(probe.PeakActiveCalls == 0,
            "a new generation must start with an isolated peak counter");

        var staleFailure = await CaptureFailureAsync(
            probe.HoldAsync(
                firstGeneration,
                expectedAcceptedCalls: 1,
                holdDurationMilliseconds: 1).AsTask());
        Ensure(staleFailure is InvalidOperationException,
            "an old client generation must not join the replacement release gate");
        Ensure(probe.ActiveCalls == 0 && probe.PeakActiveCalls == 0,
            "stale generation rejection must not mutate probe counters");
    }

    [Test]
    public void HoldOptionsShouldParseIndependentClientConnectionAndServerCapacities()
    {
        var options = LoadTestOptions.Parse([
            "--mode", "local",
            "--transport", "tcp",
            "--operation", "hold",
            "--client-count", "4",
            "--concurrency-per-client", "8",
            "--hold-duration", "2",
            "--min-connections", "1",
            "--max-connections", "1",
            "--max-concurrent-calls-per-connection", "16",
            "--max-concurrent-calls-per-server", "24",
            "--max-pending-requests-per-connection", "32",
            "--request-timeout", "disabled",
            "--metrics-port", "0"
        ]);

        Ensure(options.Operation == "hold", "hold operation");
        Ensure(options.ClientCount == 4 && options.ConcurrencyPerClient == 8,
            "independent client count and one-shot calls per client");
        Ensure(options.HoldDurationSeconds == 2, "shared hold duration");
        Ensure(options.MinConnections == 1 && options.MaxConnections == 1,
            "one unambiguous connection per independent client");
        Ensure(options.MaxConcurrentCallsPerConnection == 16,
            "per-connection call capacity");
        Ensure(options.MaxConcurrentCallsPerServer == 24,
            "independent server-wide call capacity");
        Ensure(options.MaxPendingRequestsPerConnection == 32,
            "pending request capacity");
        Ensure(options.DisableRequestTimeout, "explicit request-timeout mode");

        var runtime = new SharpLinkRuntimeOptions();
        Program.ConfigureRuntime(runtime, options);
        Ensure(runtime.FlowControl.MaxConcurrentCallsPerConnection == 16,
            "LoadTest applies the configured per-connection capacity");
        Ensure(runtime.FlowControl.MaxConcurrentCallsPerServer == 24,
            "LoadTest applies the configured server-wide capacity");
        Ensure(runtime.Protocol.MaxPendingRequestsPerConnection == 32,
            "LoadTest applies the configured pending request capacity");
    }

    [Test]
    public void HoldOptionsShouldDisableTimeoutByDefault()
    {
        var options = LoadTestOptions.Parse([
            "--operation", "hold"
        ]);

        Ensure(options.DisableRequestTimeout,
            "the default hold run must not race the gate against the normal 30-second timeout");
    }

    [Test]
    public void OneWayResourceExhaustionShouldYieldWithoutChangingOtherFailureLoops()
    {
        Ensure(
            Program.ShouldYieldAfterBackpressure(
                "oneway",
                new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "send queue full")),
            "OneWay backpressure must yield so every worker and the stage timer can run");
        Ensure(
            !Program.ShouldYieldAfterBackpressure(
                "echo",
                new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "send queue full")),
            "request-response throughput loops must retain their existing scheduling");
        Ensure(
            !Program.ShouldYieldAfterBackpressure(
                "oneway",
                new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "connection closed")),
            "ordinary OneWay failures must retain their existing scheduling");
    }

    [Test]
    public void FixedQueueOneWayShouldRetryOnlyLocalSendQueueBackpressure()
    {
        var sendQueueFull = new SharpLinkException(
            SharpLinkErrorCode.ResourceExhausted,
            "Session send queue exceeded its 67108864-byte limit (send_queue_capacity).");

        Ensure(
            Program.ShouldRetryOneWaySendQueueBackpressure(true, "oneway", sendQueueFull),
            "fixed-queue OneWay throughput must retry local SendPump backpressure");
        Ensure(
            !Program.ShouldRetryOneWaySendQueueBackpressure(false, "oneway", sendQueueFull),
            "the dedicated profile-default backpressure workload must retain raw rejection counts");
        Ensure(
            !Program.ShouldRetryOneWaySendQueueBackpressure(true, "echo", sendQueueFull),
            "request-response workloads must not use OneWay retry semantics");
        Ensure(
            !Program.ShouldRetryOneWaySendQueueBackpressure(
                true,
                "oneway",
                new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    "Server concurrent call capacity was exhausted.")),
            "server-side capacity rejection must remain a formal workload failure");
    }

    [Test]
    public void HoldOptionsShouldRejectCapacityMaskingConfigurations()
    {
        var anonymousFailure = CaptureFailure(() => LoadTestOptions.Parse([
            "--operation", "hold",
            "--transport", "anonymous"
        ]));
        Ensure(anonymousFailure is ArgumentException &&
               anonymousFailure.Message.Contains("independent clients", StringComparison.Ordinal),
            "anonymous pipe cannot model independent clients");

        var dynamicPoolFailure = CaptureFailure(() => LoadTestOptions.Parse([
            "--operation", "hold",
            "--min-connections", "1",
            "--max-connections", "2"
        ]));
        Ensure(dynamicPoolFailure is ArgumentException &&
               dynamicPoolFailure.Message.Contains("exactly one", StringComparison.Ordinal),
            "pooled connection routing would make theoretical capacity ambiguous");

        var fixedMultiConnectionFailure = CaptureFailure(() => LoadTestOptions.Parse([
            "--operation", "hold",
            "--min-connections", "2",
            "--max-connections", "2"
        ]));
        Ensure(fixedMultiConnectionFailure is ArgumentException &&
               fixedMultiConnectionFailure.Message.Contains("exactly one", StringComparison.Ordinal),
            "even a fixed multi-connection pool cannot guarantee even admission without pinning");

        var admissionFailure = CaptureFailure(() => LoadTestOptions.Parse([
            "--operation", "hold",
            "--admission", "immediate"
        ]));
        Ensure(admissionFailure is ArgumentException &&
               admissionFailure.Message.Contains("admission disabled", StringComparison.Ordinal),
            "admission must not mask call-capacity exhaustion");

        var timeoutFailure = CaptureFailure(() => LoadTestOptions.Parse([
            "--operation", "hold",
            "--request-timeout", "100ms"
        ]));
        Ensure(timeoutFailure is ArgumentException &&
               timeoutFailure.Message.Contains("request-timeout disabled", StringComparison.Ordinal),
            "finite client deadlines must not expire before the shared gate opens");

        var serverLimitFailure = CaptureFailure(() => LoadTestOptions.Parse([
            "--operation", "hold",
            "--max-concurrent-calls-per-server", "0"
        ]));
        Ensure(serverLimitFailure is ArgumentOutOfRangeException
        {
            ParamName: nameof(SharpLinkFlowControlOptions.MaxConcurrentCallsPerServer)
        },
            "server capacity uses the public runtime validation contract");

        var attemptedCallsFailure = CaptureFailure(() => LoadTestOptions.Parse([
            "--operation", "hold",
            "--client-count", "1024",
            "--concurrency-per-client", "1025"
        ]));
        Ensure(attemptedCallsFailure is ArgumentOutOfRangeException
        {
            ParamName: "concurrencyPerClient"
        },
            "one run must remain under the documented aggregate hard bound");
    }

    [Test]
    public void HoldResultValidationShouldRejectContaminatedCapacityEvidence()
    {
        var valid = new HoldCapacityResult(
            ClientCount: 2,
            ConnectionCount: 2,
            AttemptedCalls: 5,
            AcceptedCalls: 3,
            PeakActiveCalls: 3,
            CompletedCalls: 3,
            ResourceExhaustedCalls: 2,
            CancelledCalls: 0,
            OtherFailedCalls: 0,
            ActiveCallsAfterRelease: 0,
            HealthyCallsAfterRelease: 2,
            MaxConcurrentCallsPerConnection: 2,
            MaxConcurrentCallsPerServer: 3,
            MaxPendingRequestsPerConnection: 4,
            ProcessorCount: 1,
            Runtime: ".NET test",
            GcMode: "workstation",
            Transport: "Tcp",
            Profile: "Balanced",
            ResourceExhaustedReasons: "server_call_capacity:2",
            TopFailures: "SharpLinkException[ResourceExhausted]:2");

        var validReasons = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["server_call_capacity"] = 2
        };
        HoldCapacityRunner.ValidateResult(
            valid,
            expectedAcceptedCalls: 3,
            resourceExhaustedReasons: validReasons);

        var peakFailure = CaptureFailure(() => HoldCapacityRunner.ValidateResult(
            valid with { AcceptedCalls = 2, PeakActiveCalls = 2 },
            expectedAcceptedCalls: 3,
            resourceExhaustedReasons: validReasons));
        Ensure(peakFailure is InvalidOperationException &&
               peakFailure.Message.Contains("Expected 3 accepted", StringComparison.Ordinal),
            "under-admission must fail the experiment");

        var cancellationFailure = CaptureFailure(() => HoldCapacityRunner.ValidateResult(
            valid with
            {
                CompletedCalls = 2,
                ResourceExhaustedCalls = 1,
                CancelledCalls = 2
            },
            expectedAcceptedCalls: 3,
            resourceExhaustedReasons: validReasons));
        Ensure(cancellationFailure is InvalidOperationException,
            "cancellation-contaminated evidence must fail the experiment");

        var healthFailure = CaptureFailure(() => HoldCapacityRunner.ValidateResult(
            valid with { HealthyCallsAfterRelease = 1 },
            expectedAcceptedCalls: 3,
            resourceExhaustedReasons: validReasons));
        Ensure(healthFailure is InvalidOperationException &&
               healthFailure.Message.Contains("healthy", StringComparison.Ordinal),
            "post-release connection failure must fail the experiment");

        var unrelatedReasonFailure = CaptureFailure(() => HoldCapacityRunner.ValidateResult(
            valid with { ResourceExhaustedReasons = "send_queue_capacity:2" },
            expectedAcceptedCalls: 3,
            resourceExhaustedReasons: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["send_queue_capacity"] = 2
            }));
        Ensure(unrelatedReasonFailure is InvalidOperationException &&
               unrelatedReasonFailure.Message.Contains("send_queue_capacity", StringComparison.Ordinal),
            "send-queue exhaustion must not pass as call-capacity evidence");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(1, cancellation.Token);
    }

    private static Exception CaptureFailure(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new Exception("Expected the operation to fail.");
    }

    private static async Task<Exception> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new Exception("Expected the operation to fail.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
