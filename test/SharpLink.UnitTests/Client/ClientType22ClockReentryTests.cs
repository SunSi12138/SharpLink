using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Same actual fixture/owners as ClientType22AcceptanceTests. Clock callbacks deliberately
// reenter production terminalization; no copied arbitration model and no timing sleeps.
public sealed partial class ClientType22AcceptanceTests
{
    [Test]
    public Task AckClockReentryCannotAcceptAfterItsCancellationRemovedTheExactCall()
        => VerifyClockReentry(ClockPhase.Ack);

    [Test]
    public Task StartClockReentryCannotRunWriterAfterItsCancellationRemovedTheExactCall()
        => VerifyClockReentry(ClockPhase.Start);

    [Test]
    public Task HeadClockReentryCannotTransferAfterItsCancellationRemovedTheExactCall()
        => VerifyClockReentry(ClockPhase.Head);

    [Test]
    public Task PermanentlyThrowingAckClockStillDeliversTerminalAndJoinsPhysicalOwners()
        => VerifyThrowingClock(ClockPhase.Ack);

    [Test]
    public Task PermanentlyThrowingStartClockStillDeliversTerminalAndJoinsPhysicalOwners()
        => VerifyThrowingClock(ClockPhase.Start);

    [Test]
    public Task PermanentlyThrowingHeadClockStillReturnsPreparedWriterAndOriginalOwners()
        => VerifyThrowingClock(ClockPhase.Head);

    private enum ClockPhase { Ack, Start, Head }

    private static async Task VerifyClockReentry(ClockPhase phase)
    {
        var clock = new ArbitrationClock();
        await using var f = new Fixture(clock);
        var group = f.CreateGroup(deadline: RpcDeadline.Create(TimeSpan.FromMinutes(1), clock));
        var pending = ExactPending(f, group); // Read-only observation of the original physically pinned object.
        if (phase != ClockPhase.Head) await f.PublishHead(group);
        else await f.HoldPing().WaitAsync(Timeout); // Establish actual pump before targeting the new head clock sample.
        if (phase == ClockPhase.Start) f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
        var entries = 0;
        var writer = new Writer((_, _, _) => { entries++; return ValueTask.CompletedTask; });
        CountingClockWriter? packet = phase == ClockPhase.Head ? PrepareClockHead(f, group) : null;
        Task? physical = null;
        clock.OnNextSample(f.Cancel); // Actual original cancellation callback removes the slot reentrantly.
        try
        {
            switch (phase)
            {
                case ClockPhase.Ack:
                    f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
                    Ensure(!pending.PeerAccepted, "ACK cannot commit against its reentrantly removed incarnation");
                    break;
                case ClockPhase.Start:
                    physical = group.RunWriterAsync(writer, group.ProducerToken);
                    break;
                case ClockPhase.Head:
                    Ensure(!TransferClockHead(f, group, packet!), "reentrant terminal wins before actual Request transfer");
                    Ensure(!pending.RequestPublished && !group.HeadPublicationConfirmed && packet!.ReturnCount == 1,
                        "neveraccepted prepared writer is returned once, no Request/head commit");
                    break;
            }
            Ensure(clock.Reentries == 1 && !pending.ProducerStarted, "one outside-gate sample cannot commit stale producer state");
            Ensure(await Failure(f.Response) is OperationCanceledException, "original pending operation delivers its cancellation once");
            physical ??= group.RunWriterAsync(writer, group.ProducerToken);
            await physical.WaitAsync(Timeout);
            Ensure(entries == 0 && f.Connection.PendingCalls.Count == 0 && f.Connection.PendingCalls.ActiveCount == 0 &&
                f.Connection.CallAdmissionReservationCount == 0 && f.Session.QueuedSendBytes == 0,
                "no user writer and no lost prepared/send/pending/selection physical owner");
        }
        finally
        {
            clock.Reset(); f.Output.Release(); f.Cancel();
            physical ??= group.FinishAsync();
            await Failure(physical);
        }
    }

