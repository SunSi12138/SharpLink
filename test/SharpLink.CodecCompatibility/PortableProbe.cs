using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLink.CodecCompatibility;

internal static class PortableProbe
{
    internal static string ProduceJson(
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? compilationModeOverride = null,
        string? runtimeFamilyOverride = null,
        string? executionEnvironmentOverride = null)
    {
        var envelope = Produce(
            sharpLinkCommit,
            sdkVersion,
            targetFramework,
            compilationModeOverride,
            runtimeFamilyOverride,
            executionEnvironmentOverride);
        return JsonSerializer.Serialize(envelope, typeof(CorpusEnvelope), PortableJsonContext.Default);
    }

    internal static string VerifyJson(
        string envelopesJson,
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? compilationModeOverride = null,
        string? runtimeFamilyOverride = null,
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
            runtimeFamilyOverride,
            executionEnvironmentOverride);
        return JsonSerializer.Serialize(report, typeof(VerificationReport), PortableJsonContext.Default);
    }

    internal static CorpusEnvelope Produce(
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? compilationModeOverride = null,
        string? runtimeFamilyOverride = null,
        string? executionEnvironmentOverride = null)
    {
        var manifest = CreateRuntimeManifest(
            sharpLinkCommit,
            sdkVersion,
            targetFramework,
            compilationModeOverride,
            runtimeFamilyOverride,
            executionEnvironmentOverride);
        var envelope = new CorpusEnvelope { Manifest = manifest };

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
        string? runtimeFamilyOverride = null,
        string? executionEnvironmentOverride = null)
    {
        var consumer = CreateRuntimeManifest(
            sharpLinkCommit,
            sdkVersion,
            targetFramework,
            compilationModeOverride,
            runtimeFamilyOverride,
            executionEnvironmentOverride);
        var report = new VerificationReport { Consumer = consumer };

        foreach (var envelope in envelopes.OrderBy(static item => item.Manifest.PlatformTag, StringComparer.Ordinal))
        {
            if (envelope.SchemaVersion != 1 || envelope.Manifest.SchemaVersion != 1)
                throw new InvalidOperationException($"Unsupported portable corpus schema for {envelope.Manifest.PlatformTag}.");

            var producer = envelope.Manifest;
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
        string? runtimeFamilyOverride,
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
        var runtimeFamily = runtimeFamilyOverride
            ?? (Type.GetType("Mono.Runtime") is null ? "CoreCLR" : "Mono");
        var compilationMode = compilationModeOverride
            ?? (!RuntimeFeature.IsDynamicCodeSupported
                ? "AOT"
                : RuntimeFeature.IsDynamicCodeCompiled
                    ? "JIT"
                    : "Interpreter");
        var processArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
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
            SharpLinkCommit = string.IsNullOrWhiteSpace(sharpLinkCommit) ? "unknown" : sharpLinkCommit,
            TargetFramework = targetFramework,
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            RuntimeFamily = runtimeFamily,
            RuntimeVersion = Environment.Version.ToString(),
            SdkVersion = string.IsNullOrWhiteSpace(sdkVersion) ? "unknown" : sdkVersion,
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