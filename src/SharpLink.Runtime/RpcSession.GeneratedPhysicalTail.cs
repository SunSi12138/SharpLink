namespace SharpLink.Runtime;

// Internal invocation ownership handoff, not a public generated ABI or a module acquisition.
// Implementations retain the same request/service/scope owner until ReleasePhysicalTail.
// Calls are balanced once per actual logical/physical split, never once per stream item.
internal interface IGeneratedStreamPhysicalOwner
{
    void RetainPhysicalTail();
    void ReleasePhysicalTail();
}

internal sealed partial class RpcSession
{
    private static async ValueTask FinishGeneratedEnumeratorAsync<T>(
        IAsyncEnumerator<T> enumerator,
        Task? pendingOperation,
        CancellationTokenSource lifetimeCancellation,
        IGeneratedStreamPhysicalOwner? physicalOwner,
        bool terminalSelected)
    {
        var transferred = false;
        try
        {
            if (pendingOperation is { IsCompleted: false })
            {
                // Never run Dispose concurrently with a detached MoveNext or codec/send task.
                physicalOwner?.RetainPhysicalTail();
                transferred = true;
                _ = CompleteGeneratedOperationTailAsync(pendingOperation, enumerator,
                    lifetimeCancellation, physicalOwner);
                return;
            }
            if (pendingOperation is not null)
            {
                // The source may finish between the selected terminal and this finally.
                // Observe its late fault without replacing that selected terminal.
                try { await pendingOperation.ConfigureAwait(false); }
                catch { }
            }

            var dispose = enumerator.DisposeAsync();
            if (dispose.IsCompletedSuccessfully)
            {
                dispose.GetAwaiter().GetResult();
                return;
            }
            if (!terminalSelected)
            {
                // A completed faulted source still follows ordinary awaited disposal.
                await dispose.ConfigureAwait(false);
                return;
            }
            var disposeTask = dispose.AsTask();
            physicalOwner?.RetainPhysicalTail();
            transferred = true;
            _ = CompleteGeneratedDisposeTailAsync(disposeTask, lifetimeCancellation, physicalOwner);
        }
        catch when (terminalSelected)
        {
            // A previously selected deadline/cancellation remains the externally visible result.
        }
        finally
        {
            if (!transferred)
                lifetimeCancellation.Dispose();
        }
    }

    private static async Task CompleteGeneratedOperationTailAsync<T>(
        Task operation, IAsyncEnumerator<T> enumerator, CancellationTokenSource lifetimeCancellation,
        IGeneratedStreamPhysicalOwner? physicalOwner)
    {
        try
        {
            try { await operation.ConfigureAwait(false); }
            catch { }
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            try { lifetimeCancellation.Dispose(); }
            finally { physicalOwner?.ReleasePhysicalTail(); }
        }
    }

    private static async Task CompleteGeneratedDisposeTailAsync(
        Task dispose, CancellationTokenSource lifetimeCancellation, IGeneratedStreamPhysicalOwner? physicalOwner)
    {
        try
        {
            try { await dispose.ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            try { lifetimeCancellation.Dispose(); }
            finally { physicalOwner?.ReleasePhysicalTail(); }
        }
    }
}
