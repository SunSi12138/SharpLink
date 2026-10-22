using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

public sealed class CooperativeMaintenanceProtocolTests
{
    [Test]
    public void MaintenanceEnvelopeRoundTripsEveryExactBinding()
    {
        foreach (var stage in Enum.GetValues<CooperativeMaintenanceStage>())
        {
            var message = Message(stage);
            var writer = new PooledByteBufferWriter();
            message.Write(writer);
            Ensure(writer.WrittenCount == CooperativeMaintenanceMessage.PayloadBytes, "fixed bounded payload");
            var read = CooperativeMaintenanceMessage.Read(new ReadOnlySequence<byte>(writer.WrittenMemory));
            Ensure(read == message, "all exact identities and digest survive the wire");
        }
    }

    [Test]
    public void MaintenanceRejectsMalformedOrUnboundedControl()
    {
        var writer = new PooledByteBufferWriter();
        Message(CooperativeMaintenanceStage.Freeze).Write(writer);
        var bytes = writer.WrittenMemory.ToArray();
        Reject(bytes[..^1]);
        Reject([.. bytes, 0]);
        foreach (var offset in new[] { 0, 1, 2, 3 })
        {
            var invalid = (byte[])bytes.Clone();
            invalid[offset] = 255;
            Reject(invalid);
        }
        var invalidCount = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(invalidCount.AsSpan(4), int.MaxValue);
        Reject(invalidCount);
        var missingSession = (byte[])bytes.Clone();
        missingSession.AsSpan(80, 16).Clear();
        Reject(missingSession);
    }

    [Test]
    public void MaintenanceBindingRejectsCrossIncarnationDigestAndOperation()
    {
        var expected = Message(CooperativeMaintenanceStage.Freeze);
        Ensure(expected.MatchesOperation(expected with { Stage = CooperativeMaintenanceStage.IngressFence }), "stage echo");
        Ensure(!expected.MatchesOperation(expected with { Domain = Guid.NewGuid() }), "domain isolation");
        Ensure(!expected.MatchesOperation(expected with { Server = Guid.NewGuid() }), "server incarnation isolation");
        Ensure(!expected.MatchesOperation(expected with { Participant = Guid.NewGuid() }), "participant isolation");
        Ensure(!expected.MatchesOperation(expected with { Runtime = Guid.NewGuid() }), "runtime incarnation isolation");
        Ensure(!expected.MatchesOperation(expected with { Session = Guid.NewGuid() }), "physical session isolation");
        Ensure(!expected.MatchesOperation(expected with { Operation = Guid.NewGuid() }), "operation isolation");
        Ensure(!expected.MatchesOperation(expected with { Digest = new(4, 3, 2, 1) }), "digest isolation");
        Ensure(!expected.MatchesOperation(expected with { Generation = 2 }), "sequence isolation");
    }

    [Test]
    public void MaintenancePauseRejectsRequestsButKeepsDrainProgress()
    {
        Ensure(!RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.MaintenancePaused,
            ProtocolV2FrameType.Request), "ingress cut rejects Request");
        foreach (var type in new[] { ProtocolV2FrameType.Response, ProtocolV2FrameType.Cancel,
                     ProtocolV2FrameType.StreamData, ProtocolV2FrameType.StreamComplete,
                     ProtocolV2FrameType.WindowUpdate, ProtocolV2FrameType.CooperativeMaintenance,
                     ProtocolV2FrameType.ContractManifest, ProtocolV2FrameType.Ping, ProtocolV2FrameType.Pong })
            Ensure(RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.MaintenancePaused, type), "drain progress remains legal");
        Ensure(!RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Handshaking,
            ProtocolV2FrameType.CooperativeMaintenance), "no pre-authentication control");
    }

    [Test]
    public void MaintenanceFrameRejectsRpcIdsAndFlags()
    {
        foreach (var (id, flags) in new[] { (1UL, ProtocolV2FrameFlags.None), (0UL, ProtocolV2FrameFlags.Compressed) })
        {
            var writer = new PooledByteBufferWriter();
            var token = ProtocolV2FrameWriter.BeginFrame(writer, ProtocolV2FrameType.CooperativeMaintenance, flags, id);
            Message(CooperativeMaintenanceStage.Freeze).Write(writer);
            ProtocolV2FrameWriter.EndFrame(writer, token);
            var rejected = false;
            try
            {
                var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
                ProtocolV2FrameParser.TryReadFrame(ref sequence, new SharpLinkProtocolOptions(), out _, out _);
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
            {
                rejected = true;
            }
            Ensure(rejected, "control must be unflagged request-id-zero");
        }
    }

    private static CooperativeMaintenanceMessage Message(CooperativeMaintenanceStage stage)
    {
        var enrollment = stage <= CooperativeMaintenanceStage.Enrolled;
        var welcome = stage == CooperativeMaintenanceStage.Welcome;
        return new(stage, Guid.NewGuid(), Guid.NewGuid(), welcome ? default : Guid.NewGuid(),
            welcome ? default : Guid.NewGuid(), welcome ? default : Guid.NewGuid(),
            enrollment ? default : Guid.NewGuid(), enrollment ? default : new(1, 2, 3, 4),
            enrollment ? 0 : 2, enrollment ? 0 : 1);
    }

    private static void Reject(byte[] bytes)
    {
        try { CooperativeMaintenanceMessage.Read(new ReadOnlySequence<byte>(bytes)); }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation) { return; }
        throw new InvalidOperationException("Malformed maintenance payload was accepted.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
