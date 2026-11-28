using System.IO.Pipelines;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Runtime;

public sealed class NStudyCoordinationRejectionTests
{
    private static readonly SharpLinkProtocolOptions Limits = new();

    [Test]
    public void CoordinationFrameRequiresItsExactLengthAndNoFlags()
    {
        var ready = Ready(NewChange());
        var payload = Encode(ready);
        var frame = new ReadOnlySequence<byte>(Frame(ready.Change.Id, ProtocolV2FrameFlags.None, payload.Span));
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref frame, Limits, out var header, out var body) &&
               NStudyWire.ReadCoordination(header.RequestId, body) == ready && frame.IsEmpty,
            "the normal Ready frame must be consumed and decode to its full typed identity");

        ExpectMalformed(Frame(ready.Change.Id, ProtocolV2FrameFlags.HasMetadata, payload.Span));
        ExpectMalformed(Frame(ready.Change.Id, ProtocolV2FrameFlags.None, payload.Span[..^1]));
        var tooLong = new byte[payload.Length + 1];
        payload.Span.CopyTo(tooLong);
        ExpectMalformed(Frame(ready.Change.Id, ProtocolV2FrameFlags.None, tooLong));
    }

    [Test]
    [Arguments("wrong-phase")]
    [Arguments("previous-attempt")]
    [Arguments("previous-physical-session")]
    public async Task ReadyMustMatchTheWaitingPhaseAndFullChange(string difference)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "coordination-inbox", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions(),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, NStudyWire.CoordinationCapability);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var change = NewChange();
        var ready = Ready(change);
        Deliver(session, ready);
        var accepted = await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Ready, guard.Token);
        Ensure(accepted == ready, "the matching phase and identity must satisfy the ordinary inbox");

        var waitingChange = difference == "previous-attempt" ? change with { Id = change.Id + 1 } : change;
        var expectedPhase = difference == "wrong-phase" ? NStudyPhase.Propose : NStudyPhase.Ready;
        var received = difference == "previous-physical-session"
            ? ready with { Change = change with { Authority = change.Authority with { PhysicalSession = Guid.NewGuid() } } }
            : ready;
        // Test the typed receive boundary in memory; no peer or live transport emits these controls.
        Deliver(session, received);
        try
        {
            await session.ReadNStudyCoordinationAsync(waitingChange, expectedPhase, guard.Token);
        }
        catch (SharpLinkException error) when (
            error.Code == SharpLinkErrorCode.ProtocolViolation &&
            SharpLinkProtocolViolationException.Classify(error) == ProtocolViolationReason.ProtocolState)
        {
            return;
        }
        throw new InvalidOperationException($"A Ready with {difference} must not satisfy this transition.");
    }

    private static NStudyChange NewChange() =>
        new(1, NStudyInitiator.Client, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), 42, new(1, 2),
            new(Guid.NewGuid(), 1), new(Guid.NewGuid(), 2));

    private static NStudyCoordinationMessage Ready(NStudyChange change) =>
        new(NStudyPhase.Ready, change, Guid.NewGuid());

    private static ReadOnlyMemory<byte> Encode(NStudyCoordinationMessage message)
    {
        var writer = new ArrayBufferWriter<byte>();
        NStudyWire.WriteCoordination(writer, message);
        return writer.WrittenMemory;
    }

    private static void Deliver(RpcSession session, NStudyCoordinationMessage message) =>
        _ = session.OnNStudyCoordination(message.Change.Id, new(Encode(message)));

    private static byte[] Frame(ulong id, ProtocolV2FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        using var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(writer, NStudyWire.Coordination, flags, id);
        writer.Write(payload);
        ProtocolV2FrameWriter.EndFrame(writer, token);
        return writer.WrittenMemory.ToArray();
    }

    private static void ExpectMalformed(byte[] frame)
    {
        try
        {
            var sequence = new ReadOnlySequence<byte>(frame);
            _ = ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out _, out _);
        }
        catch (SharpLinkException error) when (
            error.Code == SharpLinkErrorCode.ProtocolViolation &&
            SharpLinkProtocolViolationException.Classify(error) == ProtocolViolationReason.MalformedFrame)
        {
            return;
        }
        throw new InvalidOperationException("Invalid coordination flags or length must be classified as a malformed frame.");
    }

    private static void Ensure(bool passed, string message)
    {
        if (!passed)
            throw new InvalidOperationException(message);
    }
}
