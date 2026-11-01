namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    /// <summary>
    /// Runs the outbound frame preparation that has to happen outside any call-level arbitration.
    /// </summary>
    /// <remarks>
    /// Compression is provided by a user extension, so it can block, advance a clock, or call back
    /// into the runtime. A caller that also has to decide between publishing a Request and honouring
    /// a terminal reason therefore cannot run this while it holds that decision's gate: the gate
    /// would be held across arbitrary extension code, and a fault raised here has to surface
    /// synchronously, before the caller decides whether the frame was published.
    /// Ownership: on success the caller owns the returned packet. When this throws, the packet the
    /// caller passed in has already been returned to the pool.
    /// </remarks>
    internal IRpcByteBufferWriter PrepareOutboundFrame(
        IRpcByteBufferWriter packet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (Volatile.Read(ref _terminal) is { } terminal)
        {
            RuntimeContext.Buffers.Return(packet);
            throw terminal.Exception;
        }

        try
        {
            packet = PrepareOutboundPacket(packet, cancellationToken);
        }
        catch (Exception failure)
        {
            try { RuntimeContext.Buffers.Return(packet); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw new UnreachableException();
        }

        // Validation returns the packet itself through this helper when it rejects the frame.
        ValidateOutboundPacketOrReturn(packet, allowEmpty: false);
        return packet;
    }

    // Stack-owned transaction. Never copy after preparation: only the original caller may
    // transfer/finalize it. Accepted is irrevocable even when deferred notification fails.
    internal struct PreparedFrameEnqueue
    {
        internal SendPump? Pump;
        internal RpcSession? Session;
        internal OwnedFrame Frame;
        internal ProtocolV2FrameType FrameType;
        internal bool Reserved;
        internal bool TransferAttempted;
        internal bool TerminalAbandoned;
        internal bool Finished;
        internal SendEnqueueResult Result;
        internal TaskCompletionSource<bool>? Completion;
        internal CancellationToken CancellationToken;
        internal Exception? Refusal;
        internal bool PhaseRefused;
        internal RpcSessionProtocolPhase RefusedPhase;
        internal bool Accepted => TransferAttempted && Result == SendEnqueueResult.Accepted;

        // The original pending terminal won before queue transfer. This is neither a send
        // attempt nor queue rejection; preparation still owns its exact refund/return duties.
        internal void AbandonForTerminal()
        {
            if (TransferAttempted || Finished)
                throw new InvalidOperationException("An attempted enqueue cannot be terminal-abandoned.");
            TerminalAbandoned = true;
        }

        // Caller-selected terminal errors are created outside its arbitration gate.
        internal void Reject(Exception reason)
        {
            if (Accepted || Finished) throw new InvalidOperationException("Enqueue ownership is already final.");
            Refusal = reason ?? throw new ArgumentNullException(nameof(reason));
        }
    }

    /// <summary>Takes the prepared writer; reserve/telemetry/pump creation run OUTSIDE call gates.</summary>
    internal PreparedFrameEnqueue PrepareFrameEnqueue(
        IRpcByteBufferWriter packet,
        bool observeEmission,
        CancellationToken cancellationToken,
        IRequestEmissionFailureObserver? failureObserver,
        RpcFramePublication? publication = null,
        RpcFramePublicationRole publicationRole = RpcFramePublicationRole.None)
    {
        ArgumentNullException.ThrowIfNull(packet);
        TaskCompletionSource<bool>? completion;
        try
        {
            completion = observeEmission
                ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        }
        catch (Exception failure)
        {
            try { RuntimeContext.Buffers.Return(packet); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw new UnreachableException();
        }
        // Even ACK callers that construct an already-final packet must validate outside
        // arbitration; a previously prepared Request repeats only scalar wire checks.
        ValidateOutboundPacketOrReturn(packet, allowEmpty: false);
        // Each helper owns/returns the writer on failure; no duplicate catch return.
        var pump = GetOrCreatePumpOrReturn(packet);
        var frame = CreateFrame(packet, forceFlush: false, completion,
            failureObserver, publication, publicationRole);
        var admission = pump.PrepareEnqueue(frame);
        admission.Session = this;
        admission.FrameType = (ProtocolV2FrameType)frame.Memory.Span[5];
        admission.Completion = completion;
        admission.CancellationToken = cancellationToken;
        return admission;
    }

    /// <summary>Only scalar checks and Channel.TryWrite. No cleanup, wake, callback or error factory.</summary>
    internal bool TryTransferPreparedFrame(ref PreparedFrameEnqueue admission)
    {
        if (admission.TransferAttempted || admission.TerminalAbandoned || admission.Finished || admission.Pump is null)
            return false;
        admission.TransferAttempted = true;
        var phase = Volatile.Read(ref _protocolState).Phase;
        var frameType = admission.FrameType;
        var peerRole = Role == RpcSessionRole.Client ? RpcSessionRole.Server : RpcSessionRole.Client;
        if (!ReferenceEquals(admission.Session, this) || Volatile.Read(ref _terminal) is not null || admission.Refusal is not null)
        {
            admission.Result = SendEnqueueResult.Closed;
            return false;
        }
        if (!RpcSessionProtocolRules.IsFrameAllowed(phase, frameType) ||
            !RpcSessionProtocolRules.IsInboundDirectionAllowed(peerRole, frameType) ||
            (phase == RpcSessionProtocolPhase.Draining && frameType is
                ProtocolV2FrameType.Request or ProtocolV2FrameType.OneWayStreamRequest or ProtocolV2FrameType.BindContract))
        {
            admission.PhaseRefused = true;
            admission.RefusedPhase = phase;
            admission.Result = SendEnqueueResult.Closed;
            return false;
        }
        return admission.Pump.TryTransferPrepared(ref admission);
    }

    /// <summary>Unconditional OUTSIDE-gate finalization, including paths that never attempted transfer.</summary>
    internal void FinishPreparedFrameEnqueue(
        ref PreparedFrameEnqueue admission, out ValueTask emission, out Exception? rejection)
    {
        emission = default;
        rejection = null;
        if (admission.Finished || admission.Pump is null) return;
        admission.Finished = true;
        if (admission.Accepted)
        {
            // Do not read the frame/buffer here: the consumer may already have returned it.
            admission.Pump!.FinishAcceptedPublication();
            if (admission.Completion is not null)
                emission = AwaitEmissionAsync(admission.Completion, admission.CancellationToken);
            return;
        }
        if (admission.TerminalAbandoned)
        {
            // No synthetic Full/Closed outcome for an unattempted head. Actual cleanup
            // callbacks/refunds still finish and propagate independently below the gate.
            try { admission.Pump!.FinishTerminalAbandonedPrepared(ref admission); }
            finally
            {
                if (admission.Completion is not null)
                    ObserveAbandonedFlushCompletion(admission.Completion.Task);
            }
            return;
        }
        rejection = admission.Refusal ?? (admission.PhaseRefused
            ? new SharpLinkException(admission.RefusedPhase == RpcSessionProtocolPhase.Draining
                    ? SharpLinkErrorCode.Unavailable : SharpLinkErrorCode.ProtocolViolation,
                $"Frame {admission.FrameType} lost outbound eligibility while the Session is {admission.RefusedPhase}.")
            : CreateSendEnqueueFailure(admission.Result == SendEnqueueResult.Full
                ? SendEnqueueResult.Full : SendEnqueueResult.Closed));
        try { admission.Pump!.FinishRejectedPrepared(ref admission, rejection); }
        finally
        {
            if (admission.Completion is not null)
                ObserveAbandonedFlushCompletion(admission.Completion.Task);
        }
    }

    // Non-gated callers use the same transaction, not an ACK-only alternative behavior.
    internal bool TryEnqueuePreparedFrame(
        IRpcByteBufferWriter packet,
        bool observeEmission,
        CancellationToken cancellationToken,
        IRequestEmissionFailureObserver? failureObserver,
        out ValueTask emission,
        out Exception? rejection,
        RpcFramePublication? publication = null,
        RpcFramePublicationRole publicationRole = RpcFramePublicationRole.None)
    {
        emission = default;
        rejection = null;
        var admission = PrepareFrameEnqueue(packet, observeEmission, cancellationToken,
            failureObserver, publication, publicationRole);
        try { return TryTransferPreparedFrame(ref admission); }
        finally { FinishPreparedFrameEnqueue(ref admission, out emission, out rejection); }
    }

    /// <summary>Maps a rejected admission result to the failure the caller has to observe.</summary>
    internal Exception CreateSendEnqueueFailure(SendEnqueueResult result)
        => result switch
        {
            SendEnqueueResult.Full => SharpLinkResourceExhaustion.Create(
                SharpLinkResourceExhaustion.SendQueueCapacity,
                $"Session send queue exceeded its {RuntimeContext.FlowControl.MaxSendQueueBytes}-byte limit (send_queue_capacity)."),
            SendEnqueueResult.Closed => GetTerminalException(),
            _ => new InvalidOperationException(
                $"Send queue admission returned '{result}', which is not a rejection.")
        };

    private static async ValueTask AwaitEmissionAsync(
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The pump still owns the frame and completes this waiter during teardown.
            // Task.WaitAsync does not observe a source fault that arrives after its own
            // cancellation (an already-cancelled token cancels the wait without
            // registering on the source), so observe the late fault here to keep it off
            // the finalizer (issue #216).
            ObserveAbandonedFlushCompletion(completion.Task);
            throw;
        }
    }
}
