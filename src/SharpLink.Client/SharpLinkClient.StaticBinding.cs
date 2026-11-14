namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal StaticBindingBudget BindingBudget { get; } = new();
    private TaskCompletionSource _staticBindingChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _staticPrepareWaiters;
    private BindingTopologySnapshot _bindingTopology = BindingTopologySnapshot.Empty;
    internal Func<ValueTask>? BeforeBindingSiteCommitTestHook;

    internal ValueTask PrepareStaticBindingAsync(StaticBindingCallSite site, TimeSpan timeout, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, SharpLinkTimer.MaximumDelay);
        token.ThrowIfCancellationRequested();
        EnsureStaticSliceAvailable();
        site.EnsureLocalRunning();
        if (Volatile.Read(ref site.Snapshot).Targets.Any(static target => target.Connection.CanAcceptCalls))
            return ValueTask.CompletedTask;
        var clock = _runtimeContext.TimeProvider;
        return WaitForStaticBindingAsync(site, RpcDeadline.Create(timeout, clock.GetTimestamp(), clock.TimestampFrequency), token);
    }

    private async ValueTask WaitForStaticBindingAsync(StaticBindingCallSite site, RpcDeadline deadline, CancellationToken token)
    {
        if (Interlocked.Increment(ref _staticPrepareWaiters) > StaticBindingWire.Capacity)
        {
            Interlocked.Decrement(ref _staticPrepareWaiters);
            throw StaticBindingWire.Error(StaticBindingFailure.Capacity);
        }
        try
        {
            ScheduleStaticBindingRefresh(site, stateChanged: false);
            while (true)
            {
                var changed = Volatile.Read(ref _staticBindingChanged);
                token.ThrowIfCancellationRequested();
                EnsureStaticSliceAvailable();
                site.EnsureLocalRunning();
                var remaining = deadline.GetRemaining(_runtimeContext.TimeProvider);
                if (remaining <= TimeSpan.Zero)
                    throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Binding preparation deadline expired.");
                if (Volatile.Read(ref site.Snapshot).Targets.Any(static target => target.Connection.CanAcceptCalls)) return;
                if (Volatile.Read(ref site.Failure) is { } failure) throw failure;
                try { await changed.Task.WaitAsync(remaining, _runtimeContext.TimeProvider, token).ConfigureAwait(false); }
                catch (TimeoutException)
                { throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Binding preparation deadline expired."); }
            }
        }
        finally { Interlocked.Decrement(ref _staticPrepareWaiters); }
    }

    private void PulseStaticBindingDemands()
    {
        StaticBindingCallSite[] sites;
        lock (_staticBindingGate)
            sites = _staticBindingSites.Values.Select(reference => reference.TryGetTarget(out var site) ? site : null)
                .OfType<StaticBindingCallSite>().ToArray();
        foreach (var site in sites) ScheduleStaticBindingRefresh(site);
        SignalStaticBindingChange();
    }

    private void SignalStaticBindingChange()
        => Interlocked.Exchange(ref _staticBindingChanged,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private void ScheduleStaticBindingRefresh(StaticBindingCallSite site, bool stateChanged = true)
    {
        // Stop seals/drains the same supervisor under this existing publication gate.
        // Register the queued task before releasing it; no unowned ThreadPool callback can
        // turn up after Drain or schedule another demand on the final Stopped pulse.
        lock (_poolGate)
        {
            if (Volatile.Read(ref _stopStarted) != 0 || _poolStopping) return;
            while (true)
            {
                var state = Volatile.Read(ref site.RefreshState);
                if (state == 2) return;
                if (state == 1)
                {
                    // Joining the same demand is not a new topology/manifest event.
                    // Real state publications still mark the in-progress capture dirty.
                    if (!stateChanged) return;
                    if (Interlocked.CompareExchange(ref site.RefreshState, 2, 1) == 1) return;
                    continue;
                }
                if (Interlocked.CompareExchange(ref site.RefreshState, 1, 0) == 0) break;
            }
            TrackFrameworkTask(Task.Run(() => RefreshStaticBindingAsync(site)), "StaticBindingDemand");
        }
    }

    private async Task RefreshStaticBindingAsync(StaticBindingCallSite site)
    {
        try
        {
            EnsureStaticSliceAvailable();
            site.EnsureLocalRunning();
            var topology = Volatile.Read(ref _bindingTopology);
            var connections = topology.Connections;
            var manifests = Volatile.Read(ref _remoteContractManifestSnapshot);
            var work = new List<Task<StaticBoundTarget>>();
            var mismatch = false;
            var observedHashes = new List<string>();
            var receiptMismatch = false;
            foreach (var connection in connections)
                foreach (var manifest in manifests)
                {
                    if (!ReferenceEquals(manifest.Session, connection.Session) || !connection.CanAcceptCalls) continue;
                    if (!site.Scope.MatchesPeer(manifest.Manifest.ServerInstanceId, manifest.Manifest.RegistryEpoch))
                        receiptMismatch = true;
                    else if (site.Scope.Receipt.HasValue ||
                        manifest.Manifest.Contracts.TryGetValue(site.ContractId, out var hash) && hash == site.Hash)
                        work.Add(PrepareStaticTargetAsync(connection, site));
                    else
                    {
                        mismatch = true;
                        observedHashes.Add(manifest.Manifest.Contracts.TryGetValue(site.ContractId, out var observed)
                            ? observed.ToString() : "<missing>");
                    }
                }
            var targets = await Task.WhenAll(work).ConfigureAwait(false);
            if (BeforeBindingSiteCommitTestHook is { } beforeCommit) await beforeCommit().ConfigureAwait(false);
            lock (_poolGate)
            lock (_remoteContractManifestGate)
            lock (_staticBindingGate)
            {
                EnsureStaticSliceAvailable();
                site.EnsureLocalRunning();
                if (!ReferenceEquals(topology, Volatile.Read(ref _bindingTopology)) ||
                    !ReferenceEquals(manifests, Volatile.Read(ref _remoteContractManifestSnapshot)))
                {
                    _ = Interlocked.CompareExchange(ref site.RefreshState, 2, 1);
                    return;
                }
                // State Ack commit and site publication are separate boundaries. Invalidation
                // first records its watermark, then takes this same site gate to prune routes.
                // Recheck the scalar token here so an already-confirmed Ack cannot resurrect
                // an invalidated target while its refresh continuation was delayed.
                var confirmed = targets.Where(target => target.Connection.StaticBindings.IsConfirmedToken(target.Token)).ToArray();
                if (confirmed.Length != targets.Length) _ = Interlocked.CompareExchange(ref site.RefreshState, 2, 1);
                Volatile.Write(ref site.Failure, confirmed.Length == 0 && (mismatch || receiptMismatch)
                    ? receiptMismatch ? StaticBindingWire.Error(StaticBindingFailure.ReceiptMismatch)
                        : new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                            (ushort)(60000 + (ushort)StaticBindingFailure.HashMismatch),
                            $"Contract '{site.ContractName}' ({site.ContractId}) cannot bind assembly hash {site.Hash}; " +
                            $"Ready peers advertise {string.Join(", ", observedHashes.Distinct())}.")
                    : null);
                Volatile.Write(ref site.Snapshot, StaticBindingSnapshot.Create(topology, confirmed));
            }
        }
        catch (Exception failure) { Volatile.Write(ref site.Failure, failure); }
        finally
        {
            var rerun = Interlocked.Exchange(ref site.RefreshState, 0) == 2;
            SignalStaticBindingChange();
            if (rerun && !_shutdownCts.IsCancellationRequested) ScheduleStaticBindingRefresh(site);
        }
    }

    private static async Task<StaticBoundTarget> PrepareStaticTargetAsync(ClientConnection connection, StaticBindingCallSite site)
    {
        var ack = await connection.StaticBindings.BindAsync(site.ContractId, site.Hash, site.Scope).ConfigureAwait(false);
        return new StaticBoundTarget(connection, ack.Token);
    }

    private void PublishBindingTopology(BindingTopologySnapshot topology)
    {
        lock (_staticBindingGate)
        {
            Volatile.Write(ref _bindingTopology, topology);
            foreach (var reference in _staticBindingSites.Values)
                if (reference.TryGetTarget(out var site))
                {
                    var retained = Volatile.Read(ref site.Snapshot).Targets.Where(target =>
                        Array.IndexOf(topology.Connections, target.Connection) >= 0 && target.Connection.CanAcceptCalls).ToArray();
                    Volatile.Write(ref site.Snapshot, StaticBindingSnapshot.Create(topology, retained));
                }
        }
    }

    private void InvalidateStaticBindingRoutes(RpcSession session)
    {
        lock (_staticBindingGate)
            foreach (var reference in _staticBindingSites.Values)
                if (reference.TryGetTarget(out var site))
                    Volatile.Write(ref site.Snapshot, Volatile.Read(ref site.Snapshot).Filter(target =>
                        !ReferenceEquals(target.Connection.Session, session)));
    }

    private void InvalidateBoundTokens(RpcSession session, ulong[] tokens)
    {
        lock (_staticBindingGate)
            foreach (var reference in _staticBindingSites.Values)
                if (reference.TryGetTarget(out var site))
                    Volatile.Write(ref site.Snapshot, Volatile.Read(ref site.Snapshot).Filter(target =>
                        !ReferenceEquals(target.Connection.Session, session) || !tokens.Contains(target.Token)));
        PulseStaticBindingDemands();
    }

    private void PruneIncompatibleBindingRoutes(RpcSession session, ProtocolV2ContractManifest manifest)
    {
        lock (_staticBindingGate)
            foreach (var reference in _staticBindingSites.Values)
                if (reference.TryGetTarget(out var site) && !site.Scope.Receipt.HasValue &&
                    (!manifest.Contracts.TryGetValue(site.ContractId, out var hash) || hash != site.Hash))
                    Volatile.Write(ref site.Snapshot, Volatile.Read(ref site.Snapshot).Filter(target =>
                        !ReferenceEquals(target.Connection.Session, session)));
    }

    private StaticBoundTarget GetReadyStaticTarget(StaticBindingCallSite? site, RpcMethodDescriptor method,
        AttemptOutcomeState? outcome, EndpointRetrySelectionState? retrySelection = null)
    {
        if (site is null || method.ContractId != site.ContractId)
            throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute);
        EnsureStaticSliceAvailable();
        var snapshot = Volatile.Read(ref site.Snapshot);
        var policy = _cluster?.CaptureBindingSelectionPolicy();
        var endpoints = snapshot.Endpoints;
        var excluded = retrySelection?.GetExcludedMask(snapshot, endpoints.Length) ?? 0UL;
        for (var attempt = 0; attempt < endpoints.Length; attempt++)
        {
            var index = _cluster is null
                ? ((excluded & 1) == 0 ? 0 : -1)
                : _cluster.SelectBindingEndpoint(snapshot, policy!, excluded);
            if ((uint)index >= (uint)endpoints.Length || (excluded & (1UL << index)) != 0)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                    "The endpoint selector returned an unavailable candidate index.");
            var endpoint = endpoints[index];
            if (endpoint.Candidate is { } candidate && outcome is not null && !outcome.TryAcquire(candidate))
            {
                retrySelection?.Exclude(snapshot, index);
                excluded |= 1UL << index;
                continue;
            }
            retrySelection?.Exclude(snapshot, index);
            if (endpoint.TryReserve(out var selected))
            {
                if (selected.Connection.ActiveCallCount != 0) endpoint.Topology.EnsureExpansion();
                return selected;
            }
            outcome?.CompleteWithoutPending(PendingCallCompletionReason.ConnectionClosed,
                CreateConnectionClosedException("The selected bound endpoint connection is no longer ready."));
            endpoint.Topology.ReleaseStaleAdmission?.Invoke();
            excluded |= 1UL << index;
        }
        EnsureStaticSliceAvailable();
        SharpLinkTelemetry.RecordSelectionFailure(endpoints.Length == 0 ? "no_ready_endpoint" : "no_admitted_connection");
        throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute);
    }
}
