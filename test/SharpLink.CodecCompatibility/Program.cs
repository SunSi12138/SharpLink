using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpLink.CodecCompatibility;

internal static class Program
{
    private const string BuiltinRawCategory = "builtin-semantic-raw";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IncludeFields = true
    };

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
                "describe" => Describe(),
                "produce" => Produce(GetRequiredOption(args, "--output")),
                "verify" => Verify(GetRequiredOption(args, "--input"), GetRequiredOption(args, "--output")),
                "self" => Self(GetRequiredOption(args, "--output")),
                "summarize" => Summarize(GetRequiredOption(args, "--input"), GetRequiredOption(args, "--output")),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"codec-compatibility probe failed: {exception}");
            return 1;
        }
    }

    private static int Describe()
    {
        Console.WriteLine(JsonSerializer.Serialize(CreateRuntimeManifest(), JsonOptions));
        return 0;
    }

    private static int Produce(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var casesDirectory = Path.Combine(outputDirectory, "cases");
        Directory.CreateDirectory(casesDirectory);

        var manifest = CreateRuntimeManifest();
        foreach (var fixture in FixtureRegistry.All)
        {
            var bytes = fixture.Serialize();
            var fileName = $"{fixture.Id}.bin";
            File.WriteAllBytes(Path.Combine(casesDirectory, fileName), bytes);
            manifest.Cases.Add(new CaseManifest
            {
                Id = fixture.Id,
                Category = fixture.Category,
                CodecPath = "UnsafeBlitDirect",
                Type = fixture.TypeName,
                NativeWidth = fixture.NativeWidth,
                Size = fixture.Size,
                FieldOffsets = fixture.FieldOffsets,
                ExpectedLogicalValue = fixture.ExpectedLogicalValue,
                WireFile = $"cases/{fileName}",
                WireSha256 = Hash(bytes)
            });
        }

        manifest.PaddingPoison = FixtureRegistry.RunPaddingPoison();
        WriteJson(Path.Combine(outputDirectory, "manifest.json"), manifest);
        Console.WriteLine($"Produced {manifest.Cases.Count} fixtures for {manifest.PlatformTag} at {outputDirectory}.");
        return 0;
    }

    private static int Verify(string inputDirectory, string outputFile)
    {
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException(inputDirectory);

        var outputDirectory = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        var progressFile = Path.Combine(outputDirectory ?? ".", "verification-progress.log");
        File.WriteAllText(progressFile, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var manifestFiles = Directory.EnumerateFiles(inputDirectory, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (manifestFiles.Length == 0)
            throw new InvalidOperationException($"No producer manifest.json files were found under {inputDirectory}.");

        var consumer = CreateRuntimeManifest();
        var report = new VerificationReport { SchemaVersion = 1, Consumer = consumer };
        var skipBuiltinRaw = string.Equals(
            Environment.GetEnvironmentVariable("SHARPLINK_SKIP_BUILTIN_RAW"),
            "1",
            StringComparison.Ordinal);

        foreach (var manifestFile in manifestFiles)
        {
            var producer = ReadJson<RuntimeManifest>(manifestFile);
            if (producer.SchemaVersion != 1)
                throw new InvalidOperationException($"Unsupported producer schemaVersion {producer.SchemaVersion} in {manifestFile}.");

            ValidateSameCommit(producer, consumer, manifestFile);
            ValidateProducerCases(producer, manifestFile, skipBuiltinRaw);

            var producerRoot = Path.GetDirectoryName(manifestFile) ?? throw new InvalidOperationException($"Cannot resolve producer root for {manifestFile}.");
            foreach (var producerCase in producer.Cases
                         .Where(item => !skipBuiltinRaw || !string.Equals(item.Category, BuiltinRawCategory, StringComparison.Ordinal))
                         .OrderBy(static item => item.Id, StringComparer.Ordinal))
            {
                if (!FixtureRegistry.ById.TryGetValue(producerCase.Id, out var fixture))
                {
                    report.Results.Add(new VerificationEntry
                    {
                        Producer = producer.PlatformTag,
                        Consumer = consumer.PlatformTag,
                        Fixture = producerCase.Id,
                        Category = producerCase.Category,
                        CodecPath = producerCase.CodecPath,
                        ProducerSize = producerCase.Size,
                        ConsumerPointerSize = consumer.PointerSize,
                        ProducerPointerSize = producer.PointerSize,
                        ProducerFieldOffsets = producerCase.FieldOffsets,
                        ProducerWireHash = producerCase.WireSha256,
                        ExpectedLogicalValue = producerCase.ExpectedLogicalValue,
                        Classification = "PROBE_UNAVAILABLE",
                        Blocking = true
                    });
                    continue;
                }

                var wirePath = Path.Combine(producerRoot, producerCase.WireFile.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(wirePath))
                    throw new FileNotFoundException($"Producer wire file is missing for {producerCase.Id}.", wirePath);

                var producerBytes = File.ReadAllBytes(wirePath);
                var observedHash = Hash(producerBytes);
                if (!string.Equals(observedHash, producerCase.WireSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Wire hash mismatch for {producer.PlatformTag}/{producerCase.Id}: manifest={producerCase.WireSha256}, observed={observedHash}.");

                var begin = $"VERIFY_BEGIN producer={producer.PlatformTag} consumer={consumer.PlatformTag} fixture={producerCase.Id} size={producerCase.Size}";
                Console.WriteLine(begin);
                Console.Out.Flush();
                AppendProgress(progressFile, begin);

                var result = fixture.Verify(producerBytes, producerCase, producer, consumer);
                report.Results.Add(result);

                var end = $"VERIFY_END producer={producer.PlatformTag} consumer={consumer.PlatformTag} fixture={producerCase.Id} classification={result.Classification} blocking={result.Blocking}";
                Console.WriteLine(end);
                Console.Out.Flush();
                AppendProgress(progressFile, end);
            }
        }

        WriteJson(outputFile, report);
        PrintVerificationFailures(report.Results);
        var blocking = report.Results.Count(static result => result.Blocking);
        Console.WriteLine($"Verified {report.Results.Count} producer/fixture pairs on {consumer.PlatformTag}; blocking failures: {blocking}.");
        return blocking == 0 ? 0 : 1;
    }

    private static int Self(string outputDirectory)
    {
        var corpusDirectory = Path.Combine(outputDirectory, "corpus");
        var reportFile = Path.Combine(outputDirectory, "self-verification.json");
        var produceExitCode = Produce(corpusDirectory);
        return produceExitCode == 0 ? Verify(corpusDirectory, reportFile) : produceExitCode;
    }

    private static int Summarize(string inputDirectory, string outputDirectory)
    {
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException(inputDirectory);

        var reportFiles = Directory.EnumerateFiles(inputDirectory, "verification.json", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (reportFiles.Length == 0)
            throw new InvalidOperationException($"No verification.json files were found under {inputDirectory}.");

        var results = new List<VerificationEntry>();
        foreach (var reportFile in reportFiles)
        {
            var report = ReadJson<VerificationReport>(reportFile);
            if (report.SchemaVersion != 1)
                throw new InvalidOperationException($"Unsupported verification schemaVersion {report.SchemaVersion} in {reportFile}.");
            results.AddRange(report.Results);
        }

        results = results
            .OrderBy(static result => result.Producer, StringComparer.Ordinal)
            .ThenBy(static result => result.Consumer, StringComparer.Ordinal)
            .ThenBy(static result => result.Fixture, StringComparer.Ordinal)
            .ToList();

        var summary = new CompatibilitySummary
        {
            SchemaVersion = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            BlockingFailures = results.Count(static result => result.Blocking),
            Results = results
        };

        Directory.CreateDirectory(outputDirectory);
        WriteJson(Path.Combine(outputDirectory, "compatibility-summary.json"), summary);
        File.WriteAllText(Path.Combine(outputDirectory, "compatibility-summary.md"), CreateMarkdownSummary(summary), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        PrintVerificationFailures(results);
        Console.WriteLine($"Summarized {results.Count} matrix entries from {reportFiles.Length} consumers; blocking failures: {summary.BlockingFailures}.");
        return summary.BlockingFailures == 0 ? 0 : 1;
    }

    private static RuntimeManifest CreateRuntimeManifest()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "macos"
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "linux"
                    : "unknown";
        var runtimeFamily = Type.GetType("Mono.Runtime") is null ? "CoreCLR" : "Mono";
        var compilationMode = !RuntimeFeature.IsDynamicCodeSupported
            ? "AOT"
            : RuntimeFeature.IsDynamicCodeCompiled
                ? "JIT"
                : "Interpreter";
        var processArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        const string executionEnvironment = "hosted-desktop";

        return new RuntimeManifest
        {
            SchemaVersion = 1,
            SharpLinkCommit = Environment.GetEnvironmentVariable("SHARPLINK_COMMIT")
                ?? Environment.GetEnvironmentVariable("GITHUB_SHA")
                ?? "unknown",
            TargetFramework = "net10.0",
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            RuntimeFamily = runtimeFamily,
            RuntimeVersion = Environment.Version.ToString(),
            SdkVersion = Environment.GetEnvironmentVariable("SHARPLINK_SDK_VERSION") ?? "unknown",
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            ExecutionEnvironment = executionEnvironment,
            Os = os,
            OsVersion = RuntimeInformation.OSDescription,
            ProcessArchitecture = processArchitecture,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            PointerSize = IntPtr.Size,
            IsLittleEndian = BitConverter.IsLittleEndian,
            CompilationMode = compilationMode,
            PlatformTag = $"{os}-{processArchitecture}-{executionEnvironment}-{runtimeFamily.ToLowerInvariant()}-net10"
        };
    }

    private static void ValidateSameCommit(RuntimeManifest producer, RuntimeManifest consumer, string source)
    {
        if (!string.Equals(producer.SharpLinkCommit, consumer.SharpLinkCommit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SharpLink commit mismatch for producer {producer.PlatformTag} in {source}: producer={producer.SharpLinkCommit}, consumer={consumer.SharpLinkCommit}.");
        }
    }

    private static void ValidateProducerCases(RuntimeManifest producer, string source, bool skipBuiltinRaw)
    {
        var expectedIds = FixtureRegistry.All
            .Where(item => !skipBuiltinRaw || !string.Equals(item.Category, BuiltinRawCategory, StringComparison.Ordinal))
            .Select(static item => item.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var relevantCases = producer.Cases
            .Where(item => !skipBuiltinRaw || !string.Equals(item.Category, BuiltinRawCategory, StringComparison.Ordinal))
            .ToArray();

        var duplicates = relevantCases
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length != 0)
            throw new InvalidOperationException($"Producer {producer.PlatformTag} in {source} contains duplicate fixture IDs: {string.Join(", ", duplicates)}.");

        var actualIds = relevantCases.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        var missing = expectedIds.Where(id => !actualIds.Contains(id)).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Producer {producer.PlatformTag} in {source} is missing expected fixture IDs: {string.Join(", ", missing)}.");
    }

    private static string CreateMarkdownSummary(CompatibilitySummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# UnsafeBlit compatibility summary");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{summary.GeneratedAtUtc:O}`  ");
        builder.AppendLine($"Blocking failures: `{summary.BlockingFailures}`");
        builder.AppendLine();
        builder.AppendLine("| Producer | Consumer | Fixture | Producer size | Consumer size | Cross decode | Logical equal | Segmented decode | Segmented logical equal | Byte equal | First diff | Classification |");
        builder.AppendLine("|---|---|---|---:|---:|---|---|---|---|---|---:|---|");
        foreach (var result in summary.Results)
        {
            builder.Append('|').Append(Escape(result.Producer))
                .Append('|').Append(Escape(result.Consumer))
                .Append('|').Append(Escape(result.Fixture))
                .Append('|').Append(result.ProducerSize)
                .Append('|').Append(result.ConsumerSize)
                .Append('|').Append(FormatResult(result.CrossDeserializeResult))
                .Append('|').Append(FormatResult(result.LogicalEquality))
                .Append('|').Append(FormatResult(result.SegmentedCrossDeserializeResult))
                .Append('|').Append(FormatResult(result.SegmentedLogicalEquality))
                .Append('|').Append(result.ByteForByteEquality)
                .Append('|').Append(result.FirstDifferingByteOffset?.ToString() ?? string.Empty)
                .Append('|').Append(Escape(result.Classification))
                .AppendLine("|");
        }

        return builder.ToString();
    }

    private static string FormatResult(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "not-run"
    };

    private static void PrintVerificationFailures(IEnumerable<VerificationEntry> results)
    {
        foreach (var result in results.Where(static item => item.Blocking))
        {
            Console.Error.WriteLine(
                $"BLOCKER fixture={result.Fixture} producer={result.Producer} consumer={result.Consumer} " +
                $"producerSize={result.ProducerSize} consumerSize={result.ConsumerSize} " +
                $"producerPointer={result.ProducerPointerSize} consumerPointer={result.ConsumerPointerSize} " +
                $"producerOffsets={JsonSerializer.Serialize(result.ProducerFieldOffsets, JsonOptions)} " +
                $"consumerOffsets={JsonSerializer.Serialize(result.ConsumerFieldOffsets, JsonOptions)} " +
                $"producerHash={result.ProducerWireHash} localHash={result.ConsumerLocalWireHash} " +
                $"cross={FormatResult(result.CrossDeserializeResult)} logical={FormatResult(result.LogicalEquality)} " +
                $"segmentedCross={FormatResult(result.SegmentedCrossDeserializeResult)} segmentedLogical={FormatResult(result.SegmentedLogicalEquality)} " +
                $"firstDiff={result.FirstDifferingByteOffset?.ToString() ?? "none"} " +
                $"classification={result.Classification} exception={result.ExceptionType}: {result.ExceptionMessage} " +
                $"expected={result.ExpectedLogicalValue} actual={result.ActualLogicalValue}");
        }
    }

    private static void AppendProgress(string path, string line)
        => File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static T ReadJson<T>(string path)
    {
        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        return value ?? throw new InvalidOperationException($"Failed to deserialize {path} as {typeof(T).Name}.");
    }

    private static void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string GetRequiredOption(string[] args, string name)
    {
        for (var index = 1; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }

        throw new ArgumentException($"Missing required option {name}.");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  SharpLink.CodecCompatibility describe");
        Console.Error.WriteLine("  SharpLink.CodecCompatibility produce --output <dir>");
        Console.Error.WriteLine("  SharpLink.CodecCompatibility verify --input <producer-root> --output <verification.json>");
        Console.Error.WriteLine("  SharpLink.CodecCompatibility self --output <dir>");
        Console.Error.WriteLine("  SharpLink.CodecCompatibility summarize --input <verification-root> --output <dir>");
    }
}
