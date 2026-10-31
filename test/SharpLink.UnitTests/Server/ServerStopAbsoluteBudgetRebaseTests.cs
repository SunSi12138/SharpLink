using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class ServerStopAbsoluteBudgetRebaseTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments("None")]
    [Arguments("Timestamp")]
    [Arguments("CreateTimer")]
    public async Task AbsoluteStopBudgetMustNotRestartWhileArming(string advanceAt)
    {
        var clock = new AdvancingClock(advanceAt);
        var source = NewSource();
        var wait = Wait(source.Task, clock);
        try
        {
            var now = clock.Manual.GetTimestamp();
            var due = clock.Manual.EarliestTimerTimestamp;
            if (advanceAt != "None")
            {
                Ensure(now == Budget.Ticks, "the injected clock movement must reach the original absolute budget");
                Ensure(wait.IsCompleted || due <= Budget.Ticks,
                    $"Stop wait rebased beyond its absolute budget: now={now}, deadline={Budget.Ticks}, due={due}, samples={clock.Samples}");
            }
            else
            {
                Ensure(now == 0 && due == Budget.Ticks, "the stationary-clock control must arm the original boundary");
                clock.Manual.Advance(Budget);
            }
            Ensure(!await wait.WaitAsync(Bound), "the unfinished source must lose to the original budget");
        }
        finally
        {
            source.TrySetResult();
            await JoinAsync(wait);
            await clock.Manual.WaitForTimersDrainedAsync().WaitAsync(Bound);
            Ensure(clock.Manual.ActiveTimerCount == 0, "the stop waiter must release its timer");
        }
    }

    [Test]
    [Arguments("Success", false)]
    [Arguments("Fault", false)]
    [Arguments("Cancellation", false)]
    [Arguments("Success", true)]
    [Arguments("Fault", true)]
    [Arguments("Cancellation", true)]
    public async Task CompletedSourceAtEntryRetainsItsTerminal(string terminal, bool expired)
    {
        var clock = new AdvancingClock("None");
        if (expired) clock.Manual.AdvanceWithoutRunningTimers(Budget);
        var source = NewSource();
        var failure = new InvalidOperationException("the original source failure");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Complete(source, terminal, failure, cancellation.Token);
        await AssertTerminalAsync(Wait(source.Task, clock), terminal, failure, cancellation.Token);
        Ensure(clock.Samples == 0 && clock.Manual.ActiveTimerCount == 0,
            "an already-completed source must retain the preexisting timer-free fast path even after expiry");
    }

    [Test]
    [Arguments("Success", true)]
    [Arguments("Fault", true)]
    [Arguments("Cancellation", true)]
    [Arguments("Success", false)]
    [Arguments("Fault", false)]
    [Arguments("Cancellation", false)]
    public async Task CompletedSourceAtDeadlineRetainsItsTerminal(string terminal, bool duringRegistration)
    {
        var source = NewSource();
        var failure = new InvalidOperationException("the original source failure at the deadline");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        void CompleteSource() => Complete(source, terminal, failure, cancellation.Token);
        var clock = new AdvancingClock(duringRegistration ? "CreateTimer" : "None",
            onCreate: duringRegistration ? CompleteSource : null,
            beforeFire: duringRegistration ? null : CompleteSource);
        var wait = Wait(source.Task, clock);
        try
        {
            if (!duringRegistration) clock.Manual.Advance(Budget);
            Ensure(source.Task.IsCompleted && clock.Manual.GetTimestamp() == Budget.Ticks,
                "the source terminal must be selected exactly at the original absolute boundary");
            await AssertTerminalAsync(wait, terminal, failure, cancellation.Token);
        }
        finally
        {
            source.TrySetResult();
            await JoinAsync(wait);
            await clock.Manual.WaitForTimersDrainedAsync().WaitAsync(Bound);
            Ensure(clock.Manual.ActiveTimerCount == 0, "completion arbitration must release every owned timer");
        }
    }

    [Test]
    [Arguments("Success")]
    [Arguments("Fault")]
    [Arguments("Cancellation")]
    public async Task ExpiredInitialSnapshotRetainsItsExistingPriority(string terminal)
    {
        var source = NewSource();
        var failure = new InvalidOperationException("source completes during the expired initial sample");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var clock = new AdvancingClock("InitialTimestamp",
            onFirstSample: () => Complete(source, terminal, failure, cancellation.Token));
        Ensure(!await Wait(source.Task, clock).WaitAsync(Bound),
            "the initial expired sample must retain its existing priority over a newly completed source");
        Ensure(source.Task.IsCompleted && clock.Manual.ActiveTimerCount == 0,
            "the expiry check must not create a timer or leave source work unfinished");
        _ = source.Task.Exception;
    }

    private static Task<bool> Wait(Task source, TimeProvider clock)
        => SharpLinkServer.ServerLifecycleCoordinator.WaitUntilWithProviderAsync(source, Budget.Ticks, clock);

    private static TaskCompletionSource NewSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Complete(TaskCompletionSource source, string terminal, Exception failure, CancellationToken token)
    {
        if (terminal == "Success") source.TrySetResult();
        else if (terminal == "Fault") source.TrySetException(failure);
        else source.TrySetCanceled(token);
    }

    private static async Task AssertTerminalAsync(Task<bool> wait, string terminal, Exception failure, CancellationToken token)
    {
        Exception? observed = null;
        bool result = false;
        try { result = await wait.WaitAsync(Bound); }
        catch (Exception exception) { observed = exception; }
        if (terminal == "Success")
            Ensure(observed is null && result, "graceful completion must win when the source is already terminal");
        else if (terminal == "Fault")
            Ensure(ReferenceEquals(observed, failure), "the original source exception must not become a timeout");
        else
            Ensure(observed is OperationCanceledException cancellation && cancellation.CancellationToken == token,
                "the original source cancellation token must not become a timeout");
    }

    private static async Task JoinAsync(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (Exception) when (task.IsCompleted) { _ = task.Exception; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class AdvancingClock(string advanceAt, Action? onCreate = null, Action? beforeFire = null,
        Action? onFirstSample = null) : TimeProvider
    {
        internal ManualTimeProvider Manual { get; } = new();
        internal int Samples;
        public override long TimestampFrequency => Manual.TimestampFrequency;
        public override long GetTimestamp()
        {
            var sample = Interlocked.Increment(ref Samples);
            if (sample == 1 && advanceAt == "InitialTimestamp")
            {
                Manual.AdvanceWithoutRunningTimers(Budget);
                onFirstSample?.Invoke();
            }
            if (sample == 2 && advanceAt == "Timestamp")
                Manual.AdvanceWithoutRunningTimers(Budget);
            return Manual.GetTimestamp();
        }
        public override DateTimeOffset GetUtcNow() => Manual.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (advanceAt == "CreateTimer") Manual.AdvanceWithoutRunningTimers(Budget);
            onCreate?.Invoke();
            return Manual.CreateTimer(value =>
            {
                beforeFire?.Invoke();
                callback(value);
            }, state, dueTime, period);
        }
    }
}
