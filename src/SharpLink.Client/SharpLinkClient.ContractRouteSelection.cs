namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private static int SelectContractEndpointCore(ContractRouteView view,
        EndpointSelectionPolicyGeneration policy, ReadOnlySpan<int> capacities, ulong excluded,
        ref int roundRobinCursor, ref int leastPendingCursor)
    {
        var length = view.Buckets.Length;
        var available = 0;
        for (var index = 0; index < length; index++)
            if ((excluded & (1UL << index)) == 0) available++;
        if (available == 0) return -1;
        if (policy.Selector is { } selector)
            return selector.Select(new SharpLinkEndpointSelectionContext(view.Candidates, excluded));
        if (available == 1)
            return EndpointSelectionKernel.SelectRandomIndex(length, excluded, available, 0);
        switch (policy.Strategy)
        {
            case SharpLinkLoadBalancingStrategy.RoundRobin:
                return EndpointSelectionKernel.SelectRoundRobinIndex(ref roundRobinCursor, length, excluded);
            case SharpLinkLoadBalancingStrategy.Random:
                return EndpointSelectionKernel.SelectRandomIndex(length, excluded, available, Random.Shared.Next(available));
            case SharpLinkLoadBalancingStrategy.LeastPending:
                var start = unchecked((uint)Interlocked.Increment(ref leastPendingCursor));
                var selected = -1;
                var minimum = int.MaxValue;
                for (var offset = 0; offset < length; offset++)
                {
                    var index = (int)((start + (uint)offset) % (uint)length);
                    if ((excluded & (1UL << index)) != 0) continue;
                    var load = view.Buckets[index].Endpoint.ActiveCalls();
                    if (selected < 0 || load < minimum) { selected = index; minimum = load; }
                }
                return selected;
            default:
                var first = EndpointSelectionKernel.SelectRandomIndex(length, excluded, available, Random.Shared.Next(available));
                var second = EndpointSelectionKernel.SelectRandomIndex(length, excluded | (1UL << first),
                    available - 1, Random.Shared.Next(available - 1));
                // Positive samples belong to this selection step. A later cut is handled by exact reserve.
                return EndpointSelectionKernel.CompareNormalizedLoad(
                    view.Buckets[first].Endpoint.ActiveCalls(), capacities[first],
                    view.Buckets[second].Endpoint.ActiveCalls(), capacities[second]) <= 0 ? first : second;
        }
    }

    internal sealed partial class ContractCallSite
    {
        private ClientConnection ReserveClusterBoundConnection(EndpointRetrySelectionState? retry,
            AttemptOutcomeState? outcome, out BoundTarget binding)
        {
            if (Volatile.Read(ref _owner._contractRoutesStopped)) throw CreateConnectionClosedException("Client is stopping.");
            if (Volatile.Read(ref _contractRouteRevisionExhausted) ||
                Volatile.Read(ref _owner._contractRouteTopology).IsTerminal) throw RouteRevisionExhausted();
            var view = Volatile.Read(ref _contractRouteView);
            var topology = view.Topology;
            if (topology.IsTerminal) throw RouteRevisionExhausted();
            var selection = topology.Selection;
            if (selection is null || view.Buckets.Length == 0) throw NoBoundRoute();
            var policy = selection.CaptureSelectionPolicy();
            Span<int> capacities = stackalloc int[64];
            var eligible = SampleContractCapacity(view, capacities);
            if (eligible == 0) throw NoBoundRoute();
            var excluded = retry?.GetContractExcludedMask(topology.IndexMap, eligible) ?? 0UL;
            var mask = view.Buckets.Length == 64 ? ulong.MaxValue : (1UL << view.Buckets.Length) - 1;
            excluded |= mask & ~eligible;
            for (var attempt = 0; attempt < view.Buckets.Length; attempt++)
            {
                LocalControl.ThrowIfRetired();
                if (Volatile.Read(ref _owner._contractRoutesStopped)) throw CreateConnectionClosedException("Client is stopping.");
                eligible = SampleContractCapacity(view, capacities);
                excluded |= mask & ~eligible;
                if ((mask & ~excluded) == 0) break;
                int index;
                try { index = selection.SelectContractEndpoint(view, policy, capacities, excluded); }
                catch (Exception error) when (policy.HasCustomSelector)
                {
                    var failure = new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                        "The endpoint selector failed.", error);
                    try { _owner._logger?.LogError(error, "SharpLink endpoint selector failed."); }
                    catch (Exception diagnostics) { throw new AggregateException(failure, diagnostics); }
                    throw failure;
                }
                if ((uint)index >= (uint)view.Buckets.Length || (excluded & (1UL << index)) != 0)
                    throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                        "The endpoint selector returned an unavailable candidate index.");
                var bucket = view.Buckets[index];
                if (bucket.EligibleCount() == 0)
                {
                    excluded |= 1UL << index;
                    continue;
                }
                var candidate = view.Candidates[index];
                if (outcome is not null && !outcome.TryAcquire(candidate))
                {
                    retry?.Exclude(topology.IndexMap, index);
                    excluded |= 1UL << index;
                    continue;
                }
                retry?.Exclude(topology.IndexMap, index);
                var target = TryReserveContractBucket(bucket);
                if (target is not null)
                {
                    try
                    {
                        // Successful reserve owns this physical session; no redirect or Ready-membership recheck.
                        if (target.Connection.ActiveCallCount != 0) bucket.Endpoint.Selected(target.Connection);
                        binding = target;
                        return target.Connection;
                    }
                    catch (Exception primary)
                    {
                        try { target.Connection.ReleaseCallAdmissionReservation(); }
                        catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                        throw;
                    }
                }
                var reservationFailure = CreateConnectionClosedException(
                    "The selected contract endpoint has no reservable confirmed session.");
                outcome?.CompleteWithoutPending(PendingCallCompletionReason.ConnectionClosed, reservationFailure);
                // Existing dynamic failure-only generation retirement is essential: stale admission can
                // recreate lazy policy state after the factory was released between check and TryAcquire.
                try { bucket.Endpoint.RetireAdmission?.Invoke(candidate); }
                catch (Exception retirementFailure) { throw new AggregateException(reservationFailure, retirementFailure); }
                excluded |= 1UL << index;
            }
            binding = null!;
            throw NoBoundRoute();
        }

        private static ulong SampleContractCapacity(ContractRouteView view, Span<int> capacities)
        {
            ulong eligible = 0;
            for (var index = 0; index < view.Buckets.Length; index++)
            {
                var count = view.Buckets[index].EligibleCount();
                capacities[index] = count;
                if (count != 0) eligible |= 1UL << index;
            }
            return eligible;
        }

        private BoundTarget? TryReserveContractBucket(ContractRouteBucket bucket)
        {
            Span<int> live = stackalloc int[64];
            var count = 0;
            for (var index = 0; index < bucket.Targets.Length; index++)
                if (bucket.Targets[index].IsEligible) live[count++] = index;
            if (count == 0) return null;
            var selected = 0;
            if (count > 1)
            {
                var first = Random.Shared.Next(count);
                var second = Random.Shared.Next(count - 1);
                if (second >= first) second++;
                selected = bucket.Targets[live[first]].Connection.ActiveCallCount <=
                    bucket.Targets[live[second]].Connection.ActiveCallCount ? first : second;
            }
            for (var offset = 0; offset < count; offset++)
            {
                var target = bucket.Targets[live[(selected + offset) % count]];
                if (!target.IsEligible || LocalControl.IsRetired || !target.Connection.TryReserveExactCallAdmission())
                    continue;
                if (target.IsValidForReservedCall && !LocalControl.IsRetired) return target;
                target.Connection.ReleaseCallAdmissionReservation();
            }
            return null;
        }
    }
}
