namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{

    private static ValueTask DispatchStreamChunkAsync(RpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw CreateProtocolViolationException("StreamData stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        var streamPayload = payload.Slice(sizeof(ushort));
        return session.StreamManager.DispatchChunkAsync(requestId, streamId, streamPayload);
    }

    private void DispatchStreamComplete(
        ClientConnection connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        SharpLinkProtocolOptions limits)
    {
        var streamId = TryReadStreamId(ref payload);
        if ((flags & ProtocolV2FrameFlags.Error) == 0)
        {
            if (streamId == 0)
            {
                connection.PendingCalls.RequireNStudyAcceptance(requestId);
                connection.PendingCalls.TryComplete(
                    requestId,
                    PendingCallCompletionReason.RemoteStreamComplete);
            }
            else
                connection.Session.StreamManager.CompleteStream(requestId, streamId, exception: null);
            return;
        }
        var error = ProtocolV2PayloadCodec.ReadError(payload, flags, limits.MaxErrorMessageBytes);
        var exception = SharpLinkResourceExhaustion.CreateRemote(
            error.Code,
            error.DetailCode,
            error.Message);
        if (streamId == 0)
        {
            connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.RemoteStreamComplete,
                exception);
        }
        else
            connection.Session.StreamManager.CompleteStream(requestId, streamId, exception);
    }

    private static ushort TryReadStreamId(ref ReadOnlySequence<byte> payload)
    {
        var firstSpan = payload.FirstSpan;
        ushort streamId;
        if (firstSpan.Length >= sizeof(ushort))
        {
            streamId = BinaryPrimitives.ReadUInt16LittleEndian(firstSpan);
        }
        else
        {
            var reader = new SequenceReader<byte>(payload);
            if (!reader.TryReadLittleEndian(out short streamIdBits))
                throw CreateProtocolViolationException("StreamComplete stream ID is truncated.");
            streamId = unchecked((ushort)streamIdBits);
        }

        payload = payload.Slice(sizeof(ushort));
        return streamId;
    }

    private void HandleDisconnected(ClientConnection connection, Exception ex)
    {
        connection.ObserveFatalFailureForAdmission();
        if (_cluster is not null)
        {
            _cluster.HandleConnectionFailure(connection, ex);
            return;
        }

        if (!TryStartConnectionCleanup(connection, "DisconnectedConnectionCleanup", ex))
            return;

        var session = connection.Session;
        using var sessionScope = BeginSessionLogScope(_logger, session.Id);
        LogClientDisconnectedWithError(_logger, ex);

        if (ReadyConnectionCount != 0)
        {
            TransitionTo(SharpLinkConnectionState.Ready);
            EnsureReconnectLoop();
            return;
        }

        SharpLinkReconnectPolicy reconnectPolicy;
        long readyTimestamp;
        bool hasReadyTimestamp;
        lock (_stateGate)
        {
            reconnectPolicy = CaptureReconnectPolicy().Policy;
            readyTimestamp = _readyTimestamp;
            hasReadyTimestamp = _hasReconnectReadyTimestamp;
            _readyTimestamp = default;
            _hasReconnectReadyTimestamp = false;
        }
        if (HasReachedReconnectStableWindow(readyTimestamp, hasReadyTimestamp, reconnectPolicy))
            Volatile.Write(ref _reconnectDelayTicks, reconnectPolicy.InitialDelay.Ticks);
        EnsureReconnectLoop(publishRecoveryState: true);
    }

    internal void HandleConnectionFatalFailure(ClientConnection connection, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(exception);
        if (_cluster is not null)
        {
            _cluster.HandleConnectionFailure(connection, exception);
            return;
        }

        HandleDisconnected(connection, exception);
    }

    private void EnsureReconnectLoop(bool publishRecoveryState = false)
    {
        lock (_stateGate)
        {
            if (_shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0)
                return;
            if (publishRecoveryState && ReadyConnectionCount == 0)
                TransitionTo(SharpLinkConnectionState.Reconnecting);
            if (_reconnectTask is not { IsCompleted: false })
            {
                _reconnectTask = ReconnectLoopAsync();
                TrackFrameworkTask(_reconnectTask, "ReconnectLoop");
            }
            if (_reconnectSignal.CurrentCount == 0)
                _reconnectSignal.Release();
        }
    }

    private async Task ReconnectLoopAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                await _reconnectSignal.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                return;
            }

            while (!_shutdownCts.IsCancellationRequested &&
                   ReadyConnectionCount < _connectionPoolOptions.MinConnections)
            {
                var generation = CaptureReconnectPolicy();
                var baseDelay = ResolveReconnectDelay(Volatile.Read(ref _reconnectDelayTicks), generation.Policy);
                try
                {
                    if (!await WaitForReconnectDelayAsync(baseDelay, generation, _shutdownCts.Token).ConfigureAwait(false))
                        continue;
                    SharpLinkTelemetry.ReconnectAttempt();
                    await ConnectOneAsync(_shutdownCts.Token).ConfigureAwait(false);
                    PublishReadyState();
                    var completionPolicy = generation.Policy;
                    if (IsCurrentReconnectPolicyGeneration(generation))
                    {
                        Volatile.Write(
                            ref _reconnectDelayTicks,
                            ResolveReconnectCompletionDelay(baseDelay, reconnected: true, completionPolicy).Ticks);
                    }
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    using var scope = BeginSessionLogScope(_logger, "reconnect");
                    LogClientConnectionAttemptFailed(_logger, nameof(ReconnectLoopAsync), ex);
                    var completionPolicy = generation.Policy;
                    if (IsCurrentReconnectPolicyGeneration(generation))
                    {
                        Volatile.Write(
                            ref _reconnectDelayTicks,
                            ResolveReconnectCompletionDelay(baseDelay, reconnected: false, completionPolicy).Ticks);
                    }
                    TransitionTo(SharpLinkConnectionState.Reconnecting);
                }
            }
        }
    }

    private ClientConnection GetReadyConnection(
        RpcMethodDescriptor method,
        EndpointRetrySelectionState? retrySelection,
        AttemptOutcomeState? attemptOutcome,
        NStudyPreparedSite prepared)
    {
        // Reached only by a remote continuation, not by a local interceptor result.
        if (ReferenceEquals(prepared, NStudyPreparedSite.Unprepared))
            throw NStudyPreparedSite.Unavailable();

        if (_cluster is not null)
            return !prepared.IsBalanced
                ? ReserveClusterPreparedSite(prepared, attemptOutcome)
                : _cluster.GetReadyConnection(method, retrySelection, attemptOutcome, prepared: prepared);

        if (attemptOutcome is null || _fixedEndpoint is null || ReadyConnectionCount == 0)
            return ReservePreparedSite(prepared);

        var candidate = new SharpLinkEndpointCandidate(
            _fixedEndpoint,
            ReadyConnectionCount,
            ActiveClientCallCount,
            generation: 0);
        if (!attemptOutcome.TryAcquire(candidate))
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The configured endpoint admission policy rejected the endpoint.");
        try
        {
            return ReservePreparedSite(prepared);
        }
        catch (Exception exception)
        {
            attemptOutcome.CompleteLocalFailure(exception);
            throw;
        }
    }

    private ClientConnection ReserveClusterPreparedSite(NStudyPreparedSite prepared, AttemptOutcomeState? outcome)
    {
        // Without endpoint admission policy, the captured binding and the exact connection's
        // reservation gate are sufficient. Refresh, endpoint retirement and failure close that
        // gate; resolving the endpoint again would block an ordinary call on the topology lock.
        if (outcome is null)
            return ReservePreparedSite(prepared);

        if (_shutdownCts.IsCancellationRequested || State == SharpLinkConnectionState.Stopped)
            throw CreateConnectionClosedException("Client is not accepting new calls.");
        if (!prepared.TryGetConnection(out var owner) || prepared.Find(owner) is null ||
            !TryGetNStudyClusterCandidate(owner, out var candidate))
            throw NStudyPreparedSite.Unavailable();
        if (!outcome.TryAcquire(candidate))
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The endpoint admission policy rejected the prepared endpoint.");
        try
        {
            // Do not use the cluster picker or the Session refresh redirect here.
            return ReservePreparedSite(prepared);
        }
        catch (Exception exception)
        {
            outcome.CompleteLocalFailure(exception);
            throw;
        }
    }

    private ClientConnection ReservePreparedSite(NStudyPreparedSite prepared)
    {
        if (_shutdownCts.IsCancellationRequested || State == SharpLinkConnectionState.Stopped)
            throw CreateConnectionClosedException("Client is not accepting new calls.");
        if (prepared.IsBalanced)
            return ReserveFixedPoolPreparedCohort(prepared);

        if (!prepared.TryGetConnection(out var connection) ||
            prepared.Find(connection) is null ||
            !connection.TryReserveExactCallAdmission())
            throw NStudyPreparedSite.Unavailable();
        return connection;
    }

    private ClientConnection ReserveFixedPoolPreparedCohort(NStudyPreparedSite prepared)
    {
        for (var snapshotAttempt = 0; snapshotAttempt < 2; snapshotAttempt++)
        {
            ClientConnection? selected = null;
            var leastLoad = long.MaxValue;
            foreach (var candidate in Volatile.Read(ref _readyConnections))
            {
                if (!candidate.CanAcceptCalls || prepared.Find(candidate) is null)
                    continue;
                var load = (long)candidate.ActiveCallCount + candidate.CallAdmissionReservationCount;
                if (load < leastLoad)
                {
                    selected = candidate;
                    leastLoad = load;
                }
            }
            if (selected is null)
                break;
            if (selected.TryReserveExactCallAdmission())
            {
                // Expand the transport pool under load, without binding or using the new Session here.
                if (selected.ActiveCallCount != 0)
                    EnsureExpansion();
                return selected;
            }
        }

        throw NStudyPreparedSite.Unavailable();
    }

    private bool RemoveReadyConnection(ClientConnection connection)
    {
        lock (_poolGate)
        {
            if (!_connections.Remove(connection))
                return false;
            PublishReadySnapshotLocked();
            return true;
        }
    }

    private bool TryStartConnectionCleanup(
        ClientConnection connection,
        string operation,
        Exception? failure = null)
    {
        lock (_poolGate)
        {
            if (_poolStopping || !_connections.Remove(connection))
                return false;

            PublishReadySnapshotLocked();
            QueueConnectionCleanup(connection, operation, failure);
            return true;
        }
    }

    // Call while owning the topology gate that detaches the connection. Register before stop
    // can seal supervision, but never execute Fail/Dispose synchronously under that gate:
    // pending completion can wait for registration, which may need the same topology gate.
    private void QueueConnectionCleanup(ClientConnection connection, string operation, Exception? failure = null)
        => TrackFrameworkTask(Task.Run(async () =>
        {
            Exception? completionFailure = null;
            try
            {
                if (failure is not null)
                    connection.Fail(failure);
            }
            catch (Exception exception)
            {
                completionFailure = exception;
            }
            try { await DisposeDisconnectedConnectionAsync(connection).ConfigureAwait(false); }
            catch (Exception exception)
            {
                if (completionFailure is not null)
                    throw new AggregateException(completionFailure, exception);
                throw;
            }
            if (completionFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(completionFailure).Throw();
        }), operation);

    private void MarkConnectionDraining(ClientConnection connection)
    {
        if (!connection.MarkDraining())
            return;
        ReportGoAwayToCircuitBreaker(connection);
        if (_cluster is not null)
        {
            _cluster.MarkConnectionDraining(connection);
            return;
        }
        lock (_poolGate)
            PublishReadySnapshotLocked();

        RetireDrainingConnectionIfIdle(connection);

        // The peer is draining this connection, not stopping the client.
        EnsureReconnectLoop(publishRecoveryState: true);
    }

    private void ReportGoAwayToCircuitBreaker(ClientConnection connection)
    {
        if (_endpointAdmissionPolicy is not SharpLinkCircuitBreaker breaker)
            return;

        if (_cluster?.TryGetEndpointCandidate(connection, out var clusterEndpoint) == true)
        {
            breaker.ReportInfrastructureFailure(clusterEndpoint);
            return;
        }

        if (_fixedEndpoint is { } fixedEndpoint)
        {
            var endpoint = new SharpLinkEndpointCandidate(
                fixedEndpoint,
                ReadyConnectionCount,
                ActiveClientCallCount,
                generation: 0);
            breaker.ReportInfrastructureFailure(endpoint);
        }
    }

    internal void RetireDrainingConnectionIfIdle(ClientConnection connection)
    {
        if (_cluster is not null)
        {
            _cluster.RetireDrainingConnectionIfIdle(connection);
            return;
        }
        if (connection.State != ClientConnectionState.Draining ||
            connection.ActiveCallCount != 0 ||
            !TryStartConnectionCleanup(connection, "DrainingConnectionCleanup"))
        {
            return;
        }
    }

    private void EnsureExpansion()
    {
        if (ReadyConnectionCount >= _connectionPoolOptions.MaxConnections)
            return;

        lock (_stateGate)
        {
            if (_shutdownCts.IsCancellationRequested ||
                ReadyConnectionCount >= _connectionPoolOptions.MaxConnections ||
                _expansionTask is { IsCompleted: false })
            {
                return;
            }
            _expansionTask = ExpandOneAsync();
            TrackFrameworkTask(_expansionTask, "ConnectionPoolExpansion");
        }
    }

    private async Task ExpandOneAsync()
    {
        try
        {
            if (ReadyConnectionCount >= _connectionPoolOptions.MaxConnections)
                return;
            await ConnectOneAsync(_shutdownCts.Token).ConfigureAwait(false);
            PublishReadyState();
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            using var scope = BeginSessionLogScope(_logger, "pool-expand");
            LogClientConnectionAttemptFailed(_logger, nameof(ExpandOneAsync), ex);
            if (!_shutdownCts.IsCancellationRequested &&
                ReadyConnectionCount < _connectionPoolOptions.MinConnections)
            {
                TransitionTo(SharpLinkConnectionState.Reconnecting);
                EnsureReconnectLoop();
            }
        }
    }

    private static async Task DisposeDisconnectedConnectionAsync(ClientConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException)
        {
        }
    }
}
