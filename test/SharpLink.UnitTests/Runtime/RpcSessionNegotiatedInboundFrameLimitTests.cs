using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcSessionNegotiatedInboundFrameLimitTests
{
    [Test]
    public Task ClientPostHandshakeParserShouldRejectRawFrameAboveNegotiatedMaximum()
        => AssertRejectsAboveNegotiatedMaximumAsync(serverRole: false);

    [Test]
    public Task ServerPostHandshakeParserShouldRejectRawFrameAboveNegotiatedMaximum()
        => AssertRejectsAboveNegotiatedMaximumAsync(serverRole: true);

    [Test]
    public async Task ServerPostHandshakeParserShouldAcceptRawFrameAtNegotiatedMaximum()
    {
        using var context = CreateContext();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "negotiated-frame-accept",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, maxFramePayloadBytes: 2048);

        var buffer = CreateRawFrame(ProtocolV2FrameType.Request, payloadLength: 2048);
        var parsed = session.TryReadInboundFrame(
            ref buffer,
            context.Protocol,
            out var header,
            out var payload);

        Ensure(parsed, "a complete frame at the negotiated maximum must parse");
        Ensure(header.Type == ProtocolV2FrameType.Request, "frame type");
        Ensure(header.RequestId == 1, "request id");
        Ensure(payload.Length == 2048, "payload length");
        Ensure(buffer.IsEmpty, "the parsed frame must be consumed exactly once");

        await input.Writer.CompleteAsync();
        await output.Reader.CompleteAsync();
    }

    private static async Task AssertRejectsAboveNegotiatedMaximumAsync(bool serverRole)
    {
        using var context = CreateContext();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            serverRole ? "server-negotiated-frame-reject" : "client-negotiated-frame-reject",
            input.Reader,
            output.Writer,
            serverRole
                ? RpcSessionTestFixture.ServerOptions(context)
                : RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, maxFramePayloadBytes: 2048);

        var frameType = serverRole ? ProtocolV2FrameType.Request : ProtocolV2FrameType.Response;
        var buffer = CreateRawFrame(frameType, payloadLength: 3072);
        SharpLinkException? failure = null;
        try
        {
            _ = session.TryReadInboundFrame(
                ref buffer,
                context.Protocol,
                out _,
                out _);
        }
        catch (SharpLinkException exception)
        {
            failure = exception;
        }

        Ensure(failure is { Code: SharpLinkErrorCode.ProtocolViolation },
            "a raw frame above the negotiated maximum must be a protocol violation");
        Ensure(failure is not null &&
               failure.Message.Contains("negotiated maximum", StringComparison.Ordinal),
            "the violation should identify the negotiated frame boundary");

        await input.Writer.CompleteAsync();
        await output.Reader.CompleteAsync();
    }

    private static SharpLinkRuntimeContext CreateContext()
        => new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Protocol.MaxFramePayloadBytes = 4096)
            .Build(includeGeneratedAssemblyCatalog: false);

    private static ReadOnlySequence<byte> CreateRawFrame(
        ProtocolV2FrameType type,
        int payloadLength)
    {
        var frame = new byte[ProtocolV2Constants.HeaderBytes + payloadLength];
        frame[0] = ProtocolV2Constants.Magic;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1, sizeof(int)), payloadLength);
        frame[5] = (byte)type;
        frame[6] = (byte)ProtocolV2FrameFlags.None;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(7, sizeof(ulong)), 1);
        return new ReadOnlySequence<byte>(frame);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
