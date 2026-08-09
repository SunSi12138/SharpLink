namespace SharpLink.Client;

internal static class EndpointSelectionKernel
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

    public static ClientConnection? SelectConnection(ClientConnection[] connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (connections.Length == 0)
            return null;
        if (connections.Length == 1)
            return connections[0].CanAcceptCalls ? connections[0] : null;
        var first = Random.Shared.Next(connections.Length);
        var second = Random.Shared.Next(connections.Length - 1);
        if (second >= first)
            second++;
        var selected = SelectLeastLoaded(connections, first, second);
        if (selected.CanAcceptCalls)
            return selected;
        for (var index = 0; index < connections.Length; index++)
            if (connections[index].CanAcceptCalls)
                return connections[index];
        return null;
    }

    public static ClientConnection SelectLeastLoaded(
        ClientConnection[] connections,
        int first,
        int second)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(second);
        if ((uint)first >= (uint)connections.Length || (uint)second >= (uint)connections.Length)
            throw new ArgumentOutOfRangeException(nameof(first));
        var firstConnection = connections[first];
        var secondConnection = connections[second];
        return firstConnection.ActiveCallCount <= secondConnection.ActiveCallCount
            ? firstConnection
            : secondConnection;
    }
}
