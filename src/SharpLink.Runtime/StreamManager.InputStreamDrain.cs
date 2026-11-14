namespace SharpLink.Runtime;

internal sealed partial class StreamManager
{
    // Capture stable mailboxes before user invocation: session teardown may remove the
    // routing entries, but cannot invalidate their already-admitted physical children.
    internal InputStreamDrain CaptureInputStreamDrain(long requestId, int streamCount)
    {
        var routes = Volatile.Read(ref _dispatchersByRequestId);
        if (routes is null || !routes.TryGetValue(requestId, out var request))
            return default;
        return request.CaptureInputStreamDrain(streamCount);
    }

    internal readonly struct InputStreamDrain(
        PreAdmissionStreamDispatcher? first,
        PreAdmissionStreamDispatcher?[]? remaining)
    {
        internal bool IsEmpty => first is null && remaining is null;

        internal async ValueTask CompleteAsync()
        {
            Exception? failure = null;
            // Every child gate closes before the first await. A dispatch that won its
            // mailbox gate before Abandon remains counted by that same mailbox.
            Close(first, ref failure);
            if (remaining is not null)
                foreach (var child in remaining)
                    Close(child, ref failure);
            await JoinAsync(first).ConfigureAwait(false);
            if (remaining is not null)
                foreach (var child in remaining)
                    await JoinAsync(child).ConfigureAwait(false);
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Close(PreAdmissionStreamDispatcher? child, ref Exception? failure)
        {
            try { child?.Abandon(out _); }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
        }

        private static ValueTask JoinAsync(PreAdmissionStreamDispatcher? child)
            => child?.WaitForAbandonedChildAsync() ?? ValueTask.CompletedTask;
    }

    private sealed partial class RequestDispatchers
    {
        internal InputStreamDrain CaptureInputStreamDrain(int count)
        {
            lock (_gate)
            {
                if (_byStreamId is null)
                    return default;
                var first = _byStreamId.TryGetValue(1, out var entry)
                    ? entry.Dispatcher as PreAdmissionStreamDispatcher : null;
                PreAdmissionStreamDispatcher?[]? remaining = null;
                if (count > 1)
                {
                    remaining = new PreAdmissionStreamDispatcher?[count - 1];
                    for (var id = 2; id <= count; id++)
                        if (_byStreamId.TryGetValue((ushort)id, out entry))
                            remaining[id - 2] = entry.Dispatcher as PreAdmissionStreamDispatcher;
                }
                return new InputStreamDrain(first, remaining);
            }
        }
    }
}

