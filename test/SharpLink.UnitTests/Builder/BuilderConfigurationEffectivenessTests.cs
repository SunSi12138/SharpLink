using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Builder;

/// <summary>
/// #801: distinguish live Builder configuration from retired runtime Codec/Serializer
/// injection. These tests go through the actual public Build/materialization path.
/// </summary>
public sealed class BuilderConfigurationEffectivenessTests
{
    [Test]
    public async Task ClientBufferAndStateStoreConfigurationShouldReachTheMaterializedRuntime()
    {
        await using var client = SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopTransport())
            .DisableRequestTimeout()
            .UseBufferWriterPool(static options =>
            {
                options.InitialCapacity = 2048;
                options.MaxPooledWriters = 4;
            })
            .UseStateStoreConcurrency(static options =>
            {
                options.StripeCount = 8;
                options.InitialMapCapacityPerStripe = 3;
            })
            .UseProtocol(static options => options.MaxMetadataBytes = 8192)
            .Build();

        var context = (SharpLinkRuntimeContext)((IRpcChannel)client).RuntimeContext;
        Ensure(context.Buffers.InitialCapacity == 2048,
            "Client UseBufferWriterPool must configure the real runtime's pooled packet writers");
        Ensure(context.Concurrency.StripeCount == 8 &&
               context.Concurrency.InitialMapCapacityPerStripe == 3,
            "Client UseStateStoreConcurrency must reach the session's actual striped state-store configuration");
        Ensure(context.Options.Protocol.MaxMetadataBytes == 8192,
            "Client UseProtocol must reach the frozen runtime options");

        var rented = context.Buffers.Rent();
        try
        {
            Ensure(rented.GetSpan(1).Length >= 2048,
                "the actual Client writer lease must use the configured initial buffer size");
        }
        finally
        {
            context.Buffers.Return(rented);
        }
    }

    [Test]
    public async Task ServerBufferAndStateStoreConfigurationShouldReachTheMaterializedRuntime()
    {
        await using var server = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopTransport())
            .UseBufferWriterPool(static options =>
            {
                options.InitialCapacity = 3072;
                options.MaxPooledWriters = 2;
            })
            .UseStateStoreConcurrency(static options =>
            {
                options.StripeCount = 16;
                options.InitialMapCapacityPerStripe = 5;
            })
            .UseProtocol(static options => options.MaxMetadataBytes = 4096)
            .Build();

        var context = GetServerContext(server);
        Ensure(context.Buffers.InitialCapacity == 3072,
            "Server UseBufferWriterPool must configure its real runtime writer pool");
        Ensure(context.Concurrency.StripeCount == 16 &&
               context.Concurrency.InitialMapCapacityPerStripe == 5,
            "Server UseStateStoreConcurrency must reach actual session/call map configuration");
        Ensure(context.Options.Protocol.MaxMetadataBytes == 4096,
            "Server UseProtocol must reach its frozen protocol options");

        var rented = context.Buffers.Rent();
        try
        {
            Ensure(rented.GetSpan(1).Length >= 3072,
                "the actual Server writer lease must use the configured initial buffer size");
        }
        finally
        {
            context.Buffers.Return(rented);
        }
    }

    [Test]
    public async Task RequiredAuthenticationShouldNeedServerProviderWhileAnonymousRemainsDefault()
    {
        await using var anonymous = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopTransport())
            .Build();

        EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseTransport(new NoopTransport())
                .RequireAuthentication()
                .Build();
        });

        await using var authenticated = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopTransport())
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(
                static (_, _) => ValueTask.FromResult(SharpLinkAuthenticationResult.Success)))
            .RequireAuthentication()
            .Build();
    }

    private static SharpLinkRuntimeContext GetServerContext(ISharpLinkServer server)
    {
        var field = server.GetType().GetField("_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(server) as SharpLinkRuntimeContext
            ?? throw new Exception("Cannot find the materialized Server RuntimeContext.");
    }

    private static void EnsureThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class NoopTransport : IClientTransportFactory, IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
