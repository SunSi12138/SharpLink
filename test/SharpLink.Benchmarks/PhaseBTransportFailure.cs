using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

// Case-owned failure arbitration only. No timers, polling or per-item operations.
// Publishing the cause must precede cancellation: WaitAsync may otherwise report
// cancellation before the participant's fault reaches Task.WhenAll.
internal sealed class PhaseBTransportFailure(CancellationTokenSource cancellation)
{
    private sealed class Failure(string origin, Exception error)
    {
        internal string Origin { get; } = origin;
        internal ExceptionDispatchInfo Error { get; } = ExceptionDispatchInfo.Capture(error);
        internal TaskCompletionSource CancellationFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? CancellationFailure;
    }

    private Failure? _first;
    internal string Origin => Volatile.Read(ref _first)?.Origin ?? "case-timeout-or-external-cancellation";

    internal void RecordAndCancel(string origin, Exception error)
    {
        // Sibling cancellations after the winning fault are consequences, not causes.
        if (error is OperationCanceledException && cancellation.IsCancellationRequested) return;
        var first = new Failure(origin, error);
        if (Interlocked.CompareExchange(ref _first, first, null) is not null) return;
        try { cancellation.Cancel(); }
        catch (Exception callbackError) { first.CancellationFailure = callbackError; }
        finally { first.CancellationFinished.TrySetResult(); }
    }

    internal async Task ObserveAsync(Task task, string origin)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error)
        {
            RecordAndCancel(origin, error);
            throw;
        }
    }

    internal async Task WaitAsync(Task[] participants)
    {
        try { await Task.WhenAll(participants).WaitAsync(cancellation.Token).ConfigureAwait(false); }
        catch
        {
            var first = Volatile.Read(ref _first);
            if (first is not null)
            {
                // Cancellation wakes this observer before synchronous callbacks finish.
                // Retain a callback failure as secondary rather than racing its publication.
                await first.CancellationFinished.Task.ConfigureAwait(false);
                if (first.CancellationFailure is not null)
                    throw new AggregateException("The case failed and cancellation cleanup also failed.",
                        first.Error.SourceException, first.CancellationFailure);
                first.Error.Throw();
            }
            throw;
        }
    }
}
