using System.IO.Pipelines;
using SharpLink.Client;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

// Real connection/session admission objects; not a replacement protocol MODEL or transport proof.
public sealed class Protocol3BindingDrainOutcomeTests
{
    [Test]
    public async Task ValidatedSessionDrainingResultWithdrawsOwnerWithoutAbortingPreCutPublication()
    {
        await using var client = ClientBuilderTestHelper.Build(new NonConnectingFactory());
        var context = (SharpLinkRuntimeContext)client.RuntimeContext;
        var input = new Pipe();
        var output = new Pipe();
        var session = CreateSession(context, input, output);
        await using var connection = new ClientConnection(client, session, new CancellationTokenSource(), 8, context);
        Ensure(connection.TryReserveExactCallAdmission(), "capture a call reservation before the cold result");

        var failure = SharpLinkClient.ApplyBindingRejection(connection, Protocol3BindStatus.SessionDraining);
        Ensure(failure.Code == SharpLinkErrorCode.Unavailable &&
            failure.DetailCode == SharpLinkErrorDetails.Unavailable.SessionBindingDraining, "exact status12 semantic error");
        Ensure(connection.RequiresCapacitySafeDrain && !connection.HasExhaustedProtocolIdentity,
            "status12 does not invent an identity-exhaustion diagnosis");
        Ensure(connection.State == ClientConnectionState.Draining && connection.OccupiesConnectionCapacity,
            "old physical owner remains charged to the ordinary pool while admitted work exists");
        Ensure(!connection.CanAcceptCalls && !connection.TryReserveExactCallAdmission(), "no new owner admission");
        Ensure(session.IsConnected && session.ProtocolPhase == RpcSessionProtocolPhase.Ready,
            "a local withdrawal is not a wire-phase abort of already admitted frames");
        connection.AssertStateInvariant();
        Ensure(connection.TryBeginUntrackedCall() && connection.ActiveCallCount == 1,
            "the pre-cut reservation still transfers to its existing physical owner");
        Ensure(connection.PendingCalls.AllocateRequestId() == 1 && !connection.PendingCalls.RequestIdsExhausted,
            "pre-cut publication does not consume or seal an unrelated identity domain");

        try
        {
            await connection.Binding.BindAsync(new Protocol3BindRequest(Protocol3BindingRequirement.Compatible,
                1, new RpcHash128(1, 2), null));
            throw new InvalidOperationException("A locally withdrawn Session accepted a new Bind.");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable &&
                exception.DetailCode == SharpLinkErrorDetails.Unavailable.SessionBindingDraining,
                "new Bind is rejected before issuing an ID or frame");
        }
        Ensure(connection.Binding.ActiveOperationCount == 0 && client.BindingBudget.Operations == 0,
            "rejected new Bind leaves no protocol owner");
        SharpLinkClient.ApplyBindingRejection(connection, Protocol3BindStatus.SessionDraining);
        Ensure(connection.ActiveCallCount == 1, "duplicate status12 cannot release an admitted owner");
        connection.EndUntrackedCall();
        await input.Writer.CompleteAsync();
        await output.Reader.CompleteAsync();
    }

    [Test]
    public async Task DeterministicContractMismatchDoesNotWithdrawTheWholeSession()
    {
        await using var client = ClientBuilderTestHelper.Build(new NonConnectingFactory());
        var context = (SharpLinkRuntimeContext)client.RuntimeContext;
        var input = new Pipe();
        var output = new Pipe();
        var session = CreateSession(context, input, output);
        await using var connection = new ClientConnection(client, session, new CancellationTokenSource(), 8, context);
        var failure = SharpLinkClient.ApplyBindingRejection(connection, Protocol3BindStatus.HashMismatch);
        Ensure(failure.Code == SharpLinkErrorCode.FailedPrecondition &&
            failure.DetailCode == SharpLinkErrorDetails.FailedPrecondition.BindHashMismatch, "exact contract failure");
        Ensure(!connection.RequiresCapacitySafeDrain && connection.CanAcceptCalls &&
            session.ProtocolPhase == RpcSessionProtocolPhase.Ready, "one contract rejection leaves unrelated routes available");
        Ensure(connection.TryReserveExactCallAdmission(), "unrelated calls remain admissible");
        connection.ReleaseCallAdmissionReservation();
        await input.Writer.CompleteAsync();
        await output.Reader.CompleteAsync();
    }

    private static RpcSession CreateSession(SharpLinkRuntimeContext context, Pipe input, Pipe output)
    {
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport("binding-drain-outcome", input.Reader,
            output.Writer, RpcSessionTestFixture.ClientOptions(context), completeHandshake: false);
        Ensure(session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
            ProtocolV2Negotiator.MandatoryCapabilities, context.Protocol.MaxFramePayloadBytes,
            context.FlowControl.StreamReceiveWindowBytes, context.FlowControl.ConnectionReceiveWindowBytes,
            authority: new Protocol3BindingAuthority(Guid.NewGuid(), Guid.NewGuid(), Protocol3BindingLimits.Default))),
            "exact current handshake authority");
        session.NotifyConnected();
        return session;
    }

    private static void Ensure(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
