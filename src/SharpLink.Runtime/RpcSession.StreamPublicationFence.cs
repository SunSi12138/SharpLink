namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    /// <summary>
    /// Called once by the original physical producer-group owner AFTER joining every
    /// MoveNext/send/Dispose tail. That original admission reservation remains held until
    /// this method ends; zero wire bytes do not bound outstanding fences by queue bytes.
    /// </summary>
    internal ValueTask CompleteStreamPublicationAsync(RpcStreamPublication group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!ReferenceEquals(group.Session, this)) throw new InvalidOperationException("Stream fence belongs to another Session.");
        group.BeginFence();
        return CompleteStreamFenceCoreAsync(group, RpcFramePublicationRole.StreamFence);
    }

    // Called by the one cleanup publisher before producer joins. Does not seal producers,
    // end their ownership, or replace the later final fence after BOTH branches join.
    internal ValueTask CompleteStreamCleanupPublicationAsync(RpcStreamPublication group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!ReferenceEquals(group.Session, this)) throw new InvalidOperationException("Stream fence belongs to another Session.");
        group.BeginCleanupFence();
        return CompleteStreamFenceCoreAsync(group, RpcFramePublicationRole.StreamCleanupFence);
    }

    private async ValueTask CompleteStreamFenceCoreAsync(RpcStreamPublication group, RpcFramePublicationRole role)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!ReferenceEquals(group.Session, this))
            throw new InvalidOperationException("Stream fence belongs to another Session.");
        TaskCompletionSource<bool>? completion = null;
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = false;
        Exception? failure = null;
        try
        {
            writer = RuntimeContext.Buffers.Rent();
            ownsWriter = true;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Each helper takes the writer on entry and returns it itself on failure.
            ownsWriter = false;
            var pump = GetOrCreatePumpOrReturn(writer);
            var frame = CreateFrame(writer, forceFlush: false, completion,
                publication: group, publicationRole: role);
            var result = pump.TryEnqueue(frame);
            if (result != SendEnqueueResult.Accepted)
                throw CreateSendEnqueueFailure(result);
            // No caller-cancelled WaitAsync wrapper: actual frame cleanup owns this signal.
            // Ordinary batching may naturally issue an empty FlushAsync for this marker.
            await completion.Task.ConfigureAwait(false);
            if (pump.IsStopRequested)
                throw GetTerminalException();
            return;
        }
        catch (Exception exception) { failure = exception; }

        if (ownsWriter && writer is not null)
        {
            try { RuntimeContext.Buffers.Return(writer); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        }
        // A failed/neveraccepted marker says nothing about preceding frame ownership.
        try { await CloseAndDrainOutboundAsync(failure!).ConfigureAwait(false); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        // An uncertain publication may have completed during the exact pump drain.
        if (completion?.Task.IsCompleted == true)
        {
            try { await completion.Task.ConfigureAwait(false); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        }
        failure = RpcPublicationErrors.Combine(failure, group.CleanupFailure);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
    }

    /// <summary>
    /// Cold failure cleanup only. The reason terminalizes this exact Session; successful
    /// physical drain returns normally. Cleanup faults are retained with the reason.
    /// This joins neither ClientConnection.Dispose nor whole Session.Dispose.
    /// </summary>
    internal async ValueTask CloseAndDrainOutboundAsync(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Exception? cleanupFailure = null;
        try { Fault(failure); }
        catch (Exception cleanup) { cleanupFailure = cleanup; }
        // Fault publishes terminal state before external telemetry/cancellation callbacks.
        // A callback failure must not skip cancellation or the exact constructor/stop join.
        try { CancelSession(); }
        catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
        var pump = CapturePumpForStop();
        if (pump is not null)
        {
            try { pump.Stop(); }
            catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
            try { await pump.WaitForStopAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { cleanupFailure = RpcPublicationErrors.Combine(cleanupFailure, cleanup); }
        }
        if (cleanupFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                RpcPublicationErrors.Combine(failure, cleanupFailure)!).Throw();
    }

}
