namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    private bool TryBeginSlotMutationLocked(
        bool allowRunningConnectivityTransition,
        out MultiClusterSnapshot snapshot,
        out MutationRejection rejection)
    {
        snapshot = Volatile.Read(ref _snapshot);
        var state = (SharpLinkMultiClusterState)_state;
        var lifecycle = LifecycleState;
        if (state == SharpLinkMultiClusterState.Connecting &&
            !(allowRunningConnectivityTransition && lifecycle == SharpLinkClientLifecycleState.Running))
        {
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.Busy,
                "Cluster slot lifecycle operations are unavailable while the coordinator is connecting.");
            return false;
        }
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted ||
            lifecycle is SharpLinkClientLifecycleState.Draining or SharpLinkClientLifecycleState.Stopped or SharpLinkClientLifecycleState.Faulted)
        {
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.LifecycleClosed,
                $"Multi-cluster client lifecycle state '{lifecycle}' does not accept cluster slot lifecycle operations.");
            return false;
        }
        if (lifecycle == SharpLinkClientLifecycleState.Starting)
        {
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.Busy,
                "Cluster slot lifecycle operations are unavailable while the coordinator is starting.");
            return false;
        }
        if (_slotMutationInProgress || _activeAssemblyReplacements != 0 ||
            _unregisterOperations.Count != 0 || _drainingRegistrations.Count != 0)
        {
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.Busy,
                "A cluster or dynamic assembly lifecycle operation is already in progress.");
            return false;
        }
        if (state is not SharpLinkMultiClusterState.Created and
            not SharpLinkMultiClusterState.Connecting and
            not SharpLinkMultiClusterState.Ready and
            not SharpLinkMultiClusterState.Degraded)
        {
            throw new InvalidOperationException($"Unexpected multi-cluster mutation state '{state}'.");
        }

        _slotMutationInProgress = true;
        rejection = default;
        return true;
    }

    private bool TryGetPublishableSnapshotLocked(
        bool allowRunningConnectivityTransition,
        out MultiClusterSnapshot snapshot,
        out MutationRejection rejection)
    {
        snapshot = Volatile.Read(ref _snapshot);
        var state = (SharpLinkMultiClusterState)_state;
        var lifecycle = LifecycleState;
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted ||
            lifecycle is SharpLinkClientLifecycleState.Draining or SharpLinkClientLifecycleState.Stopped or SharpLinkClientLifecycleState.Faulted)
        {
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.LifecycleClosed,
                $"Multi-cluster client lifecycle state '{lifecycle}' changed before the cluster slot could be published.");
            return false;
        }
        if (lifecycle == SharpLinkClientLifecycleState.Starting)
        {
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.Busy,
                "The coordinator is still starting and cannot publish a cluster slot mutation.");
            return false;
        }
        if (state == SharpLinkMultiClusterState.Connecting)
        {
            if (allowRunningConnectivityTransition && lifecycle == SharpLinkClientLifecycleState.Running)
            {
                rejection = default;
                return true;
            }
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.Busy,
                "The coordinator is connecting and cannot publish this cluster slot mutation.");
            return false;
        }
        if (state is SharpLinkMultiClusterState.Created or SharpLinkMultiClusterState.Ready or SharpLinkMultiClusterState.Degraded)
        {
            rejection = default;
            return true;
        }
        throw new InvalidOperationException($"Unexpected multi-cluster publication state '{state}'.");
    }

    private async Task<CandidateActivationOutcome> StartAddCandidateWhenRequiredAsync(
        SharpLinkClusterSlot candidate,
        CancellationToken cancellationToken)
    {
        var lifecycle = LifecycleState;
        if (lifecycle == SharpLinkClientLifecycleState.Created)
            return new CandidateActivationOutcome(false, null);
        if (lifecycle == SharpLinkClientLifecycleState.Starting)
        {
            return new CandidateActivationOutcome(
                false,
                Reject(SharpLinkClusterMutationFailureCode.Busy,
                    "The coordinator is still starting and cannot publish an added cluster."));
        }
        if (lifecycle is SharpLinkClientLifecycleState.Draining or SharpLinkClientLifecycleState.Stopped or SharpLinkClientLifecycleState.Faulted)
        {
            return new CandidateActivationOutcome(
                false,
                Reject(SharpLinkClusterMutationFailureCode.LifecycleClosed,
                    $"Multi-cluster client lifecycle state '{lifecycle}' cannot publish an added cluster."));
        }
        if (lifecycle != SharpLinkClientLifecycleState.Running)
            throw new InvalidOperationException($"Unexpected lifecycle state '{lifecycle}' while starting an added cluster.");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            await candidate.Client.StartAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _shutdown.IsCancellationRequested)
        {
            return new CandidateActivationOutcome(
                false,
                Reject(SharpLinkClusterMutationFailureCode.LifecycleClosed,
                    "The coordinator began shutting down before the added cluster could be published."));
        }
        return new CandidateActivationOutcome(true, null);
    }

    private async Task<CandidateActivationOutcome> ConnectReplacementCandidateWhenRequiredAsync(
        SharpLinkClusterSlot candidate,
        CancellationToken cancellationToken)
    {
        SharpLinkMultiClusterState state;
        SharpLinkClientLifecycleState lifecycle;
        lock (_gate)
        {
            state = (SharpLinkMultiClusterState)_state;
            lifecycle = LifecycleState;
        }
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted ||
            lifecycle is SharpLinkClientLifecycleState.Draining or SharpLinkClientLifecycleState.Stopped or SharpLinkClientLifecycleState.Faulted)
        {
            return new CandidateActivationOutcome(
                false,
                Reject(SharpLinkClusterMutationFailureCode.LifecycleClosed,
                    $"Multi-cluster client lifecycle state '{lifecycle}' cannot publish a replacement candidate."));
        }
        if (state == SharpLinkMultiClusterState.Connecting || lifecycle == SharpLinkClientLifecycleState.Starting)
        {
            return new CandidateActivationOutcome(
                false,
                Reject(SharpLinkClusterMutationFailureCode.Busy,
                    "The coordinator is starting or connecting and cannot publish a replacement candidate."));
        }
        if (state == SharpLinkMultiClusterState.Created)
            return new CandidateActivationOutcome(false, null);
        if (state is not SharpLinkMultiClusterState.Ready and not SharpLinkMultiClusterState.Degraded)
            throw new InvalidOperationException($"Unexpected multi-cluster replacement state '{state}'.");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            if (lifecycle == SharpLinkClientLifecycleState.Running)
                await candidate.Client.StartAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _shutdown.IsCancellationRequested)
        {
            return new CandidateActivationOutcome(
                false,
                Reject(SharpLinkClusterMutationFailureCode.LifecycleClosed,
                    "The coordinator began shutting down before the replacement candidate could start."));
        }

        try
        {
            await candidate.Client.ConnectAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _shutdown.IsCancellationRequested)
        {
            return new CandidateActivationOutcome(
                false,
                Reject(SharpLinkClusterMutationFailureCode.LifecycleClosed,
                    "The coordinator began shutting down before the replacement candidate became available."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedReplacementAvailabilityFailure(exception))
        {
            return new CandidateActivationOutcome(
                false,
                Reject(
                    SharpLinkClusterMutationFailureCode.CandidateUnavailable,
                    $"Replacement candidate could not become available: {exception.Message}"));
        }
        return new CandidateActivationOutcome(true, null);
    }

    private static bool IsExpectedReplacementAvailabilityFailure(Exception exception)
        => exception is SharpLinkException sharpLinkException
            ? IsExpectedReplacementSharpLinkAvailabilityFailure(sharpLinkException.Code)
            : exception is System.IO.IOException
                or System.Net.Sockets.SocketException
                or System.Security.Authentication.AuthenticationException
                or TimeoutException
                or UnauthorizedAccessException;

    private static bool IsExpectedReplacementSharpLinkAvailabilityFailure(SharpLinkErrorCode code)
        => code is SharpLinkErrorCode.AuthenticationRejected
            or SharpLinkErrorCode.AuthenticationExpired
            or SharpLinkErrorCode.AuthorizationDenied
            or SharpLinkErrorCode.PermissionDenied
            or SharpLinkErrorCode.ConnectionClosed
            or SharpLinkErrorCode.HeartbeatTimeout
            or SharpLinkErrorCode.Unavailable;

    private bool TryValidateSteadyBudget(
        int currentBudget,
        int addedBudget,
        out MutationRejection rejection)
    {
        var nextBudget = checked(currentBudget + addedBudget);
        if (nextBudget > _options.MaxTotalConfiguredConnections)
        {
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.CapacityExceeded,
                $"Configured child connection budget ({nextBudget}) exceeds MaxTotalConfiguredConnections ({_options.MaxTotalConfiguredConnections}).");
            return false;
        }
        rejection = default;
        return true;
    }

    private bool TryValidateReplacementBudgetLocked(
        MultiClusterSnapshot snapshot,
        SharpLinkClusterKey cluster,
        SharpLinkClusterSlot existingSlot,
        SharpLinkClusterSlot candidateSlot,
        out int nextBudget,
        out MutationRejection rejection)
    {
        if (!snapshot.Clusters.TryGetValue(cluster, out var currentSlot) ||
            !ReferenceEquals(currentSlot, existingSlot))
        {
            throw new InvalidOperationException($"Cluster '{cluster}' changed while its replacement was prepared.");
        }

        nextBudget = checked(snapshot.ConfiguredConnectionBudget - existingSlot.ConfiguredConnectionBudget +
            candidateSlot.ConfiguredConnectionBudget);
        if (nextBudget > _options.MaxTotalConfiguredConnections)
        {
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.CapacityExceeded,
                $"Replacement child connection budget ({nextBudget}) exceeds MaxTotalConfiguredConnections ({_options.MaxTotalConfiguredConnections}).");
            return false;
        }
        if (!TryValidateTransitionBudget(
                snapshot.ConfiguredConnectionBudget,
                candidateSlot.ConfiguredConnectionBudget,
                out rejection))
        {
            return false;
        }
        rejection = default;
        return true;
    }

    private bool TryValidateTransitionBudget(
        int currentBudget,
        int candidateBudget,
        out MutationRejection rejection)
    {
        var transitionBudget = checked(currentBudget + _transitionConnectionBudget + candidateBudget);
        var transitionLimit = checked(_options.MaxTotalConfiguredConnections * 2);
        if (transitionBudget > transitionLimit)
        {
            rejection = Reject(
                SharpLinkClusterMutationFailureCode.CapacityExceeded,
                $"Transition child connection budget ({transitionBudget}) exceeds the bounded transition limit ({transitionLimit}).");
            return false;
        }
        rejection = default;
        return true;
    }

    private static bool TryMergeRoutes(
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration> currentRoutes,
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration> incomingRoutes,
        out FrozenDictionary<Type, SharpLinkClusterRouteRegistration> mergedRoutes,
        out MutationRejection rejection)
    {
        var nextRoutes = currentRoutes.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var contractIds = nextRoutes.Values.Select(static route => route.ContractId).ToHashSet();
        foreach (var pair in incomingRoutes)
        {
            if (nextRoutes.ContainsKey(pair.Key) || !contractIds.Add(pair.Value.ContractId))
            {
                mergedRoutes = currentRoutes;
                rejection = Reject(
                    SharpLinkClusterMutationFailureCode.RouteConflict,
                    $"Contract '{pair.Key.FullName}' ({pair.Value.ContractId}) is already routed to another assembly or cluster.");
                return false;
            }
            nextRoutes.Add(pair.Key, pair.Value);
        }
        mergedRoutes = nextRoutes.ToFrozenDictionary();
        rejection = default;
        return true;
    }

    private static MutationRejection Reject(
        SharpLinkClusterMutationFailureCode code,
        string message)
        => new(code, message);

    private readonly record struct MutationRejection(
        SharpLinkClusterMutationFailureCode Code,
        string Message);

    private readonly record struct CandidateActivationOutcome(
        bool Activated,
        MutationRejection? Rejection);

}
