namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // Diagnostic experiment for #244: replace the BCL ConcurrentBag used by the
    // compressed cancellable handoff work-item pool with a FIFO queue. Rent happens
    // on the request-loop thread while Return happens on the worker thread, so this
    // isolates ConcurrentBag's cross-thread steal/local-bag bookkeeping from the
    // unavoidable ThreadPool hop without changing handoff, ExecutionContext, or
    // cancellation semantics. Remove this file once the attribution run completes.
    private sealed class ConcurrentBag<T>
        where T : class
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<T> _queue = new();

        public bool TryTake(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? item)
            => _queue.TryDequeue(out item);

        public void Add(T item) => _queue.Enqueue(item);
    }
}
