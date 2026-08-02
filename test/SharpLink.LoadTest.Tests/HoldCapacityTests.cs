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
            "--min-connections", "2",
            "--max-connections", "2",
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
        Ensure(options.MinConnections == 2 && options.MaxConnections == 2,
            "fixed connections per independent client");
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
               dynamicPoolFailure.Message.Contains("fixed", StringComparison.Ordinal),
            "a dynamically growing pool would make connection capacity ambiguous");

        var admissionFailure = CaptureFailure(() => LoadTestOptions.Parse([
            "--operation", "hold",
            "--admission", "immediate"
        ]));
        Ensure(admissionFailure is ArgumentException &&
               admissionFailure.Message.Contains("admission disabled", StringComparison.Ordinal),
            "admission must not mask call-capacity exhaustion");

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
