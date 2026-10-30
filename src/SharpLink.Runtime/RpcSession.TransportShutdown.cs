namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private Task StartTransportDispose()
    {
        lock (_transportDisposeGate)
            return _transportDisposeTask ??= DisposeTransportAfterPumpAsync();
    }

    private async Task DisposeTransportAfterPumpAsync()
    {
        // Fault can run inside the pump itself. Publish this owner and return to that pump
        // before aborting user I/O or joining its physical lifetime; never inline cleanup here.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var pump = CapturePumpForStop();
        pump?.Stop();
        var writesDrained = pump?.WaitForStopAsync().AsTask() ?? Task.CompletedTask;
        // The public terminal winner and physical output disposition are separate.
        // Only a successful physical join with no failed/canceled output can flush.
        // A prior Stopping/read-side winner must never authorize replay after a later
        // write failure; its exception identity remains selected by the original CAS.
        if (writesDrained.IsCompletedSuccessfully && pump?.RequiresTerminalOutputDiscard != true)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            return;
        }
        if (_transport is IQuiescingTransportConnection quiescing)
        {
            await quiescing.DisposeAfterWritesAsync(writesDrained, GetTerminalException()).ConfigureAwait(false);
            return;
        }

        // Custom transports own their abort semantics. Request cancellation without completing
        // either pipe, then retain ownership if the actual writer ignores that request.
        Exception? cleanupException = null;
        try { _transport.Input.CancelPendingRead(); }
        catch (Exception exception) { cleanupException = exception; }
        try { _transport.Output.CancelPendingFlush(); }
        catch (Exception exception) { cleanupException = CombineCleanupExceptions(cleanupException, exception); }
        try { await writesDrained.ConfigureAwait(false); }
        catch (Exception exception) { cleanupException = CombineCleanupExceptions(cleanupException, exception); }
        try
        {
            var terminal = Volatile.Read(ref _terminal);
            if (terminal is { State: SessionTerminalState.QuiescentPumpFault } &&
                _transport is IQuiescentPumpFaultDisposal faultedOutput)
                await faultedOutput.DisposeAfterPumpFaultAsync(terminal.Exception).ConfigureAwait(false);
            else
                await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { cleanupException = CombineCleanupExceptions(cleanupException, exception); }
        if (cleanupException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }

    private static void ObserveTransportDispose(Task disposeTask)
    {
        if (!disposeTask.IsCompletedSuccessfully)
            _ = ObserveTransportDisposeAsync(disposeTask);
    }

    private static async Task ObserveTransportDisposeAsync(Task disposeTask)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch
        {
            // DisposeAsync awaits the same single-flight task and preserves this failure for the owner.
        }
    }
}
