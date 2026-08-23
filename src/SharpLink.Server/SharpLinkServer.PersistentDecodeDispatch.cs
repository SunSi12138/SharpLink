namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ValueTask DispatchRpcWithPersistentDecodeAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        ServerRequestEnvelope request,
        ServiceRegistration serviceInfo,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? admittedCallState,
        ServerRequestPermit requestOwner,
        ServerRetainedAdmissionPayload? retainedAdmissionPayload)
    {
        var session = connection.Session;
        var retainedPayload = retainedAdmissionPayload;
        var retainedUseOwned = false;
        var callState = admittedCallState;
        try
        {
            if (retainedPayload is null)
            {
                if (!TryCopyAdmissionPayload(payload, flags, out retainedPayload))
                {
                    var rejection = CreateRetainedCompressedResourceExhaustion();
                    CompleteFailedRequestStreams(session, requestId, rejection);
                    var responseSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        rejection,
                        connection.ConnectionToken);
                    return ReleaseDispatchResourcesAfterResponseAsync(
                        responseSend,
                        callState,
                        requestId,
                        requestCancellationMap,
                        connection,
                        requestOwner);
                }
            }

            retainedPayload!.AcquireUse();
            retainedUseOwned = true;
            var stablePayload = retainedPayload.Payload;
            if (!TryPrepareCompressedRequestDecode(
                    requestOwner,
                    retainedPayload.RetainedPermit,
                    flags,
                    stablePayload,
                    out var decodePermit,
                    out var resourceRejection))
            {
                ReleaseRetainedPayloadUse(retainedPayload, ref retainedUseOwned);
                var rejection = resourceRejection ?? throw new InvalidOperationException(
                    "Persistent request decode resource rejection is missing its error.");
                CompleteFailedRequestStreams(session, requestId, rejection);
                var responseSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    rejection,
                    connection.ConnectionToken);
                return ReleaseDispatchResourcesAfterResponseAsync(
                    responseSend,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
            }

            callState ??= CreateTrackedCallState(
                connection,
                requestId,
                request.RpcDeadline,
                serverLoopToken,
                serviceInfo.ModuleCancellation,
                supportsCooperativeCancellation: true,
                requestCancellationMap) ?? throw new InvalidOperationException(
                    "Persistent decode requires a pre-activation cancellation state.");
            var result = new PersistentDecodeResult();
            var workItem = new ServerDecodeWorkItem(cancellationToken =>
            {
                result.Payload = session.DecodeInboundPayload(
                    ProtocolV2FrameType.Request,
                    flags,
                    stablePayload,
                    cancellationToken,
                    out var decodedOwner);
                result.Owner = decodedOwner;
                return ValueTask.CompletedTask;
            });
            var decodeTask = DecodeExecutor.EnqueueAsync(workItem, callState.InvocationToken);
            retainedUseOwned = false;
            return AwaitPersistentDecodeAndContinueAsync(
                decodeTask,
                decodePermit!,
                retainedPayload,
                result,
                connection,
                requestId,
                flags,
                request,
                serviceInfo,
                requestCancellationMap,
                serverLoopToken,
                callState,
                requestOwner);
        }
        catch
        {
            if (retainedPayload is not null)
                ReleaseRetainedPayloadUse(retainedPayload, ref retainedUseOwned);
            ReleaseDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestOwner);
            throw;
        }
    }

    private async ValueTask AwaitPersistentDecodeAndContinueAsync(
        ValueTask decodeTask,
        ServerDecodePermit decodePermit,
        ServerRetainedAdmissionPayload retainedPayload,
        PersistentDecodeResult result,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ServerRequestEnvelope request,
        ServiceRegistration serviceInfo,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        ServerRequestPermit requestOwner)
    {
        var session = connection.Session;
        var retainedUseOwned = true;
        try
        {
            await decodeTask.ConfigureAwait(false);
            ReleaseRetainedPayloadUse(retainedPayload, ref retainedUseOwned);
            decodePermit.CompleteDecode();
            request = ReadRequestEnvelope(session, result.Payload, flags);
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
        {
            ReleaseRetainedPayloadUse(retainedPayload, ref retainedUseOwned);
            session.ReturnDecodedPayload(result.Owner);
            result.Owner = null;
            CompleteFailedRequestStreams(session, requestId, exception);
            var responseSend = session.SendRpcErrorWithBackpressureAsync(
                requestId,
                exception,
                connection.ConnectionToken);
            await ReleaseDispatchResourcesAfterResponseAsync(
                responseSend,
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestOwner).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException exception)
        {
            ReleaseRetainedPayloadUse(retainedPayload, ref retainedUseOwned);
            session.ReturnDecodedPayload(result.Owner);
            result.Owner = null;
            CompleteFailedRequestStreams(session, requestId, exception);
            var responseSend = session.SendRpcErrorWithBackpressureAsync(
                requestId,
                MapServerCancellationException(callState, request.RpcDeadline),
                connection.ConnectionToken);
            await ReleaseDispatchResourcesAfterResponseAsync(
                responseSend,
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestOwner).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            ReleaseRetainedPayloadUse(retainedPayload, ref retainedUseOwned);
            session.ReturnDecodedPayload(result.Owner);
            result.Owner = null;
            CompleteFailedRequestStreams(session, requestId, exception);
            ReleaseDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestOwner);
            throw;
        }

        var decodedOwner = result.Owner;
        result.Owner = null;
        await ContinueRpcDispatch(
            connection,
            requestId,
            flags,
            request,
            serviceInfo,
            requestCancellationMap,
            serverLoopToken,
            callState,
            requestOwner,
            decodedOwner).ConfigureAwait(false);
    }

    private static void ReleaseRetainedPayloadUse(
        ServerRetainedAdmissionPayload retainedPayload,
        ref bool retainedUseOwned)
    {
        if (!retainedUseOwned)
            return;

        retainedUseOwned = false;
        try
        {
            retainedPayload.Dispose();
        }
        finally
        {
            retainedPayload.ReleaseUse();
        }
    }

    private sealed class PersistentDecodeResult
    {
        internal ReadOnlySequence<byte> Payload { get; set; }

        internal IRpcByteBufferWriter? Owner { get; set; }
    }
}
