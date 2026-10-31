using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class ContractBindingTcpVerticalSliceTests
{
    [Test]
    public async Task BoundShapeMismatchReceivedOverTcpClosesTheExactSessionWithoutBusiness()
    {
        await using var harness = new Harness();
        var bound = await PrepareCapturedBindingAsync(harness);
        var clientConnection = CurrentConnection(harness.Client);
        var closed = NewGate();
        bound.Connection.Session.OnDisconnected += _ => closed.TrySetResult();
        var method = BoundContract().Methods.Single(item => item.Kind == RpcMethodKind.Unary);
        var requestId = clientConnection.PendingCalls.AllocateRequestId();
        var payload = BoundPayload(bound.Token, method.MethodId);

        // Structurally legal type 4 / OneWay, semantically invalid against the exact bound
        // generated unary facts. This traverses the real TCP reader and dispatcher.
        var session = clientConnection.Session;
        var writer = session.RentFrameWriter();
        using (writer.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.OneWay, (ulong)requestId))
            writer.Write(payload);
        var prepared = session.PrepareOutboundFrame(writer, CancellationToken.None);
        if (!session.TryEnqueuePreparedFrame(prepared, false, CancellationToken.None, null,
                out var emission, out var rejection))
            throw rejection!;
        await emission;
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bound.Connection.CloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        // Close completion can precede the dispatch finally by one continuation. Require its
        // physical binding lease to finish too; do not overwrite counters during cleanup.
        await WaitForNoRequestOwnerAsync(harness.Server);
        AssertRejectedResources(harness, bound.Connection, bound.Registration, closed: true);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ExactBoundDispatcherClosesOnEnvelopeFlagsOrExplicitCountViolation(int malformedKind)
    {
        await using var harness = new Harness();
        var bound = await PrepareCapturedBindingAsync(harness);
        var method = BoundContract().Methods.Single(item => item.Kind ==
            (malformedKind == 2 ? RpcMethodKind.OneWay : RpcMethodKind.Unary));
        var type = malformedKind == 2 ? ProtocolV2FrameType.OneWayStreamRequest : ProtocolV2FrameType.Request;
        var payload = BoundPayload(bound.Token, method.MethodId, malformedKind == 2 ? (ushort)1 : null);
        if (malformedKind == 0) Array.Resize(ref payload, 15);
        var flags = malformedKind == 0 ? ProtocolV2FrameFlags.None : ProtocolV2FrameFlags.OneWay;

        // Exact production component boundary on the negotiated TCP Session. Type 23 is
        // deliberately a count-validation control, NOT a claim of implemented streaming dispatch.
        await harness.Server.DispatchRequestAsync(bound.Connection, 1, flags, new(payload),
            bound.Connection.CallCancellations, bound.Connection.ConnectionToken, type)
            .WaitAsync(TimeSpan.FromSeconds(5));
        AssertRejectedResources(harness, bound.Connection, bound.Registration, closed: true);
    }

    [Test]
    public async Task UndeclaredBoundMethodUsesFrozenDomainAndReturnsExactWireError()
    {
        await using var harness = new Harness();
        var bound = await PrepareCapturedBindingAsync(harness);
        var declared = BoundContract().Methods.Select(method => method.MethodId).ToHashSet();
        long missing = 1;
        while (declared.Contains(missing)) missing++;
        Ensure(ReferenceEquals(bound.Registration.Facts!.Resolve(missing), RpcMethodFacts.UnknownMethod),
            "undeclared IDs resolve to the canonical host sentinel without invoking a stub");
        var clientConnection = CurrentConnection(harness.Client);
        var pending = clientConnection.PendingCalls.Rent<int>(out var requestId);
        await harness.Server.DispatchRequestAsync(bound.Connection, requestId, ProtocolV2FrameFlags.HasReturn,
            new(BoundPayload(bound.Token, missing)), bound.Connection.CallCancellations,
            bound.Connection.ConnectionToken, ProtocolV2FrameType.Request).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            _ = await pending.AsValueTask().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Undeclared bound method unexpectedly succeeded.");
        }
        catch (SharpLinkException failure)
        {
            Ensure(failure.Code == SharpLinkErrorCode.NotFound &&
                failure.DetailCode == SharpLinkErrorDetails.NotFound.UnknownBoundMethod,
                "the actual TCP response preserves NotFound/UnknownBoundMethod");
        }
        AssertRejectedResources(harness, bound.Connection, bound.Registration, closed: false);
        Ensure(clientConnection.PendingCalls.ActiveCount == 0, "wire rejection completed the exact pending call");
    }

    private static async Task<(ServerConnectionState Connection, ulong Token, ServerBindingRegistration Registration)>
        PrepareCapturedBindingAsync(Harness harness)
    {
        var captured = new TaskCompletionSource<(ServerConnectionState, Protocol3BindResult)>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.BeforeBindingResultForTesting = (connection, result) =>
        {
            captured.TrySetResult((connection, result));
            return Task.CompletedTask;
        };
        var proxy = harness.Client.CreateProxy<IBindingVerticalService>();
        await harness.ConnectAsync();
        await proxy.PrepareAsync(TimeSpan.FromSeconds(5));
        var (connection, result) = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(result.Status == Protocol3BindStatus.Success, "generated TCP preparation actually bound");
        // Prepare observes the peer result, not the server's final control-owner release.
        // Join that exact owner before direct component controls install another Bind ID.
        var operations = typeof(ServerConnectionState).GetField("_bindingOperations",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while ((int)operations.GetValue(connection)! != 0)
                await Task.Delay(1, timeout.Token);
        var entry = connection.Bindings.Capture(result.Token)
            ?? throw new InvalidOperationException("Prepared token is absent from the exact Session.");
        return (connection, result.Token, entry.Registration);
    }

    private static ClientConnection CurrentConnection(SharpLinkClient client)
        => ((ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!).Single();

    private static SharpLinkGeneratedContractDescriptor BoundContract()
        => GlobalCatalogManifestSource.Instance.CreateSnapshot().SelectMany(item => item.Contracts)
            .Single(item => item.ContractType == typeof(IBindingVerticalService));

    private static byte[] BoundPayload(ulong token, long method, ushort? streamCount = null)
    {
        var payload = new byte[streamCount.HasValue ? 18 : 16];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, token);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), method);
        if (streamCount.HasValue) BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), streamCount.Value);
        return payload;
    }

    private static void AssertRejectedResources(Harness harness, ServerConnectionState connection,
        ServerBindingRegistration registration, bool closed)
    {
        Ensure(harness.Service.Calls == 0, "malformed/invalid facts cannot invoke generated business code");
        Ensure(connection.ActiveCalls == 0 && connection.CallCancellations.Count == 0,
            "existing request permits and cancellation owners are released; no second binding-group owner exists");
        Ensure(harness.Server.ActiveCallCountForDiagnostics == 0 && harness.Server.PendingCallAdmissionsForDiagnostics == 0,
            "no global call or admission reservation remains");
        Ensure(harness.Server.ActiveDecodeCountForDiagnostics == 0 && harness.Server.RetainedCompressedBytesForDiagnostics == 0 &&
            harness.Server.DecodedBytesInFlightForDiagnostics == 0 && harness.Server.PreAdmissionStreamBytesForDiagnostics == 0,
            "rejection retains no decode, payload or pre-admission stream resources");
        if (closed)
            Ensure(connection.LifecycleState == ServerConnectionLifecycleState.Closed && !connection.Session.IsConnected &&
                connection.Bindings.IsClosed, "protocol violation terminally closes the exact Session and its binding table");
        else
            Ensure(connection.LifecycleState == ServerConnectionLifecycleState.Ready && connection.Session.CanAcceptCalls,
                "invalid local stub facts are an RPC Internal error, not a peer protocol violation");
    }

    private static async Task WaitForNoRequestOwnerAsync(SharpLinkServer server)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.ActiveCallCountForDiagnostics != 0)
            await Task.Delay(1, timeout.Token);
    }
}
