namespace SharpLink.Runtime;

internal sealed partial class StreamManager
{
    private readonly record struct RequestDrainEntry(ushort StreamId, DispatcherEntry Entry);

    private sealed class RequestDispatchers
    {
        private DispatcherEntry? _defaultDispatcher;
        private readonly Lock _gate = new();
        private readonly Dictionary<ushort, DispatcherEntry> _byStreamId = [];

        public bool TryRegister(
            long requestId,
            ushort streamId,
            DispatcherEntry entry,
            Func<long, ushort, StreamFlowController.ResolvedReceiveCreditLease>? resolveReceiveCreditLease,
            out StreamFlowController.ResolvedReceiveCreditLease receiveCreditLease)
        {
            lock (_gate)
            {
                if (streamId == 0 ? _defaultDispatcher is not null : _byStreamId.ContainsKey(streamId))
                {
                    receiveCreditLease = default;
                    return false;
                }

                // The entry has already bound its dispatcher. Only a free route key may
                // attach receive flow state, and only this captured generation may be rolled back.
                try
                {
                    receiveCreditLease = resolveReceiveCreditLease?.Invoke(requestId, streamId) ?? default;
                    entry.ReceiveCreditLease = receiveCreditLease;
                    if (streamId == 0)
                        Volatile.Write(ref _defaultDispatcher, entry);
                    else
                        _byStreamId.Add(streamId, entry);
                    return true;
                }
                catch
                {
                    // If insertion failed after resolution, retire only the generation this
                    // unpublished entry captured. Never re-resolve a route key during rollback.
                    var lease = entry.ReceiveCreditLease;
                    _ = lease.Owner?.FlushConsumed(in lease);
                    throw;
                }
            }
        }

        public bool TryAttachPreAdmission(
            ushort streamId,
            IStreamDispatcher dispatcher,
            out bool alreadyCompleted)
        {
            alreadyCompleted = false;
            if (streamId == 0)
            {
                var entry = Volatile.Read(ref _defaultDispatcher);
                if (entry?.Dispatcher is not PreAdmissionStreamDispatcher preAdmission ||
                    !entry.TryAcquire())
                {
                    return false;
                }
                try
                {
                    if (!preAdmission.TryBeginAttach(dispatcher, out alreadyCompleted))
                        return false;
                    preAdmission.FinishAttach(dispatcher);
                    return true;
                }
                finally
                {
                    entry.Release();
                }
            }

            DispatcherEntry? acquiredEntry;
            PreAdmissionStreamDispatcher? acquiredPreAdmission;
            lock (_gate)
            {
                if (!_byStreamId.TryGetValue(streamId, out var entry) ||
                    entry.Dispatcher is not PreAdmissionStreamDispatcher preAdmission ||
                    !entry.TryAcquire())
                {
                    return false;
                }
                if (!preAdmission.TryBeginAttach(dispatcher, out alreadyCompleted))
                {
                    entry.Release();
                    return false;
                }
                acquiredEntry = entry;
                acquiredPreAdmission = preAdmission;
            }
            try
            {
                acquiredPreAdmission.FinishAttach(dispatcher);
                return true;
            }
            finally
            {
                acquiredEntry.Release();
            }
        }

        public bool TryAbandonInboundRoute(ushort streamId, out bool peerTerminalReceived)
        {
            peerTerminalReceived = false;
            if (streamId == 0)
            {
                var entry = Volatile.Read(ref _defaultDispatcher);
                if (entry?.Dispatcher is not PreAdmissionStreamDispatcher preAdmission ||
                    !entry.TryAcquire())
                {
                    return false;
                }
                try
                {
                    preAdmission.Abandon(out _);
                    peerTerminalReceived = entry.PeerTerminalReceived;
                    return true;
                }
                finally
                {
                    entry.Release();
                }
            }

            DispatcherEntry? acquiredEntry;
            PreAdmissionStreamDispatcher? acquiredPreAdmission;
            lock (_gate)
            {
                if (!_byStreamId.TryGetValue(streamId, out var entry) ||
                    entry.Dispatcher is not PreAdmissionStreamDispatcher preAdmission ||
                    !entry.TryAcquire())
                {
                    return false;
                }
                acquiredEntry = entry;
                acquiredPreAdmission = preAdmission;
            }
            try
            {
                acquiredPreAdmission.Abandon(out _);
                peerTerminalReceived = acquiredEntry.PeerTerminalReceived;
                return true;
            }
            finally
            {
                acquiredEntry.Release();
            }
        }

