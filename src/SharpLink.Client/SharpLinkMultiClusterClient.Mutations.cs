using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    ValueTask<SharpLinkClusterAddResult> ISharpLinkMultiClusterLifecycleControl.AddClusterAsync(
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

    private async ValueTask<SharpLinkClusterAddResult> AddClusterCoreAsync(
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
        ColdMetadataPreparation.ThrowIfCallbackReentry(_metadataDomain);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var metadata = CreateMetadataStorage();
        SharpLinkPreparedCluster? candidate = null;
        var published = false;
        var mutationBegan = false;
        var publishedBudget = 0;
        var failureStage = "state_validation";
        try
        {
            LogMutationStage(_logger, "add", cluster.Value, "started", "pending", 0, 0);
            builder.UseLoggerFactoryIfUnset(_loggerFactory);
            MutationRejection? rejection = null;
            lock (_gate)
            {
                if (!TryBeginSlotMutationLocked(
                        allowRunningConnectivityTransition: true,
                        out var snapshot,
                        out var beginRejection))
                {
                    rejection = beginRejection;
                }
                else
                {
                    mutationBegan = true;
                    if (snapshot.Clusters.ContainsKey(cluster))
                    {
                        rejection = Reject(
                            SharpLinkClusterMutationFailureCode.AlreadyExists,
                            $"Cluster '{cluster}' is already configured.");
                    }
                    else if (snapshot.Clusters.Count >= _options.MaxClusters)
                    {
                        rejection = Reject(
                            SharpLinkClusterMutationFailureCode.CapacityExceeded,
                            $"Configured cluster count would exceed MaxClusters ({_options.MaxClusters}).");
                    }
                }
            }
            if (rejection is { } initialRejection)
                return await RejectAddAsync(initialRejection, candidate, failureStage, started).ConfigureAwait(false);

            failureStage = "candidate_preparation";
            candidate = SharpLinkMultiClusterClientBuilder.PrepareRuntimeCluster(
                cluster,
                builder,
                allowDynamicContracts,
                manifestSource,
                routeSource,
                _metadataDomain);
            failureStage = "budget_preflight";
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetPublishableSnapshotLocked(
                        allowRunningConnectivityTransition: true,
                        out var snapshot,
                        out var publishRejection))
                {
                    rejection = publishRejection;
                }
                else if (snapshot.Clusters.ContainsKey(cluster))
                {
                    rejection = Reject(
                        SharpLinkClusterMutationFailureCode.AlreadyExists,
                        $"Cluster '{cluster}' was added by another operation.");
                }
                else if (!TryValidateSteadyBudget(
                             snapshot.ConfiguredConnectionBudget,
                             candidate.Slot.ConfiguredConnectionBudget,
                             out var steadyRejection))
                {
                    rejection = steadyRejection;
                }
                else if (!TryValidateTransitionBudget(
                             snapshot.ConfiguredConnectionBudget,
                             candidate.Slot.ConfiguredConnectionBudget,
                             out var transitionRejection))
                {
                    rejection = transitionRejection;
                }
                else if (!TryMergeRoutes(
                             snapshot.Routes,
                             candidate.StaticRoutes,
                             metadata,
                             out _,
                             out var routeRejection))
                {
                    rejection = routeRejection;
                }
            }
            if (rejection is { } preflightRejection)
                return await RejectAddAsync(preflightRejection, candidate, failureStage, started).ConfigureAwait(false);

            failureStage = "candidate_start";
            var activation = await StartAddCandidateWhenRequiredAsync(
                candidate.Slot, cancellationToken).ConfigureAwait(false);
            if (activation.Rejection is { } activationRejection)
                return await RejectAddAsync(activationRejection, candidate, failureStage, started).ConfigureAwait(false);
            LogMutationStage(_logger, "add", cluster.Value,
                activation.Activated ? "candidate_started" : "candidate_prepared", "success",
                candidate.Slot.ConfiguredConnectionBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);

            failureStage = "snapshot_validation";
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>? nextRoutes = null;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetPublishableSnapshotLocked(
                        allowRunningConnectivityTransition: true,
                        out var snapshot,
                        out var publishRejection))
                {
                    rejection = publishRejection;
                }
                else if (snapshot.Clusters.ContainsKey(cluster))
                {
                    rejection = Reject(
                        SharpLinkClusterMutationFailureCode.AlreadyExists,
                        $"Cluster '{cluster}' was added by another operation.");
                }
                else if (!TryValidateSteadyBudget(
                             snapshot.ConfiguredConnectionBudget,
                             candidate.Slot.ConfiguredConnectionBudget,
                             out var steadyRejection))
                {
                    rejection = steadyRejection;
                }
                else if (!TryValidateTransitionBudget(
                             snapshot.ConfiguredConnectionBudget,
                             candidate.Slot.ConfiguredConnectionBudget,
                             out var transitionRejection))
                {
                    rejection = transitionRejection;
                }
                else if (!TryMergeRoutes(
                             snapshot.Routes,
                             candidate.StaticRoutes,
                             metadata,
                             out nextRoutes,
                             out var routeRejection))
                {
                    rejection = routeRejection;
                }
                else
                {
                    metadata.ReserveCollection(checked(snapshot.Clusters.Count + 1));
                    metadata.ReserveCollection(checked(snapshot.Clusters.Count + 1));
                    var nextClusters = snapshot.Clusters.ToDictionary(static pair => pair.Key, static pair => pair.Value);
                    nextClusters.Add(cluster, candidate.Slot);
                    var nextBudget = checked(snapshot.ConfiguredConnectionBudget + candidate.Slot.ConfiguredConnectionBudget);
                    PublishSnapshotLocked(MultiClusterSnapshot.Create(
                        nextClusters.ToFrozenDictionary(), nextRoutes!, nextBudget, metadata, snapshot));
                    _slotMutationInProgress = false;
                    mutationBegan = false;
                    candidate.Publish();
                    published = true;
                    publishedBudget = nextBudget;
                }
            }
            if (rejection is { } publicationRejection)
                return await RejectAddAsync(publicationRejection, candidate, failureStage, started).ConfigureAwait(false);

            LogMutationStage(_logger, "add", cluster.Value, "snapshot_published", "success", publishedBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            RecordMutation("add", "success", _timeProvider.GetElapsedTime(started));
            return SharpLinkClusterAddResult.Success();
        }
        catch (Exception exception)
        {
            LogMutationStage(_logger, "add", cluster.Value, "rollback", "failed", 0,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds, failureStage);
            RecordMutation("add", "failed", _timeProvider.GetElapsedTime(started));
            if (candidate is not null && !published)
                await RethrowAfterCandidateCleanupAsync(exception, candidate).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (mutationBegan)
                EndSlotMutation();
            _mutationGate.Release();
        }

        async ValueTask<SharpLinkClusterAddResult> RejectAddAsync(
            MutationRejection rejected,
            SharpLinkPreparedCluster? rejectedCandidate,
            string stage,
            long operationStarted)
        {
            LogMutationStage(_logger, "add", cluster.Value, "rollback", "rejected", 0,
                _timeProvider.GetElapsedTime(operationStarted).TotalMilliseconds, stage);
            RecordMutation("add", "rejected", _timeProvider.GetElapsedTime(operationStarted));
            if (rejectedCandidate is not null)
                await rejectedCandidate.DisposeAsync().ConfigureAwait(false);
            return SharpLinkClusterAddResult.Failure(rejected.Code, rejected.Message);
        }
    }

    async ValueTask<SharpLinkClusterReplacementResult> ISharpLinkMultiClusterLifecycleControl.ReplaceClusterAsync(
        SharpLinkClusterKey cluster,
        SharpClientBuilder builder,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken)
    {
        ValidateClusterKey(cluster);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        var started = _timeProvider.GetTimestamp();
        ColdMetadataPreparation.ThrowIfCallbackReentry(_metadataDomain);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var metadata = CreateMetadataStorage();
        SharpLinkPreparedCluster? candidate = null;
        SharpLinkClusterSlot? existingSlot = null;
        DynamicAssemblyRegistration[] registrations = [];
        var published = false;
        var mutationBegan = false;
        var publishedBudget = 0;
        var failureStage = "state_validation";
        try
        {
            LogMutationStage(_logger, "replace", cluster.Value, "started", "pending", 0, 0);
            builder.UseLoggerFactoryIfUnset(_loggerFactory);
            MutationRejection? rejection = null;
            lock (_gate)
            {
                if (!TryBeginSlotMutationLocked(
                        allowRunningConnectivityTransition: false,
                        out var snapshot,
                        out var beginRejection))
                {
                    rejection = beginRejection;
                }
                else
                {
                    mutationBegan = true;
                    if (!snapshot.Clusters.TryGetValue(cluster, out existingSlot))
                    {
                        rejection = Reject(
                            SharpLinkClusterMutationFailureCode.NotFound,
                            $"Cluster '{cluster}' is not configured.");
                    }
                    else
                    {
                        metadata.ReserveCollection(_dynamicRegistrations.Count);
                        registrations = _dynamicRegistrations
                            .Where(registration => ReferenceEquals(registration.Slot, existingSlot))
                            .ToArray();
                    }
                }
            }
            if (rejection is { } initialRejection)
                return await RejectReplacementAsync(initialRejection, candidate, failureStage, started).ConfigureAwait(false);

            failureStage = "candidate_preparation";
            candidate = SharpLinkMultiClusterClientBuilder.PrepareReplacementCluster(existingSlot!, builder, _metadataDomain);
            failureStage = "budget_preflight";
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetPublishableSnapshotLocked(
                        allowRunningConnectivityTransition: false,
                        out var snapshot,
                        out var publishRejection))
                {
                    rejection = publishRejection;
                }
                else if (!TryValidateReplacementBudgetLocked(
                             snapshot,
                             cluster,
                             existingSlot!,
                             candidate.Slot,
                             out _,
                             out var budgetRejection))
                {
                    rejection = budgetRejection;
                }
            }
            if (rejection is { } preflightRejection)
                return await RejectReplacementAsync(preflightRejection, candidate, failureStage, started).ConfigureAwait(false);

            failureStage = "assembly_migration";
            foreach (var registration in registrations)
            {
                var result = candidate.Slot.Client is ISharpLinkPreparedAssemblyClient preparedChild &&
                    registration.Manifest is PreparedGeneratedManifest preparedManifest
                    ? preparedChild.RegisterPreparedAssembly(preparedManifest)
                    : SharpLinkAssemblyRegistrationResult.Failure(Error(
                        SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                        "An internal replacement child must support the exact prepared metadata transaction.", registration.Assembly));
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Dynamic assembly migration for cluster '{cluster}' failed: {result.Error?.Message ?? "unknown registration error"}");
                }
            }

            failureStage = "candidate_connect";
            var activation = await ConnectReplacementCandidateWhenRequiredAsync(
                candidate.Slot, cancellationToken).ConfigureAwait(false);
            if (activation.Rejection is { } activationRejection)
                return await RejectReplacementAsync(activationRejection, candidate, failureStage, started).ConfigureAwait(false);
            LogMutationStage(_logger, "replace", cluster.Value,
                activation.Activated ? "candidate_connected" : "candidate_prepared", "success",
                candidate.Slot.ConfiguredConnectionBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);

            failureStage = "snapshot_validation";
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetPublishableSnapshotLocked(
                        allowRunningConnectivityTransition: false,
                        out var snapshot,
                        out var publishRejection))
                {
                    rejection = publishRejection;
                }
                else if (!TryValidateReplacementBudgetLocked(
                             snapshot,
                             cluster,
                             existingSlot!,
                             candidate.Slot,
                             out var nextBudget,
                             out var budgetRejection))
                {
                    rejection = budgetRejection;
                }
                else
                {
                    metadata.ReserveCollection(checked(snapshot.Clusters.Count + 1));
                    metadata.ReserveCollection(checked(snapshot.Clusters.Count + 1));
                    var nextClusters = snapshot.Clusters.ToDictionary(static pair => pair.Key, static pair => pair.Value);
                    nextClusters[cluster] = candidate.Slot;
                    metadata.ReserveCollection(snapshot.Routes.Count);
                    metadata.ReserveCollection(snapshot.Routes.Count);
                    var nextRoutes = snapshot.Routes.ToDictionary(static pair => pair.Key, pair =>
                        ReferenceEquals(pair.Value.Slot, existingSlot)
                            ? pair.Value with { Slot = candidate.Slot }
                            : pair.Value);
                    // Complete every allocating snapshot/budget prebuild before
                    // any old D2 registration lease can be released or row changed.
                    var nextTransitionBudget = checked(_transitionConnectionBudget + existingSlot!.ConfiguredConnectionBudget);
                    var replacementSnapshot = MultiClusterSnapshot.Create(
                        nextClusters.ToFrozenDictionary(), nextRoutes.ToFrozenDictionary(), nextBudget, metadata, snapshot);
                    try
                    {
                        for (var index = 0; index < _dynamicRegistrations.Count; index++)
                        {
                            var registration = _dynamicRegistrations[index];
                            if (ReferenceEquals(registration.Slot, existingSlot))
                            {
                                _dynamicRegistrations[index] = registration with { Slot = candidate.Slot };
                                registration.Dispose();
                            }
                        }
                    }
                    catch (Exception primary)
                    {
                        Exception graph = primary;
                        try { replacementSnapshot.Dispose(); }
                        catch (Exception cleanup)
                        {
                            // At least one original exists: only the loop's real
                            // clone/release can enter this catch. Prefer its live
                            // clone so snapshot uncertainty holds the last96 lease.
                            var custody = _dynamicRegistrations.FirstOrDefault(
                                registration => ReferenceEquals(registration.Slot, candidate.Slot)) ?? registrations[0];
                            custody.RecordSnapshotFailure(replacementSnapshot, cleanup);
                            graph = new AggregateException(graph, cleanup);
                        }
                        ExceptionDispatchInfo.Capture(RollbackAssemblySlotClonesLocked(
                            registrations, candidate.Slot, graph)).Throw();
                        throw;
                    }

                    _transitionConnectionBudget = nextTransitionBudget;
                    PublishSnapshotLocked(replacementSnapshot);
                    _slotMutationInProgress = false;
                    mutationBegan = false;
                    candidate.Publish();
                    published = true;
                    publishedBudget = nextBudget;
                }
            }
            if (rejection is { } publicationRejection)
                return await RejectReplacementAsync(publicationRejection, candidate, failureStage, started).ConfigureAwait(false);

            LogMutationStage(_logger, "replace", cluster.Value, "snapshot_published", "success", publishedBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            var retirement = TrackRetiredSlotCleanup(
                existingSlot!,
                existingSlot!.ConfiguredConnectionBudget,
                "replace",
                cluster,
                gracefulTimeout);
            failureStage = "retired_cleanup_wait";
            var released = await retirement.WaitAsync(
                gracefulTimeout,
                GetTimeProvider(existingSlot!.Client),
                cancellationToken).ConfigureAwait(false);
            if (!released)
            {
                LogMutationStage(_logger, "replace", cluster.Value, "forced_stop", "cleanup_pending",
                    Volatile.Read(ref _snapshot).ConfiguredConnectionBudget,
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            }
            RecordMutation("replace", released ? "success" : "forced_stop", _timeProvider.GetElapsedTime(started));
            return SharpLinkClusterReplacementResult.Success(released);
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
                await RethrowAfterCandidateCleanupAsync(exception, candidate).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (mutationBegan)
                EndSlotMutation();
            _mutationGate.Release();
        }

        async ValueTask<SharpLinkClusterReplacementResult> RejectReplacementAsync(
            MutationRejection rejected,
            SharpLinkPreparedCluster? rejectedCandidate,
            string stage,
            long operationStarted)
        {
            LogMutationStage(_logger, "replace", cluster.Value, "rollback", "rejected",
                Volatile.Read(ref _snapshot).ConfiguredConnectionBudget,
                _timeProvider.GetElapsedTime(operationStarted).TotalMilliseconds,
                stage);
            RecordMutation("replace", "rejected", _timeProvider.GetElapsedTime(operationStarted));
            if (rejectedCandidate is not null)
                await rejectedCandidate.DisposeAsync().ConfigureAwait(false);
            return SharpLinkClusterReplacementResult.Failure(rejected.Code, rejected.Message);
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
        ColdMetadataPreparation.ThrowIfCallbackReentry(_metadataDomain);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var metadata = CreateMetadataStorage();
        SharpLinkClusterSlot? existingSlot = null;
        var published = false;
        var mutationBegan = false;
        var publishedBudget = 0;
        var failureStage = "snapshot_validation";
        try
        {
            LogMutationStage(_logger, "remove", cluster.Value, "started", "pending",
                Volatile.Read(ref _snapshot).ConfiguredConnectionBudget, 0);
            MutationRejection? rejection = null;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryBeginSlotMutationLocked(
                        allowRunningConnectivityTransition: false,
                        out var snapshot,
                        out var beginRejection))
                {
                    rejection = beginRejection;
                }
                else
                {
                    mutationBegan = true;
                    if (!snapshot.Clusters.TryGetValue(cluster, out existingSlot))
                    {
                        rejection = Reject(
                            SharpLinkClusterMutationFailureCode.NotFound,
                            $"Cluster '{cluster}' is not configured.");
                    }
                    else
                    {
                        metadata.ReserveCollection(snapshot.Clusters.Count);
                        metadata.ReserveCollection(snapshot.Clusters.Count);
                        metadata.ReserveCollection(snapshot.Routes.Count);
                        metadata.ReserveCollection(snapshot.Routes.Count);
                        var nextClusters = snapshot.Clusters
                            .Where(pair => pair.Key != cluster)
                            .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                            .ToFrozenDictionary();
                        var nextRoutes = snapshot.Routes
                            .Where(pair => !ReferenceEquals(pair.Value.Slot, existingSlot))
                            .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                            .ToFrozenDictionary();
                        var nextBudget = checked(snapshot.ConfiguredConnectionBudget - existingSlot.ConfiguredConnectionBudget);
                        var nextTransitionBudget = checked(_transitionConnectionBudget + existingSlot.ConfiguredConnectionBudget);
                        var removalSnapshot = MultiClusterSnapshot.Create(nextClusters, nextRoutes, nextBudget, metadata, snapshot);
                        try
                        {
                            for (var index = _dynamicRegistrations.Count - 1; index >= 0; index--)
                            {
                                var registration = _dynamicRegistrations[index];
                                if (!ReferenceEquals(registration.Slot, existingSlot)) continue;
                                // New D2 custody must stay discoverable if its
                                // exact lease/metadata release fails before publication.
                                registration.Dispose();
                                _dynamicRegistrations.RemoveAt(index);
                            }
                        }
                        catch (Exception primary)
                        {
                            _assemblyPublicationFailed = true;
                            try { removalSnapshot.Dispose(); }
                            catch (Exception cleanup)
                            {
                                // Failed Dispose is still linked: unlink follows
                                // successful release, never the reverse.
                                var custody = _dynamicRegistrations.First(registration =>
                                    ReferenceEquals(registration.Slot, existingSlot));
                                custody.RecordSnapshotFailure(removalSnapshot, cleanup);
                                throw new AggregateException(primary, cleanup);
                            }
                            throw;
                        }
                        _transitionConnectionBudget = nextTransitionBudget;
                        PublishSnapshotLocked(removalSnapshot);
                        _slotMutationInProgress = false;
                        mutationBegan = false;
                        published = true;
                        publishedBudget = nextBudget;
                    }
                }
            }
            if (rejection is { } removeRejection)
            {
                LogMutationStage(_logger, "remove", cluster.Value, "rollback", "rejected",
                    Volatile.Read(ref _snapshot).ConfiguredConnectionBudget,
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                    failureStage);
                RecordMutation("remove", "rejected", _timeProvider.GetElapsedTime(started));
                return SharpLinkClusterRemovalResult.Failure(removeRejection.Code, removeRejection.Message);
            }

            LogMutationStage(_logger, "remove", cluster.Value, "snapshot_published", "success", publishedBudget,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            var retirement = TrackRetiredSlotCleanup(
                existingSlot!,
                existingSlot!.ConfiguredConnectionBudget,
                "remove",
                cluster,
                gracefulTimeout);
            failureStage = "retired_cleanup_wait";
            var released = await retirement.WaitAsync(
                gracefulTimeout,
                GetTimeProvider(existingSlot!.Client),
                cancellationToken).ConfigureAwait(false);
            if (!released)
            {
                LogMutationStage(_logger, "remove", cluster.Value, "forced_stop", "cleanup_pending",
                    Volatile.Read(ref _snapshot).ConfiguredConnectionBudget,
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            }
            RecordMutation("remove", released ? "success" : "forced_stop", _timeProvider.GetElapsedTime(started));
            return SharpLinkClusterRemovalResult.Success(released);
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
            if (mutationBegan)
                EndSlotMutation();
            _mutationGate.Release();
        }
    }

    private SharpLinkRetirementHandle TrackRetiredSlotCleanup(
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
        return new SharpLinkRetirementHandle(cleanup);
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
            retiredSlot.ReleaseMetadata();
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

    private void EndSlotMutation()
    {
        lock (_gate)
            _slotMutationInProgress = false;
    }

    private static async Task RethrowAfterCandidateCleanupAsync(
        Exception exception,
        SharpLinkPreparedCluster candidate)
    {
        try
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
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
