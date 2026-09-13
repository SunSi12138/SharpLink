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
        catch
        {
            RuntimeContext.Buffers.Return(packet);
            throw;
        }

        // Validation returns the packet itself through this helper when it rejects the frame.
        ValidateOutboundPacketOrReturn(packet, allowEmpty: false);
        return packet;
    }

    /// <summary>
    /// Attempts the synchronous half of a send: admission of one prepared frame into the send queue.
    /// </summary>
    /// <remarks>
    /// This is the linearization point that a caller runs under its own completion gate, so it never
    /// awaits and never invokes user code. The frame owns the packet on entry, so every rejected
    /// result has already returned it through the pump. Only an accepted frame produces an
    /// <paramref name="emission"/> waiter.
    /// </remarks>
    /// <param name="packet">A frame that <see cref="PrepareOutboundFrame"/> already accepted.</param>
    /// <param name="observeEmission">Whether the caller needs a waiter for the transport write.</param>
    /// <param name="cancellationToken">Caller cancellation observed while waiting for emission.</param>
    /// <param name="failureObserver">Observer notified when the queued frame fails in the transport.</param>
    /// <param name="emission">
    /// The emission waiter for an accepted frame, or <c>default</c> for every other outcome.
    /// </param>
    /// <param name="rejection">
    /// Set when the frame never reached the queue. The packet has already been returned to the pool
    /// on that path, so the caller must not return it again.
    /// </param>
    /// <returns><c>true</c> when the frame is in the send queue.</returns>
    internal bool TryEnqueuePreparedFrame(
        IRpcByteBufferWriter packet,
        bool observeEmission,
        CancellationToken cancellationToken,
        IRequestEmissionFailureObserver? failureObserver,
        out ValueTask emission,
        out Exception? rejection)
    {
        emission = default;
        rejection = null;
        SendPump pump;
        try
        {
            pump = GetOrCreatePumpOrReturn(packet);
        }
        catch (Exception exception)
        {
            // The pump helper returns the packet on every failure path, including a terminal race.
            rejection = exception;
            return false;
        }

        var completion = observeEmission
            ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        var result = pump.TryEnqueue(CreateFrame(
            packet,
            forceFlush: false,
            flushCompletion: completion,
            failureObserver));
        if (result != SendEnqueueResult.Accepted)
        {
            rejection = CreateSendEnqueueFailure(result);
            return false;
        }

        if (completion is not null)
            emission = AwaitEmissionAsync(completion, cancellationToken);
        return true;
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
