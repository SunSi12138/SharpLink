from pathlib import Path

Path('test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs').write_text(r'''using System.Buffers.Binary;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientTimeBudgetTests
{
    [Test]
    public async Task ExplicitMethodTimeoutShouldOverrideClientDefaultTimeBudget()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(timeProvider);
                builder.UseRequestTimeout(TimeSpan.FromSeconds(30));
            });
        await client.ConnectAsync();

        var method = MethodWithTimeout(TimeSpan.FromSeconds(120));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);

        var invocation = channel.InvokeUnaryAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);

        Ensure((sent.Header.Flags & ProtocolV2FrameFlags.HasTimeBudget) != 0,
            "explicit method timeout should emit a TimeBudget");
        Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(120),
            "method timeout must override, not be min-capped by, the 30 second client fallback");

        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
        Ensure(await invocation == 0, "method-timeout override response");
    }

    [Test]
    public async Task InheritedTimeBudgetShouldCapSelectedMethodPolicyWithoutRestartingIt()
    {
        var parentTimeProvider = new ManualTimeProvider();
        var childTimeProvider = new ManualTimeProvider();
        var parentDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(6), parentTimeProvider);
        parentTimeProvider.Advance(TimeSpan.FromSeconds(2));
        using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
            "parent",
            null,
            parentDeadline,
            parentTimeProvider));

        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(childTimeProvider);
                builder.UseRequestTimeout(TimeSpan.FromSeconds(30));
            });
        await client.ConnectAsync();

        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var invocation = channel.InvokeUnaryAsync(
            MethodWithTimeout(TimeSpan.FromSeconds(120)),
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);

        Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(4),
            "a downstream call must propagate the parent's remaining TimeBudget instead of restarting 120 seconds");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
        Ensure(await invocation == 0, "inherited-budget response");
    }

    private static RpcMethodDescriptor MethodWithTimeout(TimeSpan timeout)
        => new(
            ContractId: 1,
            MethodId: 287,
            Kind: RpcMethodKind.Unary,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: timeout);

    private static TimeSpan ReadTimeBudget(TestSentFrame sent)
        => TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(
            sent.Payload.AsSpan(ProtocolV2Constants.RequestPrefixBytes, sizeof(long))));

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
''')

Path('test/SharpLink.UnitTests/Protocol/ProtocolV2TimeBudgetCompatibilityTests.cs').write_text(r'''namespace SharpLink.UnitTests.Protocol;

public class ProtocolV2TimeBudgetCompatibilityTests
{
    [Test]
    public void PreTimeBudgetProtocolMinorShouldBeRejectedAtNegotiation()
    {
        var serverPolicy = ProtocolV2Negotiator.CreateImplementedPolicy(
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>());
        var legacyOffer = new ProtocolV2HandshakeRequest(
            MinorVersion: 3,
            SupportedCapabilities: ProtocolV2Capabilities.None,
            RequiredCapabilities: ProtocolV2Capabilities.None,
            MaxFramePayloadBytes: 4 * 1024 * 1024,
            StreamReceiveWindowBytes: 1024 * 1024,
            ConnectionReceiveWindowBytes: 16 * 1024 * 1024,
            AuthenticationPayload: ReadOnlyMemory<byte>.Empty);

        var failure = CaptureException(() => ProtocolV2Negotiator.NegotiateServer(legacyOffer, serverPolicy));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Unimplemented },
            "minor 3 must be rejected instead of negotiating an absolute-deadline wire shape");
    }

    [Test]
    public void ClientShouldRejectServerResponseBelowTimeBudgetBoundary()
    {
        var clientPolicy = ProtocolV2Negotiator.CreateImplementedPolicy(
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>());
        var offer = ProtocolV2Negotiator.CreateClientOffer(
            clientPolicy,
            ProtocolV2Capabilities.None,
            ReadOnlyMemory<byte>.Empty);
        var legacyResponse = new ProtocolV2HandshakeResponse(
            MinorVersion: 3,
            NegotiatedCapabilities: ProtocolV2Capabilities.None,
            MaxFramePayloadBytes: 4 * 1024 * 1024,
            StreamReceiveWindowBytes: 1024 * 1024,
            ConnectionReceiveWindowBytes: 16 * 1024 * 1024);

        var failure = CaptureException(() =>
            ProtocolV2Negotiator.ValidateServerResponse(offer, legacyResponse, clientPolicy));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Unimplemented },
            "client must reject a server that selects the legacy absolute-deadline minor");
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
''')
