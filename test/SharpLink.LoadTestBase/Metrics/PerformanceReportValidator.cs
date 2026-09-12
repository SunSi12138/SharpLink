using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SharpLink.LoadTestBase;

public static class PerformanceReportValidator
{
    public static PerformanceReportValidation AnalyzeDirectories(
        string expectedSourceCommit,
        IEnumerable<PerformanceReportDirectoryExpectation> directoryExpectations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceCommit);
        ArgumentNullException.ThrowIfNull(directoryExpectations);

        var failures = new List<string>();
        var files = new List<string>();
        foreach (var expectation in directoryExpectations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectation.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectation.Directory);
            ArgumentOutOfRangeException.ThrowIfLessThan(expectation.ExpectedJsonFileCount, 1);

            var directory = Path.GetFullPath(expectation.Directory);
            if (!Directory.Exists(directory))
            {
                failures.Add($"{expectation.Name} report directory does not exist: {directory}.");
                continue;
            }

            var directoryFiles = Directory
                .EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            if (directoryFiles.Length != expectation.ExpectedJsonFileCount)
            {
                failures.Add(
                    $"{expectation.Name} report count mismatch: expected " +
                    $"{expectation.ExpectedJsonFileCount}, found {directoryFiles.Length}.");
            }
            files.AddRange(directoryFiles);
        }

        files = files
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            failures.Add("No performance report JSON files were found.");

        var resultCount = 0;
        foreach (var file in files)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                resultCount += ValidateFile(
                    document.RootElement,
                    expectedSourceCommit,
                    Path.GetFileName(file),
                    failures);
            }
            catch (Exception exception) when (exception is
                IOException or JsonException or InvalidOperationException or
                KeyNotFoundException or FormatException or OverflowException)
            {
                failures.Add($"{Path.GetFileName(file)} is not a valid performance report: {exception.Message}");
            }
        }

        return new PerformanceReportValidation(
            PerformanceReportCompatibility.CurrentSchemaVersion,
            expectedSourceCommit,
            files.Count,
            resultCount,
            failures.Count == 0,
            failures);
    }

    private static int ValidateFile(
        JsonElement root,
        string expectedSourceCommit,
        string fileName,
        List<string> failures)
    {
        var schemaVersion = root.GetProperty("SchemaVersion").GetInt32();
        if (schemaVersion != PerformanceReportCompatibility.CurrentSchemaVersion)
        {
            failures.Add(
                $"{fileName} schema mismatch: expected " +
                $"{PerformanceReportCompatibility.CurrentSchemaVersion}, found {schemaVersion}.");
        }

        var sourceCommit = root.GetProperty("SourceCommit").GetString();
        if (!string.Equals(sourceCommit, expectedSourceCommit, StringComparison.Ordinal))
        {
            failures.Add(
                $"{fileName} source commit mismatch: expected {expectedSourceCommit}, " +
                $"found {sourceCommit ?? "null"}.");
        }

        var results = root.GetProperty("Results");
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            failures.Add($"{fileName} contains no performance results.");
            return 0;
        }

        var index = 0;
        foreach (var result in results.EnumerateArray())
        {
            ValidateResult(result, schemaVersion, fileName, index, failures);
            index++;
        }
        return index;
    }

    private static void ValidateResult(
        JsonElement result,
        int schemaVersion,
        string fileName,
        int index,
        List<string> failures)
    {
        var label = $"{fileName} Results[{index}]";
        var recorderMode = result.GetProperty("RecorderMode").GetString();
        var recorderVersion = result.GetProperty("RecorderVersion").GetString() ?? string.Empty;
        try
        {
            PerformanceReportCompatibility.EnsureComparable(
                PerformanceReportCompatibility.CurrentSchemaVersion,
                StageLatencyRecorder.Version,
                schemaVersion,
                recorderVersion);
        }
        catch (InvalidOperationException exception)
        {
            failures.Add($"{label}: {exception.Message}");
        }

        if (!string.Equals(recorderMode, "formal", StringComparison.Ordinal) ||
            !result.GetProperty("FormalComparable").GetBoolean())
        {
            failures.Add($"{label} is not formal-comparable evidence.");
        }

        var failureCount = result.GetProperty("Failure").GetInt64();
        var validationFailureCount = GetOptionalInt64(result, "ValidationFailure");
        var cancelledCount = GetOptionalInt64(result, "Cancelled");
        var tailObserverFailureCount = GetOptionalInt64(result, "TailObserverFailure");
        if (failureCount != 0 || validationFailureCount != 0 ||
            cancelledCount != 0 || tailObserverFailureCount != 0)
        {
            failures.Add(
                $"{label} contains failures: failure={failureCount}, " +
                $"validation={validationFailureCount}, cancelled={cancelledCount}, " +
                $"tailObserver={tailObserverFailureCount}.");
        }

        var started = result.GetProperty("OperationsStartedDuringMeasurement").GetInt64();
        var completed = result.GetProperty("OperationsCompleted").GetInt64();
        var success = result.GetProperty("Success").GetInt64();
        if (started != completed || completed != success)
        {
            failures.Add(
                $"{label} operation counts are incomplete: " +
                $"started={started}, completed={completed}, success={success}.");
        }

        var sampleCount = result.GetProperty("SampleCount").GetInt64();
        var maximumSampleCapacity = result.GetProperty("MaximumSampleCapacity").GetInt32();
        if (sampleCount != success || sampleCount <= 0 || maximumSampleCapacity < sampleCount)
        {
            failures.Add(
                $"{label} formal sample contract is invalid: samples={sampleCount}, " +
                $"success={success}, capacity={maximumSampleCapacity}.");
        }

        if (result.GetProperty("WorkerCount").GetInt32() <= 0 ||
            result.GetProperty("StopwatchFrequency").GetInt64() <= 0 ||
            result.GetProperty("MeasurementDurationSeconds").GetDouble() <= 0)
        {
            failures.Add($"{label} contains invalid worker/timing metadata.");
        }

        foreach (var percentile in new[] { "P50Us", "P95Us", "P99Us", "P999Us" })
        {
            if (!result.TryGetProperty(percentile, out var value) || value.ValueKind != JsonValueKind.Number)
                failures.Add($"{label} is missing formal percentile {percentile}.");
        }
    }

    private static long GetOptionalInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetInt64() : 0;
}

public sealed record PerformanceReportValidation(
    int SchemaVersion,
    string SourceCommit,
    int FilesValidated,
    int ResultsValidated,
    bool Passed,
    IReadOnlyList<string> Failures);

public sealed record PerformanceReportDirectoryExpectation(
    string Name,
    string Directory,
    int ExpectedJsonFileCount);
