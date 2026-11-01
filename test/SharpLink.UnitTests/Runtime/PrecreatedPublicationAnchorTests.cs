using System.Buffers.Binary;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

// Actual Runtime publication/supervisor controls. Client's original-task assignment, winning
// pending CAS and receipt/drain state machine remain separate Client integration obligations.
public sealed class PrecreatedPublicationAnchorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task UnadoptedGroupCannotReadIdentityOrEnterFlowAndFences()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "unadopted-publication", input.Reader, output.Writer, RpcSessionTestFixture.ClientOptions());
        var group = new DeferredPublication(1);
        var codec = new CountingCodec();
        Ensure(group.DeclaredStreamCount == 1, "validated count exists before connection selection");
        Invalid(() => _ = group.Binding);
        Invalid(() => _ = group.RequestId);
        Invalid(() => _ = group.Session);
        Invalid(() => group.ValidateStreamId(1));
        Invalid(() => group.BeginCleanupFence());
        Invalid(() => group.BeginFence());
        Invalid(() => session.SendClientStreamChunkAsync(301, 1, 9, codec, null, default,
            TimeProvider.System, CancellationToken.None, group).GetAwaiter().GetResult());
        Invalid(() => session.CompleteStreamPublicationAsync(group).GetAwaiter().GetResult());
        Invalid(() => session.CompleteStreamCleanupPublicationAsync(group).GetAwaiter().GetResult());
        Ensure(codec.SerializeCount == 0 && session.QueuedSendBytes == 0 && session.PreCreditSerializedBytes == 0,
            "unadopted identity fails before user codec, flow budget, queue or physical fence");
        group.Adopt(new TestRpcBindingPublication(session), 301);
        group.BeginCleanupFence();
        group.BeginFence();
        Ensure(group.RequestId == 301, "failed pre-adoption fence attempts did not mutate fence state");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task UnadoptedRequestCannotTransferToRealSendQueue()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "unadopted-request", input.Reader, output.Writer, RpcSessionTestFixture.ClientOptions());
        var group = new DeferredPublication(1);
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.None, 302))
        {
            var payload = writer.GetSpan(16);
            BinaryPrimitives.WriteUInt64LittleEndian(payload, 1UL << 16);
            BinaryPrimitives.WriteInt64LittleEndian(payload[8..], 1);
            writer.Advance(16);
        }
        Invalid(() => session.SendPacket(writer, publication: group, publicationRole: RpcFramePublicationRole.RequestHead));
        await session.CloseAndDrainOutboundAsync(new InvalidOperationException("negative publication cleanup"));
        Ensure(session.QueuedSendBytes == 0, "invalid pre-adoption Request never becomes an accepted queue owner");
        if (output.Reader.TryRead(out var read))
        {
            Ensure(read.Buffer.Length == 0, "no invented identity can reach transport");
            output.Reader.AdvanceTo(read.Buffer.End);
        }
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task OneShotAdoptionCannotRebindIdentityAndInvalidInputDoesNotHalfPublish()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "one-shot-publication", input.Reader, output.Writer, RpcSessionTestFixture.ClientOptions());
        var first = new TestRpcBindingPublication(session, 1UL << 16);
        var second = new TestRpcBindingPublication(session, 2UL << 16);
        var group = new DeferredPublication(2);
        try { group.Adopt(first, 0); throw new InvalidOperationException("invalid ID was accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Invalid(() => _ = group.RequestId);
        group.Adopt(first, 303);
        Invalid(() => group.Adopt(second, 304));
        Ensure(ReferenceEquals(group.Binding, first) && ReferenceEquals(group.Session, session) && group.RequestId == 303,
            "one-shot identity never replaces token/session/full request ID after adoption");
        group.ValidateProducer(session, 303);
        Invalid(() => group.ValidateProducer(session, 304));
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task ExistingGroupGateSerializesCompetingAdoptionsWithoutMixedIdentity()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "adoption-race", input.Reader, output.Writer, RpcSessionTestFixture.ClientOptions());
        var first = new TestRpcBindingPublication(session, 1UL << 16);
        var second = new TestRpcBindingPublication(session, 2UL << 16);
        var group = new DeferredPublication(1);
        using var start = new Barrier(2);
        bool Attempt(RpcBindingPublication binding, long id)
        {
            Ensure(start.SignalAndWait(Timeout), "both adoption contenders reached existing-gate boundary");
            try { group.Adopt(binding, id); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Task.Run(() => Attempt(first, 305)), Task.Run(() => Attempt(second, 306))).WaitAsync(Timeout);
        Ensure(outcomes[0] != outcomes[1] &&
            (ReferenceEquals(group.Binding, first) ? group.RequestId == 305 : ReferenceEquals(group.Binding, second) && group.RequestId == 306),
            "one existing group gate admits one complete immutable identity, not independent per-field races");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task SealRejectsFreshAnchorBeforeOriginEvenWhileOriginalAnchorRemainsActive()
    {
        var supervisor = new FrameworkTaskSupervisor();
        var first = NewSignal();
        var root = new DeferredPublication(1);
        FrameworkTaskRegistration? original = null;
        supervisor.AdmitStrictPhysicalAnchor(first.Task, "original-anchor", root, ref original);
        supervisor.Seal();
        var fresh = NewSignal();
        FrameworkTaskRegistration? rejected = null;
        var started = 0;
        try
        {
            supervisor.AdmitStrictPhysicalAnchor(fresh.Task, "forbidden-fresh-cleanup", new DeferredPublication(1), ref rejected);
            started++;
        }
        catch (InvalidOperationException) { }
        Ensure(started == 0 && rejected is null && supervisor.CaptureSnapshot().ActiveTasks == 1 &&
            ReferenceEquals(original!.RetainedOwnerRoot, root),
            "Seal forbids fresh origin/cleanup admission without dropping the already-owned anchor");
        fresh.SetResult(); // Rejected setup remains caller-owned; no physical work started.
        var drain = supervisor.DrainAsync();
        Ensure(!drain.IsCompleted, "existing anchor still participates in actual supervisor drain");
        first.SetResult();
        await drain.WaitAsync(Timeout);
        Ensure(original!.RetainedOwnerRoot is null, "completed exact anchor releases its strong group root");
    }

    [Test]
    public async Task AcceptedRegistrationRootsOriginalGroupAndTaskUntilAnchorCompletion()
    {
        var supervisor = new FrameworkTaskSupervisor();
        var completion = NewSignal();
        var origin = NewSignal();
        var root = new DeferredPublication(1);
        FrameworkTaskRegistration? receipt = null;
        supervisor.AdmitStrictPhysicalAnchor(completion.Task, "prestarted-anchor", root, ref receipt);
        root.ActualOrigin = origin.Task; // Existing group field, not a Task rooted by its TCS alone.
        supervisor.Seal();
        var drain = supervisor.DrainAsync();
        Ensure(!drain.IsCompleted && ReferenceEquals(receipt!.RetainedOwnerRoot, root) &&
            ReferenceEquals(((DeferredPublication)receipt.RetainedOwnerRoot!).ActualOrigin, origin.Task),
            "actual supervisor registration roots the same original group and actual origin task");
        origin.SetResult();
        await origin.Task;
        Ensure(!drain.IsCompleted, "origin completion is not automatically all anchor phases completed");
        root.ActualOrigin = null;
        completion.SetResult();
        await drain.WaitAsync(Timeout);
        Ensure(receipt!.RetainedOwnerRoot is null, "root release follows exact anchor completion only");
    }

    [Test]
    public void CompletedAnchorCannotBeAdmittedAsIfPhysicalWorkHadNotStarted()
    {
        var supervisor = new FrameworkTaskSupervisor();
        FrameworkTaskRegistration? receipt = null;
        Invalid(() => supervisor.AdmitStrictPhysicalAnchor(Task.CompletedTask, "already-ended", new DeferredPublication(1), ref receipt));
        Ensure(receipt is null && supervisor.CaptureSnapshot().TotalTracked == 0 && supervisor.CaptureSnapshot().ActiveTasks == 0,
            "pre-start contract excludes completed-task fast-path ambiguity");
    }

    [Test]
    public async Task AdmissionAndSealUseSameGateWithoutPostSealFreshOrigin()
    {
        var supervisor = new FrameworkTaskSupervisor();
        var completion = NewSignal();
        var root = new DeferredPublication(1);
        FrameworkTaskRegistration? receipt = null;
        using var start = new Barrier(2);
        var originStarted = false;
        await Task.WhenAll(Task.Run(() =>
        {
            Ensure(start.SignalAndWait(Timeout), "admission contender ready");
            try
            {
                supervisor.AdmitStrictPhysicalAnchor(completion.Task, "racing-anchor", root, ref receipt);
                originStarted = true;
                root.ActualOrigin = Task.CompletedTask; // Synchronous original assignment, not anchor completion.
            }
            catch (InvalidOperationException) { }
        }), Task.Run(() =>
        {
            Ensure(start.SignalAndWait(Timeout), "Seal contender ready");
            supervisor.Seal();
        })).WaitAsync(Timeout);
        Ensure(originStarted == (receipt is not null), "origin can start only with definitive prior anchor admission");
        var drain = supervisor.DrainAsync();
        if (receipt is not null)
            Ensure(!drain.IsCompleted && ReferenceEquals(receipt.RetainedOwnerRoot, root),
                "admission winner remains rooted even when origin completes synchronously after Seal");
        completion.SetResult();
        await drain.WaitAsync(Timeout);
        Ensure(receipt is null || receipt.RetainedOwnerRoot is null, "exact admitted registration releases its root once");
    }

    private sealed class DeferredPublication(ushort count) : RpcStreamPublication(count)
    {
        // Mirrors the required caller serialization contract, not a second Runtime lock.
        private readonly Lock _existingGroupGate = new();
        internal Task? ActualOrigin;
        internal void Adopt(RpcBindingPublication binding, long id)
        {
            lock (_existingGroupGate) InitializeIdentity(binding, id);
        }
    }

    private sealed class CountingCodec : IRpcCodec<int>
    {
        internal int SerializeCount;
        public void Serialize(in int value, IBufferWriter<byte> buffer) => SerializeCount++;
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new NotSupportedException();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Invalid(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected local invalid-state rejection.");
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
