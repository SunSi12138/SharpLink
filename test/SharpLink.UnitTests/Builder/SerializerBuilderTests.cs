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
        var anonymous = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopTransport())
            .Build();
        await DisposeAsync(anonymous);

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseTransport(new NoopTransport())
                .RequireAuthentication()
                .Build();
        });

        var authenticated = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
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
        var firstClient = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopTransport())
            .UseSerializer(type => type == typeof(Payload) ? firstCodec : null)
            .Build();
        var secondClient = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopTransport())
            .UseSerializer(type => type == typeof(Payload) ? secondCodec : null)
            .Build();
        var firstServer = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopTransport())
            .UseSerializer(type => type == typeof(Payload) ? firstCodec : null)
            .Build();
        var secondServer = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
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
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec<Payload>(codec)
            .Build();
        var registration = context.PrepareGeneratedManifest(new TaggedManifest());
        context.PublishGeneratedCodecs(registration.Codecs);
        context.AdoptGeneratedManifest(registration);

        context.ReleaseGeneratedManifest(registration);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<Payload>(), codec),
            "generated module cleanup must preserve an explicit codec");
    }

    [Test]
    public void PublishedGeneratedCodecShouldReplaceCachedFallbackCodec()
    {
        var fallback = new TaggedCodec("fallback");
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseCodecResolver(type => type == typeof(Payload) ? fallback : null)
            .Build();
        Ensure(ReferenceEquals(context.Codecs.GetCodec<Payload>(), fallback),
            "fallback codec cached before module publication");

        var registration = context.PrepareGeneratedManifest(new TaggedManifest());
        context.PublishGeneratedCodecs(registration.Codecs);
        context.AdoptGeneratedManifest(registration);

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
        public string WireFormatId => "sharplink-native/v1";
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? new TaggedCodec("generated")
                : throw new ArgumentException("Native factory does not accept an Adapter Scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<Payload>;
    }

    private sealed class TaggedManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(TaggedManifest).Assembly;
        public string CompileTimeDescriptor => "tagged-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [new TaggedCodecFactory()];
        public IReadOnlyList<string> Dependencies => [];
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
