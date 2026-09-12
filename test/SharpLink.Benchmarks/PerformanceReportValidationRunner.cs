using System;
using System.IO;
using System.Text.Json;
using SharpLink.LoadTestBase;

namespace SharpLink.Benchmarks;

internal static class PerformanceReportValidationRunner
{
    private const int ExpectedMatrixReportCount = 27;
    private const int ExpectedStreamReportCount = 10;

    public static void Run(string[] args)
    {
        if (args.Length != 4)
        {
            throw new ArgumentException(
                "Usage: --validate-performance-reports " +
                "<expected-source-commit> <output-json> <matrix-directory> <stream-directory>");
        }

        var validation = PerformanceReportValidator.AnalyzeDirectories(
            args[0],
            [
                new("matrix", args[2], ExpectedMatrixReportCount),
                new("stream", args[3], ExpectedStreamReportCount)
            ]);
        var output = Path.GetFullPath(args[1]);
        var outputDirectory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            output,
            JsonSerializer.Serialize(validation, new JsonSerializerOptions { WriteIndented = true }));

        if (!validation.Passed)
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Failures));
    }
}
