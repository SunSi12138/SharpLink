namespace SharpLink.Runtime;

internal sealed partial class StreamManager
{
    private void RetireStreamAfterDispatches(
        long requestId, ushort streamId, RequestDispatchers requestDispatchers,
        DispatcherEntry entry, bool clearCallback)
    {
        if (entry.HasActiveDispatches)
        {
            ScheduleStreamRetirement(requestId, streamId, requestDispatchers, entry, clearCallback);
            return;
        }
        FinishStreamRetirement(requestId, streamId, requestDispatchers, entry, clearCallback);
    }

    private void ScheduleStreamRetirement(
        long requestId, ushort streamId, RequestDispatchers requestDispatchers,
        DispatcherEntry entry, bool clearCallback)
    {
        // Keep closure allocation out of the ordinary already-drained lifecycle path.
        entry.RunWhenDispatchesDrained(() => FinishStreamRetirement(
            requestId, streamId, requestDispatchers, entry, clearCallback));
        // Preserve the existing immediate detach notification. The atomic cleanup pin
        // prevents pool return until callbacks, credit and route retirement are finished.
        entry.Detach();
    }

    private void FinishStreamRetirement(
        long requestId, ushort streamId, RequestDispatchers requestDispatchers,
        DispatcherEntry entry, bool clearCallback)
    {
        try
        {
            if (clearCallback) ClearBytesConsumedCallback(entry.Dispatcher);
        }
        finally
        {
            try
            {
                PublishReceiveTerminal(requestId, streamId, entry);
            }
            finally
            {
                _ = requestDispatchers.FinishRetirement(streamId, entry);
                entry.Detach();
                RemoveEmptyRequest(requestId, requestDispatchers);
            }
        }
    }

    // Cold path only: completing a request must not wait on an acquired dispatch on the
    // caller's thread, nor detach the receive callback that dispatch still owns.
    private void CompleteRequestEntriesWhenDrained(
        long requestId,
        RequestDispatchers requestDispatchers,
        RequestDrainEntry[] entries,
        Exception? exception,
        ref List<Exception>? failures)
    {
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

        var retirement = new DeferredRequestRetirement(this, requestId, requestDispatchers, entries);
        for (var index = 0; index < entries.Length; index++)
            entries[index].Entry.RunWhenDispatchesDrained(retirement.NotifyDrained);
        // The scheduling reference prevents an inline last-release from finalizing
        // before every entry has installed its one-shot notification.
        retirement.NotifyDrained();
    }

    private sealed class DeferredRequestRetirement
    {
        private readonly StreamManager _owner;
        private readonly long _requestId;
        private readonly RequestDispatchers _requestDispatchers;
        private readonly RequestDrainEntry[] _entries;
        private int _remaining;
        internal Action NotifyDrained { get; }

        internal DeferredRequestRetirement(
            StreamManager owner, long requestId, RequestDispatchers requestDispatchers,
            RequestDrainEntry[] entries)
        {
            _owner = owner;
            _requestId = requestId;
            _requestDispatchers = requestDispatchers;
            _entries = entries;
            _remaining = entries.Length + 1;
            NotifyDrained = OnDrained;
        }

        private void OnDrained()
        {
            if (Interlocked.Decrement(ref _remaining) != 0)
                return;
            List<Exception>? failures = null;
            _owner.FinalizeRequestDrain(_requestId, _requestDispatchers, _entries, ref failures);
            // No fire-and-forget Task hides a cleanup failure: the final release (or the
            // synchronous completion if already drained) observes it exactly once.
            ThrowCompletionFailures(failures);
        }
    }
}
