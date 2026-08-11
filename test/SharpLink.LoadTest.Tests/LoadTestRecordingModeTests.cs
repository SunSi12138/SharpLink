using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest.Tests;

public class LoadTestRecordingModeTests
{
    [Test]
    public void RecordingOptionShouldDefaultToFormal()
    {
        var options = LoadTestOptions.Parse([]);

        Ensure(options.RecordingMode == LatencyRecordingMode.Formal,
            "formal recording must be the default decision-quality mode");
        Ensure(options.MaximumRecordedOperations == 30_000_000,
            "the default formal sample capacity is explicit and bounded");
        Ensure(options.DrainTimeoutSeconds == 5, "the default drain timeout is bounded");
    }

    [Test]
    public void RecordingOptionShouldParseOffFormalDiagnosticAndValidationModes()
    {
        var cases = new[]
        {
            (Text: "off", Expected: LatencyRecordingMode.Off),
            (Text: "formal", Expected: LatencyRecordingMode.Formal),
            (Text: "diagnostic", Expected: LatencyRecordingMode.Diagnostic),
            (Text: "validation-dual", Expected: LatencyRecordingMode.ValidationDual)
        };

        foreach (var item in cases)
        {
            var options = LoadTestOptions.Parse(["--recording", item.Text]);
            Ensure(options.RecordingMode == item.Expected, $"parse --recording {item.Text}");
        }
    }

    [Test]
    public void TailObserverShouldBeExplicitAndLimitedToTheAddGateWorkload()
    {
        var enabled = LoadTestOptions.Parse(["--operation", "add", "--tail-observer"]);
        Ensure(enabled.TailObserver, "the independent tail probe is opt-in");

        var failure = CaptureFailure(() => LoadTestOptions.Parse(
            ["--operation", "echo", "--tail-observer"]));
        Ensure(failure is ArgumentException,
            "the gate probe cannot silently measure a different workload operation");
    }

    [Test]
    public void TailObserverShouldRejectStaticAndDynamicEndpointTopologies()
    {
        foreach (var topologyOption in new[] { "--static-endpoints", "--dynamic-endpoints" })
        {
            var failure = CaptureFailure(() => LoadTestOptions.Parse([
                "--operation", "add",
                "--tail-observer",
                topologyOption, "4"
            ]));
            Ensure(failure is ArgumentException &&
                   failure.Message.Contains("fixed TCP endpoint", StringComparison.Ordinal),
                $"{topologyOption} cannot make the observer connect to an unresolved/default port");
        }

        var topologyWithoutObserver = LoadTestOptions.Parse(["--static-endpoints", "4"]);
        Ensure(topologyWithoutObserver.UseStaticEndpoints,
            "the topology workload itself remains supported when no tail observer is requested");
    }

    [Test]
    public void TailObserverShouldHonorTheConfiguredFormalSampleCapacity()
    {
        var options = LoadTestOptions.Parse([
            "--operation", "add",
            "--tail-observer",
            "--maximum-recorded-operations", "1500001"
        ]);

        Ensure(options.TailObserverMaximumRecordedOperations == 1_500_001,
            "tail probes use the configured capacity instead of an undocumented one-million cap");
    }

    [Test]
    public void RecordingOptionShouldRejectUnknownValues()
    {
        var failure = CaptureFailure(() => LoadTestOptions.Parse(["--recording", "approximate"]));

        Ensure(failure is ArgumentException &&
               failure.Message.Contains("Unsupported recording mode", StringComparison.Ordinal),
            "unknown modes cannot accidentally become formal evidence");
    }

    [Test]
    public void FormalCapacityShouldCoverEveryConfiguredWorkerWhileOffAllocatesNoSamples()
    {
        var boundary = LoadTestOptions.Parse([
            "--recording", "formal",
            "--concurrency", "8",
            "--maximum-recorded-operations", "8"
        ]);
        Ensure(boundary.MaximumRecordedOperations == 8,
            "formal exact worker-count boundary is accepted");

        var formalFailure = CaptureFailure(() => LoadTestOptions.Parse([
            "--recording", "formal",
            "--concurrency", "8",
            "--maximum-recorded-operations", "7"
        ]));
        Ensure(formalFailure is ArgumentException &&
               formalFailure.Message.Contains("one sample slot per configured worker", StringComparison.Ordinal),
            "formal mode cannot create a zero-capacity worker");

        var off = LoadTestOptions.Parse([
            "--recording", "off",
            "--concurrency", "8",
            "--maximum-recorded-operations", "1"
        ]);
        Ensure(off.RecordingMode == LatencyRecordingMode.Off && off.MaximumRecordedOperations == 1,
            "off mode does not require or allocate a per-worker latency sample buffer");
    }

    [Test]
    public void DrainTimeoutOptionShouldEnforceDocumentedBounds()
    {
        var options = LoadTestOptions.Parse(["--drain-timeout", "3600"]);
        Ensure(options.DrainTimeoutSeconds == 3600, "maximum documented drain timeout boundary");

        var zeroFailure = CaptureFailure(() => LoadTestOptions.Parse(["--drain-timeout", "0"]));
        Ensure(zeroFailure is ArgumentOutOfRangeException, "zero cannot create an unbounded/instant drain ambiguity");

        var excessiveFailure = CaptureFailure(() => LoadTestOptions.Parse(["--drain-timeout", "3601"]));
        Ensure(excessiveFailure is ArgumentOutOfRangeException, "drain timeout retains a finite hard bound");
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
