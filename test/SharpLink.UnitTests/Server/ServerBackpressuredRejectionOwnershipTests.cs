using System.Buffers.Binary;
using System.Reflection;
using SharpLink.Server;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class ServerBackpressuredRejectionOwnershipTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task EarlyRejectionOutlivesAllExistingPermitsButNotItsIntrinsicSend(
        bool admissionEnabled,
        bool deadlineExpired)
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        if (admissionEnabled)
            ((ISharpLinkServer)server).EnableAdmissionControl(options => options.Global.UseConcurrency(1));
        typeof(SharpLinkServer).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, 2);
        await using var fixture = new BackpressuredSendOwnershipTests.Fixture();
        var connection = new ServerConnectionState(
            fixture.Session,
            new RpcSessionGeneratedServerBridge(fixture.Session),
            new StripedLongMap<ServerCallCancellationState>(),
            CancellationToken.None,
            TimeProvider.System);
        Ensure(connection.MarkReady(null), "connection must be ready for the real request-dispatch path");
        try
        {
            fixture.Session.SendPacket(fixture.Frame(1, 1000));
            await fixture.Output.FirstFlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var payload = new byte[deadlineExpired ? 24 : 16];
            BinaryPrimitives.WriteInt64LittleEndian(payload, 0x1234);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), 1);
            var dispatch = (Task)typeof(SharpLinkServer).GetMethod(
                "DispatchRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server,
                [
                    connection,
                    2L,
                    deadlineExpired ? ProtocolV2FrameFlags.HasTimeBudget : ProtocolV2FrameFlags.None,
                    new ReadOnlySequence<byte>(payload),
                    connection.CallCancellations,
                    CancellationToken.None
                ])!;
            Ensure(!dispatch.IsCompleted, "the fire-and-forget reader dispatch must still await send capacity");
            Ensure(server.ActiveCallCountForDiagnostics == 0 && server.PendingCallAdmissionsForDiagnostics == 0 &&
                connection.ActiveCalls == 0 && (server.CurrentAdmissionProgramForTests?.ActiveUses ?? 0) == 0,
                "the old rejection has no request, pending-transfer, connection or admission-program owner");
            var drain = fixture.Session.CaptureBackpressuredSendDrain();
            Ensure(!drain.IsCompleted,
                "intrinsic send ownership is visible despite every original permit census being empty");
            fixture.Output.Release();
            await Task.WhenAll(dispatch, drain).WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Session.SendPacketAndFlushAsync(fixture.Frame(3, 32));
            var ids = await fixture.ReadRequestIdsAsync();
            Ensure(ids.IndexOf(2) < ids.IndexOf(3), "actual server rejection must precede the later normal fence");
        }
        finally
        {
            fixture.Output.Release();
            await connection.CloseAsync();
        }
    }

    private static void Ensure(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
