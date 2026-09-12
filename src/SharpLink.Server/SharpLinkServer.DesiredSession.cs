namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private readonly Lock _desiredSessionGate = new();
    private readonly Guid _desiredSessionServerInstanceId = Guid.NewGuid();
    private SharpLinkServerDesiredSessionSnapshot? _desiredSession;
    private ulong _desiredSessionRollingGeneration;
    private ulong _desiredSessionRolloutRequestEpoch;
    private readonly ConcurrentDictionary<string, SharpLinkServerDesiredSessionSnapshot> _sessionDesiredSnapshots = new();
    private object? _desiredSessionRolloutWorker;
    private Task? _desiredSessionRolloutTask;

    internal Func<SharpLinkServerDesiredSessionSnapshot, CancellationToken, ValueTask>? _desiredSessionRolloutTestHook;

    public SharpLinkServerDesiredSessionSnapshot DesiredSession => CaptureDesiredSession();

    public async ValueTask<SharpLinkServerDesiredSessionSnapshot> PublishDesiredSessionAsync(
        SharpLinkServerDesiredSessionConfiguration configuration,
        SharpLinkSessionRolloutMode rolloutMode = SharpLinkSessionRolloutMode.FutureOnly,
        CancellationToken cancellationToken = default)
    {
        var result = await TryPublishDesiredSessionCoreAsync(configuration, rolloutMode, cancellationToken)
            .ConfigureAwait(false);
        if (result.Succeeded && result.Snapshot is { } snapshot)
            return snapshot;
        throw new InvalidOperationException(
            result.Message ?? "Desired session configuration was rejected by the server lifecycle.");
    }

    internal async ValueTask<SharpLinkServerDesiredSessionPublicationResult> TryPublishDesiredSessionCoreAsync(
        SharpLinkServerDesiredSessionConfiguration configuration,
        SharpLinkSessionRolloutMode rolloutMode,
        CancellationToken cancellationToken)
    {
        ValidateDesiredSessionCandidate(configuration, rolloutMode);
        cancellationToken.ThrowIfCancellationRequested();

        SharpLinkServerDesiredSessionSnapshot published;
        Task? rolloutTask = null;
        lock (_stateGate)
        {
            var state = CurrentState;
            if (_lifecycle.HasStopStarted ||
                state is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                return SharpLinkServerDesiredSessionPublicationResult.Failure(
                    SharpLinkRuntimeConfigurationUpdateFailureCode.LifecycleClosed,
                    $"Server state '{state}' does not accept desired-session publication.");
            }

            lock (_desiredSessionGate)
            {
                var current = GetOrCreateDesiredSessionLocked();
                if (current.Configuration.MaxFramePayloadBytes == configuration.MaxFramePayloadBytes)
                {
                    published = current;
                }
                else
                {
                    published = new SharpLinkServerDesiredSessionSnapshot(
                        _desiredSessionServerInstanceId,
                        checked(current.Generation + 1),
                        configuration with { });
                    _desiredSession = published;
                }

                if (rolloutMode == SharpLinkSessionRolloutMode.RollingRefresh)
                {
                    if (_desiredSessionRollingGeneration < published.Generation)
                        _desiredSessionRollingGeneration = published.Generation;
                    _desiredSessionRolloutRequestEpoch = checked(_desiredSessionRolloutRequestEpoch + 1);
                    rolloutTask = EnsureDesiredSessionRolloutWorkerLocked();
                }
            }
        }

        if (rolloutTask is not null)
        {
            if (cancellationToken.CanBeCanceled)
                await rolloutTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                await rolloutTask.ConfigureAwait(false);
        }

        return SharpLinkServerDesiredSessionPublicationResult.Success(published);
    }

    private void ValidateDesiredSessionCandidate(
        SharpLinkServerDesiredSessionConfiguration configuration,
        SharpLinkSessionRolloutMode rolloutMode)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (rolloutMode is not SharpLinkSessionRolloutMode.FutureOnly and
            not SharpLinkSessionRolloutMode.RollingRefresh)
        {
            throw new ArgumentOutOfRangeException(nameof(rolloutMode));
        }
        if (configuration.MaxFramePayloadBytes is < SharpLinkProtocolOptions.MinMaxFramePayloadBytes ||
            configuration.MaxFramePayloadBytes > _protocolOptions.MaxFramePayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                $"MaxFramePayloadBytes must be between {SharpLinkProtocolOptions.MinMaxFramePayloadBytes} and the build-time hard ceiling {_protocolOptions.MaxFramePayloadBytes} bytes.");
        }
    }

    private SharpLinkServerDesiredSessionSnapshot CaptureDesiredSession()
    {
        lock (_desiredSessionGate)
            return GetOrCreateDesiredSessionLocked();
    }

    private SharpLinkServerDesiredSessionSnapshot GetOrCreateDesiredSessionLocked()
    {
        if (_desiredSession is { } current)
            return current;
        var initial = new SharpLinkServerDesiredSessionSnapshot(
            _desiredSessionServerInstanceId,
            1,
            new SharpLinkServerDesiredSessionConfiguration
            {
                MaxFramePayloadBytes = _protocolOptions.MaxFramePayloadBytes
            });
        _desiredSession = initial;
        return initial;
    }

    private Task EnsureDesiredSessionRolloutWorkerLocked()
    {
        if (_desiredSessionRolloutWorker is not null)
            return _desiredSessionRolloutTask!;

        var owner = new object();
        _desiredSessionRolloutWorker = owner;
        var task = RunDesiredSessionRolloutWorkerAsync(owner);
        _desiredSessionRolloutTask = task;
        TrackFrameworkTask(task, "DesiredSessionRollingRefresh");
        return task;
    }

    private async Task RunDesiredSessionRolloutWorkerAsync(object owner)
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                ulong targetGeneration;
                ulong requestEpoch;
                SharpLinkServerDesiredSessionConfiguration targetConfiguration;
                lock (_desiredSessionGate)
                {
                    targetGeneration = _desiredSessionRollingGeneration;
                    requestEpoch = _desiredSessionRolloutRequestEpoch;
                    targetConfiguration = GetOrCreateDesiredSessionLocked().Configuration with { };
                }

                var target = new SharpLinkServerDesiredSessionSnapshot(
                    _desiredSessionServerInstanceId,
                    targetGeneration,
                    targetConfiguration);
                if (_desiredSessionRolloutTestHook is { } hook)
                    await hook(target, _forceStopCts.Token).ConfigureAwait(false);
                await RequestRollingSessionRefreshAsync(targetGeneration, _forceStopCts.Token)
                    .ConfigureAwait(false);

                lock (_desiredSessionGate)
                {
                    if (!ReferenceEquals(_desiredSessionRolloutWorker, owner))
                        return;
                    if (_desiredSessionRollingGeneration != targetGeneration ||
                        _desiredSessionRolloutRequestEpoch != requestEpoch)
                    {
                        continue;
                    }
                    _desiredSessionRolloutWorker = null;
                    _desiredSessionRolloutTask = null;
                    return;
                }
            }
        }
        finally
        {
            lock (_desiredSessionGate)
            {
                if (ReferenceEquals(_desiredSessionRolloutWorker, owner))
                {
                    _desiredSessionRolloutWorker = null;
                    _desiredSessionRolloutTask = null;
                }
            }
        }
    }

    private void BindDesiredSessionSnapshot(RpcSession session, SharpLinkServerDesiredSessionSnapshot snapshot)
    {
        _sessionDesiredSnapshots[session.Id] = snapshot;
        session.OnDisconnected += _ => UnbindDesiredSessionSnapshot(session);
    }

    private void UnbindDesiredSessionSnapshot(RpcSession session)
        => _sessionDesiredSnapshots.TryRemove(session.Id, out _);

    private async ValueTask RequestRollingSessionRefreshAsync(
        ulong targetGeneration,
        CancellationToken cancellationToken)
    {
        if (targetGeneration == 0)
            return;
        var request = new ProtocolV2SessionRefreshRequested(
            _desiredSessionServerInstanceId,
            targetGeneration);
        foreach (var connection in _connectionRegistry.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CurrentState != ServerState.Running)
                return;
            var session = connection.Session;
            if (!_sessionDesiredSnapshots.TryGetValue(session.Id, out var pinned) ||
                pinned.ServerInstanceId != _desiredSessionServerInstanceId ||
                pinned.Generation >= targetGeneration ||
                !session.IsConnected ||
                (session.NegotiatedCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0)
            {
                continue;
            }

            try
            {
                await session.SendSessionRefreshRequestedWithBackpressureAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedConnectionTermination(exception, connection.ConnectionToken))
            {
            }
        }
    }

    private async Task RunSessionRefreshIfStaleAsync(
        RpcSession session,
        SharpLinkServerDesiredSessionSnapshot pinned)
    {
        try
        {
            await RequestSessionRefreshIfStaleAsync(session, pinned, session.LifetimeToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedConnectionTermination(exception, session.LifetimeToken))
        {
        }
        catch (Exception exception)
        {
            LogDeferredCleanupFailed(_logger, "SessionRefreshCatchUp", exception);
        }
    }

    private async ValueTask RequestSessionRefreshIfStaleAsync(
        RpcSession session,
        SharpLinkServerDesiredSessionSnapshot pinned,
        CancellationToken cancellationToken)
    {
        if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0)
            return;
        if (!TryCreateRollingSessionRefreshRequest(pinned, out var request))
            return;
        await session.SendSessionRefreshRequestedWithBackpressureAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool TryCreateRollingSessionRefreshRequest(
        SharpLinkServerDesiredSessionSnapshot pinned,
        out ProtocolV2SessionRefreshRequested request)
    {
        lock (_desiredSessionGate)
        {
            var rollingGeneration = _desiredSessionRollingGeneration;
            if (rollingGeneration == 0 ||
                pinned.ServerInstanceId != _desiredSessionServerInstanceId ||
                pinned.Generation >= rollingGeneration)
            {
                request = default;
                return false;
            }

            request = new ProtocolV2SessionRefreshRequested(
                _desiredSessionServerInstanceId,
                rollingGeneration);
            return true;
        }
    }

    internal bool TryCreateRollingSessionRefreshRequestForTesting(
        SharpLinkServerDesiredSessionSnapshot pinned,
        out ProtocolV2SessionRefreshRequested request)
        => TryCreateRollingSessionRefreshRequest(pinned, out request);
}
