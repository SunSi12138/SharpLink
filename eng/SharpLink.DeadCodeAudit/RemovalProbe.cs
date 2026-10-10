using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Deliberately applied only to the ephemeral GitHub Actions checkout.
// It never edits the original dev branch or commits source changes.
internal static class RemovalProbe
{
    internal static int Run(string root, string evidenceDirectory)
    {
        root = Path.GetFullPath(root);
        evidenceDirectory = Path.GetFullPath(evidenceDirectory);
        Directory.CreateDirectory(evidenceDirectory);

        var csvFile = Path.Combine(evidenceDirectory, "tier-A.csv");
        if (!File.Exists(csvFile))
        {
            Console.Error.WriteLine("Missing index CSV: " + csvFile);
            return 2;
        }

        var entries = File.ReadLines(csvFile).Skip(1)
            .Select(ParseCsv)
            .Where(x => x.Length >= 12 && x[3] == "Method"
                && x[4] is "Private" or "Internal"
                && string.IsNullOrEmpty(x[11])
                && x[6] == "0" && x[7] == "0" && x[8] == "0"
                && x[9] == "0" && x[10] == "0")
            .Select(x => new RemovalCandidate(x[0], int.Parse(x[1]), x[5]))
            .Where(x => x.Path.StartsWith("src/", StringComparison.Ordinal)
                && !x.Path.StartsWith("src/SharpLink.Shared/", StringComparison.Ordinal))
            .ToArray();

        var removed = new List<RemovalCandidate>();
        var skipped = new List<string>();

        foreach (var group in entries.GroupBy(e => e.Path))
        {
            var physicalPath = Path.Combine(root, group.Key.Replace('/', Path.DirectorySeparatorChar));
            var original = File.ReadAllText(physicalPath);
            var tree = CSharpSyntaxTree.ParseText(original,
                new CSharpParseOptions(LanguageVersion.Latest), physicalPath);
            var rootSyntax = tree.GetRoot();

            var candidatesByLine = group
                .GroupBy(g => g.Line).ToDictionary(g => g.Key, g => g.ToArray());
            var edits = new List<(int Start, int Length, RemovalCandidate Candidate)>();
            foreach (var method in rootSyntax.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (!candidatesByLine.TryGetValue(line, out var matches))
                    continue;
                if (matches.Length != 1)
                {
                    skipped.Add(group.Key + ":" + line + " is ambiguous");
                    continue;
                }
                var wanted = matches[0];
                if (!wanted.Signature.Contains("." + method.Identifier.ValueText + "(",
                        StringComparison.Ordinal)
                    && !wanted.Signature.Contains("." + method.Identifier.ValueText + "<",
                        StringComparison.Ordinal))
                {
                    skipped.Add(group.Key + ":" + line + " signature mismatch: " + wanted.Signature);
                    continue;
                }

                // Never remove API/interface members, constructors, or extern methods:
                // this intentionally probes only syntactically explicit ordinary bodies.
                if (method.Body is null && method.ExpressionBody is null)
                {
                    skipped.Add(group.Key + ":" + line + " lacks body");
                    continue;
                }
                if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)
                    || m.IsKind(SyntaxKind.OverrideKeyword)
                    || m.IsKind(SyntaxKind.VirtualKeyword)))
                {
                    skipped.Add(group.Key + ":" + line + " unsafe dispatch modifiers");
                    continue;
                }

                // Remove XML documentation belonging to the deleted member as well.
                // Otherwise leftover /// <param> comments attach to the next
                // declaration and fail under TreatWarningsAsErrors (CS1573/CS1711).
                var start = method.Span.Start;
                foreach (var trivia in method.GetLeadingTrivia())
                    if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                        || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                        start = Math.Min(start, trivia.SpanStart);
                edits.Add((start, method.Span.End - start, wanted));
            }
            foreach (var expected in group)
                if (!edits.Any(x => x.Candidate.Equals(expected))
                    && !skipped.Any(x => x.StartsWith(expected.Path + ":" + expected.Line + " ",
                        StringComparison.Ordinal)))
                    skipped.Add(expected.Path + ":" + expected.Line + " no matching declaration");

            if (edits.Count == 0)
                continue;

            var modified = original;
            foreach (var edit in edits.OrderByDescending(e => e.Start))
            {
                modified = modified.Remove(edit.Start, edit.Length);
                removed.Add(edit.Candidate);
            }
            File.WriteAllText(physicalPath, modified, new UTF8Encoding(false));
            Console.WriteLine("PROBE: " + group.Key + " removed " + edits.Count + " declarations");
        }

        var result = new
        {
            BaselineSha = Environment.GetEnvironmentVariable("AUDIT_DEV_SHA"),
            Mode = "Ephemeral removal; skip linked SharpLink.Shared multi-assembly sources and remove attached XML docs; never push modified src",
            Requested = entries.Length,
            Removed = removed.Count,
            Skipped = skipped.Count,
            RemovedSymbols = removed.OrderBy(x => x.Path).ThenBy(x => x.Line).ToArray(),
            SkippedSymbols = skipped.OrderBy(x => x).ToArray(),
        };
        var path = Path.Combine(evidenceDirectory, "removal-probe.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PROBE TOTAL: removed {removed.Count}/{entries.Length}; skipped {skipped.Count}. Evidence: {path}");
        return removed.Count == 0 ? 3 : 0;
    }

    private static string[] ParseCsv(string line)
    {
        var values = new List<string>();
        var buffer = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    buffer.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (line[i] == ',' && !quoted)
            {
                values.Add(buffer.ToString());
                buffer.Clear();
            }
            else buffer.Append(line[i]);
        }
        values.Add(buffer.ToString());
        return values.ToArray();
    }

    private sealed record RemovalCandidate(string Path, int Line, string Signature);
}
