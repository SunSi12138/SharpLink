using ModelClient = global::SharpLink.UnitTests.BindingPrototype.BindingControlModel.Client;
using ModelServer = global::SharpLink.UnitTests.BindingPrototype.BindingControlModel.Server;
using System.Buffers.Binary;
using System.Linq;
using static SharpLink.UnitTests.BindingPrototype.BindingScalarCodec;
using static SharpLink.UnitTests.BindingPrototype.BindingControlModel;

namespace SharpLink.UnitTests.BindingPrototype;

// 35 MODEL controls mapped to S1 plan obligations. These are NOT 35 completed real S1 integration tests.
// No actual Get, generated proxy, selector, compression, socket, module, SendPump or cleanup callback runs.
public class BindingScalarModelTests
{
    private static void Check(bool condition, string message = "model assertion")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Fails(string reason, Action action)
    {
        try { action(); }
        catch (ModelViolation error) { Check(error.Message == reason, $"Expected {reason}; got {error.Message}"); return; }
        throw new InvalidOperationException("Expected model violation: " + reason);
    }
    private static Result Success(ulong token = 65536) => new(Status.Success, token, 7, 11, 13, 17, 3, ServerId, EpochId);
    private static Key Variant(ulong generation) => Receipt(Requirement.Compatible) with { Generation = generation };
    private static (ModelClient Client, Operation Operation, ModelServer Server, Result Result) Bound(bool confirm = true)
    {
        var client = new ModelClient(); var server = new ModelServer(); var op = client.Begin(Plain());
        var result = server.Bind(op.Id, op.Key);
        if (confirm) client.ConfirmEnqueue(op, true);
        return (client, op, server, result);
    }

