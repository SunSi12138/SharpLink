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
        ServerDecodeQueuePermit? queuePermit = null;
        var retainedUseOwned = false;
        var callState = admittedCallState;
        try
        {
            // Scheduler admission precedes D-specific long-lived retention and all provider/decode
            // budgets. A full executor therefore rejects without copying this RequestLoop frame or
            // reserving decode/decoded-byte resources.
            if (!DecodeExecutor.TryReserveQueueSlot(out queuePermit))
            {
                retainedPayload?.Dispose();
                var rejection = CreateDecodeQueueResourceExhaustion();
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

            var reservedQueuePermit = queuePermit ?? throw new InvalidOperationException(
                "Persistent decode queue admission did not return its permit.");

            if (retainedPayload is null)
            {
                if (!TryCopyAdmissionPayload(payload, flags, out retainedPayload))
                {
                    reservedQueuePermit.Dispose();
                    queuePermit = null;
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

            var persistentRetainedPayload = retainedPayload ?? throw new InvalidOperationException(
                "Persistent decode requires a retained request payload.");
            persistentRetainedPayload.AcquireUse();
            retainedUseOwned = true;
            var stablePayload = persistentRetainedPayload.Payload;

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
                var workerRetainedUseOwned = true;
                try
                {
                    // Provider-concurrency and decoded-byte ownership begin only after a worker has
                    // won Queued -> Running. Queued requests therefore do not consume these budgets.
                    if (!TryPrepareCompressedRequestDecode(
                            requestOwner,
                            persistentRetainedPayload.RetainedPermit,
                            flags,
                            stablePayload,
                            out var decodePermit,
                            out var resourceRejection))
                    {
                        result.DecodePermit = decodePermit;
                        result.ResourceRejection = resourceRejection ?? throw new InvalidOperationException(
                            "Persistent request decode resource rejection is missing its error.");
                        return ValueTask.CompletedTask;
                    }

                    result.DecodePermit = decodePermit;
                    result.Payload = session.DecodeInboundPayload(
                        ProtocolV2FrameType.Request,
                        flags,
                        stablePayload,
                        cancellationToken,
                        out var decodedOwner);
                    result.Owner = decodedOwner;
                    return ValueTask.CompletedTask;
                }
                finally
                {
                    try
                    {
                        // Provider execution is done before this worker can service another item.
                        // Return the physical compressed owner first; only then release the active
                        // decode credit/retained accounting. Decoded-byte ownership remains attached
                        // to the completed permit until request activation/teardown transfers it.
                        ReleaseRetainedPayloadUse(
                            persistentRetainedPayload,
                            ref workerRetainedUseOwned);
                    }
                    finally
                    {
                        result.DecodePermit?.CompleteDecode();
                        Volatile.Write(ref result.RetainedUseReleased, 1);
                    }
                }
            });

            var decodeTask = DecodeExecutor.EnqueueReservedAsync(
                connection,
                reservedQueuePermit,
                workItem,
                callState.InvocationToken);
            queuePermit = null;
            retainedUseOwned = false;
            return AwaitPersistentDecodeAndContinueAsync(
                decodeTask,
                persistentRetainedPayload,
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
            queuePermit?.Dispose();
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
            ReconcileRetainedPayloadUse(result, retainedPayload, ref retainedUseOwned);

            if (result.ResourceRejection is { } resourceRejection)
            {
                session.ReturnDecodedPayload(result.Owner);
                result.Owner = null;
                CompleteFailedRequestStreams(session, requestId, resourceRejection);
                var rejectionSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    resourceRejection,
                    connection.ConnectionToken);
                await ReleaseDispatchResourcesAfterResponseAsync(
                    rejectionSend,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner).ConfigureAwait(false);
                return;
            }

            request = ReadRequestEnvelope(
                session, result.Payload, flags, request.RpcDeadline);
        }
        catch (ServerDecodeExecutorClosedException)
        {
            ReconcileRetainedPayloadUse(result, retainedPayload, ref retainedUseOwned);
            session.ReturnDecodedPayload(result.Owner);
            result.Owner = null;
            var exception = new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "Server is draining.");
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
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
        {
            ReconcileRetainedPayloadUse(result, retainedPayload, ref retainedUseOwned);
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
            ReconcileRetainedPayloadUse(result, retainedPayload, ref retainedUseOwned);
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
            ReconcileRetainedPayloadUse(result, retainedPayload, ref retainedUseOwned);
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

    private static void ReconcileRetainedPayloadUse(
        PersistentDecodeResult result,
        ServerRetainedAdmissionPayload retainedPayload,
        ref bool retainedUseOwned)
    {
        if (Volatile.Read(ref result.RetainedUseReleased) != 0)
        {
            retainedUseOwned = false;
            return;
        }

        ReleaseRetainedPayloadUse(retainedPayload, ref retainedUseOwned);
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

        internal ServerDecodePermit? DecodePermit { get; set; }

        internal SharpLinkException? ResourceRejection { get; set; }

        internal int RetainedUseReleased;
    }
}
