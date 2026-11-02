namespace SharpLink.UnitTests.Runtime;

public class RetirementGraceBudgetTests
{
    [Test]
    public void FullTimeSpanRangeShouldNotUseRpcHalfRingNormalization()
    {
        var provider = new TimestampProvider(long.MaxValue, 0);
        var maximum = RetirementGraceBudget.Capture(TimeSpan.MaxValue, provider).CreateClock();
        var belowMaximum = RetirementGraceBudget.Capture(TimeSpan.MaxValue - TimeSpan.FromTicks(1), provider).CreateClock();

        Ensure(maximum.Remaining == TimeSpan.MaxValue, "MaxValue remains its exact original budget");
        Ensure(belowMaximum.Remaining == TimeSpan.MaxValue - TimeSpan.FromTicks(1),
            "MaxValue minus one tick must not acquire an RPC deadline clamp");
        Ensure(maximum.RemainingNumerator - belowMaximum.RemainingNumerator == (UInt128)long.MaxValue,
            "adjacent duration ticks differ by the captured frequency exactly");
        AssertSliceBounds(maximum.NextSlice, provider.TimestampFrequency);
    }

    [Test]
    public void RepeatedFractionalElapsedSamplesShouldPreserveTheirRemainder()
    {
        var provider = new TimestampProvider(3, 0);
        var clock = RetirementGraceBudget.Capture(TimeSpan.FromSeconds(1), provider).CreateClock();

        Ensure(clock.ObserveTimestamp(1) && clock.Remaining.Ticks == 6_666_667,
            "remaining duration rounds upward only at timer projection");
        Ensure(clock.RemainingNumerator == 20_000_000, "first exact rational remainder");
        Ensure(clock.ObserveTimestamp(2) && clock.Remaining.Ticks == 3_333_334,
            "a second sample cannot compound earlier rounding");
        Ensure(clock.RemainingNumerator == 10_000_000, "second exact rational remainder");
        Ensure(clock.ObserveTimestamp(3) && clock.IsExpired && clock.NextSlice == TimeSpan.Zero,
            "exact accumulated equality expires");
    }

    [Test]
    public void SubTimeSpanTickSamplesShouldAccumulateBeforeProjection()
    {
        var provider = new TimestampProvider(30_000_000, 17);
        var clock = RetirementGraceBudget.Capture(TimeSpan.FromTicks(1), provider).CreateClock();

        Ensure(clock.ObserveTimestamp(18) && !clock.IsExpired && clock.Remaining == TimeSpan.FromTicks(1),
            "one third of a duration tick remains a positive rational budget");
        Ensure(clock.ObserveTimestamp(19) && !clock.IsExpired && clock.RemainingNumerator == 10_000_000,
            "two sub-tick samples accumulate without loss");
        Ensure(clock.ObserveTimestamp(20) && clock.IsExpired, "third sample reaches exact equality");
    }

    [Test]
    public void BackwardAndHalfRingSamplesShouldNotMoveAcceptedBaseline()
    {
        var provider = new TimestampProvider(TimeSpan.TicksPerSecond, 100);
        var clock = RetirementGraceBudget.Capture(TimeSpan.FromTicks(10), provider).CreateClock();
        var original = clock.RemainingNumerator;

        Ensure(!clock.ObserveTimestamp(99), "backward sample is ambiguous rather than elapsed");
        Ensure(clock.AcceptedTimestamp == 100 && clock.RemainingNumerator == original,
            "rejected backward sample changes neither rational budget nor baseline");
        Ensure(clock.ObserveTimestamp(100) && clock.RemainingNumerator == original,
            "returning to the previous accepted timestamp must consume zero");
        Ensure(!clock.ObserveTimestamp(unchecked(100L + long.MinValue)), "exact half-ring is ambiguous");
        Ensure(clock.AcceptedTimestamp == 100 && clock.RemainingNumerator == original,
            "ambiguous sample cannot rebase the next observation");
        Ensure(clock.ObserveTimestamp(110) && clock.IsExpired, "only accepted forward delta consumes budget");
    }

    [Test]
    public void ObservableTimestampWrapShouldConsumeUnsignedDelta()
    {
        var origin = long.MaxValue - 4;
        var provider = new TimestampProvider(TimeSpan.TicksPerSecond, origin);
        var clock = RetirementGraceBudget.Capture(TimeSpan.FromTicks(10), provider).CreateClock();

        Ensure(clock.ObserveTimestamp(unchecked(origin + 9)) && clock.Remaining == TimeSpan.FromTicks(1),
            "signed-boundary wrap with a short observable interval consumes nine ticks");
        Ensure(clock.ObserveTimestamp(unchecked(origin + 10)) && clock.IsExpired,
            "one remaining tick expires across signed timestamp wrap");
    }

