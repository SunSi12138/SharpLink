using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public sealed class RuntimeTimeProviderPhase08Tests
{
    private static readonly DateTimeOffset UtcStart =
        new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void RuntimeContextShouldUseSystemTimeProviderByDefault()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);

        Ensure(ReferenceEquals(context.TimeProvider, TimeProvider.System),
            "the default runtime clock must be the process System provider");
    }

    [Test]
    public void RuntimeContextShouldRetainButNotDisposeTheApplicationTimeProvider()
    {
        var provider = new CallerOwnedTimeProvider(UtcStart);
        var builder = new SharpLinkRuntimeContextBuilder().UseTimeProvider(provider);
        var context = builder.Build(includeGeneratedAssemblyCatalog: false);

        Ensure(ReferenceEquals(context.TimeProvider, provider),
            "the context must retain the exact application-owned provider instance");

        context.Dispose();

        Ensure(!provider.IsDisposed,
            "disposing a runtime context must not dispose its application-owned clock");
        provider.Dispose();
        Ensure(provider.IsDisposed, "the caller must remain able to dispose its own clock");
    }

    [Test]
    public void RuntimeContextBuilderShouldRejectANullTimeProviderWithoutChangingItsDefault()
    {
        var builder = new SharpLinkRuntimeContextBuilder();
        var failure = CaptureFailure(() => builder.UseTimeProvider(null!));
        using var context = builder.Build(includeGeneratedAssemblyCatalog: false);

        Ensure(failure is ArgumentNullException
        {
            ParamName: "timeProvider"
        }, "the builder must reject a null provider at configuration time");
        Ensure(ReferenceEquals(context.TimeProvider, TimeProvider.System),
            "a rejected provider must leave the builder on its System default");
    }

    [Test]
    public void RpcDeadlineShouldResolveDurationIntoMonotonicTimestampOnly()
    {
        var provider = new MutableTimeProvider(UtcStart);
        provider.SetTimestamp(1_000);

        var deadline = RpcDeadline.Create(TimeSpan.FromMilliseconds(250), provider);

        Ensure(deadline.HasValue, "a created deadline must carry a value");
        Ensure(deadline.Timestamp == 2_501_000,
            "the duration must be resolved using only the provider timestamp frequency");
    }

    [Test]
    public void RpcDeadlineShouldExpireInclusivelyAtTheExactMonotonicBoundary()
    {
        const long deadlineTimestamp = 50;
        var deadline = RpcDeadline.FromTimestamp(deadlineTimestamp);

        Ensure(!deadline.IsExpired(deadlineTimestamp - 1),
            "one provider timestamp before the boundary must remain live");
        Ensure(deadline.IsExpired(deadlineTimestamp),
            "the exact deadline timestamp must be terminal");
        Ensure(deadline.IsExpired(deadlineTimestamp + 1),
            "timestamps after the boundary must remain terminal");
    }

    [Test]
    public void RpcDeadlineShouldTreatADelayEndingAtTheDeadlineAsExpired()
    {
        var provider = new MutableTimeProvider(UtcStart);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);

        Ensure(!deadline.WouldExpireBeforeOrAt(
                TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)), provider),
            "a delay ending one provider tick before the deadline must fit");
        Ensure(deadline.WouldExpireBeforeOrAt(TimeSpan.FromSeconds(5), provider),
            "a delay ending exactly at the deadline must be rejected");
    }

    [Test]
    public void RpcDeadlineShouldSaturateTimestampConversionInsteadOfOverflowing()
    {
        var frequencySaturation = SharpLinkTime.AddDuration(
            timestamp: 123,
            TimeSpan.FromSeconds(2),
            timestampFrequency: long.MaxValue);
        var additionSaturation = SharpLinkTime.AddDuration(
            timestamp: long.MaxValue - 1,
            TimeSpan.FromSeconds(1),
            timestampFrequency: TimeSpan.TicksPerSecond);

        Ensure(frequencySaturation == long.MaxValue,
            "duration conversion beyond Int64 timestamp space must saturate");
        Ensure(additionSaturation == long.MaxValue,
            "adding a valid duration near Int64.MaxValue must saturate");
    }

    [Test]
    public void RpcDeadlineRemainingShouldNotWrapAcrossExtremeTimestampOrigins()
    {
        var remaining = RpcDeadline.GetRemaining(
            long.MaxValue,
            long.MinValue,
            timestampFrequency: 1);

        Ensure(remaining == TimeSpan.MaxValue,
            "remaining time across the full timestamp range must saturate instead of wrapping to expired");
    }

    [Test]
    public void RpcDeadlineShouldIgnoreUtcJumpsAfterResolution()
    {
        var provider = new MutableTimeProvider(UtcStart);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), provider);

        provider.SetUtcNow(UtcStart.AddDays(1));

        Ensure(!deadline.IsExpired(provider),
            "a forward UTC jump must not expire a locally resolved monotonic deadline");
        Ensure(deadline.GetRemaining(provider) == TimeSpan.FromSeconds(10),
            "remaining time must be derived only from the monotonic timestamp");
        provider.SetTimestamp(TimeSpan.FromSeconds(10).Ticks);
        Ensure(deadline.IsExpired(provider),
            "the deadline must expire when its monotonic boundary is reached");
    }

    [Test]
    public async Task SharpLinkTimerDelayShouldCompleteOnlyAtTheFakeTimeBoundary()
    {
        var provider = new ManualTimeProvider(UtcStart);
        var delay = SharpLinkTimer.DelayAsync(
            TimeSpan.FromSeconds(5), provider, CancellationToken.None).AsTask();

        provider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!delay.IsCompleted,
            "provider-aware delay must remain pending one fake-clock tick before its due time");

        provider.Advance(TimeSpan.FromTicks(1));
        await delay;
        Ensure(delay.IsCompletedSuccessfully,
            "provider-aware delay must complete at the exact fake-clock boundary");
    }

    [Test]
    public async Task SharpLinkTimerDelayShouldHonorCancellationWithoutAdvancingTime()
    {
        var provider = new ManualTimeProvider(UtcStart);
        using var cancellation = new CancellationTokenSource();
        var delay = SharpLinkTimer.DelayAsync(
            TimeSpan.FromMinutes(1), provider, cancellation.Token).AsTask();

        cancellation.Cancel();
        var failure = await CaptureFailureAsync(delay);

        Ensure(failure is OperationCanceledException,
            "cancellation must terminate the provider-aware delay without a wall-clock wait");
        Ensure(delay.IsCanceled, "the canceled provider-aware delay must publish Canceled state");
        provider.Advance(TimeSpan.FromMinutes(1));
        Ensure(delay.IsCanceled,
            "advancing the provider after cancellation must not resurrect the delay");
    }

    [Test]
    public async Task SharpLinkTimerWaitShouldTimeOutAtTheExactDeadlineTimestamp()
    {
        var provider = new ManualTimeProvider(UtcStart);
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(2), provider);
        var wait = SharpLinkTimer.WaitAsync(
            neverCompletes.Task, deadline, provider).AsTask();

        provider.Advance(TimeSpan.FromSeconds(2).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!wait.IsCompleted,
            "a deadline wait must remain pending immediately before the fake-time boundary");

        provider.Advance(TimeSpan.FromTicks(1));
        Ensure(!await wait,
            "a task still incomplete at the exact monotonic deadline must time out");
    }

    [Test]
    public async Task SharpLinkTimerWaitShouldPropagateCallerCancellation()
    {
        var provider = new ManualTimeProvider(UtcStart);
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromMinutes(1), provider);
        using var cancellation = new CancellationTokenSource();
        var wait = SharpLinkTimer.WaitAsync(
            neverCompletes.Task, deadline, provider, cancellation.Token).AsTask();

        cancellation.Cancel();
        var failure = await CaptureFailureAsync(wait);

        Ensure(failure is OperationCanceledException canceled &&
               canceled.CancellationToken == cancellation.Token,
            "deadline wait must preserve the caller cancellation token");
        Ensure(wait.IsCanceled,
            "caller cancellation must publish Canceled rather than a deadline result");
    }

    [Test]
    public async Task SemaphoreReleaseRacingTheExactDeadlineShouldReturnItsPermit()
    {
        var provider = new ManualTimeProvider(UtcStart);
        using var semaphore = new SemaphoreSlim(0, 1);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), provider);
        var wait = SharpLinkTimer.WaitAsync(
            semaphore, deadline, provider, CancellationToken.None).AsTask();
        using var releaseAtDeadline = provider.CreateTimer(
            static state => ((SemaphoreSlim)state!).Release(),
            semaphore,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        provider.Advance(TimeSpan.FromSeconds(1));

        Ensure(!await wait,
            "the exact monotonic deadline must win over a capacity release at the same timestamp");
        Ensure(semaphore.Wait(0),
            "a release observed after timeout must be returned instead of stealing the next slot");
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        internal void SetUtcNow(DateTimeOffset value) => _utcNow = value;

        internal void SetTimestamp(long value) => _timestamp = value;
    }

    private sealed class CallerOwnedTimeProvider(DateTimeOffset utcNow) : TimeProvider, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => 0;

        public void Dispose() => IsDisposed = true;
    }
}
