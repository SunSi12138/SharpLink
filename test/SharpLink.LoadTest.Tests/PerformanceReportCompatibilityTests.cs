using System.Text.Json;
using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest.Tests;

public class PerformanceReportCompatibilityTests
{
    [Test]
    public void SameSchemaAndRecorderShouldAllowComparison()
    {
        PerformanceReportCompatibility.EnsureComparable(
            PerformanceReportCompatibility.CurrentSchemaVersion,
            StageLatencyRecorder.Version,
            PerformanceReportCompatibility.CurrentSchemaVersion,
            StageLatencyRecorder.Version);
    }

    [Test]
    public void SchemaMismatchShouldFailFastBeforePercentageComparison()
    {
        var failure = CaptureFailure(() => PerformanceReportCompatibility.EnsureComparable(
            baselineSchemaVersion: 1,
            baselineRecorderVersion: "legacy-histogram-v1",
            candidateSchemaVersion: PerformanceReportCompatibility.CurrentSchemaVersion,
            candidateRecorderVersion: StageLatencyRecorder.Version));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("schema mismatch", StringComparison.OrdinalIgnoreCase) &&
               failure.Message.Contains("baseline=1", StringComparison.Ordinal),
            "old and current report schemas fail before any percentage can be computed");
    }

    [Test]
    public void RecorderMismatchShouldFailFastWithinTheSameSchema()
    {
        var failure = CaptureFailure(() => PerformanceReportCompatibility.EnsureComparable(
            PerformanceReportCompatibility.CurrentSchemaVersion,
            "legacy-histogram-v1",
            PerformanceReportCompatibility.CurrentSchemaVersion,
            StageLatencyRecorder.Version));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("recorder mismatch", StringComparison.OrdinalIgnoreCase),
            "equal schema numbers do not make different recorder semantics comparable");
    }

    [Test]
    public void CurrentFormalReportShouldExposeRequiredRecorderAndStageMetadata()
    {
        var result = CreateResult(
            recorderMode: "formal",
            recorderVersion: StageLatencyRecorder.Version,
            formalComparable: true,
            p50Us: 12,
            sampleCount: 5,
            maximumSampleCapacity: 10);

        using var document = SerializeReport(result);
        var root = document.RootElement;
        Ensure(root.GetProperty(nameof(PerformanceReport<object, object>.SchemaVersion)).GetInt32() ==
               PerformanceReportCompatibility.CurrentSchemaVersion,
            "wire report carries the current schema version");
        Ensure(root.GetProperty(nameof(PerformanceReport<object, object>.SourceCommit)).GetString() == "test-commit",
            "wire report identifies the source commit under the new semantic name");

        var stage = root.GetProperty(nameof(PerformanceReport<object, object>.Results))[0];
        Ensure(stage.GetProperty(nameof(StageResult.RecorderMode)).GetString() == "formal",
            "formal recorder mode flag");
        Ensure(stage.GetProperty(nameof(StageResult.RecorderVersion)).GetString() == StageLatencyRecorder.Version,
            "formal recorder semantic version");
        Ensure(stage.GetProperty(nameof(StageResult.StopwatchFrequency)).GetInt64() == 10_000_000,
            "stopwatch frequency needed to reproduce tick conversion");
        Ensure(stage.GetProperty(nameof(StageResult.WarmupDurationSeconds)).GetDouble() == 2,
            "warmup duration is separate metadata");
        Ensure(stage.GetProperty(nameof(StageResult.MeasurementDurationSeconds)).GetDouble() == 10,
            "measurement denominator is separate metadata");
        Ensure(stage.GetProperty(nameof(StageResult.DrainDurationSeconds)).GetDouble() == 3,
            "drain duration is not folded into measurement");
        Ensure(stage.GetProperty(nameof(StageResult.WorkerCount)).GetInt32() == 2,
            "new schema exposes workerCount explicitly rather than requiring legacy-field inference");
        Ensure(stage.GetProperty(nameof(StageResult.SampleCount)).GetInt64() == 5,
            "formal sample count is explicit");
        Ensure(stage.GetProperty(nameof(StageResult.MaximumSampleCapacity)).GetInt32() == 10,
            "formal hard capacity is explicit");
        Ensure(stage.GetProperty(nameof(StageResult.FormalComparable)).GetBoolean(),
            "formal results opt into decision-quality comparison");
    }

    [Test]
    public void DiagnosticAndOffReportsShouldRemainNonComparableAndOffLatencyShouldBeOmitted()
    {
        var diagnostic = CreateResult(
            recorderMode: "diagnostic",
            recorderVersion: "legacy-diagnostic-v1",
            formalComparable: false,
            p50Us: 12,
            sampleCount: 5,
            maximumSampleCapacity: 0);
        using var diagnosticDocument = SerializeReport(diagnostic);
        var diagnosticStage = diagnosticDocument.RootElement
            .GetProperty(nameof(PerformanceReport<object, object>.Results))[0];
        Ensure(!diagnosticStage.GetProperty(nameof(StageResult.FormalComparable)).GetBoolean(),
            "diagnostic realtime results are explicitly excluded from formal comparisons");

        var off = CreateResult(
            recorderMode: "off",
            recorderVersion: "off-v1",
            formalComparable: false,
            p50Us: null,
            sampleCount: 0,
            maximumSampleCapacity: 0);
        using var offDocument = SerializeReport(off);
        var offStage = offDocument.RootElement
            .GetProperty(nameof(PerformanceReport<object, object>.Results))[0];
        Ensure(!offStage.GetProperty(nameof(StageResult.FormalComparable)).GetBoolean(),
            "recording-off control is not itself formal latency evidence");
        Ensure(offStage.GetProperty(nameof(StageResult.RecorderMode)).GetString() == "off",
            "recording-off mode is explicit");
        Ensure(offStage.GetProperty(nameof(StageResult.SampleCount)).GetInt64() == 0 &&
               offStage.GetProperty(nameof(StageResult.MaximumSampleCapacity)).GetInt32() == 0,
            "recording-off reports no allocated/recorded latency capacity");
        Ensure(!offStage.TryGetProperty(nameof(StageResult.P50Us), out _) &&
               !offStage.TryGetProperty(nameof(StageResult.P95Us), out _) &&
               !offStage.TryGetProperty(nameof(StageResult.P99Us), out _) &&
               !offStage.TryGetProperty(nameof(StageResult.P999Us), out _) &&
               !offStage.TryGetProperty(nameof(StageResult.AvgUs), out _) &&
               !offStage.TryGetProperty(nameof(StageResult.MinUs), out _) &&
               !offStage.TryGetProperty(nameof(StageResult.MaxUs), out _),
            "recording-off omits unavailable latency fields instead of fabricating zero microseconds");
    }

    [Test]
    public void TailObserverShouldRemainSeparateFromRecordingOffWorkloadLatency()
    {
        var result = CreateResult(
            recorderMode: "off",
            recorderVersion: "off-v1",
            formalComparable: false,
            p50Us: null,
            sampleCount: 0,
            maximumSampleCapacity: 0,
            tailObserverSampleCount: 100,
            tailObserverP99Us: 25,
            tailObserverP999Us: 40);

        using var document = SerializeReport(result);
        var stage = document.RootElement.GetProperty(nameof(PerformanceReport<object, object>.Results))[0];
        Ensure(!stage.TryGetProperty(nameof(StageResult.P99Us), out _),
            "recording-off workload latency remains unavailable");
        Ensure(stage.GetProperty(nameof(StageResult.TailObserverSampleCount)).GetInt64() == 100 &&
               stage.GetProperty(nameof(StageResult.TailObserverP99Us)).GetDouble() == 25 &&
               stage.GetProperty(nameof(StageResult.TailObserverP999Us)).GetDouble() == 40,
            "the dedicated probe exposes independently comparable tail evidence");
    }

    private static JsonDocument SerializeReport(StageResult result)
    {
        var report = new PerformanceReport<LoadTestOptions, StageResult>(
            PerformanceReportCompatibility.CurrentSchemaVersion,
            "SharpLink.LoadTest",
            DateTimeOffset.UnixEpoch,
            "test-commit",
            "test-os",
            "X64",
            "X64",
            ".NET test",
            8,
            false,
            "Interactive",
            "2.0.0",
            new LoadTestOptions(),
            [result]);
        var json = JsonSerializer.Serialize(report, report.GetType(), LoadTestJsonContext.Default);
        return JsonDocument.Parse(json);
    }

    private static StageResult CreateResult(
        string recorderMode,
        string recorderVersion,
        bool formalComparable,
        double? p50Us,
        long sampleCount,
        int maximumSampleCapacity,
        long tailObserverSampleCount = 0,
        double? tailObserverP99Us = null,
        double? tailObserverP999Us = null)
        => new(
            Operation: "add",
            Concurrency: 2,
            Success: 100,
            Failure: 0,
            SendQueueBackpressureRetries: 0,
            Qps: 10,
            OneWayPayloadMegabytesPerSecond: 0,
            RoundTripPayloadMegabytesPerSecond: 0,
            P50Us: p50Us,
            P95Us: p50Us,
            P99Us: p50Us,
            P999Us: p50Us,
            AvgUs: p50Us,
            MinUs: p50Us,
            MaxUs: p50Us,
            WarmupDurationSeconds: 2,
            MeasurementDurationSeconds: 10,
            DrainDurationSeconds: 3,
            OperationsStartedDuringMeasurement: 100,
            OperationsCompleted: 100,
            SampleCount: sampleCount,
            MaximumSampleCapacity: maximumSampleCapacity,
            RecorderMode: recorderMode,
            RecorderVersion: recorderVersion,
            StopwatchFrequency: 10_000_000,
            FormalComparable: formalComparable,
            TailObserverSampleCount: tailObserverSampleCount,
            TailObserverFailure: 0,
            TailObserverP99Us: tailObserverP99Us,
            TailObserverP999Us: tailObserverP999Us,
            ErrorRatePercent: 0,
            TopFailures: string.Empty,
            Evidence: null!);

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
