using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Client;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class Protocol3IdentityExhaustionPublicationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalSealPreservesPreCutPublicationButRejectsNewReservations(bool bindingDomain)
    {
        await using var client = ClientBuilderTestHelper.Build(new NonConnectingFactory());
        var context = (SharpLinkRuntimeContext)client.RuntimeContext;
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "identity-exhaustion-publication", input.Reader, output.Writer,
            RpcSessionTestFixture.ClientOptions(context), completeHandshake: false);
        var authority = new Protocol3BindingAuthority(Guid.NewGuid(), Guid.NewGuid(), Protocol3BindingLimits.Default);
        Ensure(session.TryCompleteHandshake(new NegotiatedSessionOptions(0,
            ProtocolV2Negotiator.MandatoryCapabilities, context.Protocol.MaxFramePayloadBytes,
            context.FlowControl.StreamReceiveWindowBytes, context.FlowControl.ConnectionReceiveWindowBytes,
            authority: authority)));
        session.NotifyConnected();
        await using var connection = new ClientConnection(client, session, new CancellationTokenSource(), 8, context);

        // Freeze both calls after owner admission but before Request enqueue. Allocating the
        // final ID invokes the real owner callback synchronously in precisely this interval.
        Ensure(connection.TryReserveExactCallAdmission());
        Ensure(connection.TryReserveExactCallAdmission());
        if (!bindingDomain)
            typeof(PendingRequestTable).GetField("_nextId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(connection.PendingCalls, long.MaxValue - 2);
        var earlier = connection.PendingCalls.AllocateRequestId();
        var last = connection.PendingCalls.AllocateRequestId();
        if (bindingDomain)
            connection.OnProtocolIdentityExhausted(); // The exact shared Bind-exhaustion callback.
        else
            Ensure(earlier == long.MaxValue - 1 && last == long.MaxValue);

        Ensure(connection.State == ClientConnectionState.Draining && connection.OccupiesConnectionCapacity);
        Ensure(!connection.CanAcceptCalls && !connection.TryReserveExactCallAdmission());
        Ensure(connection.CallAdmissionReservationCount == 2 && session.ProtocolPhase == RpcSessionProtocolPhase.Ready);
        connection.AssertStateInvariant();
        Ensure(connection.TryBeginUntrackedCall());
        Ensure(connection.TryBeginUntrackedCall());
        Ensure(connection.CallAdmissionReservationCount == 0 && connection.ActiveCallCount == 2);

        // Exercise the production publication guard at the frozen pre-enqueue boundary, not
        // a separate permissive test predicate. No transport or producer semantics are claimed.
        var guard = typeof(RpcSession).GetMethod("EnsureOutboundFrameAllowed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var type in new[] { ProtocolV2FrameType.Request, ProtocolV2FrameType.OneWayStreamRequest, ProtocolV2FrameType.BindContract })
            guard.Invoke(session, new object[] { session.ProtocolPhase, type });

        // Control: a genuine wire-phase drain still rejects business publication unchanged.
        session.MarkDraining();
        try
        {
            guard.Invoke(session, new object[] { session.ProtocolPhase, ProtocolV2FrameType.Request });
            throw new InvalidOperationException("The physical draining guard must reject Request.");
        }
        catch (TargetInvocationException failure) when (failure.InnerException is SharpLinkException { Code: SharpLinkErrorCode.Unavailable }) { }
        connection.EndUntrackedCall();
        connection.EndUntrackedCall();
        await input.Writer.CompleteAsync();
        await output.Reader.CompleteAsync();
    }

    private static void Ensure(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Protocol3 local exhaustion publication assertion failed.");
    }
}
