using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed partial class ProducerReservationOwnershipTests
{
    [Test]
    [Arguments(RpcMethodKind.ClientStreaming, "cancel")]
    [Arguments(RpcMethodKind.ClientStreaming, "deadline")]
    [Arguments(RpcMethodKind.ClientStreaming, "close")]
    [Arguments(RpcMethodKind.OneWay, "cancel")]
    [Arguments(RpcMethodKind.OneWay, "deadline")]
    [Arguments(RpcMethodKind.OneWay, "close")]
    [Arguments(RpcMethodKind.DuplexStreaming, "cancel")]
    [Arguments(RpcMethodKind.DuplexStreaming, "deadline")]
    [Arguments(RpcMethodKind.DuplexStreaming, "close")]
    public async Task TerminalBeforeRegistrationReturnsReleasesOnlyItsSelection(RpcMethodKind kind, string terminal)
    {
        var clock = new RegistrationClock();
        await using var fixture = await Fixture.CreateAsync(clock);
        var group = new ProducerGroup(1);
        var reservationsAtRegistration = -1;
        clock.OnPendingTimerChange = () =>
        {
            reservationsAtRegistration = fixture.Connection.CallAdmissionReservationCount;
            switch (terminal)
            {
                case "cancel": fixture.Cancellation.Cancel(); break;
                case "deadline": clock.Manual.Advance(Bound); break;
                case "close":
                    fixture.Connection.PendingCalls.FailAllPendingRequests(
                        new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "registration test close"));
                    break;
            }
        };
        var invocation = Invoke(fixture, kind, group, timed: true);
        try
        {
            var failure = await CaptureFailureAsync(invocation);
            Ensure(terminal switch
            {
                "cancel" => failure is OperationCanceledException canceled &&
                    canceled.CancellationToken == fixture.Cancellation.Token,
                "deadline" => failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                // Existing PrepareGeneratedServerStreamAsync maps a removed non-canceled
                // registration to deadline, including this close seam. Characterize that
                // unchanged behavior here; this ownership patch does not repair precedence.
                "close" when kind == RpcMethodKind.DuplexStreaming =>
                    failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "close" => failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                _ => false
            }, $"the baseline registration outcome must survive cleanup: {kind}/{terminal}; " +
                $"actual={failure}; tokenMatches={failure is OperationCanceledException canceledFailure && canceledFailure.CancellationToken == fixture.Cancellation.Token}");
            await fixture.JoinProducersAsync();
            Ensure(reservationsAtRegistration == 2, "pending registration must not consume the producer selection");
            Ensure(group.Calls == 0, "already terminal registration must not start outgoing producers");
            Ensure(fixture.Connection.PendingCalls.Count == 0 && fixture.Connection.ActiveCallCount == 0 &&
                fixture.Connection.CallAdmissionReservationCount == 1,
                "failed registration startup must release exactly its own selection");
        }
        finally
        {
            clock.OnPendingTimerChange = null;
            await fixture.Cancellation.CancelAsync();
            _ = await CaptureFailureAsync(invocation);
            await fixture.JoinProducersAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HealthSelectionTransfersWithoutConsumingUnrelatedReservation(bool supported)
    {
        await using var fixture = await Fixture.CreateAsync(capabilities: supported
            ? ProtocolV2Capabilities.HealthCheck : ProtocolV2Capabilities.None);
        var health = fixture.Client.CheckHealthAsync(fixture.Cancellation.Token).AsTask();
        try
        {
            if (supported)
                await fixture.Transport.Connection.WaitForSentPacket(ProtocolV2FrameType.HealthCheck).WaitAsync(Bound);
            else
                await health.WaitAsync(Bound);
            Ensure(fixture.Connection.CallAdmissionReservationCount == 1,
                "health request must transfer or release its own reservation before waiting for a response");
        }
        finally
        {
            await fixture.Cancellation.CancelAsync();
            _ = await CaptureFailureAsync(health);
        }
        Ensure(fixture.Connection.CallAdmissionReservationCount == 1 && fixture.Connection.ActiveCallCount == 0,
            "health terminal must leave only the unrelated selection");
    }

    private sealed class RegistrationClock : TimeProvider
    {
        internal ManualTimeProvider Manual { get; } = new();
        internal Action? OnPendingTimerChange;
        public override long TimestampFrequency => Manual.TimestampFrequency;
        public override long GetTimestamp() => Manual.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => Manual.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Manual.CreateTimer(callback, state, dueTime, period);
            return state is PendingDeadlineScheduler ? new RegistrationTimer(this, timer) : timer;
        }

        private sealed class RegistrationTimer(RegistrationClock owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = inner.Change(dueTime, period);
                if (dueTime != Timeout.InfiniteTimeSpan)
                    Interlocked.Exchange(ref owner.OnPendingTimerChange, null)?.Invoke();
                return changed;
            }
            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
