using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

internal static class GeneratedSourceFixture
{
    private const int ChangedLineLimit = 4;
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GeneratedSources");

    internal static string ReadInput(string fixtureName)
        => ReadFixtureFile($"{fixtureName}.input.txt");

    internal static void AssertGeneratedSource(
        string fixtureName,
        string assemblyName,
        string source,
        string hintName,
        params MetadataReference[] additionalReferences)
    {
        var result = GeneratorTestHarness.Run(assemblyName, source, additionalReferences);
        var errors = result.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            throw new Exception(
                $"Fixture '{fixtureName}' reported generator errors: " +
                string.Join(" | ", errors.Select(static diagnostic =>
                    $"{diagnostic.Id}: {diagnostic.GetMessage()}")));
        }

        var generated = result.Results
            .SelectMany(static generatorResult => generatorResult.GeneratedSources)
            .Where(generatedSource => string.Equals(
                generatedSource.HintName,
                hintName,
                StringComparison.Ordinal))
            .ToArray();
        if (generated.Length != 1)
        {
            var available = string.Join(
                ", ",
                result.Results
                    .SelectMany(static generatorResult => generatorResult.GeneratedSources)
                    .Select(static generatedSource => generatedSource.HintName)
                    .OrderBy(static name => name, StringComparer.Ordinal));
            throw new Exception(
                $"Fixture '{fixtureName}' expected exactly one generated source '{hintName}', " +
                $"but found {generated.Length}. Available: {available}");
        }

        var expected = NormalizeLineEndings(ReadFixtureFile($"{fixtureName}.expected.txt"));
        var actual = NormalizeLineEndings(generated[0].SourceText.ToString());
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Exception(BuildReadableDiff(fixtureName, hintName, expected, actual));
    }

    private static string ReadFixtureFile(string fileName)
    {
        var path = Path.Combine(FixtureDirectory, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Generator fixture file was not copied to the test output: {path}", path);
        return File.ReadAllText(path);
    }

    private static string NormalizeLineEndings(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return normalized.TrimEnd('\n') + "\n";
    }

    internal static string BuildReadableDiff(
        string fixtureName,
        string hintName,
        string expected,
        string actual)
    {
        var expectedLines = SplitLines(expected);
        var actualLines = SplitLines(actual);
        var commonLength = Math.Min(expectedLines.Length, actualLines.Length);
        var firstDifference = 0;
        while (firstDifference < commonLength &&
               string.Equals(expectedLines[firstDifference], actualLines[firstDifference], StringComparison.Ordinal))
        {
            firstDifference++;
        }

        if (firstDifference == commonLength && expectedLines.Length == actualLines.Length)
            return $"Fixture '{fixtureName}' differed for '{hintName}'.";

        var commonSuffixLength = 0;
        while (expectedLines.Length - commonSuffixLength - 1 >= firstDifference &&
               actualLines.Length - commonSuffixLength - 1 >= firstDifference &&
               string.Equals(
                   expectedLines[expectedLines.Length - commonSuffixLength - 1],
                   actualLines[actualLines.Length - commonSuffixLength - 1],
                   StringComparison.Ordinal))
        {
            commonSuffixLength++;
        }

        var expectedChangeEnd = expectedLines.Length - commonSuffixLength;
        var actualChangeEnd = actualLines.Length - commonSuffixLength;
        var diff = new StringBuilder();
        diff.Append("Fixture '").Append(fixtureName).Append("' differed for '")
            .Append(hintName).Append("' at line ").Append(firstDifference + 1).AppendLine(".");
        diff.AppendLine("--- expected");
        diff.AppendLine("+++ actual");

        var prefixStart = Math.Max(0, firstDifference - 3);
        for (var index = prefixStart; index < firstDifference; index++)
            AppendContextLine(diff, index, index, expectedLines[index]);

        AppendChangedLines(diff, '-', expectedLines, firstDifference, expectedChangeEnd);
        AppendChangedLines(diff, '+', actualLines, firstDifference, actualChangeEnd);

        var suffixContextLength = Math.Min(3, commonSuffixLength);
        for (var offset = 0; offset < suffixContextLength; offset++)
        {
            var expectedIndex = expectedChangeEnd + offset;
            var actualIndex = actualChangeEnd + offset;
            AppendContextLine(diff, expectedIndex, actualIndex, expectedLines[expectedIndex]);
        }

        return diff.ToString();
    }

    private static string[] SplitLines(string value)
    {
        if (value.Length > 0 && value[^1] == '\n')
            value = value[..^1];
        return value.Split('\n');
    }

    private static void AppendChangedLines(
        StringBuilder diff,
        char marker,
        string[] lines,
        int start,
        int end)
    {
        var visibleEnd = Math.Min(end, start + ChangedLineLimit);
        for (var index = start; index < visibleEnd; index++)
            diff.Append(marker).Append(' ').Append(index + 1).Append(" | ").AppendLine(lines[index]);

        var remaining = end - visibleEnd;
        if (remaining > 0)
            diff.Append(marker).Append(" ... | ").Append(remaining).AppendLine(" more changed line(s)");
    }

    private static void AppendContextLine(
        StringBuilder diff,
        int expectedIndex,
        int actualIndex,
        string line)
    {
        diff.Append("  ");
        if (expectedIndex == actualIndex)
            diff.Append(expectedIndex + 1);
        else
            diff.Append(expectedIndex + 1).Append('/').Append(actualIndex + 1);
        diff.Append(" | ").AppendLine(line);
    }
}
