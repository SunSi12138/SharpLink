using Microsoft.Extensions.Logging;

namespace SharpLink.Runtime;

/// <summary>Bounds local version approval without transferring its physical lifetime to a Session.</summary>
internal sealed partial class NStudyApprovalScheduler
{
    internal static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DeclineReserve = TimeSpan.FromSeconds(1);

    // A coordinated cohort can contain eight peers. Do not queue more application work
    // behind callbacks that may remain active after their decisions have expired.
    internal const int Capacity = 8;
    private int _activeCount;

    internal int ActiveCount => Volatile.Read(ref _activeCount);

    internal async ValueTask<SharpLinkVersionDecision> DecideAsync(NStudyChange change,
        Func<NStudyChange, CancellationToken, ValueTask<SharpLinkVersionDecision>> approve, ILogger logger,
        TimeProvider clock, RpcDeadline controlDeadline, CancellationToken controlToken)
    {
        controlToken.ThrowIfCancellationRequested();
        var budget = controlDeadline.GetRemaining(clock) - DeclineReserve;
        if (budget <= TimeSpan.Zero)
            return SharpLinkVersionDecision.Reject;

        if (budget > ApprovalTimeout)
            budget = ApprovalTimeout;
        var approvalDeadline = RpcDeadline.Create(budget, clock);
        if (!TryEnter())
            return SharpLinkVersionDecision.Reject;
        var application = new CancellationTokenSource();
        var work = Task.Run(() => InvokeAsync(change, approve, logger, application.Token));
        var decidedOnTime = false;
        try
        {
            // Application cancellation handlers have their own token. They must not
            // run synchronously on the transport/caller watchdog or delay a decline.
            var decision = await work.WaitAsync(approvalDeadline.GetRemaining(clock), clock, controlToken)
                .ConfigureAwait(false);
            controlToken.ThrowIfCancellationRequested();
            decidedOnTime = !approvalDeadline.IsExpired(clock);
            return decidedOnTime ? decision : SharpLinkVersionDecision.Reject;
        }
        catch (TimeoutException)
        {
            controlToken.ThrowIfCancellationRequested();
            return SharpLinkVersionDecision.Reject;
        }
        finally
        {
            _ = ReleaseAfterExitAsync(work, application, requestCancellation: !decidedOnTime, logger);
        }
    }

    private bool TryEnter()
    {
        while (true)
        {
            var active = ActiveCount;
            if (active >= Capacity)
                return false;
            if (Interlocked.CompareExchange(ref _activeCount, active + 1, active) == active)
                return true;
        }
    }

    private static async Task<SharpLinkVersionDecision> InvokeAsync(NStudyChange change,
        Func<NStudyChange, CancellationToken, ValueTask<SharpLinkVersionDecision>> approve, ILogger logger, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return SharpLinkVersionDecision.Reject;
        try
        {
            var decision = await approve(change, token).ConfigureAwait(false);
            return decision is SharpLinkVersionDecision.Approve or SharpLinkVersionDecision.Reject or SharpLinkVersionDecision.Defer
                ? decision
                : throw new InvalidOperationException("The version approval callback returned an unknown decision.");
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException || !token.IsCancellationRequested)
                ReportFailure(logger, error);
            return SharpLinkVersionDecision.Reject;
        }
    }

    private async Task ReleaseAfterExitAsync(Task work, CancellationTokenSource application,
        bool requestCancellation, ILogger logger)
    {
        try
        {
            var cancellation = requestCancellation ? application.CancelAsync() : Task.CompletedTask;
            await Task.WhenAll(work, cancellation).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReportFailure(logger, error);
        }
        finally
        {
            application.Dispose();
            Interlocked.Decrement(ref _activeCount);
        }
    }

    private static void ReportFailure(ILogger logger, Exception error)
    {
        try
        {
            LogNStudyApprovalFailed(logger, error);
        }
        catch (Exception)
        {
            // Diagnostic providers must not turn a local decline into a Session fault.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local version approval failed. The prepared change was declined.")]
    private static partial void LogNStudyApprovalFailed(ILogger logger, Exception error);
}
