namespace SharpLink.Client;

internal static class StaticEndpointSelection
{
    public static int CompareNormalizedLoad(
        int firstActiveCalls,
        int firstReadyConnections,
        int secondActiveCalls,
        int secondReadyConnections)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstActiveCalls);
        ArgumentOutOfRangeException.ThrowIfNegative(secondActiveCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstReadyConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(secondReadyConnections);
        var first = (long)firstActiveCalls * secondReadyConnections;
        var second = (long)secondActiveCalls * firstReadyConnections;
        return first.CompareTo(second);
    }

    public static int SelectRandomIndex(int length, ulong excluded, int availableCount, int target)
    {
        if (availableCount <= 0 || target < 0 || target >= availableCount)
            return -1;
        for (var index = 0; index < length; index++)
        {
            if ((excluded & (1UL << index)) != 0)
                continue;
            if (target-- == 0)
                return index;
        }
        return -1;
    }

    public static int SelectRoundRobinIndex(ref int cursor, int length, ulong excluded)
    {
        var start = unchecked((uint)Interlocked.Increment(ref cursor));
        for (var offset = 0; offset < length; offset++)
        {
            var index = (int)((start + (uint)offset) % (uint)length);
            if ((excluded & (1UL << index)) == 0)
                return index;
        }
        return -1;
    }
}
