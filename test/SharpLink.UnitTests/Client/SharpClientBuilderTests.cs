using System.Reflection;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class SharpClientBuilderTests
{
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
    public Task BuildShouldCarryConfiguredRequestTimeout()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseSerializer(new NoopSerializer())
            .UseRequestTimeout(TimeSpan.FromSeconds(2));

        var client = builder.Build();
        var timeout = ReadRequestTimeout(client);
        Ensure(timeout == TimeSpan.FromSeconds(2), "request timeout should be applied");
        (client as IDisposable)?.Dispose();
        return Task.CompletedTask;
    }

    [Test]
    public Task BuildShouldClearRequestTimeoutAfterDisable()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseSerializer(new NoopSerializer())
            .UseRequestTimeout(TimeSpan.FromSeconds(2))
            .DisableRequestTimeout();

        var client = builder.Build();
        var timeout = ReadRequestTimeout(client);
        Ensure(timeout is null, "request timeout should be disabled");
        (client as IDisposable)?.Dispose();
        return Task.CompletedTask;
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
    public async Task BuildShouldThrowWhenTransportDoesNotSupportRpcSessionFlush()
    {
        var builder = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseSerializer(new NoopSerializer())
            .UseRpcSessionFlush(8192, TimeSpan.FromMilliseconds(2));

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public Task BuildShouldApplyRpcSessionFlushToConfigurableTransport()
    {
        var transport = new FlushConfigurableNoopTransport();
        var builder = SharpClientBuilder.Create()
            .UseTransport(transport)
            .UseSerializer(new NoopSerializer())
            .UseRpcSessionFlush(8192, TimeSpan.FromMilliseconds(2));

        _ = builder.Build();
        Ensure(transport.ConfiguredOptions.HasValue, "flush options should be configured");
        Ensure(transport.ConfiguredOptions.Value.FlushSizeThreshold == 8192, "flush size should match");
        Ensure(transport.ConfiguredOptions.Value.MaxLatency == TimeSpan.FromMilliseconds(2), "max latency should match");
        return Task.CompletedTask;
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

    private sealed class NoopTransport : ITransport
    {
        public Task<IRpcSession> ConnectAsync(ISerializer serializer, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class FlushConfigurableNoopTransport : ITransport, SharpLink.Runtime.IRpcSessionFlushConfigurableTransport
    {
        public SharpLink.Runtime.RpcSessionFlushOptions? ConfiguredOptions { get; private set; }

        public Task<IRpcSession> ConnectAsync(ISerializer serializer, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void ConfigureRpcSessionFlush(SharpLink.Runtime.RpcSessionFlushOptions options)
        {
            ConfiguredOptions = options;
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoopSerializer : ISerializer
    {
        public void Serialize<T>(in T value, IBufferWriter<byte> writer)
        {
        }

        public T Deserialize<T>(ref ReadOnlySequence<byte> sequence) => default!;
    }
}