        public bool TryMarkPeerTerminal(ushort streamId)
        {
            if (streamId == 0)
            {
                var entry = Volatile.Read(ref _defaultDispatcher);
                if (entry is null || !entry.TryAcquire())
                    return false;
                try
                {
                    entry.MarkPeerTerminalReceived();
                    return true;
                }
                finally
                {
                    entry.Release();
                }
            }

            DispatcherEntry? acquiredEntry;
            lock (_gate)
            {
                if (!_byStreamId.TryGetValue(streamId, out var entry) || !entry.TryAcquire())
                    return false;
                acquiredEntry = entry;
            }
            try
            {
                acquiredEntry.MarkPeerTerminalReceived();
                return true;
            }
            finally
            {
                acquiredEntry.Release();
            }
        }

        public bool TryCompleteRetainedRoute(
            ushort streamId,
            Exception? exception,
            out DispatcherEntry entry)
        {
            if (streamId == 0)
            {
                var found = Volatile.Read(ref _defaultDispatcher);
                if (found?.Dispatcher is PreAdmissionStreamDispatcher preAdmission &&
                    preAdmission.TryCompleteAndRetain(exception))
                {
                    entry = found;
                    return true;
                }
                entry = null!;
                return false;
            }

            lock (_gate)
            {
                if (_byStreamId.TryGetValue(streamId, out var found) &&
                    found.Dispatcher is PreAdmissionStreamDispatcher preAdmission &&
                    preAdmission.TryCompleteAndRetain(exception))
                {
                    entry = found;
                    return true;
                }
                entry = null!;
                return false;
            }
        }

        public bool TryGetPreAdmission(
            ushort streamId,
            out PreAdmissionStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                var found = Volatile.Read(ref _defaultDispatcher)?.Dispatcher as
                    PreAdmissionStreamDispatcher;
                dispatcher = found!;
                return found is not null;
            }
            lock (_gate)
            {
                var found = _byStreamId.TryGetValue(streamId, out var entry)
                    ? entry.Dispatcher as PreAdmissionStreamDispatcher
                    : null;
                dispatcher = found!;
                return found is not null;
            }
        }

