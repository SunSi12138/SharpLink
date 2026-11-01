namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // Synchronous seams deliberately cannot suspend over RequestLoop's borrowed Pipe payload.
    internal Action<ServerConnectionState, long>? BeforeRequestAcceptedForTesting { get; set; }
    internal Action<ServerConnectionState, long>? AfterRequestAcceptedForTesting { get; set; }

    private Exception? PrepareRequestAccepted(
        ServerConnectionState connection, long requestId, in ServerRequestEnvelope request,
        in ResolvedMethodCall resolved, StripedLongMap<ServerCallCancellationState> cancellations,
        CancellationToken serverLoopToken, ref ServerCallCancellationState? callState)
    {
        if (resolved.Method.Kind is not (RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming))
            return null;
        var session = connection.Session;
        try
        {
            // Repeat the existing authentication-expiry decision before producer permission.
            // Invocation retains its later recheck; arbitrary interceptors still run only there.
            if (connection.AuthenticationContext?.IsExpired() == true)
                return new SharpLinkException(SharpLinkErrorCode.AuthenticationExpired,
                    "Authentication token has expired.");
            // Caller already owns the request permit and ONE exact module invocation lease.
            // No decode/user entry precedes this point, and ACK is not request activation.
            callState = EnsurePreDecodeCallState(connection, callState, requestId, request.RpcDeadline,
                serverLoopToken, resolved.ModuleCancellation, cancellations);
            if (!TryReservePreInvocationRequestStreams(session, resolved.ClientStreamCount, requestId,
                    callState.InvocationToken, retainUntilLocalCompletion: true))
                return new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                    "The complete client stream route set was not accepted.");

            BeforeRequestAcceptedForTesting?.Invoke(connection, requestId);
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            RpcSession.PreparedFrameEnqueue admission;
            try
            {
                using (writer.BeginPacketScope(ProtocolV2FrameType.RequestAccepted,
                           ProtocolV2FrameFlags.None, unchecked((ulong)requestId))) { }
                ownsWriter = false; // Preparation takes the writer on every outcome.
                admission = session.PrepareFrameEnqueue(writer, observeEmission: false,
                    cancellationToken: default, failureObserver: null);
            }
            finally
            {
                if (ownsWriter) _runtimeContext.Buffers.Return(writer);
            }

            var notifyCancellation = false;
            var accepted = false;
            Exception? failure = null;
            try
            {
                accepted = callState.TryPublishRequestAccepted(session, ref admission,
                    resolved.ClientStreamCount, out notifyCancellation);
            }
            catch (Exception exception) { failure = exception; }
            if (!accepted && failure is null && callState.Reason != ServerCallCancellationReason.None)
            {
                // Error construction is outside the terminal gate, like all callbacks.
                failure = MapServerCancellationException(callState, request.RpcDeadline);
                admission.Reject(failure);
            }
            // Unconditional second phase: buffer/byte rollback, wake and every callback are
            // outside the terminal gate. A wake failure cannot undo Accepted stream ownership.
            try
            {
                session.FinishPreparedFrameEnqueue(ref admission, out _, out var rejection);
                failure = RpcPublicationErrors.Combine(failure, rejection);
            }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            if (notifyCancellation)
            {
                try { callState.NotifyInvocationCancellation(); }
                catch (Exception notification) { failure = RpcPublicationErrors.Combine(failure, notification); }
            }
            if (!accepted)
                return failure ?? MapServerCancellationException(callState, request.RpcDeadline);
            if (failure is not null)
                return failure;
            AfterRequestAcceptedForTesting?.Invoke(connection, requestId);
            // Deferred wake failure keeps Accepted ownership but faults the exact Session.
            // Never decode/start business merely because queue transfer won earlier.
            if (!session.IsConnected)
                return new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                    "RequestAccepted publication lost its physical Session.");
            if (callState.Reason != ServerCallCancellationReason.None)
                return MapServerCancellationException(callState, request.RpcDeadline);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async ValueTask RejectRequestAcceptanceAsync(
        ServerConnectionState connection, long requestId, Exception failure,
        ServerCallCancellationState? callState, StripedLongMap<ServerCallCancellationState> cancellations,
        ServerRequestPermit owner, ServerRetainedAdmissionPayload? retained)
    {
        var session = connection.Session;
        Exception? cleanupFailure = null;
        try
        {
            // No borrowed RequestLoop sequence is retained across this asynchronous error send.
            try { retained?.Dispose(); }
            catch (Exception cleanup) { cleanupFailure = cleanup; }
            try { CompleteFailedRequestStreams(session, requestId, failure, callState, owner); }
            catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
            if (cleanupFailure is not null)
                await session.CloseAndDrainOutboundAsync(RpcPublicationErrors.Combine(failure, cleanupFailure)!)
                    .ConfigureAwait(false);
            else if (session.IsConnected)
            {
                var error = failure as SharpLinkException ?? new SharpLinkException(
                    SharpLinkErrorCode.Internal, "Client stream admission failed.", failure);
                try
                {
                    _ = callState?.TryClaimResponse();
                    await session.SendRpcErrorWithBackpressureAsync(requestId, error,
                        connection.ConnectionToken).ConfigureAwait(false);
                }
                catch (Exception sendFailure)
                {
                    await session.CloseAndDrainOutboundAsync(RpcPublicationErrors.Combine(failure, sendFailure)!)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ReleaseDispatchResources(callState, requestId, cancellations, connection, owner);
        }
    }
}
