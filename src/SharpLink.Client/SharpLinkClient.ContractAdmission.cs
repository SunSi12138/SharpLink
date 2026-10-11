namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private const int MaxContractWaiters = 1024;
    private int _contractWaiters;
    internal int ContractWaiterCountForTesting => Volatile.Read(ref _contractWaiters);

    private sealed class ContractAdmissionMissException() : SharpLinkException(
        SharpLinkErrorCode.Unavailable, "No compatible reservable contract session is available.");

    private ValueTask<ClientConnection> AdmitContractCallAsync(
        RpcMethodDescriptor method, ResolvedCallControl control,
        EndpointRetrySelectionState? selection, AttemptOutcomeState? outcome,
        CancellationToken cancellationToken)
    {
        var requirement = control.ContractRequirement ?? throw new SharpLinkException(
            SharpLinkErrorCode.FailedPrecondition, "A locally bound contract is required.");
        // Subscribe before reading facts/selection: a publication between them must remain visible.
        var changed = Volatile.Read(ref _remoteContractChanged).Task;
        // The existing post-reservation checkpoint is authoritative for the synchronous path.
        // Only a compatibility miss adds a waiting boundary that needs its own budget sample.
        ThrowIfContractWaitTerminated(requirement, default, cancellationToken);
        try
        {
            return ValueTask.FromResult(ReserveLiveContractConnection(method, selection, outcome, requirement, cancellationToken));
        }
        catch (ContractAdmissionMissException)
        {
            ThrowIfContractWaitTerminated(requirement, control.Deadline, cancellationToken);
            var snapshot = GetContractReadiness(requirement);
            if (requirement.Availability != RpcAvailability.WaitForCompatible)
                throw new SharpLinkContractAdmissionException(SharpLinkErrorCode.Unavailable,
                    "No compatible contract endpoint is available.", snapshot);
            ReserveContractWaiter(requirement);
            return WaitForContractCallAsync(method, control, selection, outcome, changed, cancellationToken);
        }
    }

    private ClientConnection ReserveLiveContractConnection(RpcMethodDescriptor method,
        EndpointRetrySelectionState? selection, AttemptOutcomeState? outcome,
        ContractRequirement requirement, CancellationToken cancellationToken)
    {
        var connection = GetReadyConnection(method, selection, outcome, requirement);
        try
        {
            // This is the queued-to-admitted lifetime boundary. Until it succeeds, this helper
            // owns the reservation; retirement/cancellation cannot leave it for a caller that
            // never received the connection. In-flight calls keep their existing module lease.
            ThrowIfContractWaitTerminated(requirement, default, cancellationToken);
            return connection;
        }
        catch
        {
            connection.ReleaseCallAdmissionReservation();
            throw;
        }
    }

    private async ValueTask<ClientConnection> WaitForContractCallAsync(
        RpcMethodDescriptor method, ResolvedCallControl control,
        EndpointRetrySelectionState? selection, AttemptOutcomeState? outcome,
        Task changed, CancellationToken cancellationToken)
    {
        var requirement = control.ContractRequirement!;
        try
        {
            if (State == SharpLinkConnectionState.Created)
                await ConnectForContractWaitAsync(requirement, control.Deadline, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                await WaitForContractChangeAsync(changed, requirement, control.Deadline, cancellationToken).ConfigureAwait(false);
                changed = Volatile.Read(ref _remoteContractChanged).Task;
                ThrowIfContractWaitTerminated(requirement, control.Deadline, cancellationToken);
                try { return ReserveLiveContractConnection(method, selection, outcome, requirement, cancellationToken); }
                catch (ContractAdmissionMissException) { }
            }
        }
        finally { Interlocked.Decrement(ref _contractWaiters); }
    }

    public async ValueTask<ContractReadinessSnapshot> WaitForContractAsync(
        ContractRequirement requirement, ContractWaitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        options ??= new ContractWaitOptions();
        ValidateReadinessMinimum(options.MinimumCompatibleEndpoints);
        if (options.Timeout is { } specifiedTimeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(specifiedTimeout, TimeSpan.Zero);
        var deadline = options.Timeout is { } timeout ? RpcDeadline.Create(timeout, _runtimeContext.TimeProvider) : default;
        var reserved = false;
        try
        {
            while (true)
            {
                var changed = Volatile.Read(ref _remoteContractChanged).Task;
                ThrowIfContractWaitTerminated(requirement, deadline, cancellationToken);
                var snapshot = GetContractReadiness(requirement);
                if (snapshot.CompatibleEndpoints >= options.MinimumCompatibleEndpoints)
                    return snapshot;
                if (!reserved)
                {
                    ReserveContractWaiter(requirement);
                    reserved = true;
                }
                if (State == SharpLinkConnectionState.Created)
                    await ConnectForContractWaitAsync(requirement, deadline, cancellationToken).ConfigureAwait(false);
                await WaitForContractChangeAsync(changed, requirement, deadline, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { if (reserved) Interlocked.Decrement(ref _contractWaiters); }
    }

    private async Task ConnectForContractWaitAsync(ContractRequirement requirement,
        RpcDeadline deadline, CancellationToken cancellationToken)
    {
        var connect = ConnectAsync(CancellationToken.None).AsTask();
        // Connect belongs to the client; this caller bounds only its own observation. Supervise
        // the shared task even if this caller's deadline/retirement wins first.
        TrackFrameworkTask(connect, "ContractAdmissionConnect", TaskObservationMode.ExternallyObserved);
        await WaitForContractChangeAsync(connect, requirement, deadline, cancellationToken).ConfigureAwait(false);
        ThrowIfContractWaitTerminated(requirement, deadline, cancellationToken);
        await connect.ConfigureAwait(false);
    }

    private void ReserveContractWaiter(ContractRequirement requirement)
    {
        if (Interlocked.Increment(ref _contractWaiters) <= MaxContractWaiters)
            return;
        Interlocked.Decrement(ref _contractWaiters);
        throw new SharpLinkContractAdmissionException(SharpLinkErrorCode.ResourceExhausted,
            "The bounded contract admission queue is full.", GetContractReadiness(requirement));
    }

    private void ThrowIfContractWaitTerminated(ContractRequirement requirement, RpcDeadline deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = State;
        if (_shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0 ||
            state is SharpLinkConnectionState.Stopped or SharpLinkConnectionState.Faulted)
            throw new SharpLinkContractAdmissionException(SharpLinkErrorCode.ConnectionClosed,
                "The contract target is no longer accepting calls.", GetContractReadiness(requirement));
        if (state == SharpLinkConnectionState.Draining)
            throw new SharpLinkContractAdmissionException(SharpLinkErrorCode.Unavailable,
                "The contract target is draining.", GetContractReadiness(requirement));
        if (requirement.LocalRetirement?.IsCompleted == true)
            throw new SharpLinkContractAdmissionException(SharpLinkErrorCode.Unavailable,
                "The local contract registration is retiring.", GetContractReadiness(requirement));
        if (deadline.IsExpired(_runtimeContext.TimeProvider))
            throw new SharpLinkContractAdmissionException(SharpLinkErrorCode.DeadlineExceeded,
                "The logical call budget expired during contract admission.", GetContractReadiness(requirement));
    }

    private async Task WaitForContractChangeAsync(Task changed, ContractRequirement requirement,
        RpcDeadline deadline, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var signal = requirement.LocalRetirement is { } retirement ? Task.WhenAny(changed, retirement) : changed;
        try
        {
            if (deadline.HasValue)
            {
                var remaining = deadline.GetRemaining(_runtimeContext.TimeProvider);
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException();
                await signal.WaitAsync(remaining, _runtimeContext.TimeProvider, linked.Token).ConfigureAwait(false);
            }
            else
                await signal.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SharpLinkContractAdmissionException(SharpLinkErrorCode.DeadlineExceeded,
                "The logical call budget expired during contract admission.", GetContractReadiness(requirement));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SharpLinkContractAdmissionException(SharpLinkErrorCode.ConnectionClosed,
                "The client stopped during contract admission.", GetContractReadiness(requirement));
        }
    }
}