    private static async Task VerifyThrowingClock(ClockPhase phase)
    {
        var clock = new ArbitrationClock();
        await using var f = new Fixture(clock);
        var group = f.CreateGroup(deadline: RpcDeadline.Create(TimeSpan.FromMinutes(1), clock));
        var pending = ExactPending(f, group);
        if (phase != ClockPhase.Head) await f.PublishHead(group);
        else await f.HoldPing().WaitAsync(Timeout); // Establish actual pump before targeting the new head clock sample.
        if (phase == ClockPhase.Start) f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
        var expected = new InvalidOperationException("permanently-throwing-original-clock");
        var entries = 0;
        var writer = new Writer((_, _, _) => { entries++; return ValueTask.CompletedTask; });
        CountingClockWriter? packet = phase == ClockPhase.Head ? PrepareClockHead(f, group) : null;
        Task? physical = null;
        clock.ThrowPermanently(expected); // Kept broken until logical AND physical owners have settled.
        try
        {
            Exception? directFailure = null;
            switch (phase)
            {
                case ClockPhase.Ack:
                    directFailure = Capture(() => f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId));
                    Ensure(!pending.PeerAccepted, "throwing sample cannot grant ACK acceptance");
                    break;
                case ClockPhase.Start:
                    physical = group.RunWriterAsync(writer, group.ProducerToken);
                    break;
                case ClockPhase.Head:
                    directFailure = Capture(() => TransferClockHead(f, group, packet!));
                    Ensure(!pending.RequestPublished && !group.HeadPublicationConfirmed && packet!.ReturnCount == 1,
                        "throwing sample cannot transfer or leak prepared writer");
                    break;
            }
            if (phase != ClockPhase.Start) Ensure(ContainsClockFailure(directFailure, expected), "direct clock failure preserved");
            Ensure(ReferenceEquals(await Failure(f.Response), expected), "pending operation observes original clock error, not a second resampling failure");
            physical ??= group.RunWriterAsync(writer, group.ProducerToken);
            var physicalFailure = await Failure(physical);
            if (phase != ClockPhase.Head) Ensure(ContainsClockFailure(physicalFailure, expected), "cleanup clock/producer failure remains observed through real Session drain");
            Ensure(entries == 0 && !pending.ProducerStarted && f.Connection.PendingCalls.Count == 0 &&
                f.Connection.PendingCalls.ActiveCount == 0 && f.Connection.CallAdmissionReservationCount == 0 &&
                f.Session.QueuedSendBytes == 0 && (packet is null || packet.ReturnCount == 1),
                "permanent provider failure cannot strand original capacity, frame or producer owner");
        }
        finally
        {
            // Reset only for fixture shutdown, AFTER the assertions above joined real owners.
            clock.Reset(); f.Output.Release(); f.Cancel();
            physical ??= group.FinishAsync();
            await Failure(physical);
        }
    }

    private static PendingRequestTable.PendingCall ExactPending(Fixture f, ClientStreamPublicationGroup group)
    {
        var slots = (PendingRequestTable.PendingCall?[])typeof(PendingRequestTable)
            .GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Connection.PendingCalls)!;
        return slots.Single(call => call?.Id == group.RequestId)!;
    }

    private static CountingClockWriter PrepareClockHead(Fixture f, ClientStreamPublicationGroup group)
    {
        var packet = new CountingClockWriter();
        using (packet.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.Cancellable, (ulong)group.RequestId))
        {
            var span = packet.GetSpan(16);
            BinaryPrimitives.WriteUInt64LittleEndian(span, group.Binding.Token);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], 1);
            packet.Advance(16);
        }
        Ensure(ReferenceEquals(f.Session.PrepareOutboundFrame(packet, group.ProducerToken), packet), "uncompressed test head keeps its counted actual owner");
        return packet;
    }

    private static bool TransferClockHead(Fixture f, ClientStreamPublicationGroup group, CountingClockWriter packet)
        => f.Connection.PendingCalls.TryPublishRequest(group.RequestId, f.Session, packet, false,
            group.ProducerToken, f.Connection.PendingCalls, out _, out _, group);

    private static bool ContainsClockFailure(Exception? failure, Exception expected)
        => ReferenceEquals(failure, expected) || failure is AggregateException aggregate && aggregate.InnerExceptions.Any(error => ContainsClockFailure(error, expected));

    private sealed class ArbitrationClock : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        private Action? _next;
        private Exception? _failure;
        internal int Reentries;
        internal void OnNextSample(Action action) => _next = action;
        internal void ThrowPermanently(Exception error) => Volatile.Write(ref _failure, error);
        internal void Reset() { _next = null; Volatile.Write(ref _failure, null); }
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp()
        {
            if (Volatile.Read(ref _failure) is { } failure) throw failure;
            var action = Interlocked.Exchange(ref _next, null);
            if (action is not null) { Interlocked.Increment(ref Reentries); action(); }
            return _inner.GetTimestamp();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _inner.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class CountingClockWriter : IRpcByteBufferWriter
    {
        private readonly PooledByteBufferWriter _inner = new();
        internal int ReturnCount;
        public int WrittenCount => _inner.WrittenCount;
        public ReadOnlyMemory<byte> WrittenMemory => _inner.WrittenMemory;
        public Span<byte> WrittenSpan => _inner.WrittenSpan;
        public int Capacity => _inner.Capacity;
        public void Advance(int count) => _inner.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public void Clear() => _inner.Clear();
        public void Dispose() { Interlocked.Increment(ref ReturnCount); _inner.Dispose(); }
    }
}
