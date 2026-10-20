namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly Dictionary<ClientConnection, ProtocolV2SessionRefreshRequested> _sessionRefreshDebt = [];
    private object? _sessionRefreshWorker;
    private Task? _sessionRefreshTask;

    // Deterministic review-race hooks. They are null in production and stay off the ordinary RPC path.
    // Only one reference on a production client; hook slots are allocated on test use.
    private Delegate?[]? _clientTestHookSlots;

    internal Action? _afterSessionRefreshEligibilitySwapTestHook
    {
        get => ReadClientTestHook<Action>(0);
        set => WriteClientTestHook(0, value);
    }

    internal Action<ClientConnection>? _beforeSessionRefreshEligibilityCommitTestHook
    {
        get => ReadClientTestHook<Action<ClientConnection>>(1);
        set => WriteClientTestHook(1, value);
    }

    internal Action<ClientConnection>? _afterSessionRefreshCommitReservationTestHook
    {
        get => ReadClientTestHook<Action<ClientConnection>>(2);
        set => WriteClientTestHook(2, value);
    }

    internal Action<ClientConnection>? _beforeSessionRefreshCutLockTestHook
    {
        get => ReadClientTestHook<Action<ClientConnection>>(3);
        set => WriteClientTestHook(3, value);
    }

    internal Action<ClientConnection, ClientConnection>? _beforeSessionRefreshCutPublicationTestHook
    {
        get => ReadClientTestHook<Action<ClientConnection, ClientConnection>>(4);
        set => WriteClientTestHook(4, value);
    }

    internal Action<ClientConnection, ClientConnection>? _afterSessionRefreshCutPublicationTestHook
    {
        get => ReadClientTestHook<Action<ClientConnection, ClientConnection>>(5);
        set => WriteClientTestHook(5, value);
    }

    internal Action<ClientConnection>? _beforeFatalFailurePublicationTestHook
    {
        get => ReadClientTestHook<Action<ClientConnection>>(6);
        set => WriteClientTestHook(6, value);
    }

    internal Action<ClientConnection>? _afterFatalFailurePublicationTestHook
    {
        get => ReadClientTestHook<Action<ClientConnection>>(7);
        set => WriteClientTestHook(7, value);
    }

    internal Action? _beforeSessionRefreshWorkerReleaseTestHook
    {
        get => ReadClientTestHook<Action>(8);
        set => WriteClientTestHook(8, value);
    }

    internal Action<ClientConnection>? _callAdmissionReservedTestHook
    {
        get => ReadClientTestHook<Action<ClientConnection>>(9);
        set => WriteClientTestHook(9, value);
    }

    internal Action? _beforeContractRoutePublicationTestHook
    {
        get => ReadClientTestHook<Action>(10);
        set => WriteClientTestHook(10, value);
    }

    private T? ReadClientTestHook<T>(int index) where T : Delegate
    {
        var slots = Volatile.Read(ref _clientTestHookSlots);
        return slots is null ? null : (T?)Volatile.Read(ref slots[index]);
    }

    private void WriteClientTestHook<T>(int index, T? callback) where T : Delegate
    {
        var slots = Volatile.Read(ref _clientTestHookSlots);
        if (slots is null)
        {
            if (callback is null)
                return;
            var allocated = new Delegate?[11];
            slots = Interlocked.CompareExchange(ref _clientTestHookSlots, allocated, null) ?? allocated;
        }
        Volatile.Write(ref slots[index], callback);
    }

    internal void NotifyCallAdmissionReservedForTest(ClientConnection connection)
        => _callAdmissionReservedTestHook?.Invoke(connection);

    internal void NotifyBeforeSessionRefreshEligibilityCommitForTest(ClientConnection connection)
        => _beforeSessionRefreshEligibilityCommitTestHook?.Invoke(connection);

    internal void NotifyAfterSessionRefreshCommitReservationForTest(ClientConnection connection)
        => _afterSessionRefreshCommitReservationTestHook?.Invoke(connection);

    internal void NotifyBeforeSessionRefreshCutLockForTest(ClientConnection connection)
        => _beforeSessionRefreshCutLockTestHook?.Invoke(connection);

    internal void NotifyBeforeSessionRefreshCutPublicationForTest(ClientConnection source, ClientConnection replacement)
        => _beforeSessionRefreshCutPublicationTestHook?.Invoke(source, replacement);

    internal void NotifyAfterSessionRefreshCutPublicationForTest(ClientConnection source, ClientConnection replacement)
        => _afterSessionRefreshCutPublicationTestHook?.Invoke(source, replacement);

    internal void NotifyBeforeFatalFailurePublicationForTest(ClientConnection connection)
        => _beforeFatalFailurePublicationTestHook?.Invoke(connection);

    internal void NotifyAfterFatalFailurePublicationForTest(ClientConnection connection)
        => _afterFatalFailurePublicationTestHook?.Invoke(connection);

    internal void TryAdvancePlannedSessionRefreshRetirement(ClientConnection connection)
    {
        if (!connection.HasPlannedSessionRefreshRetirement)
            return;
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
                ProtocolV2SessionRefreshRequested processedRequest = default;
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
                        if (source is null && CanPlanFixedRefreshLocked())
                        {
                            source = candidate;
                            processedRequest = pair.Value;
                        }
                    }
                    if (stale is not null)
                    {
                        for (var index = 0; index < stale.Count; index++)
                            _sessionRefreshDebt.Remove(stale[index]);
                    }
                    if (_sessionRefreshDebt.Count == 0)
                    {
                        _beforeSessionRefreshWorkerReleaseTestHook?.Invoke();
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
                    var completed = await ReplaceFixedSessionAsync(source, processedRequest, _shutdownCts.Token).ConfigureAwait(false);
                    if (completed)
                    {
                        lock (_poolGate)
                        {
                            if (_sessionRefreshDebt.TryGetValue(source, out var current) && current == processedRequest)
                                _sessionRefreshDebt.Remove(source);
                        }
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

    private static void CompleteSessionRefreshDebtLocked(
        Dictionary<ClientConnection, ProtocolV2SessionRefreshRequested> debt,
        ClientConnection source,
        ClientConnection replacement,
        ProtocolV2SessionRefreshRequested processedRequest)
    {
        if (!debt.Remove(source, out var current) || current == processedRequest)
            return;

        // The replacement handshake may predate a request received during this attempt.
        // Transfer that unfulfilled intent at the cut, before source cleanup can remove it.
        // A notification from the replacement's own server takes precedence across instances.
        if (debt.TryGetValue(replacement, out var replacementRequest) &&
            (replacementRequest.ServerInstanceId != current.ServerInstanceId ||
             replacementRequest.DesiredGeneration >= current.DesiredGeneration))
        {
            return;
        }
        debt[replacement] = current;
    }

    private void ReleaseFixedSessionRefreshWorkerLocked(object owner)
    {
        if (!ReferenceEquals(_sessionRefreshWorker, owner))
            return;
        _sessionRefreshWorker = null;
        _sessionRefreshTask = null;
    }

    private bool CanPlanFixedRefreshLocked()
    {
        var retiring = 0;
        foreach (var connection in _connections)
        {
            if (connection.State == ClientConnectionState.Draining || connection.HasPlannedSessionRefreshRetirement)
                retiring++;
        }
        // Every cut needs a slot: even an idle source can admit work while the replacement
        // connects. Planned sources retain physical sessions until those admitted calls drain.
        return retiring < _connectionPoolOptions.MaxConnections;
    }

    private async Task<bool> ReplaceFixedSessionAsync(
        ClientConnection source,
        ProtocolV2SessionRefreshRequested processedRequest,
        CancellationToken cancellationToken)
    {
        lock (_poolGate)
        {
            if (_poolStopping || _shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0)
                return true;
            if (!_connections.Contains(source) || !source.CanAcceptCalls)
                return true;
            if (!CanPlanFixedRefreshLocked())
                return false;
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        RpcSession? session = null;
        ITransportConnection? transport = null;
        ClientConnection? replacement = null;
        var replacementCommitReserved = false;
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
                if (sourceStillEligible && CanPlanFixedRefreshLocked())
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

                    if (!_connections.Contains(publishedReplacement) ||
                        !publishedReplacement.CanAcceptCalls ||
                        !publishedReplacement.TryReserveSessionRefreshCommit())
                    {
                        _connections.Remove(publishedReplacement);
                    }
                    else
                    {
                        replacementCommitReserved = true;
                        if (!publishedReplacement.TryCommitSessionRefreshRetirement(source))
                        {
                            // A fatal transition linearized before the eligibility cut while this
                            // attempt already held the admission reservation. Roll the replacement
                            // back and keep the healthy source selectable so the refresh debt retries.
                            _connections.Remove(publishedReplacement);
                        }
                        else
                        {
                            CompleteSessionRefreshDebtLocked(
                                _sessionRefreshDebt, source, publishedReplacement, processedRequest);
                            // Keep the deterministic cut before contract-route publication. Calls
                            // retaining the source-only route fail fast here; the replacement becomes
                            // eligible only when its own identity is published with the new topology.
                            _afterSessionRefreshEligibilitySwapTestHook?.Invoke();
                            PublishReadySnapshotLocked();
                            published = true;
                        }
                    }
                }
            }

            if (replacementCommitReserved)
            {
                publishedReplacement.ReleaseCallAdmissionReservation();
                replacementCommitReserved = false;
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
            if (replacementCommitReserved && replacement is not null)
                replacement.ReleaseCallAdmissionReservation();
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
                QueueConnectionCleanup(source, "SessionRefreshRetiredConnectionCleanup");
            }
        }
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
