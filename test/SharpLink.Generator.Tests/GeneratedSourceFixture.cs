using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

internal static class GeneratedSourceFixture
{
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GeneratedSources");

    internal static string ReadInput(string fixtureName)
        => ReadFixtureFile($"{fixtureName}.input.txt");

    internal static void AssertGeneratedSource(
        string fixtureName,
        string assemblyName,
        string source,
        string hintName)
    {
        var result = GeneratorTestHarness.Run(assemblyName, source);
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
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var commonLength = Math.Min(expectedLines.Length, actualLines.Length);
        var firstDifference = 0;
        while (firstDifference < commonLength &&
               string.Equals(expectedLines[firstDifference], actualLines[firstDifference], StringComparison.Ordinal))
        {
            firstDifference++;
        }

        if (firstDifference == commonLength && expectedLines.Length == actualLines.Length)
            return $"Fixture '{fixtureName}' differed for '{hintName}'.";

        var start = Math.Max(0, firstDifference - 3);
        var end = Math.Min(Math.Max(expectedLines.Length, actualLines.Length), firstDifference + 4);
        var diff = new StringBuilder();
        diff.Append("Fixture '").Append(fixtureName).Append("' differed for '")
            .Append(hintName).Append("' at line ").Append(firstDifference + 1).AppendLine(".");
        diff.AppendLine("--- expected");
        diff.AppendLine("+++ actual");
        for (var index = start; index < end; index++)
        {
            var expectedLine = index < expectedLines.Length ? expectedLines[index] : null;
            var actualLine = index < actualLines.Length ? actualLines[index] : null;
            if (string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                diff.Append("  ").Append(index + 1).Append(" | ").AppendLine(expectedLine);
                continue;
            }

            if (expectedLine is not null)
                diff.Append("- ").Append(index + 1).Append(" | ").AppendLine(expectedLine);
            if (actualLine is not null)
                diff.Append("+ ").Append(index + 1).Append(" | ").AppendLine(actualLine);
        }
        return diff.ToString();
    }
}
