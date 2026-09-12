using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpLink.CodecCompatibility;

internal static class LayoutEvidenceValidation
{
    internal static void ValidateCompleteMatrix(IReadOnlyList<LayoutEvidenceReport> reports)
    {
        if (reports.Count == 0)
            throw new InvalidOperationException("No UnsafeBlit layout evidence reports were supplied.");

        var consumerReports = reports
            .GroupBy(static report => report.Consumer.PlatformTag, StringComparer.Ordinal)
            .ToArray();
        var duplicateConsumers = consumerReports
            .Where(static group => group.Count() != 1)
            .Select(static group => group.Key)
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        if (duplicateConsumers.Length != 0)
        {
            throw new InvalidOperationException(
                $"Layout evidence contains duplicate consumer reports: [{string.Join(", ", duplicateConsumers)}].");
        }

        var allResults = reports.SelectMany(static report => report.Results).ToArray();
        var duplicateResults = allResults
            .GroupBy(
                static item => (item.Profile, item.Fixture, item.Producer, item.Consumer),
                LayoutEvidenceResultKeyComparer.Instance)
            .Where(static group => group.Count() != 1)
            .Select(static group => $"{group.Key.Profile}/{group.Key.Fixture}/{group.Key.Producer}->{group.Key.Consumer}")
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        if (duplicateResults.Length != 0)
        {
            throw new InvalidOperationException(
                $"Layout evidence contains duplicate result edges: [{string.Join(", ", duplicateResults)}].");
        }

        foreach (var report in reports)
        {
            var mismatchedConsumers = report.Results
                .Where(item => !string.Equals(item.Consumer, report.Consumer.PlatformTag, StringComparison.Ordinal))
                .Select(static item => item.Consumer)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToArray();
            if (mismatchedConsumers.Length != 0)
            {
                throw new InvalidOperationException(
                    $"Layout evidence report {report.Consumer.PlatformTag} contains results for other consumers: [{string.Join(", ", mismatchedConsumers)}].");
            }
        }

        var consumers = consumerReports
            .Select(static group => group.Key)
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();

        foreach (var profile in new[] { LayoutEvidenceProfiles.FixedWidth, LayoutEvidenceProfiles.NativeWidth })
        {
            var profileResults = allResults
                .Where(item => string.Equals(item.Profile, profile, StringComparison.Ordinal))
                .ToArray();
            var producers = profileResults
                .Select(static item => item.Producer)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToArray();
            if (!producers.SequenceEqual(consumers, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Layout evidence profile {profile} is not a complete producer/consumer platform set: producers=[{string.Join(", ", producers)}], consumers=[{string.Join(", ", consumers)}].");
            }

            var expectedFixtures = LayoutEvidenceFixtureRegistry.ForProfile(profile)
                .Select(static fixture => fixture.Id)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToArray();

            foreach (var report in reports)
            {
                var consumer = report.Consumer.PlatformTag;
                var consumerProfileResults = report.Results
                    .Where(item => string.Equals(item.Profile, profile, StringComparison.Ordinal))
                    .ToArray();
                var consumerProducers = consumerProfileResults
                    .Select(static item => item.Producer)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static item => item, StringComparer.Ordinal)
                    .ToArray();
                if (!consumerProducers.SequenceEqual(consumers, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Layout evidence consumer/profile {consumer}/{profile} is missing producer edges: expected=[{string.Join(", ", consumers)}], observed=[{string.Join(", ", consumerProducers)}].");
                }

                foreach (var producer in consumers)
                {
                    var observedFixtures = consumerProfileResults
                        .Where(item => string.Equals(item.Producer, producer, StringComparison.Ordinal))
                        .Select(static item => item.Fixture)
                        .OrderBy(static item => item, StringComparer.Ordinal)
                        .ToArray();
                    if (observedFixtures.SequenceEqual(expectedFixtures, StringComparer.Ordinal))
                        continue;

                    var missing = expectedFixtures.Except(observedFixtures, StringComparer.Ordinal).ToArray();
                    var extra = observedFixtures.Except(expectedFixtures, StringComparer.Ordinal).ToArray();
                    throw new InvalidOperationException(
                        $"Layout evidence edge {profile}/{producer}->{consumer} does not contain the complete fixture set: missing=[{string.Join(", ", missing)}], extra=[{string.Join(", ", extra)}].");
                }
            }
        }
    }

    internal static int ValidateRetainedPortableDomain(LayoutEvidenceSummary summary)
    {
        var retained = summary.Fixtures
            .Where(static item => !item.LegacyControl
                && !item.NativeWidth
                && string.Equals(item.WidthDomain, "fixed-width-primitive", StringComparison.Ordinal)
                && (string.Equals(item.LayoutKind, "Sequential", StringComparison.Ordinal)
                    || string.Equals(item.LayoutKind, "Explicit", StringComparison.Ordinal)))
            .ToArray();
        if (retained.Length == 0)
            throw new InvalidOperationException("UnsafeBlit layout evidence contains no retained fixed-width primitive Sequential/Explicit fixtures.");

        var unstable = retained
            .Where(static item => !item.AllCrossPlatformRawRepresentationStable)
            .Select(static item => $"{item.Fixture} ({item.RawRepresentationStableEdges}/{item.CrossPlatformEdges} stable edges)")
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        if (unstable.Length != 0)
        {
            throw new InvalidOperationException(
                "Retained UnsafeBlit portable-domain regression: fixed-width primitive Sequential/Explicit fixtures must preserve complete cross-platform raw representation stability. "
                + $"Unstable fixtures: [{string.Join(", ", unstable)}].");
        }

        return retained.Length;
    }

    private sealed class LayoutEvidenceResultKeyComparer : IEqualityComparer<(string Profile, string Fixture, string Producer, string Consumer)>
    {
        internal static LayoutEvidenceResultKeyComparer Instance { get; } = new();

        public bool Equals(
            (string Profile, string Fixture, string Producer, string Consumer) x,
            (string Profile, string Fixture, string Producer, string Consumer) y)
            => string.Equals(x.Profile, y.Profile, StringComparison.Ordinal)
               && string.Equals(x.Fixture, y.Fixture, StringComparison.Ordinal)
               && string.Equals(x.Producer, y.Producer, StringComparison.Ordinal)
               && string.Equals(x.Consumer, y.Consumer, StringComparison.Ordinal);

        public int GetHashCode((string Profile, string Fixture, string Producer, string Consumer) obj)
            => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Profile),
                StringComparer.Ordinal.GetHashCode(obj.Fixture),
                StringComparer.Ordinal.GetHashCode(obj.Producer),
                StringComparer.Ordinal.GetHashCode(obj.Consumer));
    }
}
