using System.Reflection;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class SharpClientBuilderTests
{
    [Test]
    public async Task BuildShouldUseThirtySecondUnaryTimeoutByDefault()
    {
        var client = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .Build();

        Ensure(ReadRequestTimeout(client) == TimeSpan.FromSeconds(30), "default unary timeout");
        await client.DisposeAsync();
    }

    [Test]
    public async Task UseRequestTimeoutShouldRejectNonPositiveValues()
    {
        var builder = SharpClientBuilder.Create();
        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            builder.UseRequestTimeout(TimeSpan.Zero);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldCarryConfiguredRequestTimeout()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseRequestTimeout(TimeSpan.FromSeconds(2));

        var client = builder.Build();
        var timeout = ReadRequestTimeout(client);
        Ensure(timeout == TimeSpan.FromSeconds(2), "request timeout should be applied");
        await client.DisposeAsync();
    }

    [Test]
    public async Task BuildShouldClearRequestTimeoutAfterDisable()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseRequestTimeout(TimeSpan.FromSeconds(2))
            .DisableRequestTimeout();

        var client = builder.Build();
        var timeout = ReadRequestTimeout(client);
        Ensure(timeout is null, "request timeout should be disabled");
        await client.DisposeAsync();
    }

    [Test]
    public async Task UseRpcSessionFlushShouldRejectInvalidValues()
    {
        var builder = SharpClientBuilder.Create();
        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            builder.UseRpcSessionFlush(0, TimeSpan.FromMilliseconds(1));
            return Task.CompletedTask;
        });

        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            builder.UseRpcSessionFlush(1024, TimeSpan.Zero);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldCarryRpcSessionFlushWithoutMutatingTransport()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseRpcSessionFlush(8192, TimeSpan.FromMilliseconds(2));

        var client = builder.Build();
        var options = ReadRpcSessionFlushOptions(client);
        Ensure(options is { FlushSizeThreshold: 8192 }, "flush size should match");
        Ensure(options?.MaxLatency == TimeSpan.FromMilliseconds(2), "max latency should match");
        await client.DisposeAsync();
    }

    [Test]
    public async Task BuildShouldRejectInvalidProtocolLimits()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseProtocol(static options =>
                options.MaxFramePayloadBytes = SharpLinkProtocolOptions.MinMaxFramePayloadBytes - 1);

        await EnsureThrows<ArgumentOutOfRangeException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldRejectPendingRequestCapacityThatIsNotPowerOfTwo()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseProtocol(static options => options.MaxPendingRequestsPerConnection = 1000);

        await EnsureThrows<ArgumentException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldFreezeProtocolLimitSnapshot()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseProtocol(static options => options.MaxFramePayloadBytes = 2048);

        var firstClient = builder.Build();
        builder.UseProtocol(static options => options.MaxFramePayloadBytes = 4096);
        var secondClient = builder.Build();

        Ensure(ReadMaxFramePayloadBytes(firstClient) == 2048, "first client protocol snapshot");
        Ensure(ReadMaxFramePayloadBytes(secondClient) == 4096, "second client protocol snapshot");
        await firstClient.DisposeAsync();
        await secondClient.DisposeAsync();
    }

    [Test]
    public async Task BuildShouldAllowDefaultSessionFlush()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport());

        var client = builder.Build();
        Ensure(ReadRpcSessionFlushOptions(client) is null, "default flush should remain session default");
        await client.DisposeAsync();
    }

    private static TimeSpan? ReadRequestTimeout(ISharpLinkClient client)
    {
        var hasField = client.GetType().GetField("_hasRequestTimeout", BindingFlags.Instance | BindingFlags.NonPublic);
        var valueField = client.GetType().GetField("_requestTimeoutValue", BindingFlags.Instance | BindingFlags.NonPublic);
        if (hasField is null || valueField is null)
            throw new Exception("cannot find request-timeout fields");

        var hasTimeout = (bool)(hasField.GetValue(client) ?? false);
        if (!hasTimeout)
            return null;

        return (TimeSpan?)valueField.GetValue(client);
    }

    private static int ReadMaxFramePayloadBytes(ISharpLinkClient client)
    {
        var field = client.GetType().GetField("_protocolOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(client) is not SharpLinkProtocolOptions options)
            throw new Exception("cannot find protocol-options field");

        return options.MaxFramePayloadBytes;
    }

    private static RpcSessionFlushOptions? ReadRpcSessionFlushOptions(ISharpLinkClient client)
    {
        var field = client.GetType().GetField("_rpcSessionFlushOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(client) as RpcSessionFlushOptions?;
    }

    private static async Task EnsureThrows<TException>(Func<Task> func) where TException : Exception
    {
        try
        {
            await func();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class NoopTransport : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
