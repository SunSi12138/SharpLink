namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly Dictionary<ClientConnection, ProtocolV2SessionRefreshRequested> _sessionRefreshDebt = [];
    private object? _sessionRefreshWorker;
    private Task? _sessionRefreshTask;

    // Deterministic review-race hooks. They are null in production and stay off the ordinary RPC path.
    internal Action? _afterSessionRefreshEligibilitySwapTestHook;
    internal Action? _beforeSessionRefreshWorkerReleaseTestHook;
    internal Action<ClientConnection>? _callAdmissionReservedTestHook;

    internal void NotifyCallAdmissionReservedForTest(ClientConnection connection)
        => Volatile.Read(ref _callAdmissionReservedTestHook)?.Invoke(connection);

    internal void TryAdvancePlannedSessionRefreshRetirement(ClientConnection connection)
    {
        if (_cluster is not null)
        {
            _cluster.TryAdvancePlannedSessionRefreshRetirement(connection);
            return;
        }
        TryAdvanceFixedSessionRefreshRetirement(connection);
    }

    private void HandleSessionRefreshRequest(
        RpcSession session,
        ProtocolV2SessionRefreshRequested request)
    {
        if (_cluster is not null)
        {
            _cluster.RequestSessionRefresh(session, request);
            return;
        }

        if (transportFactory is AnonymousPipeClientTransportFactory)
            return;

        lock (_poolGate)
        {
            if (_poolStopping || _shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0)
                return;

            ClientConnection? source = null;
            foreach (var connection in _connections)
            {
                if (ReferenceEquals(connection.Session, session))
                {
                    source = connection;
                    break;
                }
            }
            if (source is null || !source.CanAcceptCalls)
                return;

            if (_sessionRefreshDebt.TryGetValue(source, out var previous) &&
                previous.ServerInstanceId == request.ServerInstanceId &&
                request.DesiredGeneration <= previous.DesiredGeneration)
            {
                return;
            }

            _sessionRefreshDebt[source] = request;
            EnsureFixedSessionRefreshWorkerLocked();
        }
    }

    private void EnsureFixedSessionRefreshWorkerLocked()
    {
        if (_sessionRefreshWorker is not null)
            return;

        var owner = new object();
        _sessionRefreshWorker = owner;
        var task = RunFixedSessionRefreshRolloutAsync(owner);
        _sessionRefreshTask = task;
        TrackFrameworkTask(task, "SessionRefreshRollout");
    }

    private async Task RunFixedSessionRefreshRolloutAsync(object owner)
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                ClientConnection? source = null;
                lock (_poolGate)
                {
                    if (_poolStopping || Volatile.Read(ref _stopStarted) != 0)
                    {
                        ReleaseFixedSessionRefreshWorkerLocked(owner);
                        return;
                    }

                    List<ClientConnection>? stale = null;
                    foreach (var pair in _sessionRefreshDebt)
                    {
                        var candidate = pair.Key;
                        if (!_connections.Contains(candidate) ||
                            candidate.HasPlannedSessionRefreshRetirement ||
                            !candidate.CanAcceptCalls)
                        {
                            (stale ??= []).Add(candidate);
                            continue;
                        }
                        if (source is null && CanPlanFixedRefreshLocked(candidate))
                            source = candidate;
                    }
                    if (stale is not null)
                    {
                        for (var index = 0; index < stale.Count; index++)
                            _sessionRefreshDebt.Remove(stale[index]);
                    }
                    if (_sessionRefreshDebt.Count == 0)
                    {
                        Volatile.Read(ref _beforeSessionRefreshWorkerReleaseTestHook)?.Invoke();
                        ReleaseFixedSessionRefreshWorkerLocked(owner);
                        return;
                    }
                }

                if (source is null)
                {
                    await DelaySessionRefreshRetryAsync().ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await Task.Delay(Random.Shared.Next(10, 76), _shutdownCts.Token).ConfigureAwait(false);
                    var completed = await ReplaceFixedSessionAsync(source, _shutdownCts.Token).ConfigureAwait(false);
                    if (completed)
                    {
                        lock (_poolGate)
                            _sessionRefreshDebt.Remove(source);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    using var scope = BeginSessionLogScope(_logger, source.Session.Id);
                    LogClientConnectionAttemptFailed(_logger, nameof(RunFixedSessionRefreshRolloutAsync), exception);
                }

                await DelaySessionRefreshRetryAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_poolGate)
                ReleaseFixedSessionRefreshWorkerLocked(owner);
        }
    }

    private void ReleaseFixedSessionRefreshWorkerLocked(object owner)
    {
        if (!ReferenceEquals(_sessionRefreshWorker, owner))
            return;
        _sessionRefreshWorker = null;
        _sessionRefreshTask = null;
    }

    private bool CanPlanFixedRefreshLocked(ClientConnection source)
    {
        var retiring = 0;
        foreach (var connection in _connections)
        {
            if (connection.State == ClientConnectionState.Draining)
                retiring++;
        }
        return retiring < _connectionPoolOptions.MaxConnections || source.ActiveCallCount == 0;
    }

    private async Task<bool> ReplaceFixedSessionAsync(
        ClientConnection source,
        CancellationToken cancellationToken)
    {
        lock (_poolGate)
        {
            if (_poolStopping || _shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0)
                return true;
            if (!_connections.Contains(source) || !source.CanAcceptCalls)
                return true;
            if (!CanPlanFixedRefreshLocked(source))
                return false;
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        RpcSession? session = null;
        ITransportConnection? transport = null;
        ClientConnection? replacement = null;
        try
        {
            transport = await ConnectTransportAsync(transportFactory, attemptCts.Token).ConfigureAwait(false);
            if (transport is ITransportSecurityInfo securityInfo)
                LogTlsEstablished(_logger, securityInfo.Protocol, securityInfo.CipherSuite);
            session = new RpcSession(
                transport,
                new RpcSessionCreationOptions(
                    RpcSessionRole.Client,
                    _runtimeContext,
                    _rpcSessionFlushOptions,
                    _requestCompressionPolicy));
            transport = null;

            await CompleteHandshakeAsync(session, attemptCts.Token, cancellationToken).ConfigureAwait(false);
            if (_beforeReadyPublicationTestHook is not null)
                await _beforeReadyPublicationTestHook(attemptCts.Token).ConfigureAwait(false);

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            replacement = new ClientConnection(
                this,
                session,
                sessionCts,
                _protocolOptions.MaxPendingRequestsPerConnection,
                _runtimeContext);
            var publishedReplacement = replacement;
            var readySession = publishedReplacement.Session;
            readySession.OnDisconnected += exception => HandleDisconnected(
                publishedReplacement,
                exception ?? CreateConnectionClosedException("Transport closed."));

            var published = false;
            var sourceStillEligible = false;
            lock (_poolGate)
            {
                if (_poolStopping || _shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0)
                    throw CreateConnectionClosedException("Client stopped while refreshing a session.");

                sourceStillEligible = _connections.Contains(source) && source.CanAcceptCalls;
                if (sourceStillEligible && CanPlanFixedRefreshLocked(source))
                {
                    _connections.Add(publishedReplacement);
                    try
                    {
                        ReconcileResponseCompressionPreferenceAfterReadyPublication(readySession);
                    }
                    catch
                    {
                        _connections.Remove(publishedReplacement);
                        throw;
                    }

                    readySession.NotifyConnected();
                    TrackFrameworkTask(
                        RunHeartbeatSendLoopAsync(publishedReplacement, sessionCts.Token),
                        "HeartbeatSendLoop");
                    TrackFrameworkTask(
                        RunProcessRequestLoopAsync(publishedReplacement, sessionCts.Token),
                        "ProcessRequestLoop");

                    source.BeginPlannedSessionRefreshRetirement(publishedReplacement);
                    // Deliberately place the deterministic cut hook before immutable snapshot
                    // publication. A reader retaining the old source-only snapshot must redirect
                    // through source admission to this already-Ready replacement instead of seeing
                    // a transient Unavailable gap.
                    Volatile.Read(ref _afterSessionRefreshEligibilitySwapTestHook)?.Invoke();
                    PublishReadySnapshotLocked();
                    published = true;
                }
            }

            if (!published)
            {
                session = null;
                await publishedReplacement.DisposeAsync().ConfigureAwait(false);
                replacement = null;
                return !sourceStillEligible;
            }

            session = null;
            replacement = null;
            TryAdvanceFixedSessionRefreshRetirement(source);
            PublishReadyState();
            return true;
        }
        catch (Exception exception)
        {
            await RethrowAfterFailedConnectionCleanupAsync(
                exception,
                transport,
                replacement,
                session).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }

    private void TryAdvanceFixedSessionRefreshRetirement(ClientConnection source)
    {
        var dispose = false;
        lock (_poolGate)
        {
            if (!source.HasPlannedSessionRefreshRetirement)
                return;
            if (!_connections.Contains(source))
            {
                source.CompletePlannedSessionRefreshRetirement();
                return;
            }
            // Pending capacity, untracked-call ownership, and the selection-to-registration
            // reservation are the formal admission boundary.
            if (source.CallAdmissionReservationCount != 0 || source.ActiveCallCount != 0)
                return;

            source.CompletePlannedSessionRefreshRetirement();
            _ = source.MarkDraining();
            if (_connections.Remove(source))
            {
                PublishReadySnapshotLocked();
                dispose = true;
            }
        }

        if (dispose)
            TrackFrameworkTask(DisposeDisconnectedConnectionAsync(source), "SessionRefreshRetiredConnectionCleanup");
    }

    private async Task DelaySessionRefreshRetryAsync()
    {
        try
        {
            await Task.Delay(Random.Shared.Next(250, 751), _shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
    }
}
