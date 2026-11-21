namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private async Task AwaitOneWayAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        ServerRetainedAdmissionPayload retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        NStudyMethodFacts facts,
        ServerCallCancellationState callState,
        AdmissionProgram admissionProgram,
        NStudyServerBindings.Use binding)
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
                _ = RejectAdmission(connection.Session, requestId, decision, oneWay: true);
                return;
            }

            callState.AttachAdmissionLease(decision.Lease!);
            if (!binding.ClaimStart())
                return;
            var dispatchTask = DispatchAdmittedOneWayRpc(
                retainedPayload.SelectedService,
                facts,
                connection,
                requestId,
                flags,
                retainedPayload.Payload,
                requestCancellationMap,
                serverLoopToken,
                admissionProgram,
                callState, true, retainedPayload);
            transferred = true;
            if (!dispatchTask.IsCompletedSuccessfully)
                await dispatchTask.ConfigureAwait(false);
        }
        finally
        {
            retainedPayload.Dispose();
            if (!transferred)
            {
                try { DrainRejectedOneWayStreams(connection.Session, requestId, facts.Descriptor.ClientStreamCount); }
                finally { ReleaseAdmissionCallState(requestCancellationMap, requestId, callState); }
            }
        }
    }

    private async ValueTask AwaitRpcAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        ServerRetainedAdmissionPayload retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        AdmissionProgram admissionProgram,
        NStudyServerBindings.Use binding,
        NStudyMethodFacts facts)
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
            if (!binding.ClaimStart())
            {
                await PublishAdmissionError(connection.Session, requestId, callState, NStudyBindingRetiredError(),
                    connection.ConnectionToken).ConfigureAwait(false);
                return;
            }
            var dispatchTask = DispatchAdmittedRpcAsync(
                retainedPayload.SelectedService,
                facts,
                connection,
                requestId,
                flags,
                retainedPayload.Payload,
                requestCancellationMap,
                serverLoopToken,
                admissionProgram,
                callState, true, retainedPayload);
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
