using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientRetrySharedSupport
{
    internal static void ConfigureRetry(SharpClientBuilder builder, SharpLinkRetryOptions options)
    {
        builder.UseRetry(configured =>
        {
            configured.MaxAttempts = options.MaxAttempts;
            configured.InitialBackoff = options.InitialBackoff;
            configured.MaxBackoff = options.MaxBackoff;
            configured.JitterRatio = options.JitterRatio;
        });
    }

    internal static SharpLinkRetryOptions RetryOptions(int maxAttempts, TimeSpan initialBackoff)
        => new()
        {
            MaxAttempts = maxAttempts,
            InitialBackoff = initialBackoff,
            MaxBackoff = initialBackoff,
            JitterRatio = 0
        };

    internal static SharpLinkEndpoint Endpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    internal static Task InjectErrorAsync(
        TestClientTransportFactory transport,
        ProtocolV2FrameHeader request,
        SharpLinkErrorCode code)
    {
        var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteError(payload, code, code.ToString(), 1024, out _);
        return transport.Connection.InjectFrameAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Error,
            request.RequestId,
            payload.WrittenMemory);
    }

    internal static async Task<TException> EnsureThrows<TException>(Task invocation)
        where TException : Exception
    {
        try
        {
            await invocation;
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException exception)
        {
            return exception;
        }
    }

    internal static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
