using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SharpLink.CodecCompatibility;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            return args[0] switch
            {
                "produce" => Produce(GetOption(args, "--profile"), GetOption(args, "--output")),
                "verify" => Verify(GetOption(args, "--input"), GetOption(args, "--output")),
                "summarize" => Summarize(GetOption(args, "--input"), GetOption(args, "--output")),
                _ => throw new InvalidOperationException($"Unknown layout evidence command '{args[0]}'.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"UnsafeBlit layout evidence failed: {exception}");
            return 1;
        }
    }

    private static int Produce(string profile, string outputDirectory)
    {
        var json = LayoutEvidenceProbe.ProduceJson(
            Commit(),
            SdkVersion(),
            "net10.0",
            profile,
            expectedRuntimeFamily: "CoreCLR",
            executionEnvironmentOverride: "hosted-desktop");
        var envelope = Deserialize<LayoutEvidenceEnvelope>(json);
        WriteCorpus(envelope, outputDirectory);
        Console.WriteLine($"Produced {envelope.Cases.Count} {profile} layout fixtures for {envelope.Runtime.PlatformTag}.");
        return 0;
    }

    private static int Verify(string inputDirectory, string outputFile)
    {
        var envelopes = LoadCorpora(inputDirectory);
        var inputJson = JsonSerializer.Serialize(envelopes, typeof(List<LayoutEvidenceEnvelope>), LayoutEvidenceJsonContext.Default);
        var reportJson = LayoutEvidenceProbe.VerifyJson(
            inputJson,
            Commit(),
            SdkVersion(),
            "net10.0",
            expectedRuntimeFamily: "CoreCLR",
            executionEnvironmentOverride: "hosted-desktop");
        WriteText(outputFile, reportJson);
        var report = Deserialize<LayoutEvidenceReport>(reportJson);
        var incompatible = report.Results.Count(static item => !item.RawWireCompatible);
        Console.WriteLine($"Verified {report.Results.Count} layout evidence entries on {report.Consumer.PlatformTag}; observed incompatibilities: {incompatible}.");
        return 0;
    }

    private static int Summarize(string inputDirectory, string outputDirectory)
    {
        var files = Directory.EnumerateFiles(inputDirectory, "layout-verification.json", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException($"No layout-verification.json files found under {inputDirectory}.");
        var reports = files.Select(path => Deserialize<LayoutEvidenceReport>(File.ReadAllText(path, Encoding.UTF8))).ToArray();
        var summary = LayoutEvidenceSummaryBuilder.Build(reports);
        Directory.CreateDirectory(outputDirectory);
        var json = JsonSerializer.Serialize(summary, typeof(LayoutEvidenceSummary), LayoutEvidenceJsonContext.Default);
        WriteText(Path.Combine(outputDirectory, "unsafe-blit-layout-summary.json"), json);
        WriteText(Path.Combine(outputDirectory, "unsafe-blit-layout-summary.md"), LayoutEvidenceSummaryBuilder.CreateMarkdown(summary));
        foreach (var hypothesis in summary.Hypotheses)
            Console.WriteLine($"{hypothesis.Id}: supported={hypothesis.SupportedByObservedMatrix} evidence={string.Join("; ", hypothesis.Evidence)} counter={string.Join("; ", hypothesis.CounterEvidence)}");
        return 0;
    }

    private static void WriteCorpus(LayoutEvidenceEnvelope envelope, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var item in envelope.Cases)
        {
            if (!envelope.CaseBytesBase64.TryGetValue(item.Id, out var base64))
                throw new InvalidOperationException($"Missing encoded bytes for {item.Id}.");
            var wirePath = Path.Combine(outputDirectory, item.WireFile.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(wirePath)!);
            File.WriteAllBytes(wirePath, Convert.FromBase64String(base64));
        }
        var json = JsonSerializer.Serialize(envelope, typeof(LayoutEvidenceEnvelope), LayoutEvidenceJsonContext.Default);
        WriteText(Path.Combine(outputDirectory, "layout-manifest.json"), json);
    }

    private static List<LayoutEvidenceEnvelope> LoadCorpora(string inputDirectory)
    {
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException(inputDirectory);
        var files = Directory.EnumerateFiles(inputDirectory, "layout-manifest.json", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException($"No layout-manifest.json files found under {inputDirectory}.");
        var result = new List<LayoutEvidenceEnvelope>();
        foreach (var file in files)
        {
            var envelope = Deserialize<LayoutEvidenceEnvelope>(File.ReadAllText(file, Encoding.UTF8));
            var root = Path.GetDirectoryName(file)!;
            foreach (var item in envelope.Cases)
            {
                var wirePath = Path.Combine(root, item.WireFile.Replace('/', Path.DirectorySeparatorChar));
                var bytes = File.ReadAllBytes(wirePath);
                if (!envelope.CaseBytesBase64.TryGetValue(item.Id, out var encoded)
                    || !string.Equals(Convert.ToBase64String(bytes), encoded, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Binary/layout-manifest mismatch for {file}/{item.Id}.");
                }
            }
            result.Add(envelope);
        }
        return result;
    }

    private static T Deserialize<T>(string json) where T : class
        => JsonSerializer.Deserialize(json, typeof(T), LayoutEvidenceJsonContext.Default) as T
           ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");

    private static void WriteText(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, text.EndsWith('\n') ? text : text + "\n", new UTF8Encoding(false));
    }

    private static string Commit()
        => Environment.GetEnvironmentVariable("SHARPLINK_COMMIT")
           ?? Environment.GetEnvironmentVariable("GITHUB_SHA")
           ?? "unknown";

    private static string SdkVersion()
        => Environment.GetEnvironmentVariable("SHARPLINK_SDK_VERSION") ?? "unknown";

    private static string GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.Ordinal)) return args[index + 1];
        throw new InvalidOperationException($"Missing required option {name}.");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("produce --profile <fixed-width|native-width> --output <directory>");
        Console.Error.WriteLine("verify --input <producer-root> --output <layout-verification.json>");
        Console.Error.WriteLine("summarize --input <report-root> --output <directory>");
    }
}
