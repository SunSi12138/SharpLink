using SharpLink.FlowStatePhaseB;

namespace SharpLink.UnitTests.Runtime;

public class PhaseBWriterBoundaryTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Returned(IRpcByteBufferWriter buffer)
    {
        try { _ = buffer.WrittenCount; }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("The real pump must release its frame buffer.");
    }

    [Test]
    public async Task QueueRejectionRefundsOnlyTheUnenqueuedPublication()
    {
        await using var fixture = new PhaseBWriterBoundaryFixture(window: 32 * 1024, sendQueueBytes: 32 * 1024);
        var lease = await fixture.Credits.OpenAsync(1);
        // A real 32 KiB send queue keeps 4 KiB for protocol progress. The normal
        // 30 KiB StreamData cannot be admitted even to an otherwise empty queue.
        var ticket = await fixture.EnqueueAsync(lease, 30 * 1024);
        await ticket.Completion;
        Require(!ticket.Accepted && ticket.Rejection is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
            "deterministic queue-capacity rejection");
        Require(ticket.Refunded == true && ticket.Settlements == 1, "known-unenqueued credit is refunded once");
        Returned(ticket.Packet);
        Require(!fixture.Writer.Entered.IsCompleted && fixture.Session.IsConnected, "no bytes emitted; healthy session retained");
        var ledger = await fixture.Credits.SnapshotAsync();
        Require(ledger.Pending == 0 && ledger.Outstanding == 0 && ledger.Publications == 0 &&
            ledger.Free + ledger.Unspent == 32 * 1024, "rejection conserves connection credit");
    }

    [Test]
    public async Task ReadableFrameAndEarlyCreditDoNotRetireAStillOwnedPublication()
    {
        await using var fixture = new PhaseBWriterBoundaryFixture();
        var lease = await fixture.Credits.OpenAsync(1);
        var ticket = await fixture.EnqueueAsync(lease, 16);
        Require(ticket.Accepted, "queue accepted the frame");
        await fixture.Writer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ReadExactFrameAsync(ticket);
        await fixture.Credits.CloseAsync(lease);
        // Generation-tagged credit is still modeled; actual bytes were read from
        // the real SendPump transport before this early peer-consumption event.
        await fixture.Credits.WindowUpdateAsync(lease, 16);
        Require((await fixture.Credits.SnapshotAsync()) is { Free: 64, Retained: 1, Publications: 1, Outstanding: 0 },
            "early credit cannot recycle a writer-pinned tombstone");
        Require(!ticket.Completion.IsCompleted && ticket.Settlements == 0, "pump completion is still paused");
        fixture.Writer.Release();
        await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Require((await fixture.Credits.SnapshotAsync()) is { Free: 64, Retained: 0, Publications: 0 }, "writer finally releases the pin");
        Returned(ticket.Packet);
        var fresh = await fixture.Credits.OpenAsync(1);
        Require(ReferenceEquals(lease.State, fresh.State) && lease.Generation != fresh.Generation, "real model state reuse only after settlement");
    }

    [Test]
    public async Task CancelingObservationDoesNotCancelThePumpOrRefundAcceptedBytes()
    {
        await using var fixture = new PhaseBWriterBoundaryFixture();
        var lease = await fixture.Credits.OpenAsync(1);
        var ticket = await fixture.EnqueueAsync(lease, 16);
        await fixture.Writer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var observer = ticket.ObserveAsync(cts.Token);
        cts.Cancel();
        try { await observer; throw new Exception("expected canceled observer"); }
        catch (OperationCanceledException) { }
        Require(!ticket.Completion.IsCompleted && ticket.Settlements == 0, "durable writer completion is independent of the observer");
        Require((await fixture.Credits.SnapshotAsync()) is { Pending: 0, Outstanding: 16, Publications: 1 }, "no cancellation refund");
        await fixture.ReadExactFrameAsync(ticket);
        fixture.Writer.Release();
        await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Require(ticket.Refunded == false && ticket.Settlements == 1 && fixture.Session.IsConnected, "normal completion retains peer debt");
        await fixture.Credits.WindowUpdateAsync(lease, 16);
        await fixture.Credits.CloseAsync(lease);
        Require((await fixture.Credits.SnapshotAsync()).Free == 64, "only peer credit returns the accepted debit");
        Returned(ticket.Packet);
    }

    [Test]
    public async Task VisibleBytesThenFlushFailureCannotBeRefundedAsUnsent()
    {
        await using var fixture = new PhaseBWriterBoundaryFixture();
        var lease = await fixture.Credits.OpenAsync(1);
        var ticket = await fixture.EnqueueAsync(lease, 16);
        await fixture.Writer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ReadExactFrameAsync(ticket);
        var failure = new IOException("deterministic failure after real bytes became visible");
        fixture.Writer.FailAndRelease(failure);
        try { await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("expected transport failure"); }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ConnectionClosed && ReferenceEquals(error.InnerException, failure)) { }
        Require(!fixture.Session.IsConnected && fixture.Session.LifetimeToken.IsCancellationRequested, "actual SendPump failure terminates the real session");
        Require(ticket.Refunded == false && ticket.Settlements == 1, "failed emission after enqueue is not known-unsent");
        var ledger = await fixture.Credits.SnapshotAsync();
        Require(ledger is { Outstanding: 16, Publications: 0, Pending: 0 }, "do not manufacture send credit on transport ambiguity");
        var acquisitions = fixture.Credits.AcquireSubmissions;
        try { await fixture.EnqueueAsync(lease, 1); throw new Exception("expected stopped session rejection"); }
        catch (SharpLinkException) { }
        Require(fixture.Credits.AcquireSubmissions == acquisitions, "stopped session prevents another model admission");
        Returned(ticket.Packet);
        Returned(fixture.LastPacket!);
    }

    [Test]
    public async Task SessionFaultCancelsAWaitingModelAdmissionWithoutLeakingItsPreparedBuffer()
    {
        await using var fixture = new PhaseBWriterBoundaryFixture(window: 16);
        var first = await fixture.Credits.OpenAsync(1);
        var next = await fixture.Credits.OpenAsync(2);
        var ticket = await fixture.EnqueueAsync(first, 16);
        await fixture.Writer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = fixture.EnqueueAsync(next, 1);
        var waitingPacket = fixture.LastPacket!;
        Require((await fixture.Credits.SnapshotAsync()).Waiters == 1, "second frame is waiting on connection credit, not in the pump");
        var failure = new IOException("fault while the second writer waits for credit");
        fixture.Writer.FailAndRelease(failure);
        try { await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("expected first emission failure"); }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ConnectionClosed && ReferenceEquals(error.InnerException, failure)) { }
        try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("expected waiting credit cancellation"); }
        catch (OperationCanceledException) { }
        Require((await fixture.Credits.SnapshotAsync()) is { Waiters: 0, Pending: 0, Publications: 0, Outstanding: 16 },
            "session cancellation releases waiter ownership but not the earlier accepted debt");
        Require(!next.State.AcquirePending && !next.State.AcquireCommand.IsBusy, "prepared completion slot released");
        Returned(waitingPacket);
    }

    [Test]
    public async Task CallerCancellationBeforeAdmissionReturnsThePreparedBufferWithoutAWriterPin()
    {
        await using var fixture = new PhaseBWriterBoundaryFixture();
        var lease = await fixture.Credits.OpenAsync(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await fixture.EnqueueAsync(lease, 16, cts.Token); throw new Exception("expected canceled admission"); }
        catch (OperationCanceledException) { }
        Require((await fixture.Credits.SnapshotAsync()) is { Free: 64, Pending: 0, Outstanding: 0, Publications: 0 },
            "no admission or ownership transferred");
        Require(!fixture.Writer.Entered.IsCompleted, "no pump emission for canceled admission");
        Returned(fixture.LastPacket!);
    }
}