    [Test] public void S01_ModelFixedExtensionGoldenBytes()
    {
        var bytes = WriteAuthority(ServerId, EpochId, Limits.Default);
        Check(bytes.Length == 48 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x33,0x22,0x11,0,0x55,0x44,0x77,0x66 }));
        Check(ReadAuthority(bytes) == (ServerId, EpochId, Limits.Default));
        Check(WriteLimits(Limits.Default).AsSpan(0, 4).SequenceEqual(new byte[] { 0,16,0,0 }));
    }
    [Test] public void S02_ModelControlHeaderAndBoundedLengths()
    {
        var frame = WriteFrame(17, 9, WriteBind(Plain()));
        Check(frame[0] == 0x89 && frame[5] == 17 && frame[6] == 0 && BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(1)) == 28);
        Check(ReadFrame(frame, true, Phase.Ready) == ((byte)17, 9UL));
        frame[6] = 1; Fails("flags", () => ReadFrame(frame, true, Phase.Ready)); frame[6] = 0;
        Fails("frame_length", () => ReadFrame(frame[..^1], true, Phase.Ready));
        Fails("direction", () => ReadFrame(frame, false, Phase.Ready));
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), int.MaxValue);
        Fails("frame_length", () => ReadFrame(frame, true, Phase.Ready));
    }
    [Test] public void S03_ModelDoesNotFallbackToBusinessGrammar()
    {
        var frame = WriteFrame(17, 1, WriteBind(Plain())); frame[5] = 4;
        Fails("direction", () => ReadFrame(frame, true, Phase.Ready));
        frame[5] = 17; Fails("phase", () => ReadFrame(frame, true, Phase.Handshaking));
        // Full old/new handshake and legitimate 36-byte business payload acceptance remain integration obligations.
    }
    [Test] public void S04_ModelLimitBoundsAndReservedBytes()
    {
        Fails("limits", () => WriteLimits(Limits.Default with { Slots = 0 }));
        Fails("limits", () => WriteLimits(Limits.Default with { Operations = 65 }));
        Fails("limits", () => WriteLimits(Limits.Default with { Streams = 128 }));
        var bytes = WriteLimits(Limits.Default); bytes[14] = 1; Fails("reserved", () => ReadLimits(bytes));
        Fails("limits_length", () => ReadLimits(bytes[..^1]));
    }
    [Test] public void S05_ModelReceiptAndResultCanonicalEncodings()
    {
        foreach (var key in new[] { Plain(), Receipt(Requirement.Compatible), Receipt(Requirement.MinimumVersion), Receipt() })
            Check(ReadBind(WriteBind(key)) == key);
        foreach (Status status in Enum.GetValues<Status>())
        {
            var result = status == Status.Success ? Success() : Result.Failure(status);
            Check(ReadResult(WriteResult(result)) == result);
        }
        Fails("receipt_required", () => WriteBind(Plain() with { Requirement = Requirement.MinimumVersion }));
        Fails("failure_body", () => WriteResult(Success() with { Status = Status.HashMismatch }));
        foreach (var length in new[] { 27, 29, 75, 77 }) Fails("bind_length", () => ReadBind(new byte[length]));
        var reserved = WriteBind(Plain()); reserved[2] = 1; Fails("reserved", () => ReadBind(reserved));
    }
    [Test] public void S06_ModelDemandCreationDoesNotPublishBytes()
    {
        var client = new ModelClient(); var op = client.Begin(Receipt());
        Check(op.Id == 1 && !op.Accepted && client.CandidateCount == 0);
        // This is no claim about actual Get or Connect; neither is invoked.
    }
    [Test] public void S07_ModelUnboundDemandHasNoCandidate()
    {
        var client = new ModelClient(); var op = client.Begin(Plain());
        Check(!client.HasCandidate(op.Key) && op.Final is null);
    }
    [Test] public void S08_ModelStaticBindingAndDispatchIdentity()
    {
        var (client, op, server, result) = Bound(); client.Receive(op.Id, result);
        Check(client.HasCandidate(op.Key) && server.Dispatch(server.Capture(result.Token)) && server.BusinessExecutions == 1);
        Fails("ready_candidate_requires_release", () => client.Begin(op.Key));
    }
    [Test] public void S09_ModelMismatchDoesNotCreateEntry()
    {
        var server = new ModelServer();
        Check(server.Bind(1, Plain() with { HashLow = 99 }).Status == Status.HashMismatch);
        Check(server.Bind(2, Plain(99)).Status == Status.UnknownContract);
        Check(server.Bind(3, Receipt() with { Server = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff") }).Status == Status.ReceiptScopeMismatch);
        Check(server.Occupancy == 0 && server.BusinessExecutions == 0);
    }
    [Test] public void S10_ModelMinimumUsesRegistrationGeneration()
    {
        var server = new ModelServer { UnrelatedGlobalGeneration = 999 };
        Check(server.Bind(1, Receipt(Requirement.MinimumVersion) with { Generation = 4 }).Status == Status.BelowMinimum);
        Check(server.Bind(2, Receipt() with { Registration = 18 }).Status == Status.RegistrationMismatch);
        Check(server.Bind(3, Receipt(Requirement.Compatible) with { Generation = 999 }).Status == Status.Success);
    }
    [Test] public void S11_ModelReceiptEligibilityExceptionIsScoped()
    {
        Check(!Eligible(Plain(), false, ServerId, EpochId));
        Check(Eligible(Receipt(), false, ServerId, EpochId));
        Check(!Eligible(Receipt(), false, ServerId, new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff")) && !Eligible(Receipt(), true, ServerId, EpochId, false));
    }
    [Test] public void S12_ModelEqualTokenNumbersDoNotProveSameSession()
    {
        var first = new ModelServer(); var second = new ModelServer { Registration = 18 };
        var firstResult = first.Bind(1, Plain()); var secondResult = second.Bind(1, Plain());
        Check(firstResult.Token == secondResult.Token);
        Check(!second.Dispatch(first.Capture(firstResult.Token)));
        var (client, op, _, result) = Bound();
        Fails("session", () => client.Receive(op.Id, result, sameSession: false)); Check(client.Closed);
    }
    [Test] public void S13_ModelExactReservationNeverUsesRedirect()
    {
        var a = new Connection(new object(), 1); var b = new Connection(new object(), 2); a.Redirect = b;
        Check(a.ReserveExact(a.Session, 1)); a.Open = false;
        Check(!a.ReserveExact(a.Session, 1) && b.Reservations == 0 && a.Reservations == 1); a.Release();
        Check(!b.ReserveExact(a.Session, 1));
    }
    [Test] public void S14_ModelCapturedEntryCannotBecomeReplacement()
    {
        var server = new ModelServer(1); var old = server.Bind(1, Plain()); var capture = server.Capture(old.Token);
        server.Release(old.Token); var next = server.Bind(2, Plain());
        Check(capture is not null && capture.Token == old.Token && capture.Token != next.Token && server.Capture(old.Token) is null);
        Check(server.Dispatch(capture)); // Documented pre-release captured lookup, not module retirement proof.
    }
    [Test] public void S15_ModelOldReleaseCannotEvictNewEntry()
    {
        var server = new ModelServer(1); var old = server.Bind(1, Plain()); server.Release(old.Token);
        var next = server.Bind(2, Plain()); server.Release(old.Token);
        Check(server.Capture(next.Token) is not null && server.Occupancy == 1);
    }
    [Test] public void S16_ModelGenerationExhaustionDoesNotWrap()
    {
        var server = new ModelServer(1, 2); var a = server.Bind(1, Plain()); server.Release(a.Token);
        var b = server.Bind(2, Plain()); server.Release(b.Token);
        Check(a.Token == 65536 && b.Token == 131072 && server.Bind(3, Plain()).Status == Status.BindingCapacity);
        Check(server.Capture(0) is null && server.Capture(ulong.MaxValue) is null);
    }
    [Test] public void S17_ModelCoalescingDoesNotCancelOtherWaiter()
    {
        var client = new ModelClient(); var a = client.Begin(Plain()); var b = client.Begin(Plain());
        Check(ReferenceEquals(a, b) && a.Waiters == 2); client.CancelWaiter(a);
        Check(!a.Cancelled && a.Waiters == 1); client.ConfirmEnqueue(a, true);
        Check(!ReferenceEquals(a, client.Begin(Receipt())));
    }
    [Test] public void S18_ModelCancelledHeadRetainsBoundedRing()
    {
        var client = new ModelClient(window: 2); var a = client.Begin(Plain()); client.ConfirmEnqueue(a, true);
        var b = client.Begin(Variant(1)); client.ConfirmEnqueue(b, true); client.CancelWaiter(a);
        client.Receive(b.Id, Success(65537));
        Check(client.Retired == 0 && client.Occupancy == 2);
        Fails("window", () => client.Begin(Variant(2))); client.Expire(); Check(client.Closed && client.Occupancy == 0);
        var ordered = new ModelClient(window: 2); var x = ordered.Begin(Plain()); ordered.ConfirmEnqueue(x, true);
        var y = ordered.Begin(Variant(1)); ordered.ConfirmEnqueue(y, true);
        ordered.Receive(y.Id, Success(65537)); Check(ordered.Retired == 0);
        ordered.Receive(x.Id, Result.Failure(Status.HashMismatch)); Check(ordered.Retired == 2);
    }
    [Test] public void S19_ModelPublicationPrecedesCancellationCleanup()
    {
        var (client, op, server, result) = Bound(confirm: false); client.CancelWaiter(op);
        client.Receive(op.Id, result); Check(!op.ProtocolTerminal && client.CandidateCount == 0);
        client.ConfirmEnqueue(op, true); Check(op.CleanupRequired);
        server.Cancel(op.Id); client.ConfirmCleanup(op, true); Check(server.Occupancy == 0 && client.Retired == 1);
        var unpublished = new ModelClient(); var tentative = unpublished.Begin(Plain()); unpublished.CancelWaiter(tentative);
        unpublished.ConfirmEnqueue(tentative, false); Check(unpublished.Begin(Plain()).Id == tentative.Id);
    }
    [Test] public void S20_ModelInvalidationBeforeAckCannotResurrect()
    {
        var (client, op, _, result) = Bound(); client.Invalidate(result.Token + 65536);
        client.Receive(op.Id, result); Check(!client.HasCandidate(op.Key) && op.CleanupRequired);
        client.ConfirmCleanup(op, true); Check(client.Retired == 1);
    }
    [Test] public void S21_ModelDuplicateAndUnknownOperationRules()
    {
        var (client, op, _, result) = Bound(); client.Receive(op.Id, result); client.Receive(op.Id, result);
        Check(client.CandidateCount == 1 && client.Retired == 1);
        Fails("unknown_operation", () => client.Receive(99, result)); Check(client.Closed);
    }
    [Test] public void S22_ModelSlotAndWaiterCaps()
    {
        var server = new ModelServer(1); Check(server.Bind(1, Plain()).Status == Status.Success);
        Check(server.Bind(2, Plain()).Status == Status.BindingCapacity && server.Occupancy == 1);
        var client = new ModelClient(waiterLimit: 1); client.Begin(Plain()); Fails("waiter_capacity", () => client.Begin(Plain()));
        Check(client.Waiters == 1);
        var sites = new ModelClient(siteLimit: 1); var first = sites.Begin(Plain()); sites.ConfirmEnqueue(first, true);
        Fails("site_capacity", () => sites.Begin(Receipt()));
        var perDemand = new ModelClient(waiterLimit: 128);
        for (var i = 0; i < 64; i++) perDemand.Begin(Plain());
        Fails("waiter_capacity", () => perDemand.Begin(Plain())); Check(perDemand.Waiters == 64);
    }
    [Test] public void S23_ModelFinalQueueFailureClearsEntry()
    {
        var server = new ModelServer(); server.Bind(1, Plain(), canQueueFinal: false);
        Check(server.Closed && server.Occupancy == 0);
    }
    [Test] public void S24_ModelControlExpiryClearsCorrelationAndWaiters()
    {
        var (client, op, _, _) = Bound(); client.Expire();
        Check(client.Closed && client.Occupancy == 0 && client.Waiters == 0 && op.Waiters == 0);
        var server = new ModelServer(); var bound = server.Bind(1, Plain()); var captured = server.Capture(bound.Token);
        server.Close(); Check(!server.Dispatch(captured) && server.BusinessExecutions == 0);
    }
    [Test] public void S25_ModelUnknownTokenMethodAndShapeDoNotDispatch()
    {
        var server = new ModelServer(); var result = server.Bind(1, Plain());
        Check(!server.Dispatch(server.Capture(result.Token + 1)));
        Check(!server.Dispatch(server.Capture(result.Token), 99));
        Check(!server.Dispatch(server.Capture(result.Token), shapeMatches: false)); Check(server.BusinessExecutions == 0);
    }
    [Test] public void S26_ModelScalarRoundTripKeepsIdentity()
    {
        var result = Success(); var copy = ReadResult(WriteResult(result)); Check(copy == result);
        // No compression/metadata/persistent decoder executed; full S26 stays pending.
    }
    [Test] public void S27_ModelBindingControlsCannotMasqueradeAsOneWayStream()
    {
        Fails("type", () => WriteFrame(23, 1, new byte[18]));
        Check(!Queues.IsNewControlProgress(23)); // Full stream grammar intentionally not implemented here.
    }
    [Test] public void S28_ModelBindDoesNotRunBusinessDispatch()
    {
        var server = new ModelServer(); server.Bind(1, Plain()); Check(server.BusinessExecutions == 0);
    }
    [Test] public void S29_ModelRawMissingIdentityIsNotInferred()
    {
        Fails("identity", () => new ModelClient().Begin(default));
        // No actual public raw facade or module/metadata wrapper behavior changed/exercised.
    }
    [Test] public void S30_ModelAllNewControlsStayNormalClass()
    {
        for (byte type = 17; type <= 23; type++) Check(!Queues.IsNewControlProgress(type));
        // Existing production selectors/P2C/expansion are not modeled or accepted by this check.
    }
    [Test] public void S31_ModelInvalidKeyPrecedesStateAllocation()
    {
        var client = new ModelClient(); Fails("identity", () => client.Begin(default)); Check(client.Occupancy == 0);
        // Actual family-specific codec/cancellation/interceptor ordering stays in separate production tests.
    }
    [Test] public void S32_ModelMandatoryCleanupFailureEndsSession()
    {
        var (client, op, _, result) = Bound(); client.CancelWaiter(op); client.Receive(op.Id, result);
        client.ConfirmCleanup(op, false); Check(client.Closed && client.Occupancy == 0);
        // Does not execute a throwing claimed-frame callback; held #964 physical cleanup remains pending.
    }
    [Test] public void S33_ModelGoAwayRaceDoesNotAbortAdmittedWork()
    {
        var server = new ModelServer(); var existing = server.Bind(1, Plain()); var captured = server.Capture(existing.Token);
        var queues = new Queues(); queues.Normal.Enqueue("bind"); queues.Progress.Enqueue("goaway");
        Check(queues.DrainOne() == "goaway"); server.Phase = Phase.Draining;
        Check(queues.DrainOne() == "bind" && server.Bind(2, Plain()).Status == Status.SessionDraining);
        Check(!server.Closed && server.Occupancy == 1 && server.Dispatch(captured));
    }
    [Test] public void S34_ModelOldCancelCannotRevokeNewDemand()
    {
        var server = new ModelServer(); var a = server.Bind(1, Plain()); var b = server.Bind(2, Plain());
        Check(a.Token != b.Token); server.Cancel(1); server.Release(a.Token);
        Check(server.Capture(b.Token) is not null && server.Capture(a.Token) is null);
        var client = new ModelClient(); var old = client.Begin(Plain()); client.ConfirmEnqueue(old, true); client.CancelWaiter(old);
        Check(!ReferenceEquals(old, client.Begin(Plain())));
    }
    [Test] public void S35_ModelEarlyResultWaitsForPublicationConfirmation()
    {
        var (client, op, _, result) = Bound(confirm: false);
        client.Receive(op.Id, result); client.Receive(op.Id, result);
        Check(client.CandidateCount == 0 && client.Retired == 0); client.ConfirmEnqueue(op, true);
        Check(client.CandidateCount == 1 && client.Retired == 1);
        var bad = Bound(confirm: false); bad.Client.Receive(bad.Operation.Id, bad.Result);
        Fails("conflicting_result", () => bad.Client.Receive(bad.Operation.Id, Result.Failure(Status.HashMismatch)));
        Check(bad.Client.Closed);
        var uncertain = Bound(confirm: false); uncertain.Client.Receive(uncertain.Operation.Id, uncertain.Result);
        uncertain.Client.ConfirmEnqueue(uncertain.Operation, false); Check(uncertain.Client.Closed);
        var failure = Bound(confirm: false); failure.Client.Receive(failure.Operation.Id, Result.Failure(Status.HashMismatch));
        Check(failure.Client.Retired == 0); failure.Client.ConfirmEnqueue(failure.Operation, true);
        Check(failure.Client.Retired == 1 && failure.Client.CandidateCount == 0);
    }
}
