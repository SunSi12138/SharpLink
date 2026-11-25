namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<T> CompleteLogicalInvocation<T>(ValueTask<T> invocation)
    {
        if (invocation.IsCompleted)
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            return invocation;
        }
        return AwaitLogicalInvocationAsync(invocation);
    }

    private ValueTask CompleteLogicalInvocation(ValueTask invocation)
    {
        if (invocation.IsCompleted)
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            return invocation;
        }
        return AwaitLogicalInvocationAsync(invocation);
    }

    private async ValueTask<T> AwaitLogicalInvocationAsync<T>(ValueTask<T> invocation)
    {
        try { return await invocation.ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _activeLogicalInvocations); }
    }

    private async ValueTask AwaitLogicalInvocationAsync(ValueTask invocation)
    {
        try { await invocation.ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _activeLogicalInvocations); }
    }

    private IAsyncEnumerable<T> CompleteLogicalInvocation<T>(IAsyncEnumerable<T> invocation)
        => new LogicalInvocationAsyncEnumerable<T>(this, invocation);

    private sealed class LogicalInvocationAsyncEnumerable<T>(
        SharpLinkClient client,
        IAsyncEnumerable<T> invocation) : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        private int _enumerated;
        private int _completed;
        private IAsyncEnumerator<T>? _enumerator;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) != 0)
                throw new InvalidOperationException("A logical RPC stream can only be enumerated once.");
            try
            {
                _enumerator = invocation.GetAsyncEnumerator(cancellationToken);
                return this;
            }
            catch
            {
                Complete();
                throw;
            }
        }

        public T Current => (_enumerator ?? throw new InvalidOperationException(
            "The logical RPC stream has not been enumerated.")).Current;

        public ValueTask<bool> MoveNextAsync()
        {
            try
            {
                var move = (_enumerator ?? throw new InvalidOperationException(
                    "The logical RPC stream has not been enumerated.")).MoveNextAsync();
                if (!move.IsCompletedSuccessfully)
                    return AwaitMoveNextAsync(move);
                if (!move.Result)
                    Complete();
                return move;
            }
            catch
            {
                Complete();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                var dispose = _enumerator?.DisposeAsync() ?? ValueTask.CompletedTask;
                if (!dispose.IsCompleted)
                    return AwaitDisposeAsync(dispose);
                Complete();
                return dispose;
            }
            catch
            {
                Complete();
                throw;
            }
        }

        private async ValueTask<bool> AwaitMoveNextAsync(ValueTask<bool> move)
        {
            try
            {
                var hasNext = await move.ConfigureAwait(false);
                if (!hasNext)
                    Complete();
                return hasNext;
            }
            catch
            {
                Complete();
                throw;
            }
        }

        private async ValueTask AwaitDisposeAsync(ValueTask dispose)
        {
            try { await dispose.ConfigureAwait(false); }
            finally { Complete(); }
        }

        private void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                Interlocked.Decrement(ref client._activeLogicalInvocations);
        }
    }
}
