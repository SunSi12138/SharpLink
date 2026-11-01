namespace SharpLink.UnitTests.Runtime;

public sealed class DeclaredStreamReservationTests
{
    [Test]
    public void PartialCapacityFailureRollsBackOnlyNewExactRoutes()
    {
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null,
            maxActiveStreams: 2, activeStreamCapacityExceeded: null);
        Ensure(Reserve(manager, 31, 1), "original stable mailbox reserved");
        Exception? failure = null;
        try { _ = Reserve(manager, 31, 3); }
        catch (SharpLinkException error) { failure = error; }
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted }, "third route exceeds real cap");
        Ensure(manager.ActiveStreamCount == 1, "new stream2 rolled back; reused stream1 not doublecharged or removed");
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "failed candidate Session teardown"));
        Ensure(manager.ActiveStreamCount == 0, "caller terminalization drains the reused owner");
    }

    [Test]
    public void TerminalManagerCannotReportAWholeReservationAsAccepted()
    {
        var manager = new StreamManager();
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "closed before reservation"));
        Ensure(!Reserve(manager, 32, 2) && manager.ActiveStreamCount == 0,
            "void completion of supplied dispatcher is not route acceptance");
    }

    [Test]
    public void ReusedDeclaredRoutesDoNotDoubleChargeAndAbandonStaysMonotonic()
    {
        var manager = new StreamManager();
        Ensure(Reserve(manager, 33, 2) && Reserve(manager, 33, 2), "same stable two mailboxes reconfigure");
        Ensure(manager.ActiveStreamCount == 2, "reconfiguration has no duplicate route charge");
        manager.AbandonExistingRequestStreams(33, 2);
        Ensure(!Reserve(manager, 33, 2), "an abandoned mailbox cannot be made accepted again");
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "cleanup"));
        Ensure(manager.ActiveStreamCount == 0, "all exact route owners returned");
    }

    private static bool Reserve(StreamManager manager, long requestId, int count)
        => manager.TryReservePreAdmissionStreams(requestId, count, RpcSessionTestFixture.RuntimeContext.Buffers,
            static _ => true, static _ => { }, static () => { }, retainUntilLocalCompletion: true);
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
