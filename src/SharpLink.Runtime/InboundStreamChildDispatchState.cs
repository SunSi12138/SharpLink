namespace SharpLink.Runtime;

/// <summary>
/// Gives an attached typed stream dispatcher a lifecycle that is independent from the stable
/// inbound route stored in <see cref="StreamManager"/>.
/// </summary>
internal sealed class InboundStreamChildDispatchState(IStreamDispatchLease? lease) : IStreamDispatchState
{
    private const int ClosedMask = int.MinValue;
    private const int CountMask = int.MaxValue;
    private int _state;
    private int _detached;
    private int _drainedNotified;
    private Completions? _completions;

    internal bool IsClosed => (Volatile.Read(ref _state) & ClosedMask) != 0;

    public bool HasActiveDispatches => (Volatile.Read(ref _state) & CountMask) != 0;

    public bool IsDetached => Volatile.Read(ref _detached) != 0;

    internal bool TryAcquire()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if ((state & ClosedMask) != 0 || (state & CountMask) == CountMask)
                return false;
            if (Interlocked.CompareExchange(ref _state, state + 1, state) == state)
                return true;
        }
    }

    internal void Release()
    {
        var state = Interlocked.Decrement(ref _state);
        if ((state & CountMask) == CountMask)
            throw new InvalidOperationException("Attached stream dispatcher lease underflowed.");
        if ((state & ClosedMask) != 0 && (state & CountMask) == 0)
        {
            Volatile.Read(ref _completions)?.SignalDispatchesDrained();
            NotifyLeaseDrainedIfDetached();
        }
    }

    public void Close()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if ((state & ClosedMask) != 0)
                break;
            if (Interlocked.CompareExchange(ref _state, state | ClosedMask, state) == state)
                break;
        }
        if (!HasActiveDispatches)
            Volatile.Read(ref _completions)?.SignalDispatchesDrained();
    }

    internal void Detach()
    {
        Close();
        if (Interlocked.Exchange(ref _detached, 1) == 0)
            Volatile.Read(ref _completions)?.SignalDetached();
        NotifyLeaseDrainedIfDetached();
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

    private void NotifyLeaseDrainedIfDetached()
    {
        if (!IsDetached || HasActiveDispatches ||
            Interlocked.Exchange(ref _drainedNotified, 1) != 0)
        {
            return;
        }
        lease?.OnDispatchesDrained();
    }

    private Completions GetOrCreateCompletions()
    {
        var completions = Volatile.Read(ref _completions);
        if (completions is not null)
            return completions;
        var created = new Completions();
        return Interlocked.CompareExchange(ref _completions, created, null) ?? created;
    }

    private sealed class Completions
    {
        private int _dispatchesDrainedSignaled;
        private int _detachedSignaled;
        private TaskCompletionSource? _dispatchesDrainedCompletion;
        private TaskCompletionSource? _detachedCompletion;

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

        private static TaskCompletionSource GetOrCreateCompletion(ref TaskCompletionSource? completion)
        {
            var existing = Volatile.Read(ref completion);
            if (existing is not null)
                return existing;
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return Interlocked.CompareExchange(ref completion, created, null) ?? created;
        }
    }
}
