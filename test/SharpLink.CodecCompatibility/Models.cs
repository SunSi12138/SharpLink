using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SharpLink.CodecCompatibility;

internal sealed class RuntimeManifest : IJsonOnDeserialized
{
    [JsonRequired]
    public int SchemaVersion { get; set; }
    public string SharpLinkCommit { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public string FrameworkDescription { get; set; } = string.Empty;
    public string RuntimeFamily { get; set; } = string.Empty;
    [JsonRequired]
    public string RuntimeFamilySource { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeIdentifier { get; set; } = string.Empty;
    public string ExecutionEnvironment { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string ProcessArchitecture { get; set; } = string.Empty;
    public string OsArchitecture { get; set; } = string.Empty;
    public int PointerSize { get; set; }
    public bool IsLittleEndian { get; set; }
    public string CompilationMode { get; set; } = string.Empty;
    public string PlatformTag { get; set; } = string.Empty;
    [JsonRequired]
    public List<FixtureRegistryEntry> FixtureRegistry { get; set; } = CreateFixtureRegistry();
    public List<CaseManifest> Cases { get; set; } = [];
    public List<PaddingPoisonResult> PaddingPoison { get; set; } = [];

    void IJsonOnDeserialized.OnDeserialized()
    {
        ValidateFixtureRegistry();
        var derivedTag = $"{Os}-{ProcessArchitecture}-{ExecutionEnvironment}-{RuntimeFamily.ToLowerInvariant()}-net10";
        if (!string.Equals(PlatformTag, derivedTag, StringComparison.Ordinal))
            throw new InvalidOperationException($"Runtime manifest platformTag mismatch: recorded={PlatformTag}, derived={derivedTag}.");

        switch (PlatformTag)
        {
            case "linux-x64-hosted-desktop-coreclr-net10":
                ValidateKnownIdentity("linux", "x64", "hosted-desktop", "CoreCLR", "runtime-reflection", "linux-x64", "net10.0", 8);
                break;
            case "linux-arm64-hosted-desktop-coreclr-net10":
                ValidateKnownIdentity("linux", "arm64", "hosted-desktop", "CoreCLR", "runtime-reflection", "linux-arm64", "net10.0", 8);
                break;
            case "windows-x64-hosted-desktop-coreclr-net10":
                ValidateKnownIdentity("windows", "x64", "hosted-desktop", "CoreCLR", "runtime-reflection", "win-x64", "net10.0", 8);
                break;
            case "windows-arm64-hosted-desktop-coreclr-net10":
                ValidateKnownIdentity("windows", "arm64", "hosted-desktop", "CoreCLR", "runtime-reflection", "win-arm64", "net10.0", 8);
                break;
            case "macos-x64-hosted-desktop-coreclr-net10":
                ValidateKnownIdentity("macos", "x64", "hosted-desktop", "CoreCLR", "runtime-reflection", "osx-x64", "net10.0", 8);
                break;
            case "macos-arm64-hosted-desktop-coreclr-net10":
                ValidateKnownIdentity("macos", "arm64", "hosted-desktop", "CoreCLR", "runtime-reflection", "osx-arm64", "net10.0", 8);
                break;
            case "browser-wasm-browser-mono-net10":
                ValidateKnownIdentity("browser", "wasm", "browser", "Mono", "platform-runtime-pack", "browser-wasm", "net10.0/browser-wasm", 4);
                break;
            case "android-x64-emulator-mono-net10":
            case "android-x64-emulator-coreclr-net10":
                ValidateKnownIdentity("android", "x64", "emulator", RuntimeFamily, "loaded-runtime-library", "android-x64", "net10.0-android/android-x64", 8);
                break;
            case "ios-x64-simulator-mono-net10":
                ValidateKnownIdentity("ios", "x64", "simulator", "Mono", "platform-runtime-pack", "iossimulator-x64", "net10.0-ios/iossimulator-x64", 8);
                break;
            case "ios-arm64-simulator-mono-net10":
                ValidateKnownIdentity("ios", "arm64", "simulator", "Mono", "platform-runtime-pack", "iossimulator-arm64", "net10.0-ios/iossimulator-arm64", 8);
                break;
            case "android-arm64-physical-device-mono-net10":
            case "android-arm64-physical-device-coreclr-net10":
                ValidateKnownIdentity("android", "arm64", "physical-device", RuntimeFamily, "loaded-runtime-library", "android-arm64", "net10.0-android/android-arm64", 8);
                break;
        }
    }

    private void ValidateKnownIdentity(
        string expectedOs,
        string expectedProcessArchitecture,
        string expectedExecutionEnvironment,
        string expectedRuntimeFamily,
        string expectedRuntimeFamilySource,
        string expectedRuntimeIdentifier,
        string expectedTargetFramework,
        int expectedPointerSize)
    {
        if (!string.Equals(Os, expectedOs, StringComparison.Ordinal)
            || !string.Equals(ProcessArchitecture, expectedProcessArchitecture, StringComparison.Ordinal)
            || !string.Equals(ExecutionEnvironment, expectedExecutionEnvironment, StringComparison.Ordinal)
            || !string.Equals(RuntimeFamily, expectedRuntimeFamily, StringComparison.Ordinal)
            || !string.Equals(RuntimeFamilySource, expectedRuntimeFamilySource, StringComparison.Ordinal)
            || !string.Equals(RuntimeIdentifier, expectedRuntimeIdentifier, StringComparison.Ordinal)
            || !string.Equals(TargetFramework, expectedTargetFramework, StringComparison.Ordinal)
            || PointerSize != expectedPointerSize)
        {
            throw new InvalidOperationException(
                $"Runtime manifest {PlatformTag} has inconsistent identity: os={Os}, processArchitecture={ProcessArchitecture}, " +
                $"environment={ExecutionEnvironment}, runtimeFamily={RuntimeFamily}, runtimeFamilySource={RuntimeFamilySource}, " +
                $"runtimeIdentifier={RuntimeIdentifier}, targetFramework={TargetFramework}, pointerSize={PointerSize}.");
        }
    }

    private void ValidateFixtureRegistry()
    {
        var expected = global::SharpLink.CodecCompatibility.FixtureRegistry.All
            .OrderBy(static fixture => fixture.Id, StringComparer.Ordinal)
            .Select(static fixture => (fixture.Id, fixture.Category, fixture.NativeWidth))
            .ToArray();
        var actual = FixtureRegistry
            .OrderBy(static fixture => fixture.Id, StringComparer.Ordinal)
            .Select(static fixture => (fixture.Id, fixture.Category, fixture.NativeWidth))
            .ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"Runtime manifest {PlatformTag} fixture registry does not match the shared FixtureRegistry.");
    }

    private static List<FixtureRegistryEntry> CreateFixtureRegistry()
        => global::SharpLink.CodecCompatibility.FixtureRegistry.All
            .Select(static fixture => new FixtureRegistryEntry
            {
                Id = fixture.Id,
                Category = fixture.Category,
                NativeWidth = fixture.NativeWidth
            })
            .ToList();
}

internal sealed class FixtureRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool NativeWidth { get; set; }
}

internal sealed class CaseManifest
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CodecPath { get; set; } = "UnsafeBlitDirect";
    public string Type { get; set; } = string.Empty;
    public bool NativeWidth { get; set; }
    public int Size { get; set; }
    public Dictionary<string, int> FieldOffsets { get; set; } = [];
    public string ExpectedLogicalValue { get; set; } = string.Empty;
    public string WireFile { get; set; } = string.Empty;
    public string WireSha256 { get; set; } = string.Empty;
}

