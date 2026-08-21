using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLink.CodecCompatibility;

internal static class PortableProbe
{
    private const string BuiltinRawCategory = "builtin-semantic-raw";

    internal static string ProduceJson(
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? compilationModeOverride = null,
        string? expectedRuntimeFamily = null,
        string? executionEnvironmentOverride = null)
    {
        var envelope = Produce(
            sharpLinkCommit,
            sdkVersion,
            targetFramework,
            compilationModeOverride,
            expectedRuntimeFamily,
            executionEnvironmentOverride);
        return JsonSerializer.Serialize(envelope, typeof(CorpusEnvelope), PortableJsonContext.Default);
    }

    internal static string VerifyJson(
        string envelopesJson,
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? compilationModeOverride = null,
        string? expectedRuntimeFamily = null,
        string? executionEnvironmentOverride = null)
    {
        var envelopes = JsonSerializer.Deserialize(
                envelopesJson,
                typeof(List<CorpusEnvelope>),
                PortableJsonContext.Default) as List<CorpusEnvelope>
            ?? throw new InvalidOperationException("Failed to deserialize portable producer envelopes.");
        var report = Verify(
            envelopes,
            sharpLinkCommit,
            sdkVersion,
            targetFramework,
            compilationModeOverride,
            expectedRuntimeFamily,
            executionEnvironmentOverride);
        return JsonSerializer.Serialize(report, typeof(VerificationReport), PortableJsonContext.Default);
    }

