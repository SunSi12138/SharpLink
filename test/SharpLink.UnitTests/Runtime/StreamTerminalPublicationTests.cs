using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

public sealed class StreamTerminalPublicationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task EarlyCleanupFenceDoesNotSealAnotherDeclaredStream()
    {
        await using var fixture = new PreparedPublicationFixture();
        var session = fixture.Session;
        var group = new TestRpcStreamPublication(new TestRpcBindingPublication(session), 21, streamCount: 2);
        group.SendHead();
        session.SendClientStreamCleanupError(group, 1, CleanupError());
        Ensure(group.IsTerminalQueued(1) && !group.IsTerminalQueued(2), "queue terminal bits are per declared stream");
        await session.CompleteStreamCleanupPublicationAsync(group).AsTask().WaitAsync(Timeout);
        // The early fence did not BeginFence: another producer still owns its real tail.
        SendData(session, group, 2);
        session.SendClientStreamCleanupError(group, 2, CleanupError());
        // A duplicate terminal cannot resurrect the route or produce another peer terminal.
        session.SendClientStreamCleanupError(group, 1, CleanupError());
        await session.CompleteStreamPublicationAsync(group).AsTask().WaitAsync(Timeout);
        Ensure(session.IsConnected && session.QueuedSendBytes == 0, "healthy cleanup keeps exact Session and drains bytes");
        var read = await fixture.Output.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        var bytes = read.Buffer.ToArray();
        fixture.Output.Reader.AdvanceTo(read.Buffer.End);
        var data = 0;
        var terminals = 0;
        for (var offset = 0; offset < bytes.Length;)
        {
            var type = (ProtocolV2FrameType)bytes[offset + 5];
            if (type == ProtocolV2FrameType.StreamData) data++;
            if (type == ProtocolV2FrameType.StreamComplete) terminals++;
            offset += ProtocolV2Constants.HeaderBytes + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1));
        }
        Ensure(data == 1 && terminals == 2, "exact two terminal frames and one preterminal stream2 data frame on actual output");
    }

    [Test]
    public async Task LateDataAfterTerminalCutoffClosesExactSessionInsteadOfStrandingCredit()
    {
        await using var fixture = new PreparedPublicationFixture();
        var session = fixture.Session;
        var group = new TestRpcStreamPublication(session, 22);
        group.SendHead();
        session.SendClientStreamCleanupError(group, 1, CleanupError());
        await session.CompleteStreamCleanupPublicationAsync(group).AsTask().WaitAsync(Timeout);
        SendData(session, group, 1); // Real serialized frame, rejected by the sole consumer cutoff.
        Exception? failure = null;
        try { await session.CompleteStreamPublicationAsync(group).AsTask().WaitAsync(Timeout); }
        catch (Exception error) when (error is not TimeoutException) { failure = error; }
        Ensure(failure is not null && !session.IsConnected && session.QueuedSendBytes == 0,
            "late data cannot appear successful: exact Session closes and physical owners drain");
    }

    private static SharpLinkException CleanupError()
        => new(SharpLinkErrorCode.Cancelled, "bounded test cleanup");
    private static void SendData(RpcSession session, RpcStreamPublication group, ushort streamId)
    {
        var writer = session.RentFrameWriter();
        using (writer.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)group.RequestId))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(3), streamId);
            writer.GetSpan(3)[2] = 42;
            writer.Advance(3);
        }
        session.SendPacket(writer, publication: group, publicationRole: RpcFramePublicationRole.StreamData);
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
