namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ValueTask DispatchRpcAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken)
    {
        var cancellableCompressed =
            (flags & (ProtocolV2FrameFlags.Compressed | ProtocolV2FrameFlags.Cancellable)) ==
            (ProtocolV2FrameFlags.Compressed | ProtocolV2FrameFlags.Cancellable);
        if (!cancellableCompressed || _admissionController is not null)
        {
            return DispatchRpcAsync(
                connection,
                requestId,
                flags,
                payload,
                requestCancellationMap,
                serverLoopToken,
                admittedCallState: null,
                admissionGranted: false);
        }

        var session = connection.Session;
        var request = ReadRequestRoutingEnvelope(session, payload, flags);
        if (IsDeadlineExceeded(request.RpcDeadline) ||
            !Volatile.Read(ref _services).TryGetValue(request.InterfaceHash, out var serviceInfo) ||
            !serviceInfo.AcceptsCalls)
        {
            return DispatchRpcAsync(
                connection,
                requestId,
                flags,
                payload,
                requestCancellationMap,
                serverLoopToken,
                admittedCallState: null,
                admissionGranted: false);
        }

        var descriptor = GetMethodDescriptor(serviceInfo.Stub, request.MethodHash);
        if (descriptor.ClientStreamCount != 0)
        {
            return DispatchRpcAsync(
                connection,
                requestId,
                flags,
                payload,
                requestCancellationMap,
                serverLoopToken,
                admittedCallState: null,
                admissionGranted: false);
        }

        var retainedPayload = CopyAdmissionPayload(payload);
        ServerCallCancellationState callState;
        try
        {
            callState = CreateAdmissionWaitState(
                connection,
                requestId,
                request.RpcDeadline,
                serverLoopToken,
                serviceInfo.ModuleCancellation,
                requestCancellationMap);
        }
        catch
        {
            _runtimeContext.Buffers.Return(retainedPayload);
            throw;
        }

        return DispatchRetainedCancellableCompressedRpcAsync(
            retainedPayload,
            connection,
            requestId,
            flags,
            requestCancellationMap,
            serverLoopToken,
            callState);
    }

    private async ValueTask DispatchRetainedCancellableCompressedRpcAsync(
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState)
    {
        await Task.Yield();
        try
        {
            if (callState.Reason == ServerCallCancellationReason.RemoteCancel)
            {
                ReleaseAdmissionCallState(requestCancellationMap, requestId, callState);
                return;
            }

            var dispatch = DispatchRpcAsync(
                connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(retainedPayload.WrittenMemory),
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: false);
            if (!dispatch.IsCompletedSuccessfully)
                await dispatch.ConfigureAwait(false);
        }
        finally
        {
            _runtimeContext.Buffers.Return(retainedPayload);
        }
    }
}
