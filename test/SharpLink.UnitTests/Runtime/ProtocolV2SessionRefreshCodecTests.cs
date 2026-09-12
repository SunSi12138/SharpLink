namespace SharpLink.UnitTests.Runtime;

public sealed class ProtocolV2SessionRefreshCodecTests
{
    [Test]
    public void SessionRefreshPayloadShouldRoundTripFixedIdentityAndGeneration()
    {
        var serverInstanceId = Guid.NewGuid();
        var expected = new ProtocolV2SessionRefreshRequested(serverInstanceId, 42);
        var writer = new ArrayBufferWriter<byte>();

        ProtocolV2PayloadCodec.WriteSessionRefreshRequested(writer, expected);
        var actual = ProtocolV2PayloadCodec.ReadSessionRefreshRequested(
            new ReadOnlySequence<byte>(writer.WrittenMemory));

        Ensure(writer.WrittenCount == 24, "session refresh payload should remain fixed-width");
        Ensure(actual == expected, "session refresh payload should round-trip");
    }

    [Test]
    public void SessionRefreshFrameShouldUseBoundedConnectionControlParsing()
    {
        var expected = new ProtocolV2SessionRefreshRequested(Guid.NewGuid(), 42);
        var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.SessionRefreshRequested,
            ProtocolV2FrameFlags.None,
            0);
        ProtocolV2PayloadCodec.WriteSessionRefreshRequested(writer, expected);
        ProtocolV2FrameWriter.EndFrame(writer, token);
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);

        Ensure(ProtocolV2FrameParser.TryReadFrame(
                ref buffer,
                new SharpLinkProtocolOptions(),
                out var header,
                out var payload),
            "a complete session-refresh control frame should parse");
        Ensure(header.Type == ProtocolV2FrameType.SessionRefreshRequested,
            "frame parser should preserve the session-refresh type");
        Ensure(header.RequestId == 0 && header.Flags == ProtocolV2FrameFlags.None,
            "session refresh should remain an unflagged connection-level control frame");
        Ensure(ProtocolV2PayloadCodec.ReadSessionRefreshRequested(payload) == expected,
            "frame parser should preserve the bounded refresh payload");
        Ensure(buffer.IsEmpty, "frame parser should consume the complete refresh frame");
    }

    [Test]
    public async Task SessionRefreshFrameShouldRejectRpcRequestIds()
    {
        var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.SessionRefreshRequested,
            ProtocolV2FrameFlags.None,
            7);
        ProtocolV2PayloadCodec.WriteSessionRefreshRequested(
            writer,
            new ProtocolV2SessionRefreshRequested(Guid.NewGuid(), 1));
        ProtocolV2FrameWriter.EndFrame(writer, token);
        var frame = writer.WrittenMemory.ToArray();

        await EnsureThrows<SharpLinkException>(() =>
        {
            var buffer = new ReadOnlySequence<byte>(frame);
            _ = ProtocolV2FrameParser.TryReadFrame(
                ref buffer,
                new SharpLinkProtocolOptions(),
                out _,
                out _);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task SessionRefreshPayloadShouldRejectInvalidIdentityAndGeneration()
    {
        await EnsureThrows<ArgumentException>(() =>
        {
            var writer = new ArrayBufferWriter<byte>();
            ProtocolV2PayloadCodec.WriteSessionRefreshRequested(
                writer,
                new ProtocolV2SessionRefreshRequested(Guid.Empty, 1));
            return Task.CompletedTask;
        });

        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            var writer = new ArrayBufferWriter<byte>();
            ProtocolV2PayloadCodec.WriteSessionRefreshRequested(
                writer,
                new ProtocolV2SessionRefreshRequested(Guid.NewGuid(), 0));
            return Task.CompletedTask;
        });
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task EnsureThrows<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