    internal static CorpusEnvelope Produce(
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? compilationModeOverride = null,
        string? expectedRuntimeFamily = null,
        string? executionEnvironmentOverride = null)
    {
        var manifest = CreateRuntimeManifest(
            sharpLinkCommit,
            sdkVersion,
            targetFramework,
            compilationModeOverride,
            expectedRuntimeFamily,
            executionEnvironmentOverride);
        var envelope = new CorpusEnvelope { SchemaVersion = 1, Manifest = manifest };

        foreach (var fixture in FixtureRegistry.All)
        {
            var bytes = fixture.Serialize();
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
                WireFile = $"cases/{fixture.Id}.bin",
                WireSha256 = Hash(bytes)
            });
            envelope.CaseBytesBase64.Add(fixture.Id, Convert.ToBase64String(bytes));
        }

        manifest.PaddingPoison = FixtureRegistry.RunPaddingPoison();
        return envelope;
    }

    internal static VerificationReport Verify(
        IReadOnlyList<CorpusEnvelope> envelopes,
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? compilationModeOverride = null,
        string? expectedRuntimeFamily = null,
        string? executionEnvironmentOverride = null)
    {
        var consumer = CreateRuntimeManifest(
            sharpLinkCommit,
            sdkVersion,
            targetFramework,
            compilationModeOverride,
            expectedRuntimeFamily,
            executionEnvironmentOverride);
        var report = new VerificationReport { SchemaVersion = 1, Consumer = consumer };

        foreach (var envelope in envelopes.OrderBy(static item => item.Manifest.PlatformTag, StringComparer.Ordinal))
        {
            if (envelope.SchemaVersion != 1 || envelope.Manifest.SchemaVersion != 1)
                throw new InvalidOperationException($"Unsupported portable corpus schema for {envelope.Manifest.PlatformTag}.");

            var producer = envelope.Manifest;
            ValidateSameCommit(producer, consumer);
            ValidateProducerCases(envelope);

            foreach (var producerCase in producer.Cases.OrderBy(static item => item.Id, StringComparer.Ordinal))
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

                if (!envelope.CaseBytesBase64.TryGetValue(producerCase.Id, out var base64))
                    throw new InvalidOperationException($"Portable corpus is missing wire bytes for {producer.PlatformTag}/{producerCase.Id}.");

                var producerBytes = Convert.FromBase64String(base64);
                var observedHash = Hash(producerBytes);
                if (!string.Equals(observedHash, producerCase.WireSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Wire hash mismatch for {producer.PlatformTag}/{producerCase.Id}: manifest={producerCase.WireSha256}, observed={observedHash}.");
                }

                report.Results.Add(fixture.Verify(producerBytes, producerCase, producer, consumer));
            }
        }

        return report;
    }

    private static RuntimeManifest CreateRuntimeManifest(
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? compilationModeOverride,
        string? expectedRuntimeFamily,
        string? executionEnvironmentOverride)
    {
        var os = OperatingSystem.IsBrowser()
            ? "browser"
            : OperatingSystem.IsAndroid()
                ? "android"
                : OperatingSystem.IsIOS()
                    ? "ios"
                    : OperatingSystem.IsMacCatalyst()
                        ? "maccatalyst"
                        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                            ? "windows"
                            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                                ? "macos"
                                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                                    ? "linux"
                                    : "unknown";
        var runtimeFamily = DetectRuntimeFamily();
        if (!string.IsNullOrWhiteSpace(expectedRuntimeFamily)
            && !string.Equals(runtimeFamily, expectedRuntimeFamily, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Runtime family mismatch: expected lane {expectedRuntimeFamily}, observed {runtimeFamily} in-process.");
        }

        var compilationMode = compilationModeOverride
            ?? (!RuntimeFeature.IsDynamicCodeSupported
                ? "AOT"
                : RuntimeFeature.IsDynamicCodeCompiled
                    ? "JIT"
                    : "Interpreter");
        var processArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var runtimeIdentifier = DetectRuntimeIdentifier(os, processArchitecture);
        var executionEnvironment = executionEnvironmentOverride
            ?? (OperatingSystem.IsBrowser()
                ? "browser"
                : OperatingSystem.IsAndroid()
                    ? "android-runtime"
                    : OperatingSystem.IsIOS()
                        ? "ios-runtime"
                        : "hosted-desktop");

        return new RuntimeManifest
        {
            SchemaVersion = 1,
            SharpLinkCommit = string.IsNullOrWhiteSpace(sharpLinkCommit) ? "unknown" : sharpLinkCommit,
            TargetFramework = targetFramework,
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            RuntimeFamily = runtimeFamily,
            RuntimeVersion = Environment.Version.ToString(),
            SdkVersion = string.IsNullOrWhiteSpace(sdkVersion) ? "unknown" : sdkVersion,
            RuntimeIdentifier = runtimeIdentifier,
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

    private static void ValidateSameCommit(RuntimeManifest producer, RuntimeManifest consumer)
    {
        if (!string.Equals(producer.SharpLinkCommit, consumer.SharpLinkCommit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SharpLink commit mismatch for portable producer {producer.PlatformTag}: producer={producer.SharpLinkCommit}, consumer={consumer.SharpLinkCommit}.");
        }
    }

    private static void ValidateProducerCases(CorpusEnvelope envelope)
    {
        var producer = envelope.Manifest;
        var duplicates = producer.Cases
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length != 0)
            throw new InvalidOperationException($"Portable producer {producer.PlatformTag} contains duplicate fixture IDs: {string.Join(", ", duplicates)}.");

        var includesBuiltinRaw = producer.Cases.Any(item => string.Equals(item.Category, BuiltinRawCategory, StringComparison.Ordinal));
        var expectedIds = FixtureRegistry.All
            .Where(item => includesBuiltinRaw || !string.Equals(item.Category, BuiltinRawCategory, StringComparison.Ordinal))
            .Select(static item => item.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var actualIds = producer.Cases.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        var missing = expectedIds.Where(id => !actualIds.Contains(id)).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Portable producer {producer.PlatformTag} is missing expected fixture IDs: {string.Join(", ", missing)}.");
    }

    private static string DetectRuntimeIdentifier(string os, string processArchitecture)
    {
        var reported = RuntimeInformation.RuntimeIdentifier;
        if (!string.Equals(os, "android", StringComparison.OrdinalIgnoreCase)
            || reported.StartsWith("android-", StringComparison.OrdinalIgnoreCase))
        {
            return reported;
        }

        return processArchitecture switch
        {
            "x64" => "android-x64",
            "arm64" => "android-arm64",
            "x86" => "android-x86",
            "arm" => "android-arm",
            _ => throw new InvalidOperationException(
                $"Unsupported Android process architecture for effective RID detection: {processArchitecture}.")
        };
    }

    private static string DetectRuntimeFamily()
    {
        // .NET's browser-wasm and iOS runtime packs execute Mono. Mono.Runtime can be
        // trimmed from these closed-world applications, so reflection alone is not a
        // reliable runtime discriminator there. OperatingSystem.* is evaluated by the
        // running target and cannot be supplied by the workflow harness.
        if (OperatingSystem.IsBrowser() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            return "Mono";

        if (OperatingSystem.IsAndroid())
            return DetectAndroidRuntimeFamily();

        return Type.GetType("Mono.Runtime") is null ? "CoreCLR" : "Mono";
    }

    private static string DetectAndroidRuntimeFamily()
    {
        string processMaps;
        try
        {
            processMaps = File.ReadAllText("/proc/self/maps");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Unable to inspect loaded Android runtime libraries from /proc/self/maps.", exception);
        }

        var monoLoaded = processMaps.Contains("libmonosgen-2.0.so", StringComparison.Ordinal);
        var coreClrLoaded = processMaps.Contains("libcoreclr.so", StringComparison.Ordinal);
        if (monoLoaded == coreClrLoaded)
        {
            throw new InvalidOperationException(
                $"Unable to identify Android runtime from loaded libraries: monoLoaded={monoLoaded}, coreClrLoaded={coreClrLoaded}.");
        }

        return monoLoaded ? "Mono" : "CoreCLR";
    }

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CorpusEnvelope))]
[JsonSerializable(typeof(List<CorpusEnvelope>))]
[JsonSerializable(typeof(VerificationReport))]
internal partial class PortableJsonContext : JsonSerializerContext
{
}
