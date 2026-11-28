using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpLink.UnitTests.Runtime;

public sealed class NStudyApprovalSchedulerTests
{
    [Test]
    public async Task DeadlineKeepsOutstandingApprovalsInEndpointCapacityUntilExit()
    {
        var clock = new ManualTimeProvider();
        var endpoint = new NStudyApprovalScheduler();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        async ValueTask<SharpLinkVersionDecision> AwaitDecisionAsync(NStudyChange _, CancellationToken token)
        {
            if (Interlocked.Increment(ref count) == NStudyApprovalScheduler.Capacity)
                entered.TrySetResult();
            await release.Task;
            return token.IsCancellationRequested ? SharpLinkVersionDecision.Reject : SharpLinkVersionDecision.Approve;
        }

        var controlDeadline = RpcDeadline.Create(NStudyApprovalScheduler.ControlTimeout, clock);
        Task<SharpLinkVersionDecision> Start() => endpoint.DecideAsync(default, AwaitDecisionAsync, NullLogger.Instance,
            clock, controlDeadline, guard.Token).AsTask();

        var attempts = Enumerable.Range(0, NStudyApprovalScheduler.Capacity).Select(_ => Start()).ToArray();
        try
        {
            await entered.Task.WaitAsync(guard.Token);
            Ensure(endpoint.ActiveCount == NStudyApprovalScheduler.Capacity, "the physical endpoint quota is full");
            clock.Advance(TimeSpan.FromSeconds(3));
            var expired = await Task.WhenAll(attempts).WaitAsync(guard.Token);
            Ensure(expired.All(decision => decision == SharpLinkVersionDecision.Reject) &&
                   endpoint.ActiveCount == NStudyApprovalScheduler.Capacity,
                "logical policy expiry does not remove physically pending callbacks");
            Ensure(await Start().WaitAsync(guard.Token) == SharpLinkVersionDecision.Reject &&
                   count == NStudyApprovalScheduler.Capacity,
                "another attempt declines without starting or queuing a ninth callback");
        }
        finally
        {
            release.TrySetResult();
        }

        await WaitForExitAsync(endpoint, guard.Token);
        Ensure(await endpoint.DecideAsync(default, static (_, _) => ValueTask.FromResult(SharpLinkVersionDecision.Approve),
                NullLogger.Instance, clock, controlDeadline, guard.Token) == SharpLinkVersionDecision.Approve,
            "physically completed approvals return endpoint capacity");
    }

    [Test]
    public async Task CallerCancellationStaysDistinctFromLocalDecline()
    {
        var clock = new ManualTimeProvider();
        var endpoint = new NStudyApprovalScheduler();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<SharpLinkVersionDecision> AwaitDecisionAsync(NStudyChange _, CancellationToken token)
        {
            entered.SetResult(token);
            await release.Task;
            return SharpLinkVersionDecision.Approve;
        }

        var attempt = endpoint.DecideAsync(default, AwaitDecisionAsync, NullLogger.Instance,
            clock, RpcDeadline.Create(NStudyApprovalScheduler.ControlTimeout, clock), caller.Token).AsTask();
        try
        {
            var policyToken = await entered.Task.WaitAsync(guard.Token);
            caller.Cancel();
            try
            {
                _ = await attempt.WaitAsync(guard.Token);
                throw new InvalidOperationException("Expected caller cancellation, not a Policy decline.");
            }
            catch (OperationCanceledException error) when (error.CancellationToken == caller.Token)
            {
                Ensure(policyToken.IsCancellationRequested && endpoint.ActiveCount == 1,
                    "caller cancellation signals the application without returning its physical slot");
            }
        }
        finally
        {
            release.TrySetResult();
        }
        await WaitForExitAsync(endpoint, guard.Token);
    }

    [Test]
    public async Task InsufficientRemainingControlTimeDoesNotStartAnApproval()
    {
        var clock = new ManualTimeProvider();
        var endpoint = new NStudyApprovalScheduler();
        var calls = 0;
        var approved = await endpoint.DecideAsync(default, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(SharpLinkVersionDecision.Approve);
        }, NullLogger.Instance, clock, RpcDeadline.Create(TimeSpan.FromMilliseconds(500), clock), CancellationToken.None);
        Ensure(approved == SharpLinkVersionDecision.Reject && calls == 0 && endpoint.ActiveCount == 0,
            "reserve the remaining control budget for the orderly decline");
    }

    [Test]
    public async Task CompletedManualDecisionKeepsApproveRejectAndDeferDistinct()
    {
        var clock = new ManualTimeProvider();
        var scheduler = new NStudyApprovalScheduler();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var decision in new[]
                 { SharpLinkVersionDecision.Approve, SharpLinkVersionDecision.Reject, SharpLinkVersionDecision.Defer })
        {
            var result = await scheduler.DecideAsync(default, (_, _) => ValueTask.FromResult(decision),
                NullLogger.Instance, clock, RpcDeadline.Create(NStudyApprovalScheduler.ControlTimeout, clock), guard.Token);
            Ensure(result == decision, $"An on-time {decision} must survive the scheduler.");
            await WaitForExitAsync(scheduler, guard.Token);
        }
    }

    private static async Task WaitForExitAsync(NStudyApprovalScheduler endpoint, CancellationToken ct)
    {
        while (endpoint.ActiveCount != 0)
            await Task.Delay(1, ct);
    }

    private static void Ensure(bool condition, string detail)
    {
        if (!condition)
            throw new InvalidOperationException(detail);
    }
}
