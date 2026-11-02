using System.Buffers.Binary;
using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class ContractScopedRouteIncarnationControls
{
    [Test]
    public async Task NewIncarnationCannotReviveOldToken()
    {
        var fixture = new ContractScopedRouteHandoffFixture();
        Exception? primary = null;
        try
        {
            await fixture.InitializeAsync();
            await fixture.ExerciseNewIncarnationAsync();
        }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }
}

internal sealed partial class ContractScopedRouteHandoffFixture
{
    internal async Task ExerciseNewIncarnationAsync()
    {
        var old = OriginalTarget;
        var before = View;
        var knownBindings = SnapshotOperations().Where(operation => operation.Stage == "original BindContractDemand")
            .Select(operation => operation.Task).ToArray();
        var writerGate = new ReservationBarrier();
        _barriers.Add(writerGate);
        var oldCallbacks = 0;
        // Explicitly TEST-OWNED delayed writer, retaining a real previously confirmed
        // target/token. This is not claimed to be a delayed original network Bind Task.
        var writer = Own("test-owned old writer launcher", Task.Factory.StartNew(() =>
        {
            writerGate.Entered.TrySetResult(Source);
            writerGate.Release.Wait();
            return Source.Binding.Publication.TryCommit(old.Token, () =>
            {
                Interlocked.Increment(ref oldCallbacks);
                Site.PublishBinding(old);
            });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        await writerGate.WaitAsync();
        // Until the next real Bind is captured, a failure cannot claim the producer graph
        // closed. The existing fixture's conservative setup quarantine handles that exit.
        _initialized = false;
        var oldCleanup = Own("original old-incarnation candidate cleanup", Source.Binding.RetireCandidateAsync(old));
        await InjectOldInvalidationAsync(old.Token, "initial old invalidation");
        var reader = Own("original newer Bind frame reader", _transports[0].Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract));
        await ObserveAsync(reader);
        var requestFrame = ((Task<TestSentFrame>)reader.Task).Result;
        var request = Protocol3BindingControlCodec.ReadBindContract(new(requestFrame.Payload));
        Ensure(request.ContractId == ManualClientContractFixture.ContractId && request.Hash == ManualClientContractFixture.AssemblyHash &&
               request.Requirement == Protocol3BindingRequirement.Compatible && request.Receipt is null,
            "new incarnation comes from a real independent correlated exact-contract Bind request");
        OwnedOperation? newBinding = null;
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
            newBinding = SnapshotOperations().SingleOrDefault(operation => operation.Stage == "original BindContractDemand" &&
                !knownBindings.Contains(operation.Task));
            return newBinding is not null;
        }, "The new incarnation's original Bind Task was not captured while its reply was held.");
        Ensure(!newBinding!.Task.IsCompleted, "new network Bind source remains pending until its actual confirmation");
        var authority = _transports[0].Connection.Authority;
        var token = Protocol3BindingToken.Create(checked((ushort)Protocol3BindingToken.Slot(old.Token)), 2, authority.Limits.Slots);
        using var payload = new PooledByteBufferWriter();
        Protocol3BindingControlCodec.WriteBindResult(payload, new Protocol3BindResult(
            Protocol3BindStatus.Success, token, ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash,
            1, 1, authority.ServerInstanceId, authority.RegistryEpoch), authority.Limits.Slots);
        // One injection only; its source owns independent bytes even after observer timeout.
        var bytes = payload.WrittenMemory.ToArray();
        await ObserveAsync(Own("original newer BindResult injection", _transports[0].Connection.InjectFrameAsync(
            ProtocolV2FrameType.BindResult, ProtocolV2FrameFlags.None, requestFrame.Header.RequestId, bytes)));
        await ObserveAsync(newBinding!);
        await ObserveAsync(oldCleanup);
        _initialized = true;
        var newer = View.Targets.Single();
        Ensure(ReferenceEquals(newer.Connection, Source) && newer.Token == token && !ReferenceEquals(newer, old) &&
               View.BindingRevision > before.BindingRevision && ReferenceEquals(View.Topology.IndexMap, before.Topology.IndexMap),
            "new confirmed target replaces the exact same physical slot without changing endpoint index authority");
        AssertIncarnationAuthority(old, newer);
        writerGate.Release.Set();
        await ObserveAsync(writer);
        Ensure(!((Task<bool>)writer.Task).Result && oldCallbacks == 0,
            "delayed old writer must fail at the real floor gate without invoking its site publication callback");
        AssertIncarnationAuthority(old, newer);
        await InjectOldInvalidationAsync(old.Token, "late old invalidation");
        // Ordered same-session Ping/Pong proves the late old wire invalidation was actually
        // processed before asserting that it cannot retire or replace the newer target.
        var stamp = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(stamp, 160016);
        await ObserveAsync(Own("original invalidation fence Ping", _transports[0].Connection.InjectFrameAsync(
            ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 0, stamp)));
        var pong = Own("original invalidation fence Pong reader", _transports[0].Connection.WaitForSentFrame(ProtocolV2FrameType.Pong));
        await ObserveAsync(pong);
        Ensure(BinaryPrimitives.ReadInt64LittleEndian(((Task<TestSentFrame>)pong.Task).Result.Payload) == 160016,
            "the exact ordered fence must follow the old invalidation in the real source session loop");
        AssertIncarnationAuthority(old, newer);
        await CompleteCallAsync(StartCall("new incarnation ordinary call"), 0, Source, 1600);
        Ensure(ReservationCount == 1 && ReferenceEquals(Reservations.Single(), Source),
            "new exact target remains physically usable after both delayed old controls");
    }

    private void AssertIncarnationAuthority(SharpLinkClient.ContractCallSite.BoundTarget old,
        SharpLinkClient.ContractCallSite.BoundTarget newer)
    {
        var publication = Source.Binding.Publication;
        var floors = Read<long[]>(publication, "_retiredIncarnations");
        var slot = Protocol3BindingToken.Slot(old.Token);
        Ensure(Protocol3BindingToken.Slot(newer.Token) == slot && Protocol3BindingToken.Incarnation(old.Token) == 1 &&
               Protocol3BindingToken.Incarnation(newer.Token) == 2 && Volatile.Read(ref floors[slot]) == 1 &&
               !publication.IsEligible(old.Token) && publication.IsEligible(newer.Token) &&
               View.Targets.Length == 1 && ReferenceEquals(View.Targets[0], newer) &&
               View.Buckets[0].Targets.Length == 1 && ReferenceEquals(View.Buckets[0].Targets[0], newer) &&
               newer.IsEligible && !old.IsEligible,
            "exact slot floor1 rejects incarnation1 but preserves current incarnation2 target authority");
    }

    private async Task InjectOldInvalidationAsync(ulong token, string stage)
    {
        using var payload = new PooledByteBufferWriter();
        Protocol3BindingControlCodec.WriteBindingInvalidated(payload,
            new Protocol3BindingInvalidated(token, Protocol3BindingInvalidationReason.RegistrationRetired),
            _transports[0].Connection.Authority.Limits.Slots);
        var bytes = payload.WrittenMemory.ToArray();
        await ObserveAsync(Own(stage, _transports[0].Connection.InjectFrameAsync(
            ProtocolV2FrameType.BindingInvalidated, ProtocolV2FrameFlags.None, 0, bytes)));
    }
}