        public bool TryGetDiscarding(
            ushort streamId,
            out DiscardingStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                var found = Volatile.Read(ref _defaultDispatcher)?.Dispatcher as
                    DiscardingStreamDispatcher;
                dispatcher = found!;
                return found is not null;
            }
            lock (_gate)
            {
                var found = _byStreamId.TryGetValue(streamId, out var entry)
                    ? entry.Dispatcher as DiscardingStreamDispatcher
                    : null;
                dispatcher = found!;
                return found is not null;
            }
        }

        public bool TryAcquire(ushort streamId, out DispatcherEntry entry)
        {
            if (streamId != 0)
            {
                lock (_gate)
                {
                    if (_byStreamId.TryGetValue(streamId, out entry!) && entry.TryAcquire())
                        return true;
                    entry = null!;
                    return false;
                }
            }

            var defaultEntry = Volatile.Read(ref _defaultDispatcher);
            if (defaultEntry is not null && defaultEntry.TryAcquire())
            {
                entry = defaultEntry;
                return true;
            }

            entry = null!;
            return false;
        }

        public bool TryBeginRetirement(ushort streamId, out DispatcherEntry entry)
        {
            lock (_gate)
            {
                var found = streamId == 0
                    ? _defaultDispatcher
                    : _byStreamId.TryGetValue(streamId, out var candidate) ? candidate : null;
                if (found is null || !found.TryClaimRetirement())
                {
                    entry = null!;
                    return false;
                }

                found.Close();
                entry = found;
                return true;
            }
        }

        public bool FinishRetirement(ushort streamId, DispatcherEntry entry)
        {
            lock (_gate)
            {
                if (streamId == 0)
                {
                    if (!ReferenceEquals(_defaultDispatcher, entry))
                        return false;
                    Volatile.Write(ref _defaultDispatcher, null);
                    return true;
                }

                if (!_byStreamId.TryGetValue(streamId, out var found) ||
                    !ReferenceEquals(found, entry))
                {
                    return false;
                }

                return _byStreamId.Remove(streamId);
            }
        }

        public RequestDrainEntry[] BeginDrain()
        {
            lock (_gate)
            {
                var entries = new List<RequestDrainEntry>();
                if (_defaultDispatcher is { } defaultDispatcher &&
                    defaultDispatcher.TryClaimRetirement())
                {
                    defaultDispatcher.Close();
                    entries.Add(new RequestDrainEntry(0, defaultDispatcher));
                }

                foreach (var pair in _byStreamId)
                {
                    if (!pair.Value.TryClaimRetirement())
                        continue;
                    pair.Value.Close();
                    entries.Add(new RequestDrainEntry(pair.Key, pair.Value));
                }

                return [.. entries];
            }
        }

        public int CompleteAll(Exception? exception, ref List<Exception>? failures)
        {
            List<DispatcherEntry> claimed = [];
            lock (_gate)
            {
                if (_defaultDispatcher is { } defaultDispatcher)
                {
                    Volatile.Write(ref _defaultDispatcher, null);
                    if (defaultDispatcher.TryClaimRetirement())
                    {
                        defaultDispatcher.Close();
                        claimed.Add(defaultDispatcher);
                    }
                }

                foreach (var entry in _byStreamId.Values)
                {
                    if (!entry.TryClaimRetirement())
                        continue;
                    entry.Close();
                    claimed.Add(entry);
                }
                _byStreamId.Clear();
            }

            for (var index = 0; index < claimed.Count; index++)
                CompleteEntry(claimed[index], exception, ref failures);
            return claimed.Count;
        }

        private static void CompleteEntry(
            DispatcherEntry entry,
            Exception? exception,
            ref List<Exception>? failures)
        {
            try
            {
                entry.Dispatcher.Complete(exception);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
            try
            {
                entry.Detach();
            }
            catch (Exception detachException)
            {
                (failures ??= []).Add(detachException);
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_gate)
                    return _defaultDispatcher is null && _byStreamId.Count == 0;
            }
        }
    }

    private sealed class DispatcherEntry : IStreamDispatchState
    {
        private const long ClosedMask = long.MinValue;
        private const long DetachedMask = 1L << 32;
        private const long ReceiveRetirementPendingMask = 1L << 33;
        private const long ReceiveTerminalPublishedMask = 1L << 34;
        private const long PeerTerminalReceivedMask = 1L << 35;
        private const long RetirementClaimedMask = 1L << 36;
        private const long CountMask = int.MaxValue;
        // One atomic word orders the last release against detach. Separate count and
        // detached reads can both claim the same pooled-dispatcher drain notification.
        private long _state;
        // Cold lifecycle flags share the atomic word; acquisition count and
        // detach/cleanup ownership retain their existing, disjoint bits.
        // Lazily shares the distinct drain/detach completions without growing common entries.
        private DispatcherEntryCompletions? _completions;

        internal DispatcherEntry(
            IStreamDispatcher dispatcher,
            StreamFlowController.ResolvedReceiveCreditLease receiveCreditLease)
        {
            Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            ReceiveCreditLease = receiveCreditLease;
            if (dispatcher is IStreamDispatchLease lease)
                lease.BindDispatchState(this);
        }

        internal IStreamDispatcher Dispatcher { get; }
        internal StreamFlowController.ResolvedReceiveCreditLease ReceiveCreditLease { get; set; }

        // A detached pooled dispatcher is still pinned while final receive cleanup is
        // running, even after the final DATA acquisition has decremented the count.
        public bool HasActiveDispatches
            => (Volatile.Read(ref _state) & (CountMask | ReceiveRetirementPendingMask)) != 0;

        public bool IsDetached => (Volatile.Read(ref _state) & DetachedMask) != 0;

        internal bool PeerTerminalReceived => (Volatile.Read(ref _state) & PeerTerminalReceivedMask) != 0;

        internal void MarkPeerTerminalReceived()
            => _ = Interlocked.Or(ref _state, PeerTerminalReceivedMask);

        internal bool TryPublishReceiveTerminal()
            => (Interlocked.Or(ref _state, ReceiveTerminalPublishedMask) & ReceiveTerminalPublishedMask) == 0;

        internal bool TryClaimRetirement()
            => (Interlocked.Or(ref _state, RetirementClaimedMask) & RetirementClaimedMask) == 0;

        internal bool TryAcquire()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if ((state & ClosedMask) != 0 || (state & CountMask) == CountMask)
                    return false;
                if (Interlocked.CompareExchange(ref _state, state + 1, state) != state)
                    continue;

                return true;
            }
        }

        internal void Release()
        {
            var state = Interlocked.Decrement(ref _state);
            if ((state & CountMask) == CountMask)
                throw new InvalidOperationException("Stream dispatcher lease underflowed.");
            if ((state & ClosedMask) != 0 && (state & CountMask) == 0)
            {
                var completions = Volatile.Read(ref _completions);
                completions?.SignalDispatchesDrained();
                RunAfterDispatchesDrained(completions);
                // Use this release's atomic snapshot, not a new read after signaling:
                // a drain continuation may have detached and notified in the meantime.
                if ((state & DetachedMask) != 0 && (state & ReceiveRetirementPendingMask) == 0 &&
                    Dispatcher is IStreamDispatchLease lease)
                    lease.OnDispatchesDrained();
            }
        }

        internal void RunWhenDispatchesDrained(Action continuation)
        {
            if ((Interlocked.Or(ref _state, ReceiveRetirementPendingMask) & ReceiveRetirementPendingMask) != 0)
                throw new InvalidOperationException("Receive retirement already has a drain owner.");
            var completions = GetOrCreateCompletions();
            completions.SetAfterDispatchesDrained(continuation);
            // Close excludes new acquisitions. The cleanup pin must not count as DATA
            // when deciding who runs the continuation.
            if ((Volatile.Read(ref _state) & CountMask) == 0)
                RunAfterDispatchesDrained(completions);
        }

        private void RunAfterDispatchesDrained(DispatcherEntryCompletions? completions)
        {
            var continuation = completions?.TakeAfterDispatchesDrained();
            if (continuation is null)
                return;
            try
            {
                continuation();
            }
            finally
            {
                var state = Interlocked.And(ref _state, ~ReceiveRetirementPendingMask);
                if ((state & DetachedMask) != 0 && (state & CountMask) == 0 &&
                    Dispatcher is IStreamDispatchLease lease)
                    lease.OnDispatchesDrained();
            }
        }

        public ValueTask WaitForDispatchesDrainedAsync()
        {
            if (!HasActiveDispatches)
                return ValueTask.CompletedTask;

            var completions = GetOrCreateCompletions();
            if (!HasActiveDispatches)
            {
                completions.SignalDispatchesDrained();
                return ValueTask.CompletedTask;
            }

            return completions.WaitForDispatchesDrainedAsync();
        }

        public ValueTask WaitForDetachedAsync(CancellationToken cancellationToken)
        {
            if (IsDetached)
                return ValueTask.CompletedTask;

            var completions = GetOrCreateCompletions();
            if (IsDetached)
            {
                completions.SignalDetached();
                return ValueTask.CompletedTask;
            }

            return completions.WaitForDetachedAsync(cancellationToken);
        }

        internal bool TryClose()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if ((state & ClosedMask) != 0)
                    return false;
                if (Interlocked.CompareExchange(ref _state, state | ClosedMask, state) == state)
                    return true;
            }
        }

        public void Close() => _ = TryClose();

        internal void Detach()
        {
            var state = Interlocked.Or(ref _state, ClosedMask | DetachedMask);
            if ((state & DetachedMask) != 0)
                return;
            Volatile.Read(ref _completions)?.SignalDetached();
            // Whichever transition observes both detached and zero leases owns the
            // notification. Last Release handles a detach that still had active leases.
            if ((state & (CountMask | ReceiveRetirementPendingMask)) == 0 &&
                Dispatcher is IStreamDispatchLease lease)
                lease.OnDispatchesDrained();
        }

        private DispatcherEntryCompletions GetOrCreateCompletions()
        {
            var completions = Volatile.Read(ref _completions);
            if (completions is not null)
                return completions;

            var created = new DispatcherEntryCompletions();
            return Interlocked.CompareExchange(ref _completions, created, null) ?? created;
        }

        private sealed class DispatcherEntryCompletions
        {
            private int _dispatchesDrainedSignaled;
            private int _detachedSignaled;
            private TaskCompletionSource? _dispatchesDrainedCompletion;
            private TaskCompletionSource? _detachedCompletion;
            private Action? _afterDispatchesDrained;

            internal void SetAfterDispatchesDrained(Action continuation)
            {
                if (Interlocked.CompareExchange(ref _afterDispatchesDrained, continuation, null) is not null)
                    throw new InvalidOperationException("Receive retirement already has a drain continuation.");
            }

            internal Action? TakeAfterDispatchesDrained()
                => Interlocked.Exchange(ref _afterDispatchesDrained, null);

            internal void SignalDispatchesDrained()
            {
                if (Interlocked.Exchange(ref _dispatchesDrainedSignaled, 1) == 0)
                    Volatile.Read(ref _dispatchesDrainedCompletion)?.TrySetResult();
            }

            internal void SignalDetached()
            {
                if (Interlocked.Exchange(ref _detachedSignaled, 1) == 0)
                    Volatile.Read(ref _detachedCompletion)?.TrySetResult();
            }

            internal ValueTask WaitForDispatchesDrainedAsync()
            {
                if (Volatile.Read(ref _dispatchesDrainedSignaled) != 0)
                    return ValueTask.CompletedTask;

                var completion = GetOrCreateCompletion(ref _dispatchesDrainedCompletion);
                if (Volatile.Read(ref _dispatchesDrainedSignaled) != 0)
                    completion.TrySetResult();
                return new ValueTask(completion.Task);
            }

            internal ValueTask WaitForDetachedAsync(CancellationToken cancellationToken)
            {
                if (Volatile.Read(ref _detachedSignaled) != 0)
                    return ValueTask.CompletedTask;

                var completion = GetOrCreateCompletion(ref _detachedCompletion);
                if (Volatile.Read(ref _detachedSignaled) != 0)
                    completion.TrySetResult();
                return cancellationToken.CanBeCanceled
                    ? new ValueTask(completion.Task.WaitAsync(cancellationToken))
                    : new ValueTask(completion.Task);
            }

            private static TaskCompletionSource GetOrCreateCompletion(
                ref TaskCompletionSource? completion)
            {
                var existing = Volatile.Read(ref completion);
                if (existing is not null)
                    return existing;

                var created = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return Interlocked.CompareExchange(ref completion, created, null) ?? created;
            }
        }
    }
}