internal sealed class PaddingPoisonResult
{
    public string Fixture { get; set; } = string.Empty;
    public int Size { get; set; }
    public bool LogicalValuesEqual { get; set; }
    public bool WireBytesEqual { get; set; }
    public List<int> DifferingByteOffsets { get; set; } = [];
    public List<int> PaddingByteOffsets { get; set; } = [];
    public bool DifferencesOnlyInPadding { get; set; }
    public string SourceAHash { get; set; } = string.Empty;
    public string SourceBHash { get; set; } = string.Empty;
    public string WireAHash { get; set; } = string.Empty;
    public string WireBHash { get; set; } = string.Empty;
}

internal sealed class VerificationReport
{
    [JsonRequired]
    public int SchemaVersion { get; set; }
    public RuntimeManifest Consumer { get; set; } = new();
    public List<VerificationEntry> Results { get; set; } = [];
}

internal sealed class VerificationEntry : IJsonOnDeserialized
{
    public string Producer { get; set; } = string.Empty;
    public string Consumer { get; set; } = string.Empty;
    public string Fixture { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CodecPath { get; set; } = string.Empty;
    public int ProducerSize { get; set; }
    public int ConsumerSize { get; set; }
    public int ProducerPointerSize { get; set; }
    public int ConsumerPointerSize { get; set; }
    public Dictionary<string, int> ProducerFieldOffsets { get; set; } = [];
    public Dictionary<string, int> ConsumerFieldOffsets { get; set; } = [];
    public string ProducerWireHash { get; set; } = string.Empty;
    public string ConsumerLocalWireHash { get; set; } = string.Empty;
    public bool? CrossDeserializeResult { get; set; }
    public bool? LogicalEquality { get; set; }
    public bool? SegmentedCrossDeserializeResult { get; set; }
    public bool? SegmentedLogicalEquality { get; set; }
    public bool ByteForByteEquality { get; set; }
    public int? FirstDifferingByteOffset { get; set; }
    public string Classification { get; set; } = string.Empty;
    public bool Blocking { get; set; }
    public string ExpectedLogicalValue { get; set; } = string.Empty;
    public string ActualLogicalValue { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        var raw = string.Equals(Category, "builtin-semantic-raw", StringComparison.Ordinal)
            && (string.Equals(Classification, "IDENTICAL_RAW_REPRESENTATION", StringComparison.Ordinal)
                || string.Equals(Classification, "RAW_BUILTIN_REPRESENTATION_MISMATCH", StringComparison.Ordinal));
        if (raw)
        {
            var expectedByteEquality = string.Equals(Classification, "IDENTICAL_RAW_REPRESENTATION", StringComparison.Ordinal);
            if (CrossDeserializeResult is not null || LogicalEquality is not null
                || SegmentedCrossDeserializeResult is not null || SegmentedLogicalEquality is not null
                || ByteForByteEquality != expectedByteEquality
                || (expectedByteEquality ? FirstDifferingByteOffset is not null : FirstDifferingByteOffset is null))
            {
                throw new InvalidOperationException(
                    $"Raw result invariant mismatch for producer={Producer}, fixture={Fixture}, classification={Classification}.");
            }
            return;
        }

        var semanticSuccess = CrossDeserializeResult == true
            && LogicalEquality == true
            && (ProducerSize <= 1 || (SegmentedCrossDeserializeResult == true && SegmentedLogicalEquality == true));
        if (!semanticSuccess)
            return;

        var expectedClassification = ByteForByteEquality
            ? "IDENTICAL_BYTES_AND_COMPATIBLE"
            : "DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE";
        if (!string.Equals(Classification, expectedClassification, StringComparison.Ordinal)
            || (ByteForByteEquality ? FirstDifferingByteOffset is not null : FirstDifferingByteOffset is null))
        {
            throw new InvalidOperationException(
                $"Semantic result invariant mismatch for producer={Producer}, fixture={Fixture}: " +
                $"classification={Classification}, expected={expectedClassification}, byteEqual={ByteForByteEquality}, firstDiff={FirstDifferingByteOffset}.");
        }
    }
}

internal sealed class CompatibilitySummary
{
    [JsonRequired]
    public int SchemaVersion { get; set; }
    [JsonRequired]
    public string SummaryProfile { get; set; } = string.Empty;
    [JsonRequired]
    public string SharpLinkCommit { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public int BlockingFailures { get; set; }
    public List<VerificationEntry> Results { get; set; } = [];
}
