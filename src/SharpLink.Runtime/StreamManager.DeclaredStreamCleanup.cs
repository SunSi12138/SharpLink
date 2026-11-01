namespace SharpLink.Runtime;

internal sealed partial class StreamManager
{
    // Mutable only for the ONE cold transfer claim. The first entry is the copy-safe ownership
    // token shared by value-handle copies; this is not a counter or a new heap owner.
    private struct CapturedStreamEntry(ushort streamId, DispatcherEntry entry)
    {
        internal readonly ushort StreamId = streamId;
        internal DispatcherEntry? Entry = entry;
    }

    internal readonly struct CapturedRequestStreamCleanup : IDisposable
    {
        private readonly StreamManager? _owner;
        private readonly long _requestId;
        private readonly RequestDispatchers? _routes;
        private readonly CapturedStreamEntry[]? _entries;
        private readonly int _count;

        private CapturedRequestStreamCleanup(StreamManager owner, long requestId,
            RequestDispatchers routes, CapturedStreamEntry[] entries, int count)
        {
            _owner = owner;
            _requestId = requestId;
            _routes = routes;
            _entries = entries;
            _count = count;
        }

        internal static CapturedRequestStreamCleanup Capture(StreamManager owner, long requestId, int streamCount)
        {
            if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
            if (streamCount is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(streamCount));
            if (streamCount == 0) return default;
            var map = Volatile.Read(ref owner._dispatchersByRequestId);
            if (map is null || !map.TryGetValue(requestId, out var routes)) return default;
            var entries = new CapturedStreamEntry[streamCount];
            var count = 0;
            try
            {
                for (var index = 0; index < streamCount; index++)
                {
                    var streamId = checked((ushort)(index + 1));
                    if (routes.TryAcquire(streamId, out var entry))
                        entries[count++] = new CapturedStreamEntry(streamId, entry);
                }
                return count == 0 ? default : new CapturedRequestStreamCleanup(owner, requestId, routes, entries, count);
            }
            catch (Exception failure)
            {
                for (var index = 0; index < count; index++)
                {
                    try { entries[index].Entry!.Release(); }
                    catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
                }
                Array.Clear(entries);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw new UnreachableException();
            }
        }

        internal static bool TryCaptureForInvocation(StreamManager owner, long requestId,
            int streamCount, out CapturedRequestStreamCleanup capture)
        {
            capture = default;
            if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
            if (streamCount is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(streamCount));
            if (owner.IsTerminated) return false;
            var map = Volatile.Read(ref owner._dispatchersByRequestId);
            if (map is null || !map.TryGetValue(requestId, out var routes)) return false;
            var entries = new CapturedStreamEntry[streamCount];
            var count = 0;
            Exception? failure = null;
            try
            {
                for (var index = 0; index < streamCount; index++)
                {
                    var streamId = checked((ushort)(index + 1));
                    if (!routes.TryAcquire(streamId, out var entry)) break;
                    // Record ownership BEFORE inspecting readiness, so every rejection releases it.
                    entries[count++] = new CapturedStreamEntry(streamId, entry);
                    if (entry.Dispatcher is not PreAdmissionStreamDispatcher mailbox ||
                        !mailbox.IsAvailableForInvocationCapture) break;
                }
                var complete = count == streamCount;
                for (var index = 0; complete && index < count; index++)
                {
                    var entry = entries[index].Entry!;
                    complete = !entry.IsClosed && entry.Dispatcher is PreAdmissionStreamDispatcher mailbox &&
                        mailbox.IsAvailableForInvocationCapture;
                }
                // A close after this final check is post-capture: the exact original references
                // remain owned, and must be joined even when the Session removes its route map.
                if (complete && !owner.IsTerminated)
                {
                    capture = new CapturedRequestStreamCleanup(owner, requestId, routes, entries, count);
                    return true;
                }
            }
            catch (Exception error) { failure = error; }
            // Refusal is never an empty successful invocation capture. Rollback may run final
            // entry callbacks after a concurrent close; attempt every original reference first.
            for (var index = 0; index < count; index++)
            {
                try { entries[index].Entry!.Release(); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            }
            Array.Clear(entries);
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return false;
        }

        internal ValueTask Execute(StreamManager owner, Task? beforeAbandon, bool prerequisiteFailureOwnedByCaller)
        {
            if (_entries is null) return ValueTask.CompletedTask;
            if (!ReferenceEquals(_owner, owner))
                throw new InvalidOperationException("Captured inbound cleanup belongs to another StreamManager.");
            var first = Interlocked.Exchange(ref _entries[0].Entry, null);
            if (first is null)
                throw new InvalidOperationException("Captured inbound cleanup has already transferred or been disposed.");
            // The shared-array claim is final BEFORE any asynchronous or arbitrary child code.
            return owner.AbandonCapturedStreamsAsync(_requestId, _routes!, _entries, _count, first, beforeAbandon, prerequisiteFailureOwnedByCaller);
        }

        /// <summary>
        /// Pre-commit capture rollback only. Releases temporary entry acquisitions, not service
        /// ownership and not a proof of child physical cleanup. The caller still owns normal
        /// teardown unless Execute has claimed the handle. Copied losing Dispose is a no-op.
        /// </summary>
        public void Dispose()
        {
            if (_entries is null) return;
            var first = Interlocked.Exchange(ref _entries[0].Entry, null);
            if (first is null) return;
            Exception? failure = null;
            for (var index = 0; index < _count; index++)
            {
                var entry = index == 0 ? first : _entries[index].Entry!;
                try { entry.Release(); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            }
            Array.Clear(_entries);
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Callback-free capture. Server commits its EXISTING permit owner before Execute.</summary>
    internal CapturedRequestStreamCleanup CaptureRequestStreamsForCleanup(long requestId, int streamCount)
        => CapturedRequestStreamCleanup.Capture(this, requestId, streamCount);

    /// <summary>
    /// Invocation-entry admission: requires every declared stable mailbox before any factory,
    /// service acquisition or user code. Successful capture is callback-free; failed capture
    /// independently rolls back all acquired references and can report cleanup failures.
    /// </summary>
    internal bool TryCaptureRequestStreamsForInvocation(long requestId, int streamCount,
        out CapturedRequestStreamCleanup capture)
        => CapturedRequestStreamCleanup.TryCaptureForInvocation(this, requestId, streamCount, out capture);

    internal ValueTask AbandonCapturedRequestStreamsAsync(
        ref CapturedRequestStreamCleanup capture, Task? beforeAbandon = null,
        bool prerequisiteFailureOwnedByCaller = false)
    {
        // True is an explicit error-ownership transfer contract: this caller independently
        // joins/retains the prerequisite outcome. Children still wait for its physical exit.
        var operation = capture.Execute(this, beforeAbandon, prerequisiteFailureOwnedByCaller);
        capture = default; // Convenience only; shared-array atomic claim is the actual copy guard.
        return operation;
    }

    // Non-gated component callers use the SAME capture/execute path; no alternative ownership mode.
    internal ValueTask AbandonRequestStreamsAfterDispatchesAsync(
        long requestId, int streamCount, Task? beforeAbandon = null)
    {
        var capture = CaptureRequestStreamsForCleanup(requestId, streamCount);
        try { return AbandonCapturedRequestStreamsAsync(ref capture, beforeAbandon); }
        finally { capture.Dispose(); }
    }

    private async ValueTask AbandonCapturedStreamsAsync(long requestId, RequestDispatchers routes,
        CapturedStreamEntry[] captured, int count, DispatcherEntry first, Task? beforeAbandon,
        bool prerequisiteFailureOwnedByCaller)
    {
        // No ForceYield/Task.Run. The caller committed ownership before entering here. Fully
        // synchronous/no-child cleanup uses the async ValueTask builder's synchronous fast path.
        Exception? failure = null;
        if (beforeAbandon is not null)
        {
            try { await beforeAbandon.ConfigureAwait(false); }
            catch (Exception error)
            {
                if (!prerequisiteFailureOwnedByCaller) failure = error;
            }
        }
        // All children reach the no-new-business-dispatch cut before awaiting any one tail.
        for (var index = 0; index < count; index++)
        {
            var entry = index == 0 ? first : captured[index].Entry!;
            var streamId = captured[index].StreamId;
            if (entry.Dispatcher is not PreAdmissionStreamDispatcher mailbox)
            {
                failure = RpcPublicationErrors.Combine(failure, new InvalidOperationException(
                    "Declared inbound cleanup lost its stable pre-admission mailbox."));
                continue;
            }
            try { mailbox.Abandon(out _); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            if (entry.PeerTerminalReceived && routes.TryRemoveExact(streamId, mailbox, out var removed))
            {
                try { FinalizeRemovedEntry(requestId, streamId, routes, removed,
                    completeDispatcher: false, exception: null, clearConsumptionCallback: false); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            }
        }
        // Parent acquisitions protect exact payload through the prerequisite; release them
        // before child joins to avoid a detached-entry/child self-wait cycle.
        for (var index = 0; index < count; index++)
        {
            var entry = index == 0 ? first : captured[index].Entry!;
            try { entry.Release(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        }
        for (var index = 0; index < count; index++)
        {
            var entry = index == 0 ? first : captured[index].Entry!;
            if (entry.Dispatcher is not PreAdmissionStreamDispatcher mailbox) continue;
            try { await mailbox.WaitForPhysicalChildCleanupAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        }
        Array.Clear(captured);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
