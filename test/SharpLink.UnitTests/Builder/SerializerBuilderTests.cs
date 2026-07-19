using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Builder;

public class SerializerBuilderTests
{
    [Test]
    public async Task RequiredAuthenticationShouldNeedServerProviderWhileAnonymousRemainsDefault()
    {
        var anonymous = SharpLinkServerBuilder.Create()
            .UseTransport(new NoopTransport())
            .Build();
        await DisposeAsync(anonymous);

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpLinkServerBuilder.Create()
                .UseTransport(new NoopTransport())
                .RequireAuthentication()
                .Build();
        });

        var authenticated = SharpLinkServerBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(
                static (_, _) => ValueTask.FromResult(SharpLinkAuthenticationResult.Success)))
            .RequireAuthentication()
            .Build();
        await DisposeAsync(authenticated);
    }

    [Test]
    public async Task ClientsAndServersShouldOwnIndependentCodecProviders()
    {
        var firstCodec = new TaggedCodec("first");
        var secondCodec = new TaggedCodec("second");
        var firstClient = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseSerializer(type => type == typeof(Payload) ? firstCodec : null)
            .Build();
        var secondClient = SharpClientBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseSerializer(type => type == typeof(Payload) ? secondCodec : null)
            .Build();
        var firstServer = SharpLinkServerBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseSerializer(type => type == typeof(Payload) ? firstCodec : null)
            .Build();
        var secondServer = SharpLinkServerBuilder.Create()
            .UseTransport(new NoopTransport())
            .UseSerializer(type => type == typeof(Payload) ? secondCodec : null)
            .Build();

        try
        {
            Ensure(ReferenceEquals(GetClientContext(firstClient).Codecs.GetCodec<Payload>(), firstCodec), "first client codec");
            Ensure(ReferenceEquals(GetClientContext(secondClient).Codecs.GetCodec<Payload>(), secondCodec), "second client codec");
            Ensure(ReferenceEquals(GetServerContext(firstServer).Codecs.GetCodec<Payload>(), firstCodec), "first server codec");
            Ensure(ReferenceEquals(GetServerContext(secondServer).Codecs.GetCodec<Payload>(), secondCodec), "second server codec");
        }
        finally
        {
            await DisposeAsync(firstClient);
            await DisposeAsync(secondClient);
            await DisposeAsync(firstServer);
            await DisposeAsync(secondServer);
        }
    }

    [Test]
    public void RemovingGeneratedCodecStateShouldPreserveExplicitCodecs()
    {
        var codec = new TaggedCodec("explicit");
        var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec<Payload>(codec)
            .Build();

        context.RemoveResolvedGeneratedCodecs([typeof(Payload)]);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<Payload>(), codec),
            "generated module cleanup must preserve an explicit codec");
    }

    [Test]
    public void PublishedGeneratedCodecShouldReplaceCachedFallbackCodec()
    {
        var fallback = new TaggedCodec("fallback");
        var context = new SharpLinkRuntimeContextBuilder()
            .UseCodecResolver(type => type == typeof(Payload) ? fallback : null)
            .Build();
        Ensure(ReferenceEquals(context.Codecs.GetCodec<Payload>(), fallback),
            "fallback codec cached before module publication");

        context.PublishGeneratedCodecs(new Dictionary<Type, IRpcGeneratedCodecFactory>
        {
            [typeof(Payload)] = new TaggedCodecFactory()
        });

        Ensure(context.Codecs.GetCodec<Payload>() is TaggedCodec { Tag: "generated" },
            "generated codec takes precedence after publication");
    }

    private static IRpcRuntimeContext GetClientContext(ISharpLinkClient client)
        => ((IRpcChannel)client).RuntimeContext;

    private static SharpLinkRuntimeContext GetServerContext(ISharpLinkServer server)
    {
        var field = server.GetType().GetField("_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(server) as SharpLinkRuntimeContext
            ?? throw new Exception("cannot find server runtime context");
    }

    private static async ValueTask DisposeAsync(object value)
    {
        if (value is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static async Task EnsureThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
        await Task.CompletedTask;
    }

    private sealed class Payload;

    private sealed class TaggedCodec(string tag) : IRpcCodec<Payload>
    {
        public string Tag { get; } = tag;

        public void Serialize(in Payload value, IBufferWriter<byte> buffer)
        {
        }

        public Payload Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class TaggedCodecFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(Payload);
        public string SchemaId => "generated-test-v1";
        public IRpcCodec Create(IRpcCodecProvider provider) => new TaggedCodec("generated");
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
