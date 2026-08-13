using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    ValueTask ISharpLinkMultiClusterLifecycleControl.AddClusterAsync(
        SharpLinkClusterKey cluster,
        SharpClientBuilder builder,
        bool allowDynamicContracts,
        CancellationToken cancellationToken,
        IGeneratedManifestSource manifestSource,
        IGeneratedClusterRouteSource routeSource)
        => AddClusterCoreAsync(
            cluster,
            builder,
            allowDynamicContracts,
            cancellationToken,
            manifestSource,
            routeSource);

    private async ValueTask AddClusterCoreAsync(
        SharpLinkClusterKey cluster,
        SharpClientBuilder builder,
        bool allowDynamicContracts,
        CancellationToken cancellationToken,
        IGeneratedManifestSource manifestSource,
        IGeneratedClusterRouteSource routeSource)
    {
        ValidateClusterKey(cluster);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(manifestSource);
        ArgumentNullException.ThrowIfNull(routeSource);
        var started = _timeProvider.GetTimestamp();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SharpLinkPreparedCluster? candidate = null;
        var published = false;
        var publishedBudget = 0;
        var failureStage = "state_validation";
        try
        {
            LogMutationStage(_logger, "add", cluster.Value, "started", "pending", 0, 0);
            builder.UseLoggerFactoryIfUnset(_loggerFactory);
            lock (_gate)
            {
                var snapshot = BeginSlotMutationLocked();
                if (snapshot.Clusters.ContainsKey(cluster))
                    throw new InvalidOperationException($"Cluster '{cluster}' is already configured.");
                if (snapshot.Clusters.Count >= _options.MaxClusters)
                    throw new InvalidOperationException($"Configured cluster count would exceed MaxClusters ({_options.MaxClusters}).");
            }

            failureStage = "candidate_preparation";
            candidate = SharpLinkMultiClusterClientBuilder.PrepareRuntimeCluster(
                cluster,
                builder,
                allowDynamicContracts,
                manifestSource,
                routeSource);
            failureStage = "budget_preflight";
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = GetPublishableSnapshotLocked();
                if (snapshot.Clusters.ContainsKey(cluster))
                    throw new InvalidOperationException($"Cluster '{cluster}' was added by another operation.");
                ValidateSteadyBudget(snapshot.ConfiguredConnectionBudget, candidate.Slot.ConfiguredConnectionBudget);
                ValidateTransitionBudget(snapshot.ConfiguredConnectionBudget, candidate.Slot.ConfiguredConnectionBudget);
                _ = MergeRoutes(snapshot.Routes, candidate.StaticRoutes);
            }
            failureStage = "candidate_connect";
            var candidateConnected = await ConnectCandidateWhenRequiredAsync(
                candidate.Slot, cancellationToken).ConfigureAwait(false);
            LogMutationStage(_logger, "add", cluster.Value,
                candidateConnected ? "candidate_connected" : "candidate_prepared", "success",
                candidate.Slot.ConfiguredConnectionBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);

            failureStage = "snapshot_validation";
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = GetPublishableSnapshotLocked();
                if (snapshot.Clusters.ContainsKey(cluster))
                    throw new InvalidOperationException($"Cluster '{cluster}' was added by another operation.");
                ValidateSteadyBudget(snapshot.ConfiguredConnectionBudget, candidate.Slot.ConfiguredConnectionBudget);
                ValidateTransitionBudget(snapshot.ConfiguredConnectionBudget, candidate.Slot.ConfiguredConnectionBudget);

                var nextClusters = snapshot.Clusters.ToDictionary(static pair => pair.Key, static pair => pair.Value);
                nextClusters.Add(cluster, candidate.Slot);
                var nextRoutes = MergeRoutes(snapshot.Routes, candidate.StaticRoutes);
                var nextBudget = checked(snapshot.ConfiguredConnectionBudget + candidate.Slot.ConfiguredConnectionBudget);
                Volatile.Write(ref _snapshot, new MultiClusterSnapshot(
                    nextClusters.ToFrozenDictionary(),
                    nextRoutes,
                    nextBudget));
                _slotMutationInProgress = false;
                published = true;
                publishedBudget = nextBudget;
            }

            LogMutationStage(_logger, "add", cluster.Value, "snapshot_published", "success", publishedBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            RecordMutation("add", "success", _timeProvider.GetElapsedTime(started));
        }
        catch (Exception exception)
        {
            LogMutationStage(_logger, "add", cluster.Value, "rollback", "failed", 0,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds, failureStage);
            RecordMutation("add", "failed", _timeProvider.GetElapsedTime(started));
            if (candidate is not null && !published)
                await RethrowAfterCandidateCleanupAsync(exception, candidate.Slot.Client).ConfigureAwait(false);
            throw;
        }
        finally
        {
            EndSlotMutation();
            _mutationGate.Release();
        }
    }

    async ValueTask ISharpLinkMultiClusterLifecycleControl.ReplaceClusterAsync(
        SharpLinkClusterKey cluster,
        SharpClientBuilder builder,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken)
    {
        ValidateClusterKey(cluster);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        var started = _timeProvider.GetTimestamp();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SharpLinkPreparedCluster? candidate = null;
        SharpLinkClusterSlot? existingSlot = null;
        DynamicAssemblyRegistration[] registrations = [];
        var published = false;
        var publishedBudget = 0;
        var failureStage = "state_validation";
        try
        {
            LogMutationStage(_logger, "replace", cluster.Value, "started", "pending", 0, 0);
            builder.UseLoggerFactoryIfUnset(_loggerFactory);
            lock (_gate)
            {
                var snapshot = BeginSlotMutationLocked();
                if (!snapshot.Clusters.TryGetValue(cluster, out existingSlot))
                    throw new ArgumentException($"Cluster '{cluster}' is not configured.", nameof(cluster));
                registrations = _dynamicRegistrations
                    .Where(registration => ReferenceEquals(registration.Slot, existingSlot))
                    .ToArray();
            }

            failureStage = "candidate_preparation";
            candidate = SharpLinkMultiClusterClientBuilder.PrepareReplacementCluster(existingSlot!, builder);
            failureStage = "budget_preflight";
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = GetPublishableSnapshotLocked();
                ValidateReplacementBudgetLocked(snapshot, cluster, existingSlot!, candidate.Slot);
            }
            failureStage = "assembly_migration";
            foreach (var registration in registrations)
            {
                var result = candidate.Slot.Client.RegisterAssembly(registration.Assembly);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Dynamic assembly migration for cluster '{cluster}' failed: {result.Error?.Message ?? "unknown registration error"}");
                }
            }

            failureStage = "candidate_connect";
            var candidateConnected = await ConnectCandidateWhenRequiredAsync(
                candidate.Slot, cancellationToken).ConfigureAwait(false);
            LogMutationStage(_logger, "replace", cluster.Value,
                candidateConnected ? "candidate_connected" : "candidate_prepared", "success",
                candidate.Slot.ConfiguredConnectionBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);

            failureStage = "snapshot_validation";
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = GetPublishableSnapshotLocked();
                var nextBudget = ValidateReplacementBudgetLocked(
                    snapshot, cluster, existingSlot!, candidate.Slot);

                var nextClusters = snapshot.Clusters.ToDictionary(static pair => pair.Key, static pair => pair.Value);
                nextClusters[cluster] = candidate.Slot;
                var nextRoutes = snapshot.Routes.ToDictionary(static pair => pair.Key, pair =>
                    ReferenceEquals(pair.Value.Slot, existingSlot)
                        ? pair.Value with { Slot = candidate.Slot }
                        : pair.Value);
                for (var index = 0; index < _dynamicRegistrations.Count; index++)
                {
                    var registration = _dynamicRegistrations[index];
                    if (ReferenceEquals(registration.Slot, existingSlot))
                    {
                        _dynamicRegistrations[index] = registration with { Slot = candidate.Slot };
                    }
                }

                _transitionConnectionBudget = checked(
                    _transitionConnectionBudget + existingSlot.ConfiguredConnectionBudget);
                Volatile.Write(ref _snapshot, new MultiClusterSnapshot(
                    nextClusters.ToFrozenDictionary(),
                    nextRoutes.ToFrozenDictionary(),
                    nextBudget));
                _slotMutationInProgress = false;
                published = true;
                publishedBudget = nextBudget;
            }

            LogMutationStage(_logger, "replace", cluster.Value, "snapshot_published", "success", publishedBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            var cleanup = TrackRetiredSlotCleanup(
                existingSlot!,
                existingSlot!.ConfiguredConnectionBudget,
                "replace",
                cluster,
                gracefulTimeout);
            failureStage = "retired_cleanup_wait";
            var released = await WaitForRetiredCleanupAsync(
                cleanup,
                gracefulTimeout,
                cancellationToken,
                existingSlot!.Client).ConfigureAwait(false);
            if (!released)
            {
                LogMutationStage(_logger, "replace", cluster.Value, "forced_stop", "cleanup_pending",
                    Volatile.Read(ref _snapshot).ConfiguredConnectionBudget,
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            }
            RecordMutation("replace", released ? "success" : "forced_stop", _timeProvider.GetElapsedTime(started));
        }
        catch (Exception exception)
        {
            LogMutationStage(_logger, "replace", cluster.Value,
                published ? "cleanup_wait_failed" : "rollback", "failed",
                Volatile.Read(ref _snapshot).ConfiguredConnectionBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                failureStage);
            RecordMutation("replace", published ? "published_wait_failed" : "failed", _timeProvider.GetElapsedTime(started));
            if (candidate is not null && !published)
                await RethrowAfterCandidateCleanupAsync(exception, candidate.Slot.Client).ConfigureAwait(false);
            throw;
        }
        finally
        {
            EndSlotMutation();
            _mutationGate.Release();
        }
    }

    async ValueTask<SharpLinkClusterRemovalResult> ISharpLinkMultiClusterLifecycleControl.RemoveClusterAsync(
        SharpLinkClusterKey cluster,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken)
    {
        ValidateClusterKey(cluster);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        var started = _timeProvider.GetTimestamp();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SharpLinkClusterSlot? existingSlot = null;
        var published = false;
        var publishedBudget = 0;
        var failureStage = "snapshot_validation";
        try
        {
            LogMutationStage(_logger, "remove", cluster.Value, "started", "pending",
                Volatile.Read(ref _snapshot).ConfiguredConnectionBudget, 0);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = BeginSlotMutationLocked();
                if (!snapshot.Clusters.TryGetValue(cluster, out existingSlot))
                    throw new ArgumentException($"Cluster '{cluster}' is not configured.", nameof(cluster));

                var nextClusters = snapshot.Clusters
                    .Where(pair => pair.Key != cluster)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                    .ToFrozenDictionary();
                var nextRoutes = snapshot.Routes
                    .Where(pair => !ReferenceEquals(pair.Value.Slot, existingSlot))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                    .ToFrozenDictionary();
                _dynamicRegistrations.RemoveAll(registration => ReferenceEquals(registration.Slot, existingSlot));
                var nextBudget = checked(snapshot.ConfiguredConnectionBudget - existingSlot.ConfiguredConnectionBudget);
                _transitionConnectionBudget = checked(
                    _transitionConnectionBudget + existingSlot.ConfiguredConnectionBudget);
                Volatile.Write(ref _snapshot, new MultiClusterSnapshot(nextClusters, nextRoutes, nextBudget));
                _slotMutationInProgress = false;
                published = true;
                publishedBudget = nextBudget;
            }

            LogMutationStage(_logger, "remove", cluster.Value, "snapshot_published", "success", publishedBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            var cleanup = TrackRetiredSlotCleanup(
                existingSlot!,
                existingSlot!.ConfiguredConnectionBudget,
                "remove",
                cluster,
                gracefulTimeout);
            failureStage = "retired_cleanup_wait";
            var released = await WaitForRetiredCleanupAsync(
                cleanup,
                gracefulTimeout,
                cancellationToken,
                existingSlot!.Client).ConfigureAwait(false);
            if (!released)
            {
                LogMutationStage(_logger, "remove", cluster.Value, "forced_stop", "cleanup_pending",
                    Volatile.Read(ref _snapshot).ConfiguredConnectionBudget,
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            }
            RecordMutation("remove", released ? "success" : "forced_stop", _timeProvider.GetElapsedTime(started));
            return new SharpLinkClusterRemovalResult
            {
                Succeeded = true,
                ReferencesReleased = released,
                ForcedStop = !released
            };
        }
        catch
        {
            LogMutationStage(_logger, "remove", cluster.Value,
                published ? "cleanup_wait_failed" : "rollback", "failed",
                Volatile.Read(ref _snapshot).ConfiguredConnectionBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                failureStage);
            RecordMutation("remove", published ? "published_wait_failed" : "failed", _timeProvider.GetElapsedTime(started));
            throw;
        }
        finally
        {
            EndSlotMutation();
            _mutationGate.Release();
        }
    }

    private MultiClusterSnapshot BeginSlotMutationLocked()
    {
        var state = (SharpLinkMultiClusterState)_state;
        if (state == SharpLinkMultiClusterState.Connecting)
            throw new InvalidOperationException("Cluster slot lifecycle operations are unavailable while the coordinator is connecting.");
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            throw new InvalidOperationException($"Multi-cluster client state '{state}' does not accept cluster slot lifecycle operations.");
        if (_slotMutationInProgress || _activeAssemblyReplacements != 0 ||
            _unregisterOperations.Count != 0 || _drainingRegistrations.Count != 0)
        {
            throw new InvalidOperationException("A cluster or dynamic assembly lifecycle operation is already in progress.");
        }

        _slotMutationInProgress = true;
        return Volatile.Read(ref _snapshot);
    }

    private MultiClusterSnapshot GetPublishableSnapshotLocked()
    {
        var state = (SharpLinkMultiClusterState)_state;
        if (state is not SharpLinkMultiClusterState.Created and
            not SharpLinkMultiClusterState.Ready and
            not SharpLinkMultiClusterState.Degraded)
        {
            throw new InvalidOperationException(
                $"Multi-cluster client state '{state}' changed before the cluster slot could be published.");
        }
        return Volatile.Read(ref _snapshot);
    }

    private async Task<bool> ConnectCandidateWhenRequiredAsync(
        SharpLinkClusterSlot candidate,
        CancellationToken cancellationToken)
    {
        SharpLinkMultiClusterState state;
        lock (_gate)
            state = (SharpLinkMultiClusterState)_state;
        if (state == SharpLinkMultiClusterState.Created)
            return false;
        if (state is not SharpLinkMultiClusterState.Ready and not SharpLinkMultiClusterState.Degraded)
            throw new InvalidOperationException($"Multi-cluster client state '{state}' cannot publish a cluster candidate.");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        await candidate.Client.ConnectAsync(linkedCancellation.Token).ConfigureAwait(false);
        return true;
    }

    private void ValidateSteadyBudget(int currentBudget, int addedBudget)
    {
        var nextBudget = checked(currentBudget + addedBudget);
        if (nextBudget > _options.MaxTotalConfiguredConnections)
        {
            throw new InvalidOperationException(
                $"Configured child connection budget ({nextBudget}) exceeds MaxTotalConfiguredConnections ({_options.MaxTotalConfiguredConnections}).");
        }
    }

    private int ValidateReplacementBudgetLocked(
        MultiClusterSnapshot snapshot,
        SharpLinkClusterKey cluster,
        SharpLinkClusterSlot existingSlot,
        SharpLinkClusterSlot candidateSlot)
    {
        if (!snapshot.Clusters.TryGetValue(cluster, out var currentSlot) ||
            !ReferenceEquals(currentSlot, existingSlot))
        {
            throw new InvalidOperationException($"Cluster '{cluster}' changed while its replacement was prepared.");
        }

        var nextBudget = checked(snapshot.ConfiguredConnectionBudget - existingSlot.ConfiguredConnectionBudget +
            candidateSlot.ConfiguredConnectionBudget);
        if (nextBudget > _options.MaxTotalConfiguredConnections)
        {
            throw new InvalidOperationException(
                $"Replacement child connection budget ({nextBudget}) exceeds MaxTotalConfiguredConnections ({_options.MaxTotalConfiguredConnections}).");
        }
        ValidateTransitionBudget(snapshot.ConfiguredConnectionBudget, candidateSlot.ConfiguredConnectionBudget);
        return nextBudget;
    }

    private void ValidateTransitionBudget(int currentBudget, int candidateBudget)
    {
        var transitionBudget = checked(currentBudget + _transitionConnectionBudget + candidateBudget);
        var transitionLimit = checked(_options.MaxTotalConfiguredConnections * 2);
        if (transitionBudget > transitionLimit)
        {
            throw new InvalidOperationException(
                $"Transition child connection budget ({transitionBudget}) exceeds the bounded transition limit ({transitionLimit}).");
        }
    }

    private static FrozenDictionary<Type, SharpLinkClusterRouteRegistration> MergeRoutes(
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration> currentRoutes,
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration> incomingRoutes)
    {
        var nextRoutes = currentRoutes.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var contractIds = nextRoutes.Values.Select(static route => route.ContractId).ToHashSet();
        foreach (var pair in incomingRoutes)
        {
            if (nextRoutes.ContainsKey(pair.Key) || !contractIds.Add(pair.Value.ContractId))
            {
                throw new InvalidOperationException(
                    $"Contract '{pair.Key.FullName}' ({pair.Value.ContractId}) is already routed to another assembly or cluster.");
            }
            nextRoutes.Add(pair.Key, pair.Value);
        }
        return nextRoutes.ToFrozenDictionary();
    }

    private Task TrackRetiredSlotCleanup(
        SharpLinkClusterSlot retiredSlot,
        int connectionBudget,
        string operation,
        SharpLinkClusterKey cluster,
        TimeSpan gracefulTimeout)
    {
        var cleanup = CompleteRetiredSlotCleanupAsync(
            retiredSlot,
            connectionBudget,
            operation,
            cluster,
            gracefulTimeout);
        TrackFrameworkTask(cleanup, $"MultiClusterRetiredSlot{operation}");
        return cleanup;
    }

    private async Task CompleteRetiredSlotCleanupAsync(
        SharpLinkClusterSlot retiredSlot,
        int connectionBudget,
        string operation,
        SharpLinkClusterKey cluster,
        TimeSpan gracefulTimeout)
    {
        try
        {
            LogMutationStage(_logger, operation, cluster.Value, "draining", "pending",
                Volatile.Read(ref _snapshot).ConfiguredConnectionBudget, 0);
            await WaitForActiveCallsToDrainAsync(retiredSlot.Client, gracefulTimeout).ConfigureAwait(false);
            await retiredSlot.Client.StopAsync().ConfigureAwait(false);
            LogMutationStage(_logger, operation, cluster.Value, "completed", "success",
                Volatile.Read(ref _snapshot).ConfiguredConnectionBudget, 0);
        }
        finally
        {
            lock (_gate)
                _transitionConnectionBudget = Math.Max(0, _transitionConnectionBudget - connectionBudget);
        }
    }

    private async Task WaitForActiveCallsToDrainAsync(
        ISharpLinkClient client,
        TimeSpan gracefulTimeout)
    {
        if (gracefulTimeout == TimeSpan.Zero || client is not ISharpLinkClientDrainInspector inspector)
            return;

        var timeProvider = GetTimeProvider(client);
        var deadline = SharpLinkTime.AddDuration(
            timeProvider.GetTimestamp(),
            gracefulTimeout,
            timeProvider.TimestampFrequency);
        while (inspector.ActiveCallCount != 0 || inspector.ActiveStreamCount != 0)
        {
            var remaining = SharpLinkTime.GetRemaining(
                deadline,
                timeProvider.GetTimestamp(),
                timeProvider.TimestampFrequency);
            if (remaining <= TimeSpan.Zero)
                return;
            try
            {
                await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(25)
                        ? remaining
                        : TimeSpan.FromMilliseconds(25),
                    timeProvider,
                    _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> WaitForRetiredCleanupAsync(
        Task cleanup,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken,
        ISharpLinkClient? client = null)
        => await SharpLinkTimer.WaitAsync(
            cleanup,
            gracefulTimeout,
            client is null ? _timeProvider : GetTimeProvider(client),
            cancellationToken).ConfigureAwait(false);

    private void EndSlotMutation()
    {
        lock (_gate)
            _slotMutationInProgress = false;
    }

    private static async Task RethrowAfterCandidateCleanupAsync(
        Exception exception,
        ISharpLinkClient candidate)
    {
        try
        {
            await candidate.StopAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            throw new AggregateException(exception, cleanupException);
        }

        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private static void ValidateClusterKey(SharpLinkClusterKey cluster)
    {
        if (!SharpLinkClusterKey.IsValid(cluster.Value))
            throw new ArgumentException("A valid non-default SharpLinkClusterKey is required.", nameof(cluster));
    }
}
