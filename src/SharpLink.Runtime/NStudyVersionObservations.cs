using Microsoft.Extensions.Logging;

namespace SharpLink.Runtime;

internal enum NStudyVersionNoticeKind : byte { Proposed, Published, Completed, Declined, Failed }

// Scalar state only. A mention of a generation is not a lease, approval or Bind.
internal readonly record struct NStudyVersionNotice(NStudyVersionNoticeKind Kind,
    NStudyChange Change, bool LocalPublicationOccurred, NStudyDeclineReason DeclineReason = NStudyDeclineReason.None);

internal readonly record struct NStudyVersionObserverSnapshot(
    int Pending, bool Active, bool Sealed, long Dropped, long Faults, long TimedOut);

internal sealed partial class NStudyVersionObservationQueue
{
    internal const int Capacity = 8;
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly Queue<NStudyVersionNotice> _pending = new(Capacity);
    private readonly TaskCompletionSource _stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<NStudyVersionNotice, CancellationToken, ValueTask> _observer;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private TaskCompletionSource? _workAvailable;
    private bool _active;
    private bool _sealed;
    private long _dropped;
    private long _faults;
    private long _timedOut;

    internal NStudyVersionObservationQueue(Func<NStudyVersionNotice, CancellationToken, ValueTask> observer,
        TimeProvider clock, ILogger logger)
    {
        _observer = observer;
        _clock = clock;
        _logger = logger;
        // The endpoint worker must not inherit an attaching application's RPC scope.
        if (ExecutionContext.IsFlowSuppressed())
            Completion = Task.Run(RunAsync);
        else
        {
            using var _ = ExecutionContext.SuppressFlow();
            Completion = Task.Run(RunAsync);
        }
    }

    internal Task Completion { get; }

    internal NStudyVersionObserverSnapshot Snapshot()
    {
        lock (_gate)
            return new(_pending.Count, _active, _sealed, _dropped, Interlocked.Read(ref _faults),
                Interlocked.Read(ref _timedOut));
    }

    internal void TryPublish(NStudyVersionNotice notice)
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            if (_sealed)
                return;
            if (_pending.Count == Capacity)
            {
                // Observations are hints; a consumer can query the authoritative inventory.
                _dropped++;
                return;
            }
            _pending.Enqueue(notice);
            wake = _workAvailable;
            _workAvailable = null;
        }
        wake?.TrySetResult();
    }

    internal void Seal()
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            if (_sealed)
                return;
            _sealed = true;
            _dropped += _pending.Count;
            _pending.Clear();
            wake = _workAvailable;
            _workAvailable = null;
        }

        _stopping.TrySetResult();
        wake?.TrySetResult();
        // Stop does not join application code that may itself be awaiting Stop.
        // The worker continues owning its one in-progress callback until physical exit.
    }

    private async Task RunAsync()
    {
        while (true)
        {
            NStudyVersionNotice next;
            Task? wait = null;
            lock (_gate)
            {
                if (_sealed)
                    return;
                if (!_pending.TryDequeue(out next))
                    wait = (_workAvailable ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                else
                    _active = true;
            }
            if (wait is not null)
            {
                await wait.ConfigureAwait(false);
                continue;
            }

            try
            {
                await DeliverAsync(next).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                    _active = false;
            }
        }
    }

    private async Task DeliverAsync(NStudyVersionNotice notice)
    {
        using var application = new CancellationTokenSource();
        // A synchronous observer must be subject to the same bounded wait as an async one.
        var work = Task.Run(() => InvokeAsync(notice, application.Token));
        try
        {
            var finished = await Task.WhenAny(work, _stopping.Task).WaitAsync(CallbackTimeout, _clock)
                .ConfigureAwait(false);
            if (ReferenceEquals(finished, work))
            {
                await work.ConfigureAwait(false);
                return;
            }
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref _timedOut);
        }

        // Signal user cancellation without running its registrations on the stopping endpoint.
        try
        {
            await Task.WhenAll(work, application.CancelAsync()).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReportFault(error);
        }
    }

    private async Task InvokeAsync(NStudyVersionNotice notice, CancellationToken token)
    {
        if (_stopping.Task.IsCompleted)
            return;
        try
        {
            await _observer(notice, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            ReportFault(error);
        }
    }

    private void ReportFault(Exception error)
    {
        Interlocked.Increment(ref _faults);
        try { LogNStudyObserverFailed(_logger, error); }
        catch (Exception) { /* Diagnostic errors cannot affect a version notice or its publisher. */ }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A local version observer failed.")]
    private static partial void LogNStudyObserverFailed(ILogger logger, Exception error);
}
