namespace SharpLink.Client;

internal static class EndpointSelectionKernel
{
    // Shared by ordinary topology selection and contract-bound candidates. Cursors stay
    // topology-owned; a physical attempt captures policy once before calling this kernel.
    internal static int SelectEndpoint(
        SharpLinkEndpointCandidate[] candidates,
        EndpointSelectionPolicyGeneration policy,
        ulong excluded,
        ref int roundRobinCursor,
        ref int leastPendingCursor)
    {
        var available = 0;
        for (var index = 0; index < candidates.Length; index++)
            if ((excluded & (1UL << index)) == 0) available++;
        if (available == 0) return -1;
        if (policy.Selector is { } selector)
            return selector.Select(new SharpLinkEndpointSelectionContext(candidates, excluded));
        if (policy.Strategy == SharpLinkLoadBalancingStrategy.RoundRobin)
            return SelectRoundRobinIndex(ref roundRobinCursor, candidates.Length, excluded);
        if (policy.Strategy == SharpLinkLoadBalancingStrategy.LeastPending)
        {
            var start = unchecked((uint)Interlocked.Increment(ref leastPendingCursor));
            var selected = -1;
            for (var offset = 0; offset < candidates.Length; offset++)
            {
                var index = (int)((start + (uint)offset) % (uint)candidates.Length);
                if ((excluded & (1UL << index)) == 0 &&
                    (selected < 0 || candidates[index].ActiveCallCount < candidates[selected].ActiveCallCount))
                    selected = index;
            }
            return selected;
        }
        var first = SelectRandomIndex(candidates.Length, excluded, available, Random.Shared.Next(available));
        if (available == 1 || policy.Strategy == SharpLinkLoadBalancingStrategy.Random) return first;
        var second = SelectRandomIndex(candidates.Length, excluded | (1UL << first), available - 1,
            Random.Shared.Next(available - 1));
        // Readiness can disappear after publication. Ranking remains total; reservation
        // still rejects a stale connection and never returns it as callable.
        return CompareNormalizedLoad(candidates[first].ActiveCallCount,
            Math.Max(1, candidates[first].ReadyConnectionCount), candidates[second].ActiveCallCount,
            Math.Max(1, candidates[second].ReadyConnectionCount)) <= 0 ? first : second;
    }

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
        if (excluded == 0)
            return target < length ? target : -1;
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
            return connections[0].TryReserveCallAdmission(out var admitted) ? admitted : null;

        var first = Random.Shared.Next(connections.Length);
        var second = Random.Shared.Next(connections.Length - 1);
        if (second >= first)
            second++;
        var selected = SelectLeastLoaded(connections, first, second);
        if (selected.TryReserveCallAdmission(out var admittedSelected))
            return admittedSelected;

        for (var index = 0; index < connections.Length; index++)
        {
            var candidate = connections[index];
            if (!ReferenceEquals(candidate, selected) &&
                candidate.TryReserveCallAdmission(out var admittedCandidate))
            {
                return admittedCandidate;
            }
        }
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
