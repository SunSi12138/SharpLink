using System.Runtime.CompilerServices;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // Keep weak selection capture and queued continuation setup out of the ordinary dispatcher's
    // frame, including Tier0. Only a genuinely suspended admission request reaches these entries.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task AwaitOneWayAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        ServerRetainedAdmissionPayload retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        int clientStreamCount,
        ServerCallCancellationState callState,
        AdmissionProgram admissionProgram,
        ServiceRegistration selectedService)
        => AwaitSelectedOneWayAdmissionAsync(
            admissionTask, retainedPayload, connection, requestId, flags, requestCancellationMap,
            serverLoopToken, clientStreamCount, callState, admissionProgram,
            new QueuedServiceSelection(selectedService));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask AwaitRpcAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        ServerRetainedAdmissionPayload retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        AdmissionProgram admissionProgram,
        ServiceRegistration selectedService)
        => AwaitSelectedRpcAdmissionAsync(
            admissionTask, retainedPayload, connection, requestId, flags, requestCancellationMap,
            serverLoopToken, callState, admissionProgram, new QueuedServiceSelection(selectedService));

    private async Task AwaitSelectedOneWayAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        ServerRetainedAdmissionPayload retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        int clientStreamCount,
        ServerCallCancellationState callState,
        AdmissionProgram admissionProgram,
        QueuedServiceSelection serviceSelection)
    {
        var transferred = false;
        try
        {
            AdmissionDecision decision;
            try
            {
                decision = await admissionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                decision = CreateAdmissionCancellationDecision(callState);
            }
            if (!decision.IsAcquired)
            {
                DrainRejectedOneWayStreams(
                    connection.Session, requestId, clientStreamCount);
                _ = RejectAdmission(connection.Session, requestId, decision, oneWay: true);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, callState);
                transferred = true;
                return;
            }

            callState.AttachAdmissionLease(decision.Lease!);
            var dispatchTask = DispatchOneWayRpc(
                connection,
                requestId,
                flags,
                retainedPayload.Payload,
                requestCancellationMap,
                serverLoopToken,
                admissionProgram,
                new AdmittedRequestDispatch(serviceSelection, callState, retainedPayload, clientStreamCount));
            transferred = true;
            if (!dispatchTask.IsCompletedSuccessfully)
                await dispatchTask.ConfigureAwait(false);
        }
        finally
        {
            retainedPayload.Dispose();
            if (!transferred)
                ReleasePendingAdmissionState(connection.Session, requestCancellationMap, requestId, callState);
        }
    }

    private async ValueTask AwaitSelectedRpcAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        ServerRetainedAdmissionPayload retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        AdmissionProgram admissionProgram,
        QueuedServiceSelection serviceSelection)
    {
        var transferred = false;
        try
        {
            AdmissionDecision decision;
            try
            {
                decision = await admissionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                decision = CreateAdmissionCancellationDecision(callState);
            }
            if (!decision.IsAcquired)
            {
                await RejectAdmission(
                    connection.Session,
                    requestId,
                    decision,
                    oneWay: false,
                    callState: callState,
                    cancellationToken: connection.ConnectionToken).ConfigureAwait(false);
                return;
            }

            callState.AttachAdmissionLease(decision.Lease!);
            var dispatchTask = DispatchRpcAsync(
                connection,
                requestId,
                flags,
                retainedPayload.Payload,
                requestCancellationMap,
                serverLoopToken,
                admissionProgram,
                new AdmittedRequestDispatch(serviceSelection, callState, retainedPayload));
            transferred = true;
            if ((flags & ProtocolV2FrameFlags.Compressed) != 0)
                retainedPayload.Dispose();
            if (!dispatchTask.IsCompletedSuccessfully)
                await dispatchTask.ConfigureAwait(false);
        }
        finally
        {
            retainedPayload.Dispose();
            if (!transferred)
                ReleasePendingAdmissionState(connection.Session, requestCancellationMap, requestId, callState);
        }
    }

}
