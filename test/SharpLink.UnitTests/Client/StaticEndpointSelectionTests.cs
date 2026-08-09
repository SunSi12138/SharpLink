using System.Collections.Concurrent;
using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class StaticEndpointSelectionTests
{
    [Test]
    public void PowerOfTwoComparisonShouldUseExactCrossMultiplication()
    {
        Ensure(
            StaticEndpointSelection.CompareNormalizedLoad(3, 2, 4, 3) > 0,
            "3/2 should be greater than 4/3");
        Ensure(
            StaticEndpointSelection.CompareNormalizedLoad(4, 2, 6, 3) == 0,
            "equal normalized loads");
        Ensure(
            StaticEndpointSelection.CompareNormalizedLoad(
                int.MaxValue,
                int.MaxValue,
                int.MaxValue - 1,
                int.MaxValue) > 0,
            "large operands must not overflow the 64-bit cross product");
    }

    [Test]
    public void RandomSelectionShouldOnlyReturnNonExcludedIndexes()
    {
        const ulong excluded = (1UL << 1) | (1UL << 3);
        Ensure(StaticEndpointSelection.SelectRandomIndex(5, excluded, 3, 0) == 0, "first available index");
        Ensure(StaticEndpointSelection.SelectRandomIndex(5, excluded, 3, 1) == 2, "middle available index");
        Ensure(StaticEndpointSelection.SelectRandomIndex(5, excluded, 3, 2) == 4, "last available index");
        Ensure(StaticEndpointSelection.SelectRandomIndex(5, excluded, 3, 3) == -1, "out-of-range random target");
    }

    [Test]
    [Arguments(1, 0)]
    [Arguments(2, 0)]
    [Arguments(4, 0)]
    [Arguments(4, 1)]
    [Arguments(4, 3)]
    [Arguments(16, 4)]
    [Arguments(16, 12)]
    [Arguments(64, 16)]
    [Arguments(64, 48)]
    public void RandomSelectionShouldMapEveryAvailableTargetAtScale(
        int length,
        int excludedPrefixLength)
    {
        var excluded = CreatePrefixMask(excludedPrefixLength);
        var availableCount = length - excludedPrefixLength;

        for (var target = 0; target < availableCount; target++)
        {
            Ensure(
                StaticEndpointSelection.SelectRandomIndex(
                    length,
                    excluded,
                    availableCount,
                    target) == excludedPrefixLength + target,
                $"length {length}, excluded {excludedPrefixLength}, target {target}");
        }
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 1)]
    [Arguments(4, 4)]
    [Arguments(64, 64)]
    public void SelectionShouldRejectZeroCandidates(int length, int excludedPrefixLength)
    {
        var excluded = CreatePrefixMask(excludedPrefixLength);
        var cursor = -1;

        Ensure(
            StaticEndpointSelection.SelectRandomIndex(length, excluded, 0, 0) == -1,
            "random zero candidates");
        Ensure(
            StaticEndpointSelection.SelectRoundRobinIndex(ref cursor, length, excluded) == -1,
            "round-robin zero candidates");
    }

    [Test]
    [Arguments(1, 0)]
    [Arguments(4, 0)]
    [Arguments(4, 1)]
    [Arguments(4, 3)]
    [Arguments(16, 4)]
    [Arguments(16, 12)]
    [Arguments(64, 16)]
    [Arguments(64, 48)]
    public void RoundRobinSelectionShouldNotStarveAvailableIndexesWithExclusions(
        int length,
        int excludedPrefixLength)
    {
        var excluded = CreatePrefixMask(excludedPrefixLength);
        var availableCount = length - excludedPrefixLength;
        var counts = new int[length];
        var cursor = -1;

        for (var iteration = 0; iteration < availableCount * 4; iteration++)
        {
            var selected = StaticEndpointSelection.SelectRoundRobinIndex(
                ref cursor,
                length,
                excluded);
            Ensure(selected >= excludedPrefixLength && selected < length,
                $"round-robin selected excluded index {selected}");
            counts[selected]++;
        }

        for (var index = 0; index < excludedPrefixLength; index++)
            Ensure(counts[index] == 0, $"excluded index {index} was selected");
        for (var index = excludedPrefixLength; index < length; index++)
            Ensure(counts[index] > 0, $"available index {index} was starved");
        Ensure(counts.Sum() == availableCount * 4, "round-robin selection count");
    }

    [Test]
    public void RoundRobinCursorShouldRemainBalancedUnderConcurrency()
    {
        var cursor = -1;
        var selections = new ConcurrentBag<int>();
        Parallel.For(0, 400, _ => selections.Add(StaticEndpointSelection.SelectRoundRobinIndex(ref cursor, 4, 0)));

        for (var index = 0; index < 4; index++)
            Ensure(selections.Count(selection => selection == index) == 100, "round-robin concurrent balance");
    }

    private static ulong CreatePrefixMask(int excludedPrefixLength)
        => excludedPrefixLength == 64
            ? ulong.MaxValue
            : (1UL << excludedPrefixLength) - 1;

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