    [Test]
    public void TimerSliceShouldRespectBothNativeRangeAndTimestampWindow()
    {
        foreach (var frequency in new long[] { 1, 3, TimeSpan.TicksPerSecond, long.MaxValue })
        {
            var provider = new TimestampProvider(frequency, 0);
            var clock = RetirementGraceBudget.Capture(TimeSpan.MaxValue, provider).CreateClock();
            AssertSliceBounds(clock.NextSlice, frequency);
            Ensure(clock.NextSlice > TimeSpan.Zero, "every positive supported frequency permits a positive slice");
        }
        var fastest = RetirementGraceBudget.Capture(TimeSpan.MaxValue,
            new TimestampProvider(long.MaxValue, 0)).CreateClock();
        Ensure(fastest.NextSlice < TimeSpan.FromSeconds(1),
            "a high-frequency provider cannot use the native twenty-four-day slice");
    }

    [Test]
    public void ClockShouldKeepEntryOriginAndOriginalProvider()
    {
        var provider = new TimestampProvider(TimeSpan.TicksPerSecond, 25);
        var budget = RetirementGraceBudget.Capture(TimeSpan.FromTicks(10), provider);
        provider.Timestamp = 35;
        var clock = budget.CreateClock();

        Ensure(provider.TimestampReads == 1 && ReferenceEquals(budget.Provider, provider),
            "creating the driver clock never recaptures a provider origin");
        Ensure(clock.Observe() && clock.IsExpired && provider.TimestampReads == 2,
            "time consumed before driver creation belongs to the same original budget");
    }

    [Test]
    public void EntryCaptureShouldPreserveFrequencyAndTimestampFailuresWithoutConstructingClock()
    {
        var original = new OperationCanceledException("entry provider failure");
        foreach (var frequencyFails in new[] { true, false })
        {
            var provider = new ThrowingCaptureProvider(original, frequencyFails);
            Exception? observed = null;
            try { RetirementGraceBudget.Capture(TimeSpan.Zero, provider); }
            catch (Exception error) { observed = error; }
            Ensure(ReferenceEquals(observed, original), "raw entry capture occurrence is not wrapped or cancellation-filtered");
            Ensure(provider.TimestampReads == (frequencyFails ? 0 : 1), "no timestamp call after frequency failure");
        }
    }

    [Test]
    public void SeveralCompleteNativeSlicesShouldNotResetOrRoundTheRemainingBudget()
    {
        var provider = new TimestampProvider(TimeSpan.TicksPerSecond, 0);
        var slice = SharpLinkTimer.MaximumDelay;
        var duration = TimeSpan.FromTicks(checked(slice.Ticks * 3 + 7));
        var clock = RetirementGraceBudget.Capture(duration, provider).CreateClock();
        long timestamp = 0;
        for (var index = 0; index < 3; index++)
        {
            Ensure(clock.NextSlice == slice, "native slicing uses original remaining budget");
            timestamp = checked(timestamp + slice.Ticks);
            Ensure(clock.ObserveTimestamp(timestamp), "complete slice is observable");
        }
        Ensure(clock.NextSlice == TimeSpan.FromTicks(7), "all full slices leave the exact original remainder");
        Ensure(clock.ObserveTimestamp(timestamp + 6) && !clock.IsExpired, "one tick before expiry remains positive");
        Ensure(clock.ObserveTimestamp(timestamp + 7) && clock.IsExpired, "exact original total expires");
    }

    private sealed class ThrowingCaptureProvider(Exception failure, bool frequencyFails) : TimeProvider
    {
        internal int TimestampReads;
        public override long TimestampFrequency => frequencyFails ? throw failure : TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { TimestampReads++; throw failure; }
    }

    private static void AssertSliceBounds(TimeSpan slice, long frequency)
    {
        Ensure(slice <= SharpLinkTimer.MaximumDelay, "native timer delay bound");
        var numerator = (UInt128)(ulong)slice.Ticks * (ulong)frequency;
        var units = (numerator + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
        Ensure(units <= RetirementGraceClock.MaximumScheduledTimestampUnits,
            "scheduled due cannot exceed the observable timestamp sampling target");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TimestampProvider(long frequency, long timestamp) : TimeProvider
    {
        internal long Timestamp = timestamp;
        internal int TimestampReads;
        public override long TimestampFrequency => frequency;
        public override long GetTimestamp() { TimestampReads++; return Timestamp; }
    }
}
