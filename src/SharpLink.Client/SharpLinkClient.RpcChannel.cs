


namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private void SendRpcCall(
        RpcSession session,
        long interfaceHash,
        long methodHash,
        long requestId,
        ProtocolV2FrameFlags flags,
        Action<IBufferWriter<byte>>? payloadWriter,
        DateTimeOffset? deadline = null,
        SharpLinkMetadata? metadata = null)
    {
        var hasMetadata = metadata is { Count: > 0 };
        var metadataLength = 0;
        if (deadline is not null)
            flags |= ProtocolV2FrameFlags.HasDeadline;
        if (hasMetadata)
        {
            if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.Unimplemented,
                    "The connected server did not negotiate request metadata support.");
            }
            metadataLength = ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata!);
            if (metadataLength > _protocolOptions.MaxMetadataBytes)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    $"Request metadata exceeds {_protocolOptions.MaxMetadataBytes} bytes.");
            }
            flags |= ProtocolV2FrameFlags.HasMetadata;
        }

        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.Request, flags, unchecked((ulong)requestId)))
            {
                var span = writer.GetSpan(ProtocolV2Constants.RequestPrefixBytes);
                BinaryPrimitives.WriteInt64LittleEndian(span, interfaceHash);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], methodHash);
                writer.Advance(ProtocolV2Constants.RequestPrefixBytes);
                if (deadline is { } absoluteDeadline)
                {
                    var deadlineSpan = writer.GetSpan(sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(
                        deadlineSpan,
                        absoluteDeadline.ToUnixTimeMilliseconds());
                    writer.Advance(sizeof(long));
                }
                if (hasMetadata)
                {
                    ProtocolV2PayloadCodec.WriteVarUInt32(writer, checked((uint)metadataLength));
                    ProtocolV2PayloadCodec.WriteMetadata(writer, metadata!);
                }
                payloadWriter?.Invoke(writer);
            }

            // SendPacket takes ownership even when enqueueing detects a terminal session.
            ownsWriter = false;
            session.SendPacket(writer);
        }
        finally
        {
            if (ownsWriter)
                _runtimeContext.Buffers.Return(writer);
        }
    }

    public Task SendClientStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        CancellationToken cancellationToken = default)
        => Task.FromException(new InvalidOperationException(
            "Client streams must use the connection-bound sink supplied to generated stream writers."));

    private static ValueTask DispatchStreamChunkAsync(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
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
                connection.PendingCalls.TryComplete(
                    requestId,
                    PendingCallCompletionReason.RemoteStreamComplete);
            }
            else
                connection.Session.StreamManager.CompleteStream(requestId, streamId, exception: null);
            return;
        }
        var error = ProtocolV2PayloadCodec.ReadError(payload, flags, limits.MaxErrorMessageBytes);
        var exception = new SharpLinkException(error.Code, error.Message);
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
        if (!RemoveReadyConnection(connection))
            return;

        var session = connection.Session;
        using var sessionScope = BeginSessionLogScope(_logger, session.Id);
        LogClientDisconnectedWithError(_logger, ex);

        connection.Fail(ex);
        TrackBackgroundTask(DisposeDisconnectedConnectionAsync(connection));

        if (_shutdownCts.IsCancellationRequested ||
            State is SharpLinkConnectionState.Stopped)
            return;

        if (ReadyConnectionCount != 0)
        {
            TransitionTo(SharpLinkConnectionState.Ready);
            EnsureReconnectLoop();
            return;
        }

        ResetReadySignal();
        var stableTicks = Stopwatch.GetTimestamp() - Volatile.Read(ref _readyTimestamp);
        if (stableTicks >= 30L * Stopwatch.Frequency)
            Volatile.Write(ref _reconnectDelayMilliseconds, 100);
        TransitionTo(SharpLinkConnectionState.Reconnecting);
        EnsureReconnectLoop();
    }

    private void EnsureReconnectLoop()
    {
        lock (_stateGate)
        {
            if (_shutdownCts.IsCancellationRequested)
                return;
            if (_reconnectTask is not { IsCompleted: false })
                _reconnectTask = ReconnectLoopAsync();
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
                var baseDelay = Volatile.Read(ref _reconnectDelayMilliseconds);
                var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
                var delay = TimeSpan.FromMilliseconds(baseDelay * jitter);
                try
                {
                    await Task.Delay(delay, _shutdownCts.Token).ConfigureAwait(false);
                    SharpLinkTelemetry.ReconnectAttempt();
                    await ConnectOneAsync(_shutdownCts.Token).ConfigureAwait(false);
                    PublishReadyState();
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    using var scope = BeginSessionLogScope(_logger, "reconnect");
                    LogClientBackgroundLoopUnhandledException(_logger, nameof(ReconnectLoopAsync), ex);
                    var nextDelay = Math.Min(baseDelay * 2, 5000);
                    Volatile.Write(ref _reconnectDelayMilliseconds, nextDelay);
                    TransitionTo(SharpLinkConnectionState.Reconnecting);
                }
            }
        }
    }

    private ClientConnection GetReadyConnection()
    {
        if (_cluster is not null)
            return _cluster.GetReadyConnection(method: null, retrySelection: null, attemptOutcome: null);

        var connections = Volatile.Read(ref _readyConnections);
        if (!_shutdownCts.IsCancellationRequested && connections.Length != 0)
        {
            ClientConnection selected;
            if (connections.Length == 1)
            {
                selected = connections[0];
            }
            else
            {
                var first = Random.Shared.Next(connections.Length);
                var second = Random.Shared.Next(connections.Length - 1);
                if (second >= first)
                    second++;
                selected = SelectLeastLoaded(connections, first, second);
            }

            if (selected.CanAcceptCalls)
            {
                if (selected.ActiveCallCount != 0)
                    EnsureExpansion();
                return selected;
            }
        }
        if (_shutdownCts.IsCancellationRequested || State == SharpLinkConnectionState.Stopped)
            throw CreateConnectionClosedException("Client is not accepting new calls.");
        throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink connection is ready.");
    }

    private ClientConnection GetReadyConnection(
        RpcMethodDescriptor method,
        EndpointRetrySelectionState? retrySelection,
        AttemptOutcomeState? attemptOutcome)
    {
        if (_cluster is not null)
            return _cluster.GetReadyConnection(method, retrySelection, attemptOutcome);

        if (attemptOutcome is null || _fixedEndpoint is null)
            return GetReadyConnection();

        if (ReadyConnectionCount == 0)
            return GetReadyConnection();

        var candidate = new SharpLinkEndpointCandidate(
            _fixedEndpoint,
            ReadyConnectionCount,
            ActiveClientCallCount,
            generation: 0);
        if (!attemptOutcome.TryAcquire(candidate))
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The configured endpoint admission policy rejected the endpoint.");
        try
        {
            var connection = GetReadyConnection();
            attemptOutcome.SetConnection(connection);
            return connection;
        }
        catch (Exception exception)
        {
            attemptOutcome.CompleteLocalFailure(exception);
            throw;
        }
    }

    internal static ClientConnection SelectLeastLoaded(
        ClientConnection[] connections,
        int first,
        int second)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(second);
        if ((uint)first >= (uint)connections.Length || (uint)second >= (uint)connections.Length)
            throw new ArgumentOutOfRangeException(nameof(first));
        var firstConnection = connections[first];
        var secondConnection = connections[second];
        return firstConnection.ActiveCallCount <= secondConnection.ActiveCallCount
            ? firstConnection
            : secondConnection;
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

        if (ReadyConnectionCount != 0)
        {
            EnsureReconnectLoop();
            return;
        }
        ResetReadySignal();
        TransitionTo(SharpLinkConnectionState.Draining);
        EnsureReconnectLoop();
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
            !RemoveReadyConnection(connection))
        {
            return;
        }

        TrackBackgroundTask(DisposeDisconnectedConnectionAsync(connection));
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
            LogClientBackgroundLoopUnhandledException(_logger, nameof(ExpandOneAsync), ex);

            // Expansion is opportunistic while the pool still has a ready connection, but
            // that connection can start draining while ConnectOneAsync is in flight. Once
            // the failed expansion observes that the pool fell below its minimum it must
            // hand ownership to the persistent reconnect worker. Otherwise a coalesced
            // reconnect signal can leave the client permanently stranded with zero ready
            // connections after a rolling restart.
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
