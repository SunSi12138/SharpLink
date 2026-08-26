using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpLink.CodecCompatibility;

internal readonly record struct FixturePolicyEntry(
    string Id,
    string Category,
    bool NativeWidth,
    bool RequiresSegmentedEvidence);

internal static class CompatibilityPolicy
{
    internal const int ArtifactSchemaVersion = 1;
    internal const string BaselineFixturePolicySha256 = "19ba9cda6e05e7a023af6ce76649deaf330e67d214f553c6611bab45019987d9";

    private static readonly FixturePolicyEntry[] RequiredFixtures =
    [
        new("Byte", "no-padding", false, false),
        new("Int16", "no-padding", false, true),
        new("Int32", "no-padding", false, true),
        new("Int64", "no-padding", false, true),
        new("Single", "no-padding", false, true),
        new("Double", "no-padding", false, true),
        new("Half", "no-padding", false, true),
        new("Int128", "no-padding", false, true),
        new("UInt128", "no-padding", false, true),
        new("Guid", "no-padding", false, true),
        new("Int32Pair", "no-padding", false, true),
        new("ByteInt32", "internal-padding", false, true),
        new("ByteInt64", "internal-padding", false, true),
        new("Int64Byte", "tail-padding", false, true),
        new("ByteShortIntLong", "alignment", false, true),
        new("ByteDouble", "alignment", false, true),
        new("ShortLongByte", "alignment", false, true),
        new("NestedPadded", "nested", false, true),
        new("SequentialDefault", "explicit-layout-control", false, true),
        new("Pack1", "packed-control", false, true),
        new("Pack2", "packed-control", false, true),
        new("Pack4", "packed-control", false, true),
        new("Pack8", "packed-control", false, true),
        new("ExplicitLayout", "explicit-layout-control", false, true),
        new("NativeInt", "native-width", true, true),
        new("NativeUInt", "native-width", true, true),
        new("NativePair", "native-width", true, true),
        new("ByteEnum", "enum", false, false),
        new("ShortEnum", "enum", false, true),
        new("IntEnum", "enum", false, true),
        new("LongEnum", "enum", false, true),
        new("EnumContainer", "enum", false, true),
        new("Large64", "large", false, true),
        new("Large256", "large", false, true),
        new("Large1024", "large", false, true),
        new("Large2048", "large", false, true),
        new("Vector3Value", "user-like", false, true),
        new("TimestampFlags", "user-like", false, true),
        new("IdentityCounter", "user-like", false, true),
        new("GeometryValue", "user-like", false, true),
        new("DateOnlyRaw", "builtin-semantic-raw", false, true),
        new("DateTimeRaw", "builtin-semantic-raw", false, true),
        new("DateTimeOffsetRaw", "builtin-semantic-raw", false, true),
        new("TimeOnlyRaw", "builtin-semantic-raw", false, true),
        new("TimeSpanRaw", "builtin-semantic-raw", false, true),
        new("IndexRaw", "builtin-semantic-raw", false, true),
        new("RangeRaw", "builtin-semantic-raw", false, true),
        new("RuneRaw", "builtin-semantic-raw", false, true),
        new("DecimalRaw", "builtin-semantic-raw", false, true)
    ];

    private static readonly IReadOnlyDictionary<string, FixturePolicyEntry> RequiredById = RequiredFixtures
        .ToDictionary(static entry => entry.Id, StringComparer.Ordinal);

    internal static IReadOnlyList<string> ExpectedFixtureIds { get; } = RequiredFixtures
        .Select(static entry => entry.Id)
        .OrderBy(static id => id, StringComparer.Ordinal)
        .ToArray();

    internal static IReadOnlySet<string> BuiltinRawFixtureIds { get; } = RequiredFixtures
        .Where(static entry => string.Equals(entry.Category, "builtin-semantic-raw", StringComparison.Ordinal))
        .Select(static entry => entry.Id)
        .ToHashSet(StringComparer.Ordinal);

