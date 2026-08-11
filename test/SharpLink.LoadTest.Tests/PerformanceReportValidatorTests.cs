using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest.Tests;

public class PerformanceReportValidatorTests
{
    private const string TestCommit = "0123456789abcdef0123456789abcdef01234567";

    [Test]
    public void ValidFormalLoadAndStreamReportsShouldPassDirectoryValidation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var matrix = Directory.CreateDirectory(Path.Combine(root, "matrix")).FullName;
            var stream = Directory.CreateDirectory(Path.Combine(root, "stream")).FullName;
            WriteReport(Path.Combine(matrix, "load.json"), CreateValidResult());

            var streamResult = CreateValidResult();
            streamResult["ValidationFailure"] = 0;
            streamResult["Cancelled"] = 0;
            WriteReport(Path.Combine(stream, "stream.json"), streamResult);

            var validation = PerformanceReportValidator.AnalyzeDirectories(
                TestCommit,
                [new("matrix", matrix, 1), new("stream", stream, 1)]);

            Ensure(validation.Passed, string.Join(Environment.NewLine, validation.Failures));
            Ensure(validation.FilesValidated == 2, "every load/stream JSON report is visited");
            Ensure(validation.ResultsValidated == 2, "every stage result is validated");
            Ensure(validation.Failures.Count == 0, "valid formal evidence has no diagnostics");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BrokenMatrixReportShouldExposeEveryCompletionContractViolation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var result = CreateValidResult();
            result["RecorderMode"] = "diagnostic";
            result["RecorderVersion"] = "legacy-diagnostic-v1";
            result["FormalComparable"] = false;
            result["Failure"] = 1;
            result["ValidationFailure"] = 2;
            result["Cancelled"] = 3;
            result["TailObserverFailure"] = 4;
            result["OperationsCompleted"] = 99;
            result["Success"] = 98;
            result["SampleCount"] = 97;
            result["MaximumSampleCapacity"] = 96;
            result["WorkerCount"] = 0;
            result["StopwatchFrequency"] = 0;
            result["MeasurementDurationSeconds"] = 0;
            result.Remove("P50Us");
            result.Remove("P95Us");
            result.Remove("P99Us");
            result.Remove("P999Us");
            WriteReport(
                Path.Combine(root, "broken.json"),
                result,
                schemaVersion: 1,
                sourceCommit: "wrong-commit");

            var validation = PerformanceReportValidator.AnalyzeDirectories(
                TestCommit,
                [new("matrix", root, 1)]);
            var diagnostics = string.Join(Environment.NewLine, validation.Failures);

            Ensure(!validation.Passed, "invalid matrix evidence cannot be declared complete");
            foreach (var expected in new[]
                     {
                         "schema mismatch",
                         "source commit mismatch",
                         "not formal-comparable",
                         "contains failures",
                         "operation counts are incomplete",
                         "formal sample contract is invalid",
                         "invalid worker/timing metadata",
                         "missing formal percentile P99Us",
                         "missing formal percentile P999Us"
                     })
            {
                Ensure(diagnostics.Contains(expected, StringComparison.OrdinalIgnoreCase),
                    $"validator reports {expected}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void MissingRequiredFieldShouldBecomeAValidationFailureInsteadOfEscaping()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var result = CreateValidResult();
            result.Remove("WorkerCount");
            WriteReport(Path.Combine(root, "missing-worker-count.json"), result);

            var validation = PerformanceReportValidator.AnalyzeDirectories(
                TestCommit,
                [new("matrix", root, 1)]);

            Ensure(!validation.Passed, "a structurally incomplete report cannot pass");
            Ensure(validation.Failures.Count == 1 &&
                   validation.Failures[0].Contains("not a valid performance report", StringComparison.Ordinal),
                "missing required fields are retained as report diagnostics");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void MissingExpectedReportsInEitherDirectoryShouldFailCompletion()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var matrix = Directory.CreateDirectory(Path.Combine(root, "matrix")).FullName;
            var stream = Directory.CreateDirectory(Path.Combine(root, "stream")).FullName;
            WriteReport(Path.Combine(matrix, "only-one.json"), CreateValidResult());

            var validation = PerformanceReportValidator.AnalyzeDirectories(
                TestCommit,
                [new("matrix", matrix, 2), new("stream", stream, 1)]);
            var diagnostics = string.Join(Environment.NewLine, validation.Failures);

            Ensure(!validation.Passed, "an incomplete scenario set cannot be declared complete");
            Ensure(diagnostics.Contains(
                    "matrix report count mismatch: expected 2, found 1",
                    StringComparison.Ordinal),
                "a missing matrix scenario is reported");
            Ensure(diagnostics.Contains(
                    "stream report count mismatch: expected 1, found 0",
                    StringComparison.Ordinal),
                "an empty stream directory is reported independently");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, object?> CreateValidResult()
        => new()
        {
            ["RecorderMode"] = "formal",
            ["RecorderVersion"] = StageLatencyRecorder.Version,
            ["FormalComparable"] = true,
            ["Failure"] = 0,
            ["OperationsStartedDuringMeasurement"] = 100,
            ["OperationsCompleted"] = 100,
            ["Success"] = 100,
            ["SampleCount"] = 100,
            ["MaximumSampleCapacity"] = 1_000,
            ["WorkerCount"] = 8,
            ["StopwatchFrequency"] = 10_000_000,
            ["MeasurementDurationSeconds"] = 10,
            ["P50Us"] = 10.0,
            ["P95Us"] = 20.0,
            ["P99Us"] = 30.0,
            ["P999Us"] = 40.0
        };

    private static void WriteReport(
        string path,
        Dictionary<string, object?> result,
        int schemaVersion = PerformanceReportCompatibility.CurrentSchemaVersion,
        string sourceCommit = TestCommit)
    {
        var report = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = schemaVersion,
            ["SourceCommit"] = sourceCommit,
            ["Results"] = new[] { result }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"sharplink-report-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
