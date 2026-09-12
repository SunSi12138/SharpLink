namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private readonly Lock _desiredSessionGate = new();
    private readonly Guid _desiredSessionServerInstanceId = Guid.NewGuid();
    private SharpLinkServerDesiredSessionSnapshot? _desiredSession;
    private readonly AsyncLocal<SharpLinkServerDesiredSessionSnapshot?> _acceptedDesiredSession = new();
    private readonly ConcurrentDictionary<long, SharpLinkServerDesiredSessionSnapshot> _sessionDesiredSnapshots = new();

    public SharpLinkServerDesiredSessionSnapshot DesiredSession => CaptureDesiredSession();

    public async ValueTask<SharpLinkServerDesiredSessionSnapshot> PublishDesiredSessionAsync(
        SharpLinkServerDesiredSessionConfiguration configuration,
        SharpLinkSessionRolloutMode rolloutMode = SharpLinkSessionRolloutMode.FutureOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (rolloutMode is not SharpLinkSessionRolloutMode.FutureOnly and
            not SharpLinkSessionRolloutMode.RollingRefresh)
            throw new ArgumentOutOfRangeException(nameof(rolloutMode));
        if (configuration.MaxFramePayloadBytes is < SharpLinkProtocolOptions.MinMaxFramePayloadBytes ||
            configuration.MaxFramePayloadBytes > _protocolOptions.MaxFramePayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                $"MaxFramePayloadBytes must be between {SharpLinkProtocolOptions.MinMaxFramePayloadBytes} and the build-time hard ceiling {_protocolOptions.MaxFramePayloadBytes} bytes.");
        }

        SharpLinkServerDesiredSessionSnapshot published;
        lock (_desiredSessionGate)
        {
            EnsureDesiredSessionPublicationAllowed();
            var current = GetOrCreateDesiredSessionLocked();
            if (current.Configuration.MaxFramePayloadBytes == configuration.MaxFramePayloadBytes)
                return current;

            published = new SharpLinkServerDesiredSessionSnapshot(
                _desiredSessionServerInstanceId,
                checked(current.Generation + 1),
                configuration with { });
            _desiredSession = published;
        }

        if (rolloutMode == SharpLinkSessionRolloutMode.RollingRefresh)
            await RequestRollingSessionRefreshAsync(published, cancellationToken).ConfigureAwait(false);
        return published;
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

    private void EnsureDesiredSessionPublicationAllowed()
    {
        if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            throw new InvalidOperationException("Desired session configuration cannot be published after server shutdown has started.");
    }

    private async Task HandleAcceptedConnectionAsync(
        ITransportConnection acceptedConnection,
        ServerConnectionAdmission.Lease connectionLease,
        SharpLinkServerDesiredSessionSnapshot desiredSession,
        CancellationToken cancellationToken)
    {
        var previous = _acceptedDesiredSession.Value;
        _acceptedDesiredSession.Value = desiredSession;
        try
        {
            await HandleAcceptedConnectionAsync(acceptedConnection, connectionLease, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _acceptedDesiredSession.Value = previous;
        }
    }

    private SharpLinkServerDesiredSessionSnapshot GetAcceptedDesiredSession()
        => _acceptedDesiredSession.Value ??
           throw new InvalidOperationException("Accepted connection is missing its pinned desired-session snapshot.");

    private void BindDesiredSessionSnapshot(RpcSession session, SharpLinkServerDesiredSessionSnapshot snapshot)
    {
        _sessionDesiredSnapshots[session.Id] = snapshot;
        session.OnDisconnected += _ => UnbindDesiredSessionSnapshot(session);
    }

    private void UnbindDesiredSessionSnapshot(RpcSession session)
        => _sessionDesiredSnapshots.TryRemove(session.Id, out _);

    private async ValueTask RequestRollingSessionRefreshAsync(
        SharpLinkServerDesiredSessionSnapshot desired,
        CancellationToken cancellationToken)
    {
        var request = new ProtocolV2SessionRefreshRequested(desired.ServerInstanceId, desired.Generation);
        foreach (var connection in _connectionRegistry.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CurrentState != ServerState.Running)
                return;
            var session = connection.Session;
            if (!_sessionDesiredSnapshots.TryGetValue(session.Id, out var pinned) ||
                pinned.ServerInstanceId != desired.ServerInstanceId ||
                pinned.Generation >= desired.Generation ||
                !session.IsConnected ||
                (session.NegotiatedCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0)
                continue;

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
            // A best-effort administrative catch-up must not fault an otherwise healthy server.
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
        var current = CaptureDesiredSession();
        if (current.ServerInstanceId != pinned.ServerInstanceId || current.Generation <= pinned.Generation)
            return;
        await session.SendSessionRefreshRequestedWithBackpressureAsync(
            new ProtocolV2SessionRefreshRequested(current.ServerInstanceId, current.Generation),
            cancellationToken).ConfigureAwait(false);
    }
}
