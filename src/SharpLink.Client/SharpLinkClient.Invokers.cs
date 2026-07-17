namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        cancellationToken.ThrowIfCancellationRequested();
        if (SharpLinkTelemetry.ClientCallsEnabled)
        {
            return InvokeUnaryWithTelemetryAsync(
                method, request, requestCodec, responseCodec, options, cancellationToken);
        }
        if (_clientInterceptors.Length != 0)
        {
            return InvokeUnaryInterceptedAsync(
                method, request, requestCodec, responseCodec, options, cancellationToken);
        }
        var control = ResolveCallControl(
            options,
            includeClientDefault: true,
            method.HasMethodTimeout,
            method.MethodTimeout);
        return InvokeUnaryCoreAsync(
            method.ContractId,
            method.MethodId,
            method.HasResponsePayload,
            request,
            requestCodec,
            responseCodec,
            control,
            cancellationToken);
    }

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        in TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        cancellationToken.ThrowIfCancellationRequested();
        if (SharpLinkTelemetry.ClientCallsEnabled)
        {
            return InvokeOneWayWithTelemetryAsync(
                method, request, requestCodec, streams, options, cancellationToken);
        }
        if (_clientInterceptors.Length != 0)
        {
            return InvokeOneWayInterceptedAsync(
                method, request, requestCodec, streams, options, cancellationToken);
        }
        var control = ResolveCallControl(
            options,
            includeClientDefault: false,
            method.HasMethodTimeout,
            method.MethodTimeout);
        return InvokeOneWayCoreAsync(
            method.ContractId,
            method.MethodId,
            method.HasClientStreams,
            request,
            requestCodec,
            streams,
            control,
            cancellationToken);
    }

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        cancellationToken.ThrowIfCancellationRequested();
        if (SharpLinkTelemetry.ClientCallsEnabled)
        {
            return InvokeClientStreamingWithTelemetryAsync(
                method, request, requestCodec, responseCodec, streams, options, cancellationToken);
        }
        if (_clientInterceptors.Length != 0)
        {
            return InvokeClientStreamingInterceptedAsync(
                method, request, requestCodec, responseCodec, streams, options, cancellationToken);
        }
        var control = ResolveCallControl(
            options,
            includeClientDefault: false,
            method.HasMethodTimeout,
            method.MethodTimeout);
        return InvokeClientStreamingCoreAsync(
            method.ContractId,
            method.MethodId,
            method.HasResponsePayload,
            request,
            requestCodec,
            responseCodec,
            streams,
            control,
            cancellationToken);
    }

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        if (SharpLinkTelemetry.ClientCallsEnabled)
        {
            return InvokeServerStreamingWithTelemetry(
                method, request, requestCodec, responseCodec, options, cancellationToken);
        }
        if (_clientInterceptors.Length != 0)
        {
            return InvokeServerStreamingIntercepted(
                method, request, requestCodec, responseCodec, options, cancellationToken);
        }
        return InvokeServerStreamingCore(method, request, requestCodec, responseCodec, options, cancellationToken);
    }

    private IAsyncEnumerable<TResponse> InvokeServerStreamingCore<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
    {
        var control = ResolveCallControl(
            options,
            includeClientDefault: false,
            method.HasMethodTimeout,
            method.MethodTimeout);
        var dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(cancellationToken, responseCodec);
        TrackBackgroundTask(StartServerStreamingInvokerAsync(
            dispatcher,
            method.ContractId,
            method.MethodId,
            request,
            requestCodec,
            control,
            cancellationToken));
        return dispatcher;
    }

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        if (SharpLinkTelemetry.ClientCallsEnabled)
        {
            return InvokeDuplexStreamingWithTelemetry(
                method, request, requestCodec, responseCodec, streams, options, cancellationToken);
        }
        if (_clientInterceptors.Length != 0)
        {
            return InvokeDuplexStreamingIntercepted(
                method, request, requestCodec, responseCodec, streams, options, cancellationToken);
        }
        return InvokeDuplexStreamingCore(
            method, request, requestCodec, responseCodec, streams, options, cancellationToken);
    }

    private IAsyncEnumerable<TResponse> InvokeDuplexStreamingCore<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var control = ResolveCallControl(
            options,
            includeClientDefault: false,
            method.HasMethodTimeout,
            method.MethodTimeout);
        var dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(cancellationToken, responseCodec);
        TrackBackgroundTask(StartDuplexStreamingInvokerAsync(
            dispatcher,
            method.ContractId,
            method.MethodId,
            request,
            requestCodec,
            streams,
            control,
            cancellationToken));
        return dispatcher;
    }

    private async ValueTask<TResponse> InvokeUnaryCoreAsync<TRequest, TResponse>(
        long contractId,
        long methodId,
        bool hasResponsePayload,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        ClientConnection connection;
        long requestId;
        RpcRequestOperation<TResponse> operation;
        if (!control.WaitForReady)
        {
            connection = GetReadyConnection();
            operation = connection.PendingCalls.Rent(
                responseCodec,
                PendingCallKind.Unary,
                control.DeadlineTimestamp,
                cancellationToken,
                out requestId);
        }
        else
        {
            connection = await GetReadyConnectionAsync(
                waitForReady: true,
                control.Deadline,
                cancellationToken).ConfigureAwait(false);
            var lease = await connection.PendingCalls.RentAsync(
                responseCodec,
                PendingCallKind.Unary,
                control.DeadlineTimestamp,
                waitForSlot: true,
                control.Deadline,
                cancellationToken).ConfigureAwait(false);
            requestId = lease.Id;
            operation = lease.Operation;
        }
        var flags = hasResponsePayload
            ? ProtocolV2FrameFlags.HasReturn
            : ProtocolV2FrameFlags.None;
        if (cancellationToken.CanBeCanceled || control.Deadline is not null)
            flags |= ProtocolV2FrameFlags.Cancellable;

        try
        {
            if (connection.PendingCalls.Contains(requestId))
            {
                SendRpcCall(
                    connection.Session,
                    contractId,
                    methodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata);
            }
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.SendFailure,
                exception);
        }

        return await operation.AsValueTask().ConfigureAwait(false);
    }

    private async ValueTask InvokeOneWayCoreAsync<TRequest, TStreams>(
        long contractId,
        long methodId,
        bool hasClientStreams,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var connection = control.WaitForReady
            ? await GetReadyConnectionAsync(
                waitForReady: true,
                control.Deadline,
                cancellationToken).ConfigureAwait(false)
            : GetReadyConnection();
        var flags = ProtocolV2FrameFlags.OneWay;
        if (hasClientStreams && (cancellationToken.CanBeCanceled || control.Deadline is not null))
            flags |= ProtocolV2FrameFlags.Cancellable;

        var oneWayStreamLease = hasClientStreams
            ? connection.PendingCalls.RegisterOneWayClientStream(
                control.DeadlineTimestamp,
                cancellationToken)
            : default;
        var requestId = hasClientStreams
            ? oneWayStreamLease.Id
            : connection.PendingCalls.AllocateRequestId();
        var streamCancellationToken = hasClientStreams
            ? connection.PendingCalls.GetProducerCancellationToken(requestId)
            : CancellationToken.None;
        if (hasClientStreams && !connection.PendingCalls.Contains(requestId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CreateDeadlineExceededException();
        }
        if (!hasClientStreams && !connection.TryBeginUntrackedCall())
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The selected connection is draining.");
        try
        {
            try
            {
                SendRpcCall(
                    connection.Session,
                    contractId,
                    methodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata);
                if (hasClientStreams)
                {
                    await streams.WriteAsync(connection, requestId, streamCancellationToken).ConfigureAwait(false);
                    connection.PendingCalls.TryComplete(
                        requestId,
                        PendingCallCompletionReason.LocalStreamComplete);
                    _ = await oneWayStreamLease.Operation.AsValueTask().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                if (hasClientStreams)
                {
                    connection.PendingCalls.TryComplete(
                        requestId,
                        PendingCallCompletionReason.SendFailure,
                        exception);
                    _ = await oneWayStreamLease.Operation.AsValueTask().ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            if (!hasClientStreams)
                connection.EndUntrackedCall();
        }
    }

    private async ValueTask<TResponse> InvokeClientStreamingCoreAsync<TRequest, TResponse, TStreams>(
        long contractId,
        long methodId,
        bool hasResponsePayload,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ClientConnection connection;
        long requestId;
        RpcRequestOperation<TResponse> operation;
        if (!control.WaitForReady)
        {
            connection = GetReadyConnection();
            operation = connection.PendingCalls.Rent(
                responseCodec,
                PendingCallKind.ClientStreaming,
                control.DeadlineTimestamp,
                cancellationToken,
                out requestId);
        }
        else
        {
            connection = await GetReadyConnectionAsync(
                waitForReady: true,
                control.Deadline,
                cancellationToken).ConfigureAwait(false);
            var lease = await connection.PendingCalls.RentAsync(
                responseCodec,
                PendingCallKind.ClientStreaming,
                control.DeadlineTimestamp,
                waitForSlot: true,
                control.Deadline,
                cancellationToken).ConfigureAwait(false);
            requestId = lease.Id;
            operation = lease.Operation;
        }
        var flags = hasResponsePayload
            ? ProtocolV2FrameFlags.HasReturn | ProtocolV2FrameFlags.Cancellable
            : ProtocolV2FrameFlags.Cancellable;
        var streamCancellationToken = connection.PendingCalls.GetProducerCancellationToken(requestId);
        try
        {
            if (connection.PendingCalls.Contains(requestId))
            {
                SendRpcCall(
                    connection.Session,
                    contractId,
                    methodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata);
                TrackBackgroundTask(RunGeneratedClientStreamsAsync(
                    connection,
                    streams,
                    requestId,
                    streamCancellationToken));
            }
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.SendFailure,
                exception);
        }

        return await operation.AsValueTask().ConfigureAwait(false);
    }

    private async Task RunGeneratedClientStreamsAsync<TStreams>(
        ClientConnection connection,
        TStreams streams,
        long requestId,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        try
        {
            await streams.WriteAsync(connection, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.SendFailure,
                exception);
        }
    }

    private async Task StartServerStreamingInvokerAsync<TRequest, TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        long contractId,
        long methodId,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        ClientConnection? connection = null;
        var requestId = 0L;
        try
        {
            var registration = await PrepareGeneratedServerStreamAsync(
                dispatcher,
                PendingCallKind.ServerStreaming,
                control,
                cancellationToken).ConfigureAwait(false);
            connection = registration.Connection;
            requestId = registration.RequestId;
            SendRpcCall(
                connection.Session,
                contractId,
                methodId,
                requestId,
                cancellationToken.CanBeCanceled || control.Deadline is not null
                    ? ProtocolV2FrameFlags.Cancellable
                    : ProtocolV2FrameFlags.None,
                request,
                requestCodec,
                control.Deadline,
                control.Metadata);
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStream(dispatcher, connection, requestId, exception);
        }
    }

    private async Task StartDuplexStreamingInvokerAsync<TRequest, TResponse, TStreams>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        long contractId,
        long methodId,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ClientConnection? connection = null;
        var requestId = 0L;
        try
        {
            var registration = await PrepareGeneratedServerStreamAsync(
                dispatcher,
                PendingCallKind.DuplexStreaming,
                control,
                cancellationToken).ConfigureAwait(false);
            connection = registration.Connection;
            requestId = registration.RequestId;
            var streamCancellationToken = connection.PendingCalls.GetProducerCancellationToken(requestId);
            SendRpcCall(
                connection.Session,
                contractId,
                methodId,
                requestId,
                ProtocolV2FrameFlags.Cancellable,
                request,
                requestCodec,
                control.Deadline,
                control.Metadata);
            await streams.WriteAsync(connection, requestId, streamCancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStream(dispatcher, connection, requestId, exception);
        }
    }

    private async ValueTask<StreamCallRegistration> PrepareGeneratedServerStreamAsync<TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        PendingCallKind kind,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var connection = await GetReadyConnectionAsync(
            control.WaitForReady,
            control.Deadline,
            cancellationToken).ConfigureAwait(false);
        var requestId = connection.PendingCalls.RegisterStream(
            kind,
            dispatcher,
            control.DeadlineTimestamp,
            cancellationToken);
        if (!connection.PendingCalls.Contains(requestId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CreateDeadlineExceededException();
        }
        dispatcher.SetConsumerAbandonedCallback(connection.ConsumerAbandonedCallback, requestId);
        connection.Session.StreamManager.Register(requestId, 0, dispatcher);
        return new StreamCallRegistration(connection, requestId);
    }

    private void CompleteFailedGeneratedStream<TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        ClientConnection? connection,
        long requestId,
        Exception exception)
    {
        if (connection is not null && requestId != 0)
        {
            connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.SendFailure,
                exception);
        }
        else
        {
            dispatcher.Complete(exception);
        }
    }

    private readonly record struct StreamCallRegistration(
        ClientConnection Connection,
        long RequestId);

    private void SendRpcCall<TRequest>(
        RpcSession session,
        long contractId,
        long methodId,
        long requestId,
        ProtocolV2FrameFlags flags,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata)
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
                       ProtocolV2FrameType.Request,
                       flags,
                       unchecked((ulong)requestId)))
            {
                var span = writer.GetSpan(ProtocolV2Constants.RequestPrefixBytes);
                BinaryPrimitives.WriteInt64LittleEndian(span, contractId);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], methodId);
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
                requestCodec.Serialize(request, writer);
            }

            ownsWriter = false;
            session.SendPacket(writer);
        }
        finally
        {
            if (ownsWriter)
                _runtimeContext.Buffers.Return(writer);
        }
    }
}
