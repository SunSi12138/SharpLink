namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly Lock _contractDemandGate = new();
    private readonly List<WeakReference<ContractCallSite>> _contractDemands = [];
    private int _bindingWaiters;
    internal Action<ClientBindingSession>? BindingSessionCreatedForTesting { get; set; }

    private void RegisterContractDemand(ContractCallSite site)
    {
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
        }
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
            _contractDemands.RemoveAll(weak => !weak.TryGetTarget(out var existing) || ReferenceEquals(existing, site));
        site.RetireDemand();
    }

    private void OnLocalContractRetired(ClientLocalContractControl control)
    {
        lock (_contractDemandGate)
        {
            foreach (var weak in _contractDemands)
                if (weak.TryGetTarget(out var site) && ReferenceEquals(site.LocalControl, control))
                    site.RetireDemand();
            _contractDemands.RemoveAll(weak => !weak.TryGetTarget(out var site) || ReferenceEquals(site.LocalControl, control));
        }
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
            sites = _contractDemands.Select(static weak => weak.TryGetTarget(out var site) ? site : null)
                .Where(static site => site is not null).ToArray()!;
        }
        foreach (var site in sites)
            ScheduleContractBinding(site);
    }

    private void ScheduleContractBinding(ContractCallSite site)
    {
        if (site.LocalControl.IsRetired || _shutdownCts.IsCancellationRequested)
            return;
        foreach (var connection in Volatile.Read(ref _readyConnections))
        {
            if (!connection.CanAcceptCalls || !IsContractBindingCandidate(site, connection.Session))
                continue;
            if (site.TryBeginBinding(connection))
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
        try
        {
            Protocol3BindingReceipt? receipt = site.Options.Receipt is { } original
                ? new Protocol3BindingReceipt(original.ServerInstanceId, original.RegistryEpoch,
                    (ulong)original.Generation, (ulong)original.RegistrationId) : null;
            var request = new Protocol3BindRequest((Protocol3BindingRequirement)site.Options.Requirement,
                site.Local.ContractId, site.Local.RpcAssemblyHash, receipt);
            var result = await connection.Binding.BindAsync(request).ConfigureAwait(false);
            if (result.Status != Protocol3BindStatus.Success)
            {
                site.BindingFailed(connection, MapBindingFailure(result.Status));
                return;
            }
            if (site.LocalControl.IsRetired || !connection.CanAcceptCalls ||
                !connection.Binding.Publication.TryCommit(result.Token,
                    () => site.PublishBinding(connection, result.Token)))
            {
                await connection.Binding.ReleaseAsync(result.Token).ConfigureAwait(false);
                site.BindingFailed(connection, NoBoundRoute());
            }
        }
        catch (Exception exception)
        {
            site.BindingFailed(connection, exception);
        }
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
        if (site.TryCompleteReadyPreparation(timeout, started, cancellationToken))
            return ValueTask.CompletedTask;
        if (Interlocked.Increment(ref _bindingWaiters) > 4096)
        {
            Interlocked.Decrement(ref _bindingWaiters);
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                SharpLinkErrorDetails.ResourceExhausted.BindingWaiters, "Client binding waiter capacity is full.");
        }
        return WaitForContractBindingAsync(site, timeout, started, cancellationToken);
    }

    private async ValueTask WaitForContractBindingAsync(ContractCallSite site, TimeSpan timeout,
        long started, CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            site.EnterBindingWaiter();
            entered = true;
            ScheduleContractBinding(site);
            while (true)
            {
                var signal = site.CaptureBindingSignal();
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - _runtimeContext.TimeProvider.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                    throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Prepare deadline expired.");
                if (_shutdownCts.IsCancellationRequested)
                    throw CreateConnectionClosedException("Client is stopped.");
                site.LocalControl.ThrowIfRetired();
                if (site.TryCompleteReadyPreparation(timeout, started, cancellationToken))
                    return;
                if (site.CaptureBindingFailure() is { } failure)
                    throw failure;
                try
                {
                    await signal.WaitAsync(remaining, _runtimeContext.TimeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Prepare deadline expired.");
                }
            }
        }
        finally
        {
            if (entered) site.ExitBindingWaiter();
            Interlocked.Decrement(ref _bindingWaiters);
        }
    }
}
