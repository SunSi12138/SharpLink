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
    public void RoundRobinCursorShouldRemainBalancedUnderConcurrency()
    {
        var cursor = -1;
        var selections = new ConcurrentBag<int>();
        Parallel.For(0, 400, _ => selections.Add(StaticEndpointSelection.SelectRoundRobinIndex(ref cursor, 4, 0)));

        for (var index = 0; index < 4; index++)
            Ensure(selections.Count(selection => selection == index) == 100, "round-robin concurrent balance");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
