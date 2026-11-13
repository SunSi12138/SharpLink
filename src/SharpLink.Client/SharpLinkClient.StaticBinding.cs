#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private TaskCompletionSource _staticBindingChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _staticPrepareWaiters;

    internal ValueTask PrepareStaticBindingAsync(StaticBindingCallSite site, TimeSpan timeout, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, SharpLinkTimer.MaximumDelay);
        token.ThrowIfCancellationRequested();
        EnsureStaticSliceAvailable();
        if (Volatile.Read(ref site.Snapshot).Any(static target => target.Connection.CanAcceptCalls))
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
            ScheduleStaticBindingRefresh(site);
            while (true)
            {
                var changed = Volatile.Read(ref _staticBindingChanged);
                token.ThrowIfCancellationRequested();
                EnsureStaticSliceAvailable();
                var remaining = deadline.GetRemaining(_runtimeContext.TimeProvider);
                if (remaining <= TimeSpan.Zero)
                    throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Binding preparation deadline expired.");
                if (Volatile.Read(ref site.Snapshot).Any(static target => target.Connection.CanAcceptCalls)) return;
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
        lock (_staticBindingGate)
            foreach (var reference in _staticBindingSites.Values)
                if (reference.TryGetTarget(out var site)) ScheduleStaticBindingRefresh(site);
        SignalStaticBindingChange();
    }

    private void SignalStaticBindingChange()
        => Interlocked.Exchange(ref _staticBindingChanged,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private void ScheduleStaticBindingRefresh(StaticBindingCallSite site)
    {
        while (true)
        {
            var state = Volatile.Read(ref site.RefreshState);
            if (state == 2) return;
            if (state == 1)
            {
                if (Interlocked.CompareExchange(ref site.RefreshState, 2, 1) == 1) return;
                continue;
            }
            if (Interlocked.CompareExchange(ref site.RefreshState, 1, 0) == 0) break;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static site =>
            site.Owner.TrackFrameworkTask(site.Owner.RefreshStaticBindingAsync(site), "StaticBindingDemand"), site, false);
    }

    private async Task RefreshStaticBindingAsync(StaticBindingCallSite site)
    {
        try
        {
            EnsureStaticSliceAvailable();
            var connections = Volatile.Read(ref _readyConnections);
            var manifests = Volatile.Read(ref _remoteContractManifestSnapshot);
            var work = new List<Task<StaticBoundTarget>>();
            var mismatch = false;
            foreach (var connection in connections)
                foreach (var manifest in manifests)
                {
                    if (!ReferenceEquals(manifest.Session, connection.Session) || !connection.CanAcceptCalls) continue;
                    if (manifest.Manifest.Contracts.TryGetValue(site.ContractId, out var hash) && hash == site.Hash)
                        work.Add(PrepareStaticTargetAsync(connection, site));
                    else mismatch = true;
                }
            var targets = await Task.WhenAll(work).ConfigureAwait(false);
            lock (_poolGate)
            lock (_remoteContractManifestGate)
            {
                EnsureStaticSliceAvailable();
                if (!ReferenceEquals(connections, Volatile.Read(ref _readyConnections)) ||
                    !ReferenceEquals(manifests, Volatile.Read(ref _remoteContractManifestSnapshot)))
                {
                    _ = Interlocked.CompareExchange(ref site.RefreshState, 2, 1);
                    return;
                }
                Volatile.Write(ref site.Failure, targets.Length == 0 && mismatch
                    ? StaticBindingWire.Error(StaticBindingFailure.HashMismatch) : null);
                Volatile.Write(ref site.Snapshot, targets);
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
        var ack = await connection.StaticBindings.BindAsync(site.ContractId, site.Hash).ConfigureAwait(false);
        return new StaticBoundTarget(connection, ack.Token);
    }

    private void PruneStaticBindingRoutes(ClientConnection[] connections)
    {
        lock (_staticBindingGate)
            foreach (var reference in _staticBindingSites.Values)
                if (reference.TryGetTarget(out var site))
                    Volatile.Write(ref site.Snapshot, Volatile.Read(ref site.Snapshot).Where(target =>
                        Array.IndexOf(connections, target.Connection) >= 0 && target.Connection.CanAcceptCalls).ToArray());
    }

    private void InvalidateStaticBindingRoutes(RpcSession session)
    {
        lock (_staticBindingGate)
            foreach (var reference in _staticBindingSites.Values)
                if (reference.TryGetTarget(out var site))
                    Volatile.Write(ref site.Snapshot, Volatile.Read(ref site.Snapshot).Where(target =>
                        !ReferenceEquals(target.Connection.Session, session)).ToArray());
    }

    private StaticBoundTarget GetReadyStaticTarget(StaticBindingCallSite? site, RpcMethodDescriptor method, AttemptOutcomeState? outcome)
    {
        if (site is null || method.ContractId != site.ContractId)
            throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var targets = Volatile.Read(ref site.Snapshot);
            if (_shutdownCts.IsCancellationRequested || targets.Length == 0) break;
            if (outcome is not null && _fixedEndpoint is not null && !outcome.TryAcquire(
                new SharpLinkEndpointCandidate(_fixedEndpoint, targets.Length, ActiveClientCallCount, generation: 0)))
                throw StaticBindingWire.Error(StaticBindingFailure.Unavailable);
            var selected = targets[0];
            if (targets.Length > 1)
            {
                var first = Random.Shared.Next(targets.Length);
                var second = Random.Shared.Next(targets.Length - 1);
                if (second >= first) second++;
                selected = targets[first].Connection.ActiveCallCount <= targets[second].Connection.ActiveCallCount
                    ? targets[first] : targets[second];
            }
            if (selected.Connection.TryReserveCallAdmission(out var admitted))
            {
                if (ReferenceEquals(admitted, selected.Connection))
                {
                    if (admitted.ActiveCallCount != 0) EnsureExpansion();
                    return selected;
                }
                admitted.ReleaseCallAdmissionReservation();
            }
            outcome?.CompleteLocalFailure(StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute));
        }
        EnsureStaticSliceAvailable();
        throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute);
    }
}
#endif
