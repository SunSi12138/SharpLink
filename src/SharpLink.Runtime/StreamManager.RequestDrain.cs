namespace SharpLink.Runtime;

internal sealed partial class StreamManager
{
    internal void CompleteRequestStreams(long requestId, Exception? exception)
    {
        List<Exception>? failures = null;
        var entries = BeginRequestDrain(requestId, exception, ref failures);
        FinalizeRequestDrain(requestId, entries, ref failures, mayHaveActiveDispatches: true);
        ThrowCompletionFailures(failures);
    }

    /// <summary>
    /// Removes every receive stream owned by one request and waits for StreamData dispatches that
    /// acquired their entries before removal. Callers may release request-owned Codec/module state
    /// only after this barrier completes.
    /// </summary>
    internal ValueTask CompleteRequestStreamsAfterDispatchesAsync(long requestId, Exception? exception)
    {
        List<Exception>? failures = null;
        var entries = BeginRequestDrain(requestId, exception, ref failures);
        if (entries.Length == 0)
            return ValueTask.CompletedTask;

        if (entries.All(static item => !item.Entry.HasActiveDispatches))
        {
            FinalizeRequestDrain(requestId, entries, ref failures);
            ThrowCompletionFailures(failures);
            return ValueTask.CompletedTask;
        }

        return AwaitRequestDispatchesAndFinalizeAsync(requestId, entries, failures);
    }

    private RequestDrainEntry[] BeginRequestDrain(long requestId, Exception? exception, ref List<Exception>? failures)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryRemove(requestId, out var requestDispatchers))
            return [];

        var entries = requestDispatchers.TakeAllForDrain();
        if (entries.Length == 0)
            return entries;

        SharpLinkTelemetry.AddActiveStreams(-entries.Length);
        Interlocked.Add(ref _activeStreamCount, -entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            try
            {
                entries[index].Entry.Dispatcher.Complete(exception);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
        }

        return entries;
    }

    private async ValueTask AwaitRequestDispatchesAndFinalizeAsync(
        long requestId,
        RequestDrainEntry[] entries,
        List<Exception>? failures)
    {
        for (var index = 0; index < entries.Length; index++)
            await entries[index].Entry.WaitForDispatchesDrainedAsync().ConfigureAwait(false);

        FinalizeRequestDrain(requestId, entries, ref failures);
        ThrowCompletionFailures(failures);
    }

    private void FinalizeRequestDrain(
        long requestId,
        RequestDrainEntry[] entries,
        ref List<Exception>? failures,
        bool mayHaveActiveDispatches = false)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var item = entries[index];
            try
            {
                // Leave the callback installed through Detach: a mailbox can still own raw
                // input or a child with buffered bytes, even if no peer EOF ever arrived.
                item.Entry.Detach();
            }
            catch (Exception detachException)
            {
                (failures ??= []).Add(detachException);
            }
            try
            {
                if (mayHaveActiveDispatches && item.Entry.HasActiveDispatches)
                {
                    // An already-acquired frame may be about to debit its first bytes.
                    // Its route owns the final WindowUpdate until the last lease releases.
                    item.Entry.AfterDispatchesDrained(() => PublishDeferredReceiveTerminal(requestId, item));
                }
                else
                    PublishReceiveTerminal(requestId, item.StreamId, item.Entry);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
        }
    }

    private void PublishDeferredReceiveTerminal(long requestId, RequestDrainEntry entry)
    {
        try { PublishReceiveTerminal(requestId, entry.StreamId, entry.Entry); }
        catch (Exception exception) { _activeStreamCapacityExceeded?.Invoke(exception); }
    }
}
