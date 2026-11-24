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

        async ValueTask<bool> AwaitDecisionAsync(NStudyChange _, CancellationToken token)
        {
            if (Interlocked.Increment(ref count) == NStudyApprovalScheduler.Capacity)
                entered.TrySetResult();
            await release.Task;
            return !token.IsCancellationRequested;
        }

        var controlDeadline = RpcDeadline.Create(NStudyApprovalScheduler.ControlTimeout, clock);
        Task<bool> Start() => endpoint.DecideAsync(default, AwaitDecisionAsync, NullLogger.Instance,
            clock, controlDeadline, guard.Token).AsTask();

        var attempts = Enumerable.Range(0, NStudyApprovalScheduler.Capacity).Select(_ => Start()).ToArray();
        try
        {
            await entered.Task.WaitAsync(guard.Token);
            Ensure(endpoint.ActiveCount == NStudyApprovalScheduler.Capacity, "the physical endpoint quota is full");
            clock.Advance(TimeSpan.FromSeconds(3));
            var expired = await Task.WhenAll(attempts).WaitAsync(guard.Token);
            Ensure(expired.All(decision => !decision) && endpoint.ActiveCount == NStudyApprovalScheduler.Capacity,
                "logical policy expiry does not remove physically pending callbacks");
            Ensure(!await Start().WaitAsync(guard.Token) && count == NStudyApprovalScheduler.Capacity,
                "another attempt declines without starting or queuing a ninth callback");
        }
        finally
        {
            release.TrySetResult();
        }

        await WaitForExitAsync(endpoint, guard.Token);
        Ensure(await endpoint.DecideAsync(default, static (_, _) => ValueTask.FromResult(true), NullLogger.Instance,
                clock, controlDeadline, guard.Token),
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

        async ValueTask<bool> AwaitDecisionAsync(NStudyChange _, CancellationToken token)
        {
            entered.SetResult(token);
            await release.Task;
            return true;
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
            return ValueTask.FromResult(true);
        }, NullLogger.Instance, clock, RpcDeadline.Create(TimeSpan.FromMilliseconds(500), clock), CancellationToken.None);
        Ensure(!approved && calls == 0 && endpoint.ActiveCount == 0,
            "reserve the remaining control budget for the orderly decline");
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
