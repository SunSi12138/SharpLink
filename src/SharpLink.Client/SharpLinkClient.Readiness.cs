namespace SharpLink.Client;

internal readonly record struct ClientReadinessFacts(
    int ActiveEndpoints,
    int ReadyEndpoints,
    int ReadyConnections,
    int TargetReadyEndpoints);

internal sealed class ClientReadinessPublication
{
    internal ClientReadinessPublication(SharpLinkClientReadinessSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal SharpLinkClientReadinessSnapshot Snapshot { get; }

    internal TaskCompletionSource Changed { get; }
}

internal sealed partial class SharpLinkClient
{
    private readonly Lock _readinessGate = new();
    private readonly int _maximumReadinessWaitThreshold;
    private ClientReadinessFacts _readinessFacts;
    private ClientReadinessPublication _readinessPublication;

    public SharpLinkClientReadinessSnapshot GetReadinessSnapshot()
        => Volatile.Read(ref _readinessPublication).Snapshot;

    public ValueTask<SharpLinkClientReadinessSnapshot> WaitForReadinessAsync(
        int minimumReadyEndpoints,
        CancellationToken cancellationToken = default)
    {
        ValidateReadinessMinimum(minimumReadyEndpoints);
        cancellationToken.ThrowIfCancellationRequested();

        var publication = Volatile.Read(ref _readinessPublication);
        if (IsReadinessSatisfied(publication.Snapshot, minimumReadyEndpoints))
            return ValueTask.FromResult(publication.Snapshot);

        return WaitForReadinessCoreAsync(minimumReadyEndpoints, cancellationToken);
    }

    internal ClientReadinessPublication ReadinessPublicationForTesting
        => Volatile.Read(ref _readinessPublication);

    internal void CloseStopAdmissionForTesting()
        => Volatile.Write(ref _stopStarted, 1);

    internal Task ReadySignalForTesting
        => Volatile.Read(ref _readySignal).Task;

    internal void TransitionToForTesting(SharpLinkConnectionState state)
        => TransitionTo(state);

    internal void PublishReadinessFacts(ClientReadinessFacts facts)
    {
        ValidateReadinessFacts(facts);
        TaskCompletionSource? changed;
        lock (_readinessGate)
        {
            _readinessFacts = facts;
            changed = PublishReadinessLocked();
            UpdateReadySignalLevelLocked();
        }
        changed?.TrySetResult();
    }

    private async ValueTask<SharpLinkClientReadinessSnapshot> WaitForReadinessCoreAsync(
        int minimumReadyEndpoints,
        CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            Volatile.Read(ref _stopStarted) != 0 &&
            exception is OperationCanceledException or SharpLinkException)
        {
            throw CreateConnectionClosedException(
                "Client stopped before the requested readiness level was observed.",
                exception);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publication = Volatile.Read(ref _readinessPublication);
            var snapshot = publication.Snapshot;
            if (IsReadinessSatisfied(snapshot, minimumReadyEndpoints))
                return snapshot;

            ThrowIfReadinessWaitCannotContinue(snapshot.State);
            await publication.Changed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ValidateReadinessMinimum(int minimumReadyEndpoints)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumReadyEndpoints, 1);
        if (minimumReadyEndpoints > _maximumReadinessWaitThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumReadyEndpoints),
                minimumReadyEndpoints,
                $"The configured topology supports readiness waits up to {_maximumReadinessWaitThreshold} endpoint(s).");
        }
    }

    private bool IsReadinessSatisfied(
        SharpLinkClientReadinessSnapshot snapshot,
        int minimumReadyEndpoints)
        => snapshot.State == SharpLinkConnectionState.Ready &&
           snapshot.ReadyConnections > 0 &&
           snapshot.ReadyEndpoints >= minimumReadyEndpoints &&
           Volatile.Read(ref _stopStarted) == 0;

    private static void ThrowIfReadinessWaitCannotContinue(SharpLinkConnectionState state)
    {
        if (state is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped)
            throw CreateConnectionClosedException("Client stopped before the requested readiness level was observed.");
        if (state == SharpLinkConnectionState.Faulted)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "The current Client readiness wait ended because the latest initial connection attempt faulted.");
        }
    }

    private TaskCompletionSource? PublishReadinessLocked()
    {
        var current = _readinessPublication;
        var snapshot = CreateReadinessSnapshotLocked();
        if (snapshot == current.Snapshot)
            return null;

        var next = new ClientReadinessPublication(snapshot);
        Volatile.Write(ref _readinessPublication, next);
        return current.Changed;
    }

    private void UpdateReadySignalLevelLocked()
    {
        var readyOrStopping = Volatile.Read(ref _stopStarted) != 0 ||
                              ((SharpLinkConnectionState)Volatile.Read(ref _state) == SharpLinkConnectionState.Ready &&
                               _readinessFacts.ReadyConnections != 0);
        lock (_readySignalGate)
        {
            if (readyOrStopping)
            {
                _readySignal.TrySetResult(true);
            }
            else if (_readySignal.Task.IsCompleted)
            {
                Volatile.Write(ref _readySignal, CreateReadySignal());
            }
        }
    }

    private SharpLinkClientReadinessSnapshot CreateReadinessSnapshotLocked()
        => new(
            (SharpLinkConnectionState)Volatile.Read(ref _state),
            _readinessFacts.ActiveEndpoints,
            _readinessFacts.ReadyEndpoints,
            _readinessFacts.ReadyConnections,
            _readinessFacts.TargetReadyEndpoints);

    private static void ValidateReadinessFacts(ClientReadinessFacts facts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(facts.ActiveEndpoints);
        ArgumentOutOfRangeException.ThrowIfNegative(facts.ReadyEndpoints);
        ArgumentOutOfRangeException.ThrowIfNegative(facts.ReadyConnections);
        ArgumentOutOfRangeException.ThrowIfNegative(facts.TargetReadyEndpoints);
        if (facts.ReadyEndpoints > facts.ActiveEndpoints)
            throw new ArgumentException("Ready endpoint count cannot exceed the active endpoint count.", nameof(facts));
        if (facts.TargetReadyEndpoints > facts.ActiveEndpoints)
            throw new ArgumentException("The current target cannot exceed the active endpoint count.", nameof(facts));
        if (facts.ReadyEndpoints == 0 && facts.ReadyConnections != 0)
            throw new ArgumentException("Ready connections require at least one ready endpoint.", nameof(facts));
        if (facts.ReadyConnections < facts.ReadyEndpoints)
            throw new ArgumentException("Every ready endpoint requires at least one ready connection.", nameof(facts));
    }
}
