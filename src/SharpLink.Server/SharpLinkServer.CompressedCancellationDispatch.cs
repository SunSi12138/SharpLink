namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public ValueTask DispatchRpcAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken)
        => DispatchRpcAsync(
            connection,
            requestId,
            flags,
            payload,
            requestCancellationMap,
            serverLoopToken,
            admittedCallState: null,
            admissionGranted: false,
            callCapacityGranted: false,
            allowCompressedCancellationHandoff: true,
            preparedServiceInfo: null,
            reusePreDecodeMetadata: false,
            preDecodeMetadata: null);

    // Keeps the existing dispatch-level unit harness stable while the production request-loop
    // entry point uses the six-argument overload above. SharpLinkServer itself is internal.
    private ValueTask DispatchRpcAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? admittedCallState,
        bool admissionGranted)
        => DispatchRpcAsync(
            connection,
            requestId,
            flags,
            payload,
            requestCancellationMap,
            serverLoopToken,
            admittedCallState,
            admissionGranted,
            callCapacityGranted: false,
            allowCompressedCancellationHandoff: false,
            preparedServiceInfo: null,
            reusePreDecodeMetadata: false,
            preDecodeMetadata: null);

    private ValueTask HandoffCompressedCancellableRpc(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        ServiceRegistration serviceInfo,
        RpcMethodDescriptor descriptor,
        bool reusePreDecodeMetadata,
        SharpLinkMetadata? preDecodeMetadata)
    {
        IRpcByteBufferWriter? retainedPayload = null;
        try
        {
            ServerRequestEnvelopeReader.ValidateMetadataSyntax(
                connection.Session,
                payload,
                flags,
                _protocolOptions.MaxMetadataBytes,
                _runtimeContext.TimeProvider);
            ReservePreDecodeRequestStreams(
                connection.Session,
                requestId,
                descriptor.ClientStreamCount,
                callState);
            retainedPayload = CopyAdmissionPayload(payload);
            return DispatchRetainedCancellableCompressedRpcAsync(
                retainedPayload,
                connection,
                requestId,
                flags,
                requestCancellationMap,
                serverLoopToken,
                callState,
                serviceInfo,
                reusePreDecodeMetadata,
                preDecodeMetadata);
        }
        catch (Exception exception)
        {
            if (retainedPayload is not null)
                _runtimeContext.Buffers.Return(retainedPayload);
            CompleteFailedRequestStreams(connection.Session, requestId, exception);
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
            throw;
        }
    }

    private async ValueTask DispatchRetainedCancellableCompressedRpcAsync(
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        ServiceRegistration serviceInfo,
        bool reusePreDecodeMetadata,
        SharpLinkMetadata? preDecodeMetadata)
    {
        await Task.Yield();
        ValueTask dispatch;
        try
        {
            dispatch = DispatchRpcAsync(
                connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(retainedPayload.WrittenMemory),
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true,
                callCapacityGranted: true,
                allowCompressedCancellationHandoff: false,
                preparedServiceInfo: serviceInfo,
                reusePreDecodeMetadata: reusePreDecodeMetadata,
                preDecodeMetadata: preDecodeMetadata);
        }
        finally
        {
            // DispatchRpcAsync performs compressed request decode synchronously before returning.
            // Once it returns, any async continuation owns only decoded payload/call state and the
            // raw compressed copy can be recycled immediately instead of for the full RPC lifetime.
            _runtimeContext.Buffers.Return(retainedPayload);
        }

        if (!dispatch.IsCompletedSuccessfully)
            await dispatch.ConfigureAwait(false);
    }

    private void HandoffCompressedCancellableOneWayRpc(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        ServiceRegistration serviceInfo,
        RpcMethodDescriptor descriptor,
        bool reusePreDecodeMetadata,
        SharpLinkMetadata? preDecodeMetadata)
    {
        IRpcByteBufferWriter? retainedPayload = null;
        try
        {
            ServerRequestEnvelopeReader.ValidateMetadataSyntax(
                connection.Session,
                payload,
                flags,
                _protocolOptions.MaxMetadataBytes,
                _runtimeContext.TimeProvider);
            ReservePreDecodeRequestStreams(
                connection.Session,
                requestId,
                descriptor.ClientStreamCount,
                callState);
            retainedPayload = CopyAdmissionPayload(payload);
            ObserveUserCall(
                new ValueTask(DispatchRetainedCancellableCompressedOneWayRpcAsync(
                    retainedPayload,
                    connection,
                    requestId,
                    flags,
                    requestCancellationMap,
                    serverLoopToken,
                    descriptor.ClientStreamCount,
                    callState,
                    serviceInfo,
                    reusePreDecodeMetadata,
                    preDecodeMetadata)),
                requestId);
        }
        catch
        {
            if (retainedPayload is not null)
                _runtimeContext.Buffers.Return(retainedPayload);
            DrainFailedOneWayStreams(
                connection.Session,
                requestId,
                descriptor.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection);
            throw;
        }
    }

    private async Task DispatchRetainedCancellableCompressedOneWayRpcAsync(
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        int clientStreamCount,
        ServerCallCancellationState callState,
        ServiceRegistration serviceInfo,
        bool reusePreDecodeMetadata,
        SharpLinkMetadata? preDecodeMetadata)
    {
        await Task.Yield();
        try
        {
            DispatchOneWayRpc(
                connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(retainedPayload.WrittenMemory),
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true,
                admittedClientStreamCount: clientStreamCount,
                callCapacityGranted: true,
                allowCompressedCancellationHandoff: false,
                preparedServiceInfo: serviceInfo,
                reusePreDecodeMetadata: reusePreDecodeMetadata,
                preDecodeMetadata: preDecodeMetadata);
        }
        finally
        {
            _runtimeContext.Buffers.Return(retainedPayload);
        }
    }
}
