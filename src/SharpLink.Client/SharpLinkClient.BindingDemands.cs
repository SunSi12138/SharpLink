namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly Lock _contractDemandGate = new();
    private readonly List<WeakReference<ContractCallSite>> _contractDemands = [];
    private int _bindingWaiters;
    internal ClientBindingBudget BindingBudget { get; } = new();
    internal Action<ClientBindingSession>? BindingSessionCreatedForTesting { get; set; }

    private void RegisterContractDemand(ContractCallSite site)
    {
        BindingBudget.SetTaskObserver(ObserveBindingMaintenanceTask);
        site.LocalControl.SetRetirementCallback(OnLocalContractRetired);
        lock (_contractDemandGate)
        {
            site.LocalControl.ThrowIfRetired();
            _contractDemands.RemoveAll(static weak => !weak.TryGetTarget(out var site) || site.LocalControl.IsRetired);
            foreach (var weak in _contractDemands)
                if (weak.TryGetTarget(out var existing) && ReferenceEquals(existing, site))
                    return;
            if (_contractDemands.Count >= 4096)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    SharpLinkErrorDetails.ResourceExhausted.BindingDemandSites, "Client contract demand capacity is full.");
            _contractDemands.Add(new WeakReference<ContractCallSite>(site));
            PublishContractDemandSnapshotLocked();
        }
        ContractRouteBuildForTesting?.Invoke(site, "registered");
        site.ReconcileContractRoute();
        TrackFrameworkTask(StartContractBindingDemandAsync(site), "StartContractBinding");
    }

    private async Task StartContractBindingDemandAsync(ContractCallSite site)
    {
        await Task.Yield();
        ScheduleContractBinding(site);
    }

    private async Task RefreshContractBindingsAfterReadyAsync()
    {
        await Task.Yield();
        RefreshContractBindingDemands();
    }

    private void UnregisterContractDemand(ContractCallSite site)
    {
        lock (_contractDemandGate)
        {
            _contractDemands.RemoveAll(weak => !weak.TryGetTarget(out var existing) || ReferenceEquals(existing, site));
            PublishContractDemandSnapshotLocked();
        }
        site.RetireDemand();
    }

    private void OnLocalContractRetired(ClientLocalContractControl control)
    {
        ContractCallSite[] retired;
        lock (_contractDemandGate)
        {
            retired = _contractDemands.Select(static weak => weak.TryGetTarget(out var site) ? site : null)
                .Where(site => site is not null && ReferenceEquals(site.LocalControl, control)).ToArray()!;
            _contractDemands.RemoveAll(weak => !weak.TryGetTarget(out var site) || ReferenceEquals(site.LocalControl, control));
            PublishContractDemandSnapshotLocked();
        }
        // Cancellation and cleanup supervision can invoke callbacks; no demand/site/publication gate.
        var failures = new ContractRouteFailure();
        foreach (var site in retired)
        {
            try { site.RetireDemand(); }
            catch (Exception error) { failures.Add("local-contract-retirement", error); }
        }
        failures.ThrowIfAny();
    }

    private void PulseContractBindingWaiters()
    {
        lock (_contractDemandGate)
        {
            foreach (var weak in _contractDemands)
                if (weak.TryGetTarget(out var site))
                    site.PulseBindingWaiters();
        }
    }

    private void RefreshContractBindingDemands()
    {
        ContractCallSite[] sites;
        lock (_contractDemandGate)
        {
            _contractDemands.RemoveAll(static weak => !weak.TryGetTarget(out var site) || site.LocalControl.IsRetired);
            PublishContractDemandSnapshotLocked();
            sites = _contractDemands.Select(static weak => weak.TryGetTarget(out var site) ? site : null)
                .Where(static site => site is not null).ToArray()!;
        }
        var failures = new ContractRouteFailure();
        foreach (var site in sites)
        {
            try
            {
                site.ReconcileContractRoute();
                ScheduleContractBinding(site, retryCompletedFailures: true);
            }
            catch (Exception error) { failures.Add("binding-cold-refresh", error); }
        }
        failures.ThrowIfAny();
    }

    private void ScheduleContractBinding(ContractCallSite site, bool retryCompletedFailures = false)
    {
        if (site.LocalControl.IsRetired || _shutdownCts.IsCancellationRequested)
            return;
        foreach (var connection in _cluster?.CaptureReadyConnections() ?? Volatile.Read(ref _readyConnections))
        {
            if (!connection.CanAcceptCalls || !IsContractBindingCandidate(site, connection.Session))
                continue;
            if (site.TryBeginBinding(connection, retryCompletedFailures))
                TrackFrameworkTask(BindContractDemandAsync(site, connection), "ContractBinding");
        }
    }

    private bool IsContractBindingCandidate(ContractCallSite site, RpcSession session)
    {
        if (site.Options.Receipt is { } receipt)
            return receipt.ServerInstanceId == session.BindingAuthority.ServerInstanceId &&
                   receipt.RegistryEpoch == session.BindingAuthority.RegistryEpoch;
        foreach (var binding in Volatile.Read(ref _remoteContractManifestSnapshot))
            if (ReferenceEquals(binding.Session, session))
                return binding.Manifest.Contracts.TryGetValue(site.Local.ContractId, out var hash) &&
                       hash == site.Local.RpcAssemblyHash;
        return false;
    }

    private async Task BindContractDemandAsync(ContractCallSite site, ClientConnection connection)
    {
        var infrastructure = new ContractRouteFailure(8);
        ClientBindingSession.ResultOwner? resultOwner = null;
        ContractCallSite.BoundTarget? target = null;
        Exception? bindingFailure = null;
        var published = false;
        try
        {
            Protocol3BindingReceipt? receipt = site.Options.Receipt is { } original
                ? new Protocol3BindingReceipt(original.ServerInstanceId, original.RegistryEpoch,
                    (ulong)original.Generation, (ulong)original.RegistrationId) : null;
            var request = new Protocol3BindRequest((Protocol3BindingRequirement)site.Options.Requirement,
                site.Local.ContractId, site.Local.RpcAssemblyHash, receipt);
            resultOwner = await connection.Binding.BindAsync(request, site.DemandRetirement).ConfigureAwait(false);
            var result = resultOwner.Value.Result;
            if (result.Status != Protocol3BindStatus.Success)
                bindingFailure = ApplyBindingRejection(connection, result.Status);
            else
            {
                try { target = new ContractCallSite.BoundTarget(connection, result.Token); }
                catch (Exception error)
                {
                    infrastructure.Add("binding-target", error);
                    try { connection.Binding.Close(error); }
                    catch (Exception cleanup) { infrastructure.Add("binding-close", cleanup); }
                    Task? close = null;
                    try { close = connection.Session.CloseAndDrainOutboundAsync(error).AsTask(); }
                    catch (Exception cleanup) { infrastructure.Add("session-close-start", cleanup); }
                    if (close is not null)
                    {
                        try { await close.ConfigureAwait(false); }
                        catch (Exception cleanup) { infrastructure.Add("session-close", close.Exception is { } graph ? graph : cleanup); }
                    }
                    throw;
                }
                await TrackBindingCandidateAsync(site, target).ConfigureAwait(false);
                if (!site.LocalControl.IsRetired && connection.CanAcceptCalls)
                    published = connection.Binding.Publication.TryCommit(result.Token, () => site.PublishBinding(target));
                if (!published) bindingFailure = NoBoundRoute();
            }
        }
        catch (Exception error)
        {
            bindingFailure = error;
            if (error is ContractRouteFailure) infrastructure.Add("binding-route-publication", error);
        }
        finally
        {
            var candidateRetirementCompleted = target is null || published;
            if (target is not null && !published)
            {
                Task? cleanup = null;
                try { _ = target.Connection.Binding.RetireCandidateAsync(target, out cleanup); }
                catch (Exception error) { infrastructure.Add("candidate-retirement-start", error); }
                if (cleanup is not null)
                {
                    try { await cleanup.ConfigureAwait(false); }
                    catch (Exception error) { infrastructure.Add("candidate-retirement", cleanup.Exception is { } graph ? graph : error); }
                    finally { candidateRetirementCompleted = true; }
                }
            }
            if (resultOwner is { } owner)
            {
                try { owner.Dispose(); }
                catch (Exception error) { infrastructure.Add("binding-result-owner", error); }
            }
            // Without an exact original cleanup receipt, release is unproven: keep the
            // attempt incomplete (no infrastructure retry) rather than infer completion.
            if (bindingFailure is not null && candidateRetirementCompleted)
            {
                try { site.BindingFailed(connection, bindingFailure); }
                catch (Exception error) { infrastructure.Add("binding-failure-publication", error); }
            }
            try { RetireDrainingConnectionIfIdle(connection); }
            catch (Exception error) { infrastructure.Add("binding-idle-retirement", error); }
        }
        infrastructure.ThrowIfAny();
    }

    internal static SharpLinkException ApplyBindingRejection(ClientConnection connection, Protocol3BindStatus status)
    {
        if (status == Protocol3BindStatus.SessionDraining)
            connection.OnBindingSessionDraining();
        return MapBindingFailure(status);
    }

    private static SharpLinkException MapBindingFailure(Protocol3BindStatus status) => status switch
    {
        Protocol3BindStatus.HashMismatch => new(SharpLinkErrorCode.FailedPrecondition,
            SharpLinkErrorDetails.FailedPrecondition.BindHashMismatch, "Binding hash differs."),
        Protocol3BindStatus.ReceiptScopeMismatch => new(SharpLinkErrorCode.FailedPrecondition,
            SharpLinkErrorDetails.FailedPrecondition.ReceiptScopeMismatch, "Receipt authority differs."),
        Protocol3BindStatus.BelowMinimum => new(SharpLinkErrorCode.FailedPrecondition,
            SharpLinkErrorDetails.FailedPrecondition.BelowMinimumVersion, "Registration is below the receipt minimum."),
        Protocol3BindStatus.RegistrationMismatch => new(SharpLinkErrorCode.FailedPrecondition,
            SharpLinkErrorDetails.FailedPrecondition.ExactRegistrationMismatch, "Exact registration is unavailable."),
        Protocol3BindStatus.UnknownContract => new(SharpLinkErrorCode.NotFound,
            SharpLinkErrorDetails.NotFound.UnknownBoundContract, "Contract is unavailable."),
        Protocol3BindStatus.BindingCapacity => new(SharpLinkErrorCode.ResourceExhausted,
            SharpLinkErrorDetails.ResourceExhausted.BindingSlots, "Binding slot capacity is full."),
        Protocol3BindStatus.ControlCapacity => new(SharpLinkErrorCode.ResourceExhausted,
            SharpLinkErrorDetails.ResourceExhausted.BindingOperations, "Binding operation capacity is full."),
        Protocol3BindStatus.ControlDeadline => new(SharpLinkErrorCode.DeadlineExceeded, "Binding control deadline expired."),
        Protocol3BindStatus.ScopeDenied => new(SharpLinkErrorCode.PermissionDenied, "Binding scope is denied."),
        Protocol3BindStatus.CancelledBeforeCommit => new(SharpLinkErrorCode.Cancelled, "Binding demand retired before commit."),
        Protocol3BindStatus.SessionDraining => new(SharpLinkErrorCode.Unavailable,
            SharpLinkErrorDetails.Unavailable.SessionBindingDraining, "Session is draining."),
        _ => new(SharpLinkErrorCode.Unavailable, SharpLinkErrorDetails.Unavailable.BindingRetired,
            "Registration is not currently admitting bindings.")
    };

    private static SharpLinkException NoBoundRoute() => new(SharpLinkErrorCode.Unavailable,
        SharpLinkErrorDetails.Unavailable.NoBoundRoute, "No confirmed exact-session contract route is available.");

    private ValueTask PrepareContractAsync(ContractCallSite site, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > SharpLinkTimer.MaximumDelay)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        if (_shutdownCts.IsCancellationRequested)
            throw CreateConnectionClosedException("Client is stopped.");
        site.LocalControl.ThrowIfRetired();
        var started = _runtimeContext.TimeProvider.GetTimestamp();
        var signal = site.BeginPreparation();
        if (site.TryCompleteReadyPreparation(timeout, started, cancellationToken, signal))
            return ValueTask.CompletedTask;
        if (Interlocked.Increment(ref _bindingWaiters) > 4096)
        {
            Interlocked.Decrement(ref _bindingWaiters);
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                SharpLinkErrorDetails.ResourceExhausted.BindingWaiters, "Client binding waiter capacity is full.");
        }
        return WaitForContractBindingAsync(site, timeout, started, cancellationToken, signal);
    }

    private async ValueTask WaitForContractBindingAsync(ContractCallSite site, TimeSpan timeout,
        long started, CancellationToken cancellationToken, Task<Exception?> signal)
    {
        var entered = false;
        try
        {
            site.EnterBindingWaiter();
            entered = true;
            // Stop cannot leave a waiter sleeping until its finite timeout, even before Connect.
            // Registration callbacks only pulse the original site's bounded waiter signal.
            using var stopped = _shutdownCts.Token.UnsafeRegister(
                static state => ((ContractCallSite)state!).PulseBindingWaiters(), site);
            ScheduleContractBinding(site);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - _runtimeContext.TimeProvider.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                    throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Prepare deadline expired.");
                if (_shutdownCts.IsCancellationRequested)
                    throw CreateConnectionClosedException("Client is stopped.");
                site.LocalControl.ThrowIfRetired();
                if (site.TryCompleteReadyPreparation(timeout, started, cancellationToken, signal))
                    return;
                if (site.CaptureBindingFailure() is { } failure)
                    throw failure;
                Exception? publicationFailure;
                try
                {
                    publicationFailure = await signal.WaitAsync(remaining, _runtimeContext.TimeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Prepare deadline expired.");
                }
                // A captured construction TimeoutException is not this wait's timeout.
                if (publicationFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(publicationFailure).Throw();
                signal = site.CaptureBindingSignal();
            }
        }
        finally
        {
            if (entered) site.ExitBindingWaiter();
            Interlocked.Decrement(ref _bindingWaiters);
        }
    }
}