    internal static void ValidateCurrentFixtureRegistry()
    {
        var expected = RequiredFixtures
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        var actual = FixtureRegistry.All
            .OrderBy(static fixture => fixture.Id, StringComparer.Ordinal)
            .Select(static fixture => new FixturePolicyEntry(
                fixture.Id,
                fixture.Category,
                fixture.NativeWidth,
                fixture.Size > 1))
            .ToArray();

        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                "FixtureRegistry no longer matches the compatibility baseline policy. " +
                "Update CompatibilityPolicy intentionally when changing the retained compatibility contract.");
        }
    }

    internal static void ValidateManifestFixtureRegistry(RuntimeManifest manifest)
    {
        ValidateCurrentFixtureRegistry();

        var expected = RequiredFixtures
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .Select(static entry => (entry.Id, entry.Category, entry.NativeWidth))
            .ToArray();
        var actual = manifest.FixtureRegistry
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .Select(static entry => (entry.Id, entry.Category, entry.NativeWidth))
            .ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Runtime manifest {manifest.PlatformTag} fixture registry does not match compatibility baseline policy {BaselineFixturePolicySha256}.");
        }
    }

    internal static FixturePolicyEntry GetFixturePolicy(string fixtureId)
    {
        if (!RequiredById.TryGetValue(fixtureId, out var policy))
            throw new InvalidOperationException($"Fixture {fixtureId} is not part of the compatibility baseline policy.");
        return policy;
    }

    internal static bool RequiresSegmentedEvidence(string fixtureId)
        => GetFixturePolicy(fixtureId).RequiresSegmentedEvidence;

    internal static void ValidatePaddingPoisonEvidence(RuntimeManifest manifest)
    {
        var expectedIds = new[] { "ByteInt32", "Int64Byte" };
        var actualIds = manifest.PaddingPoison.Select(static item => item.Fixture).ToArray();
        var duplicates = actualIds
            .GroupBy(static id => id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length != 0)
            throw new InvalidOperationException($"Padding-poison evidence for {manifest.PlatformTag} contains duplicate fixtures: {string.Join(", ", duplicates)}.");

        var expectedSet = expectedIds.ToHashSet(StringComparer.Ordinal);
        var actualSet = actualIds.ToHashSet(StringComparer.Ordinal);
        if (!actualSet.SetEquals(expectedSet) || actualIds.Length != expectedIds.Length)
        {
            throw new InvalidOperationException(
                $"Padding-poison evidence for {manifest.PlatformTag} must contain exactly [{string.Join(", ", expectedIds)}].");
        }

        foreach (var result in manifest.PaddingPoison)
        {
            if (!FixtureRegistry.ById.TryGetValue(result.Fixture, out var fixture))
                throw new InvalidOperationException($"Unknown padding-poison fixture {result.Fixture}.");
            if (result.Size != fixture.Size || result.Size <= 0 || !result.LogicalValuesEqual)
            {
                throw new InvalidOperationException(
                    $"Padding-poison evidence invariant mismatch for {manifest.PlatformTag}/{result.Fixture}: size={result.Size}, expectedSize={fixture.Size}, logicalEqual={result.LogicalValuesEqual}.");
            }

            var padding = result.PaddingByteOffsets.ToHashSet();
            var differing = result.DifferingByteOffsets.ToHashSet();
            if (padding.Count != result.PaddingByteOffsets.Count
                || differing.Count != result.DifferingByteOffsets.Count
                || padding.Count == 0
                || padding.Any(offset => offset < 0 || offset >= result.Size)
                || differing.Any(offset => offset < 0 || offset >= result.Size))
            {
                throw new InvalidOperationException($"Padding-poison offsets are invalid for {manifest.PlatformTag}/{result.Fixture}.");
            }

            var expectedWireEqual = differing.Count == 0;
            var expectedOnlyPadding = differing.All(padding.Contains);
            if (result.WireBytesEqual != expectedWireEqual || result.DifferencesOnlyInPadding != expectedOnlyPadding)
            {
                throw new InvalidOperationException(
                    $"Padding-poison result flags are inconsistent for {manifest.PlatformTag}/{result.Fixture}: " +
                    $"wireEqual={result.WireBytesEqual}, differencesOnlyInPadding={result.DifferencesOnlyInPadding}.");
            }

            if (!IsSha256(result.SourceAHash) || !IsSha256(result.SourceBHash)
                || !IsSha256(result.WireAHash) || !IsSha256(result.WireBHash))
            {
                throw new InvalidOperationException($"Padding-poison hashes are missing or invalid for {manifest.PlatformTag}/{result.Fixture}.");
            }
        }
    }

    internal static void ValidateServicingIdentity(RuntimeManifest manifest)
    {
        RequireKnown(manifest.PlatformTag, "sharpLinkCommit", manifest.SharpLinkCommit);
        RequireKnown(manifest.PlatformTag, "frameworkDescription", manifest.FrameworkDescription);
        RequireKnown(manifest.PlatformTag, "runtimeVersion", manifest.RuntimeVersion);
        RequireKnown(manifest.PlatformTag, "sdkVersion", manifest.SdkVersion);
        RequireKnown(manifest.PlatformTag, "osVersion", manifest.OsVersion);
        RequireKnown(manifest.PlatformTag, "osArchitecture", manifest.OsArchitecture);
        RequireKnown(manifest.PlatformTag, "compilationMode", manifest.CompilationMode);
    }

    private static void RequireKnown(string platformTag, string field, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Runtime manifest {platformTag} requires known exact-runtime identity field {field}; observed '{value}'.");
        }
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(static character => Uri.IsHexDigit(character));
}
