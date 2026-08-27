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
    private const string DesktopLinuxX64PlatformTag = "linux-x64-hosted-desktop-coreclr-net10";
    private const string AndroidArm64DeviceMonoPlatformTag = "android-arm64-physical-device-mono-net10";
    private const string AndroidArm64DeviceCoreClrPlatformTag = "android-arm64-physical-device-coreclr-net10";
    private const string SummaryProfileDesktop = "desktop";
    private const string SummaryProfileMobile = "mobile";
    private const string SummaryProfileAndroidArm64Device = "android-arm64-device";

    private static readonly string[] GuaranteedDesktopPlatformTags =
    [
        DesktopLinuxX64PlatformTag,
        "linux-arm64-hosted-desktop-coreclr-net10",
        "windows-x64-hosted-desktop-coreclr-net10",
        "windows-arm64-hosted-desktop-coreclr-net10",
        "macos-arm64-hosted-desktop-coreclr-net10",
        "macos-x64-hosted-desktop-coreclr-net10"
    ];

    private static readonly string[] AllowedAndroidArm64DeviceDesktopReferenceTags =
    [
        "linux-arm64-hosted-desktop-coreclr-net10",
        "windows-arm64-hosted-desktop-coreclr-net10",
        "macos-arm64-hosted-desktop-coreclr-net10"
    ];

    private static readonly string[] AndroidArm64DevicePlatformTags =
    [
        AndroidArm64DeviceMonoPlatformTag,
        AndroidArm64DeviceCoreClrPlatformTag
    ];

    private static readonly IReadOnlyDictionary<string, string[]> DocumentedMobileProducerTags =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["android-x64-emulator-mono-net10"] =
            [
                DesktopLinuxX64PlatformTag,
                "android-x64-emulator-mono-net10",
                "android-x64-emulator-coreclr-net10"
            ],
            ["android-x64-emulator-coreclr-net10"] =
            [
                DesktopLinuxX64PlatformTag,
                "android-x64-emulator-mono-net10",
                "android-x64-emulator-coreclr-net10"
            ],
            ["ios-x64-simulator-mono-net10"] =
            [
                DesktopLinuxX64PlatformTag,
                "ios-x64-simulator-mono-net10"
            ],
            ["ios-arm64-simulator-mono-net10"] =
            [
                DesktopLinuxX64PlatformTag,
                "ios-arm64-simulator-mono-net10"
            ]
        };

    private static readonly HashSet<string> TrustedBuiltinRawFixtureIds = FixtureRegistry.All
        .Where(static fixture => string.Equals(fixture.Category, BuiltinRawCategory, StringComparison.Ordinal))
        .Select(static fixture => fixture.Id)
        .ToHashSet(StringComparer.Ordinal);

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
                "summarize" => Summarize(
                    GetRequiredOption(args, "--input"),
                    GetRequiredOption(args, "--output"),
                    GetRequiredOption(args, "--profile")),
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
                         .Where(item => !skipBuiltinRaw || !TrustedBuiltinRawFixtureIds.Contains(item.Id))
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

    private static int Summarize(string inputDirectory, string outputDirectory, string profile)
    {
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException(inputDirectory);

        var reportFiles = Directory.EnumerateFiles(inputDirectory, "verification.json", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (reportFiles.Length == 0)
            throw new InvalidOperationException($"No verification.json files were found under {inputDirectory}.");

        var reports = new List<VerificationReport>();
        var results = new List<VerificationEntry>();
        foreach (var reportFile in reportFiles)
        {
            var report = ReadJson<VerificationReport>(reportFile);
            if (report.SchemaVersion != 1)
                throw new InvalidOperationException($"Unsupported verification schemaVersion {report.SchemaVersion} in {reportFile}.");
            if (report.Consumer.SchemaVersion != 1)
                throw new InvalidOperationException($"Unsupported consumer schemaVersion {report.Consumer.SchemaVersion} in {reportFile}.");

            reports.Add(report);
            results.AddRange(report.Results);
        }

        var sharpLinkCommit = ValidateSingleReportCommit(reports);
        switch (profile)
        {
            case SummaryProfileDesktop:
                ValidateGuaranteedDesktopIdentitySet(reports);
                break;
            case SummaryProfileMobile:
                ValidateDocumentedMobileEdgeGraph(reports);
                break;
            case SummaryProfileAndroidArm64Device:
                ValidateAndroidArm64DeviceEdgeGraph(reports);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown summary profile '{profile}'. Expected {SummaryProfileDesktop}, {SummaryProfileMobile}, or {SummaryProfileAndroidArm64Device}.");
        }

        results = results
            .OrderBy(static result => result.Producer, StringComparer.Ordinal)
            .ThenBy(static result => result.Consumer, StringComparer.Ordinal)
            .ThenBy(static result => result.Fixture, StringComparer.Ordinal)
            .ToList();

        var summary = new CompatibilitySummary
        {
            SchemaVersion = 1,
            SummaryProfile = profile,
            SharpLinkCommit = sharpLinkCommit,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            BlockingFailures = results.Count(static result => result.Blocking),
            Results = results
        };

        Directory.CreateDirectory(outputDirectory);
        WriteJson(Path.Combine(outputDirectory, "compatibility-summary.json"), summary);
        File.WriteAllText(Path.Combine(outputDirectory, "compatibility-summary.md"), CreateMarkdownSummary(summary), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        PrintVerificationFailures(results);
        Console.WriteLine($"Summarized {results.Count} {profile} evidence entries from {reportFiles.Length} consumers at commit {sharpLinkCommit}; blocking failures: {summary.BlockingFailures}.");
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

    private static string ValidateSingleReportCommit(IReadOnlyList<VerificationReport> reports)
    {
        var commits = reports
            .Select(static report => report.Consumer.SharpLinkCommit)
            .ToArray();
        if (commits.Any(static commit => string.IsNullOrWhiteSpace(commit) || string.Equals(commit, "unknown", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Evidence summary requires every consumer report to contain a known SharpLink commit.");
        }

        var distinct = commits.Distinct(StringComparer.Ordinal).OrderBy(static commit => commit, StringComparer.Ordinal).ToArray();
        if (distinct.Length != 1)
        {
            throw new InvalidOperationException(
                $"Evidence summary cannot mix SharpLink commits: [{string.Join(", ", distinct)}].");
        }

        return distinct[0];
    }

    private static void ValidateGuaranteedDesktopIdentitySet(IReadOnlyList<VerificationReport> reports)
    {
        if (reports.Count != GuaranteedDesktopPlatformTags.Length)
        {
            throw new InvalidOperationException(
                $"Guaranteed desktop summary requires exactly {GuaranteedDesktopPlatformTags.Length} consumer reports, found {reports.Count}.");
        }

        AssertExactIdentitySet(
            reports.Select(static report => report.Consumer.PlatformTag),
            GuaranteedDesktopPlatformTags,
            "desktop consumer identities");

        var expectedFixtureIds = GetExpectedFixtureIds();
        foreach (var report in reports)
        {
            ValidateConsumerPlatformTagConsistency(report.Consumer, $"desktop consumer {report.Consumer.PlatformTag}");
            if (!string.Equals(report.Consumer.ExecutionEnvironment, "hosted-desktop", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Desktop consumer {report.Consumer.PlatformTag} must report executionEnvironment=hosted-desktop, observed {report.Consumer.ExecutionEnvironment}.");
            }

            AssertExactIdentitySet(
                report.Results.Select(static result => result.Consumer),
                [report.Consumer.PlatformTag],
                $"result consumer identities for {report.Consumer.PlatformTag}");
            AssertExactIdentitySet(
                report.Results.Select(static result => result.Producer),
                GuaranteedDesktopPlatformTags,
                $"producer identities for {report.Consumer.PlatformTag}");
            AssertExactResultKeySet(
                report,
                GuaranteedDesktopPlatformTags,
                expectedFixtureIds,
                $"desktop result keys for {report.Consumer.PlatformTag}");
            ValidateStrictResultSemantics(
                report,
                allowPortableRawRepresentation: false,
                $"desktop result semantics for {report.Consumer.PlatformTag}");
        }

        AssertAggregateResultCount(
            reports,
            GuaranteedDesktopPlatformTags.Length * GuaranteedDesktopPlatformTags.Length * expectedFixtureIds.Length,
            "Guaranteed desktop");
    }

    private static void ValidateDocumentedMobileEdgeGraph(IReadOnlyList<VerificationReport> reports)
    {
        if (reports.Count != DocumentedMobileProducerTags.Count)
        {
            throw new InvalidOperationException(
                $"Documented mobile evidence requires exactly {DocumentedMobileProducerTags.Count} consumer reports, found {reports.Count}.");
        }

        AssertExactIdentitySet(
            reports.Select(static report => report.Consumer.PlatformTag),
            DocumentedMobileProducerTags.Keys,
            "documented mobile consumer identities");

        var expectedFixtureIds = GetExpectedFixtureIds();
        var expectedTotal = 0;
        foreach (var report in reports)
        {
            ValidateConsumerPlatformTagConsistency(report.Consumer, $"mobile consumer {report.Consumer.PlatformTag}");
            if (!DocumentedMobileProducerTags.TryGetValue(report.Consumer.PlatformTag, out var expectedProducers))
            {
                throw new InvalidOperationException(
                    $"Unexpected documented mobile consumer identity: {report.Consumer.PlatformTag}.");
            }

            var expectedEnvironment = report.Consumer.PlatformTag.StartsWith("android-", StringComparison.Ordinal)
                ? "emulator"
                : "simulator";
            if (!string.Equals(report.Consumer.ExecutionEnvironment, expectedEnvironment, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Mobile consumer {report.Consumer.PlatformTag} must report executionEnvironment={expectedEnvironment}, observed {report.Consumer.ExecutionEnvironment}.");
            }

            AssertExactIdentitySet(
                report.Results.Select(static result => result.Consumer),
                [report.Consumer.PlatformTag],
                $"mobile result consumer identities for {report.Consumer.PlatformTag}");
            AssertExactIdentitySet(
                report.Results.Select(static result => result.Producer),
                expectedProducers,
                $"mobile producer identities for {report.Consumer.PlatformTag}");
            AssertExactResultKeySet(
                report,
                expectedProducers,
                expectedFixtureIds,
                $"mobile result keys for {report.Consumer.PlatformTag}");
            ValidateStrictResultSemantics(
                report,
                allowPortableRawRepresentation: true,
                $"mobile result semantics for {report.Consumer.PlatformTag}");

            expectedTotal += expectedProducers.Length * expectedFixtureIds.Length;
        }

        AssertAggregateResultCount(reports, expectedTotal, "Documented mobile");
    }

    private static void ValidateAndroidArm64DeviceEdgeGraph(IReadOnlyList<VerificationReport> reports)
    {
        if (reports.Count != AndroidArm64DevicePlatformTags.Length)
        {
            throw new InvalidOperationException(
                $"Android ARM64 device evidence requires exactly {AndroidArm64DevicePlatformTags.Length} consumer reports, found {reports.Count}.");
        }

        AssertExactIdentitySet(
            reports.Select(static report => report.Consumer.PlatformTag),
            AndroidArm64DevicePlatformTags,
            "Android ARM64 device consumer identities");

        foreach (var report in reports)
        {
            ValidateConsumerPlatformTagConsistency(report.Consumer, $"Android ARM64 device consumer {report.Consumer.PlatformTag}");
            if (!string.Equals(report.Consumer.ExecutionEnvironment, "physical-device", StringComparison.Ordinal)
                || !string.Equals(report.Consumer.Os, "android", StringComparison.Ordinal)
                || !string.Equals(report.Consumer.ProcessArchitecture, "arm64", StringComparison.Ordinal)
                || !string.Equals(report.Consumer.RuntimeIdentifier, "android-arm64", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Android ARM64 device consumer {report.Consumer.PlatformTag} has inconsistent physical identity: " +
                    $"environment={report.Consumer.ExecutionEnvironment}, os={report.Consumer.Os}, processArchitecture={report.Consumer.ProcessArchitecture}, runtimeIdentifier={report.Consumer.RuntimeIdentifier}.");
            }
        }

        var producerTags = reports
            .SelectMany(static report => report.Results.Select(static result => result.Producer))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static tag => tag, StringComparer.Ordinal)
            .ToArray();
        var desktopReferenceTags = producerTags
            .Except(AndroidArm64DevicePlatformTags, StringComparer.Ordinal)
            .ToArray();
        if (desktopReferenceTags.Length != 1
            || !AllowedAndroidArm64DeviceDesktopReferenceTags.Contains(desktopReferenceTags[0], StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Android ARM64 device evidence requires exactly one supported ARM64 hosted-desktop reference producer; observed extras=[{string.Join(", ", desktopReferenceTags)}].");
        }

        var expectedProducers = new[]
        {
            desktopReferenceTags[0],
            AndroidArm64DeviceMonoPlatformTag,
            AndroidArm64DeviceCoreClrPlatformTag
        };
        var expectedFixtureIds = GetExpectedFixtureIds();
        foreach (var report in reports)
        {
            AssertExactIdentitySet(
                report.Results.Select(static result => result.Consumer),
                [report.Consumer.PlatformTag],
                $"Android ARM64 device result consumer identities for {report.Consumer.PlatformTag}");
            AssertExactIdentitySet(
                report.Results.Select(static result => result.Producer),
                expectedProducers,
                $"Android ARM64 device producer identities for {report.Consumer.PlatformTag}");
            AssertExactResultKeySet(
                report,
                expectedProducers,
                expectedFixtureIds,
                $"Android ARM64 device result keys for {report.Consumer.PlatformTag}");
            ValidateStrictResultSemantics(
                report,
                allowPortableRawRepresentation: true,
                $"Android ARM64 device result semantics for {report.Consumer.PlatformTag}");
        }

        AssertAggregateResultCount(
            reports,
            AndroidArm64DevicePlatformTags.Length * expectedProducers.Length * expectedFixtureIds.Length,
            "Android ARM64 device");
    }

    private static void ValidateStrictResultSemantics(
        VerificationReport report,
        bool allowPortableRawRepresentation,
        string label)
    {
        foreach (var result in report.Results)
        {
            var portableRawRepresentation = allowPortableRawRepresentation
                && TrustedBuiltinRawFixtureIds.Contains(result.Fixture)
                && string.Equals(result.Category, BuiltinRawCategory, StringComparison.Ordinal)
                && result.CrossDeserializeResult is null
                && result.LogicalEquality is null
                && result.SegmentedCrossDeserializeResult is null
                && result.SegmentedLogicalEquality is null;
            if (portableRawRepresentation)
            {
                if (!string.Equals(result.Classification, "IDENTICAL_RAW_REPRESENTATION", StringComparison.Ordinal)
                    && !string.Equals(result.Classification, "RAW_BUILTIN_REPRESENTATION_MISMATCH", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{label} contains raw representation-only row with unexpected classification {result.Classification}: " +
                        $"producer={result.Producer}, fixture={result.Fixture}.");
                }

                continue;
            }

            if (string.Equals(result.Classification, "EXPECTED_ARCH_DEPENDENT", StringComparison.Ordinal)
                || result.CrossDeserializeResult != true
                || result.LogicalEquality != true)
            {
                throw new InvalidOperationException(
                    $"{label} requires semantic cross-deserialization success: producer={result.Producer}, fixture={result.Fixture}, " +
                    $"classification={result.Classification}, cross={FormatResult(result.CrossDeserializeResult)}, logical={FormatResult(result.LogicalEquality)}.");
            }

            if (result.ProducerSize > 1
                && (result.SegmentedCrossDeserializeResult != true || result.SegmentedLogicalEquality != true))
            {
                throw new InvalidOperationException(
                    $"{label} requires segmented semantic success for multi-byte fixture: producer={result.Producer}, fixture={result.Fixture}, " +
                    $"size={result.ProducerSize}, segmentedCross={FormatResult(result.SegmentedCrossDeserializeResult)}, " +
                    $"segmentedLogical={FormatResult(result.SegmentedLogicalEquality)}.");
            }
        }
    }

    private static void ValidateConsumerPlatformTagConsistency(RuntimeManifest consumer, string label)
    {
        var expected = $"{consumer.Os}-{consumer.ProcessArchitecture}-{consumer.ExecutionEnvironment}-{consumer.RuntimeFamily.ToLowerInvariant()}-net10";
        if (!string.Equals(consumer.PlatformTag, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label} platformTag mismatch: recorded={consumer.PlatformTag}, derived={expected}.");
        }
    }

    private static string[] GetExpectedFixtureIds()
        => FixtureRegistry.All
            .Select(static fixture => fixture.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

    private static void AssertAggregateResultCount(IReadOnlyList<VerificationReport> reports, int expectedTotal, string label)
    {
        var actualTotal = reports.Sum(static report => report.Results.Count);
        if (actualTotal != expectedTotal)
        {
            throw new InvalidOperationException(
                $"{label} result count mismatch: expected={expectedTotal}, actual={actualTotal}.");
        }
    }

    private static void AssertExactResultKeySet(
        VerificationReport report,
        IEnumerable<string> expectedProducers,
        IEnumerable<string> expectedFixtures,
        string label)
    {
        var expectedKeys = expectedProducers
            .SelectMany(producer => expectedFixtures.Select(fixture => ResultKey(producer, fixture)))
            .ToHashSet(StringComparer.Ordinal);
        var actualKeys = report.Results
            .Select(static result => ResultKey(result.Producer, result.Fixture))
            .ToArray();
        var duplicateKeys = actualKeys
            .GroupBy(static key => key, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        if (duplicateKeys.Length != 0)
        {
            throw new InvalidOperationException(
                $"{label} contains duplicate producer/fixture keys: {string.Join(", ", duplicateKeys)}.");
        }

        var actualKeySet = actualKeys.ToHashSet(StringComparer.Ordinal);
        if (!actualKeySet.SetEquals(expectedKeys))
        {
            var missing = expectedKeys.Except(actualKeySet, StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal);
            var unexpected = actualKeySet.Except(expectedKeys, StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"{label} mismatch: missing=[{string.Join(", ", missing)}], unexpected=[{string.Join(", ", unexpected)}].");
        }
    }

    private static string ResultKey(string producer, string fixture) => $"{producer}\u001f{fixture}";

    private static void AssertExactIdentitySet(IEnumerable<string> actualValues, IEnumerable<string> expectedValues, string label)
    {
        var actual = actualValues.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        var expected = expectedValues.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label} mismatch: expected=[{string.Join(", ", expected)}], actual=[{string.Join(", ", actual)}].");
        }
    }

    private static void ValidateProducerCases(RuntimeManifest producer, string source, bool skipBuiltinRaw)
    {
        var duplicates = producer.Cases
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length != 0)
            throw new InvalidOperationException($"Producer {producer.PlatformTag} in {source} contains duplicate fixture IDs: {string.Join(", ", duplicates)}.");

        foreach (var producerCase in producer.Cases)
        {
            if (FixtureRegistry.ById.TryGetValue(producerCase.Id, out var fixture)
                && !string.Equals(producerCase.Category, fixture.Category, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Producer {producer.PlatformTag} in {source} has category mismatch for {producerCase.Id}: expected={fixture.Category}, actual={producerCase.Category}.");
            }
        }

        var expectedIds = FixtureRegistry.All
            .Where(item => !skipBuiltinRaw || !TrustedBuiltinRawFixtureIds.Contains(item.Id))
            .Select(static item => item.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var relevantCases = producer.Cases
            .Where(item => !skipBuiltinRaw || !TrustedBuiltinRawFixtureIds.Contains(item.Id))
            .ToArray();

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
        builder.AppendLine($"Profile: `{summary.SummaryProfile}`  ");
        builder.AppendLine($"SharpLink commit: `{summary.SharpLinkCommit}`  ");
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
        Console.Error.WriteLine("  SharpLink.CodecCompatibility summarize --input <verification-root> --output <dir> --profile <desktop|mobile|android-arm64-device>");
    }
}
