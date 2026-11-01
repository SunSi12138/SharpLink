using System.IO.Pipelines;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

// Actual pooled call-state gate plus actual Session/SendPump. TCP route/policy ordering is
// covered separately; these controls do not replace that integration boundary.
public sealed class ServerRequestAcceptanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task RemoteCancelBeforeTransferPublishesNoAcceptanceAndRefundsReservedWriter()
    {
        await using var fixture = new Fixture();
        using var state = fixture.State(51);
        var admission = fixture.Prepare(51);
        Ensure(fixture.Session.QueuedSendBytes > 0, "frame is physically reserved before arbitration");
        Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel), "original cancellation wins");
        Ensure(!state.TryPublishRequestAccepted(fixture.Session, ref admission, 2, out var notify) && !notify,
            "terminal call cannot transfer ACK");
        Ensure(!admission.Accepted && state.AcceptedClientStreamCount == 0 && fixture.Session.QueuedSendBytes > 0,
            "gate does not clean up reserved frame or invent accepted routes");
        admission.Reject(new SharpLinkException(SharpLinkErrorCode.Cancelled, "original terminal"));
        fixture.Session.FinishPreparedFrameEnqueue(ref admission, out _, out var rejection);
        Ensure(rejection is SharpLinkException { Code: SharpLinkErrorCode.Cancelled } &&
            fixture.Session.QueuedSendBytes == 0 && fixture.Session.IsConnected,
            "outside-gate cleanup preserves cancellation and healthy Session");
    }

    [Test]
    public async Task UnfiredOriginalDeadlineRefusesAckBeforeDeferredCancellationNotification()
    {
        await using var fixture = new Fixture();
        var clock = new ManualTimeProvider();
        using var state = ServerCallCancellationState.Rent(52,
            RpcDeadline.Create(TimeSpan.FromMilliseconds(10), clock), clock,
            default, default, supportsCooperativeCancellation: true);
        var callbacks = 0;
        using var registration = state.InvocationToken.Register(() => Interlocked.Increment(ref callbacks));
        var admission = fixture.Prepare(52);
        clock.AdvanceWithoutRunningTimers(TimeSpan.FromMilliseconds(11));
        Ensure(!state.TryPublishRequestAccepted(fixture.Session, ref admission, 1, out var notify) && notify,
            "gate samples the original monotonic deadline, not timer delivery");
        Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded && callbacks == 0 &&
            !state.InvocationToken.IsCancellationRequested, "terminal gate does not invoke cancellation callbacks");
        admission.Reject(new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "original deadline"));
        fixture.Session.FinishPreparedFrameEnqueue(ref admission, out _, out _);
        state.NotifyInvocationCancellation();
        Ensure(callbacks == 1 && fixture.Session.QueuedSendBytes == 0 && state.AcceptedClientStreamCount == 0,
            "outside-gate notification and writer cleanup occur exactly once");
    }

    [Test]
    public async Task AcceptedOwnershipSurvivesCancelBeforeDeferredWakeWithoutClaimingResponse()
    {
        await using var fixture = new Fixture();
        using var state = fixture.State(53);
        var admission = fixture.Prepare(53);
        Ensure(state.TryPublishRequestAccepted(fixture.Session, ref admission, 2, out var notify) && !notify,
            "complete route set is committed with actual queue transfer");
        Ensure(admission.Accepted && state.AcceptedClientStreamCount == 2 &&
            state.Reason == ServerCallCancellationReason.None, "ACK is nonterminal, not response activation");
        Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel), "cancel can win after admission");
        fixture.Session.FinishPreparedFrameEnqueue(ref admission, out _, out var rejection);
        await fixture.Session.FlushSendQueueAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);
        Ensure(admission.Accepted && rejection is null && state.AcceptedClientStreamCount == 2,
            "deferred wake cannot reclassify already accepted ownership");
        var read = await fixture.Output.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        var bytes = read.Buffer.ToArray();
        fixture.Output.Reader.AdvanceTo(read.Buffer.End);
        Ensure(bytes.Length == ProtocolV2Constants.HeaderBytes &&
            bytes[5] == (byte)ProtocolV2FrameType.RequestAccepted && fixture.Session.QueuedSendBytes == 0,
            "actual transport contains exactly the accepted empty type22 frame");
    }

    [Test]
    public async Task ReentrantClockCancellationWinsBeforeAckCommit()
    {
        await using var fixture = new Fixture();
        var clock = new CallbackClock();
        using var state = ServerCallCancellationState.Rent(54,
            RpcDeadline.Create(TimeSpan.FromSeconds(1), 0, clock.TimestampFrequency), clock,
            default, default, supportsCooperativeCancellation: true);
        clock.OnSample = () =>
        {
            // The inherited TryCancel path samples the same provider itself. Reenter once,
            // without changing its timestamp/deadline or replacing the original provider.
            clock.OnSample = null;
            Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel),
                "the reentrant original clock cancellation wins");
        };
        var admission = fixture.Prepare(54);
        Ensure(!state.TryPublishRequestAccepted(fixture.Session, ref admission, 1, out var notify) && !notify,
            "post-observation terminal validation forbids stale acceptance");
        admission.Reject(new SharpLinkException(SharpLinkErrorCode.Cancelled, "reentrant cancellation"));
        fixture.Session.FinishPreparedFrameEnqueue(ref admission, out _, out _);
        Ensure(clock.Samples == 2 && !admission.Accepted && state.AcceptedClientStreamCount == 0 &&
            fixture.Session.QueuedSendBytes == 0, "one ACK sample plus inherited cancellation sample, zero ACK, exact reservation refund");
    }

    [Test]
    public async Task ThrowingOriginalClockLeavesPreparedFrameForOutsideGateRefund()
    {
        await using var fixture = new Fixture();
        var failure = new InvalidOperationException("original clock failed");
        var clock = new CallbackClock { OnSample = () => throw failure };
        using var state = ServerCallCancellationState.Rent(55,
            RpcDeadline.Create(TimeSpan.FromSeconds(1), 0, clock.TimestampFrequency), clock,
            default, default, supportsCooperativeCancellation: true);
        var admission = fixture.Prepare(55);
        Exception? observed = null;
        try { state.TryPublishRequestAccepted(fixture.Session, ref admission, 1, out _); }
        catch (Exception error) { observed = error; }
        Ensure(ReferenceEquals(observed, failure) && !admission.Accepted && fixture.Session.QueuedSendBytes > 0,
            "clock failure cannot transfer or silently drop the still-owned writer");
        admission.Reject(failure);
        fixture.Session.FinishPreparedFrameEnqueue(ref admission, out _, out var rejection);
        Ensure(ReferenceEquals(rejection, failure) && state.AcceptedClientStreamCount == 0 &&
            fixture.Session.QueuedSendBytes == 0, "original failure survives exact outside-gate cleanup");
    }

    private sealed class CallbackClock : TimeProvider
    {
        internal Action? OnSample;
        internal int Samples;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { Samples++; OnSample?.Invoke(); return 0; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        internal Pipe Output { get; } = new();
        private readonly SharpLinkRuntimeContext _context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        internal RpcSession Session { get; }
        internal Fixture() => Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            Guid.NewGuid().ToString(), _input.Reader, Output.Writer, RpcSessionTestFixture.ServerOptions(_context));
        internal ServerCallCancellationState State(long id) => ServerCallCancellationState.Rent(
            id, default, TimeProvider.System, default, default, supportsCooperativeCancellation: true);
        internal RpcSession.PreparedFrameEnqueue Prepare(long id)
        {
            var writer = Session.RentFrameWriter();
            using (writer.BeginPacketScope(ProtocolV2FrameType.RequestAccepted,
                       ProtocolV2FrameFlags.None, checked((ulong)id))) { }
            return Session.PrepareFrameEnqueue(writer, false, default, null);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Session.DisposeAsync(); }
            finally
            {
                try { await _input.Writer.CompleteAsync(); }
                finally { try { await Output.Reader.CompleteAsync(); } finally { _context.Dispose(); } }
            }
        }
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
