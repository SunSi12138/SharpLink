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
