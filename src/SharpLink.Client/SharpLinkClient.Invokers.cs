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
        var control = ResolveCallControl(
            options,
            includeClientDefault: false,
            method.HasMethodTimeout,
            method.MethodTimeout);
        var requestId = _requestManager.AllocateRequestId();
        _serverStreamRequestIds.Add(requestId);
        var dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(cancellationToken, responseCodec);
        TrackBackgroundTask(StartServerStreamingInvokerAsync(
            dispatcher,
            method.ContractId,
            method.MethodId,
            requestId,
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
        var control = ResolveCallControl(
            options,
            includeClientDefault: false,
            method.HasMethodTimeout,
            method.MethodTimeout);
        var requestId = _requestManager.AllocateRequestId();
        _serverStreamRequestIds.Add(requestId);
        var dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(cancellationToken, responseCodec);
        TrackBackgroundTask(StartDuplexStreamingInvokerAsync(
            dispatcher,
            method.ContractId,
            method.MethodId,
            requestId,
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
        RpcSession session;
        long requestId;
        RpcRequestOperation<TResponse> operation;
        if (!control.WaitForReady)
        {
            session = GetReadySession();
            operation = _requestManager.Rent(responseCodec, out requestId);
        }
        else
        {
            session = await GetReadySessionAsync(
                waitForReady: true,
                control.Deadline,
                cancellationToken).ConfigureAwait(false);
            var lease = await _requestManager.RentAsync(
                responseCodec,
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

        using var timeoutRegistration = RegisterRequestTimeout(control.Deadline, requestId, isOneWay: false);
        await using var cancelRegistration = RegisterCancel(
            cancellationToken,
            requestId,
            isOneWay: false,
            cancellationToken);
        try
        {
            BindRequestToSession(requestId, session);
            SendRpcCall(
                session,
                contractId,
                methodId,
                requestId,
                flags,
                request,
                requestCodec,
                control.Deadline,
                control.Metadata);
        }
        catch (Exception exception)
        {
            TryUnbindRequest(requestId, out _);
            _requestManager.DispatchError(requestId, exception);
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
        var session = control.WaitForReady
            ? await GetReadySessionAsync(
                waitForReady: true,
                control.Deadline,
                cancellationToken).ConfigureAwait(false)
            : GetReadySession();
        var requestId = _requestManager.AllocateRequestId();
        var flags = ProtocolV2FrameFlags.OneWay;
        if (hasClientStreams && (cancellationToken.CanBeCanceled || control.Deadline is not null))
            flags |= ProtocolV2FrameFlags.Cancellable;

        var timeoutRegistration = hasClientStreams
            ? RegisterRequestTimeout(control.Deadline, requestId, isOneWay: true)
            : default;
        var cancelRegistration = hasClientStreams
            ? RegisterCancel(cancellationToken, requestId, isOneWay: true, cancellationToken)
            : default;
        using (timeoutRegistration)
        await using (cancelRegistration)
        {
            if (hasClientStreams)
                BindRequestToSession(requestId, session);
            try
            {
                SendRpcCall(
                    session,
                    contractId,
                    methodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata);
                if (hasClientStreams)
                    await streams.WriteAsync(this, requestId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (hasClientStreams)
                    TryUnbindRequest(requestId, out _);
            }
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
        RpcSession session;
        long requestId;
        RpcRequestOperation<TResponse> operation;
        if (!control.WaitForReady)
        {
            session = GetReadySession();
            operation = _requestManager.Rent(responseCodec, out requestId);
        }
        else
        {
            session = await GetReadySessionAsync(
                waitForReady: true,
                control.Deadline,
                cancellationToken).ConfigureAwait(false);
            var lease = await _requestManager.RentAsync(
                responseCodec,
                waitForSlot: true,
                control.Deadline,
                cancellationToken).ConfigureAwait(false);
            requestId = lease.Id;
            operation = lease.Operation;
        }
        var flags = hasResponsePayload
            ? ProtocolV2FrameFlags.HasReturn | ProtocolV2FrameFlags.Cancellable
            : ProtocolV2FrameFlags.Cancellable;
        using var timeoutRegistration = RegisterRequestTimeout(control.Deadline, requestId, isOneWay: false);
        await using var cancelRegistration = RegisterCancel(
            cancellationToken,
            requestId,
            isOneWay: false,
            cancellationToken);
        try
        {
            BindRequestToSession(requestId, session);
            SendRpcCall(
                session,
                contractId,
                methodId,
                requestId,
                flags,
                request,
                requestCodec,
                control.Deadline,
                control.Metadata);
            TrackBackgroundTask(RunGeneratedClientStreamsAsync(streams, requestId, cancellationToken));
        }
        catch (Exception exception)
        {
            TryUnbindRequest(requestId, out _);
            _requestManager.DispatchError(requestId, exception);
        }

        return await operation.AsValueTask().ConfigureAwait(false);
    }

    private async Task RunGeneratedClientStreamsAsync<TStreams>(
        TStreams streams,
        long requestId,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        try
        {
            await streams.WriteAsync(this, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryUnbindRequest(requestId, out _);
            _requestManager.DispatchError(requestId, exception);
        }
    }

    private async Task StartServerStreamingInvokerAsync<TRequest, TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        long contractId,
        long methodId,
        long requestId,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await PrepareGeneratedServerStreamAsync(
                dispatcher,
                requestId,
                control,
                cancellationToken).ConfigureAwait(false);
            SendRpcCall(
                session,
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
            CompleteFailedGeneratedStream(dispatcher, requestId, exception);
        }
    }

    private async Task StartDuplexStreamingInvokerAsync<TRequest, TResponse, TStreams>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        long contractId,
        long methodId,
        long requestId,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        try
        {
            var session = await PrepareGeneratedServerStreamAsync(
                dispatcher,
                requestId,
                control,
                cancellationToken).ConfigureAwait(false);
            SendRpcCall(
                session,
                contractId,
                methodId,
                requestId,
                ProtocolV2FrameFlags.Cancellable,
                request,
                requestCodec,
                control.Deadline,
                control.Metadata);
            await streams.WriteAsync(this, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStream(dispatcher, requestId, exception);
        }
    }

    private async ValueTask<RpcSession> PrepareGeneratedServerStreamAsync<TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        long requestId,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var session = await GetReadySessionAsync(
            control.WaitForReady,
            control.Deadline,
            cancellationToken).ConfigureAwait(false);
        var timeoutRegistration = RegisterStreamTimeout(control.Deadline, requestId);
        var cancelRegistration = RegisterStreamCancel(cancellationToken, requestId, cancellationToken);
        var lifetime = new StreamCallLifetime(timeoutRegistration, cancelRegistration);
        if (!_streamCallLifetimes.TryAdd(requestId, lifetime))
        {
            lifetime.Dispose();
            throw new InvalidOperationException("A stream lifetime is already registered for this request.");
        }

        session.StreamManager.Register(requestId, 0, dispatcher);
        BindRequestToSession(requestId, session);
        return session;
    }

    private void CompleteFailedGeneratedStream<TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        long requestId,
        Exception exception)
    {
        _serverStreamRequestIds.Remove(requestId);
        TryUnbindRequest(requestId, out _);
        CompleteStreamLifetime(requestId);
        dispatcher.Complete(exception);
    }

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

        var writer = _runtimeContext.Buffers.Rent();
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
