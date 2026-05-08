using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Builder;

public class SerializerBuilderTests
{
    private static readonly FieldInfo CodecResolverField = typeof(RpcCodecRegistry)
        .GetField("_codecResolver", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new Exception("cannot find RpcCodecRegistry._codecResolver");

    [Test]
    public Task BuildShouldInitializeCodecResolverWithoutRpcSessionFlush()
    {
        Func<Type, IRpcCodec?> clientResolver = static _ => null;
        Func<Type, IRpcCodec?> serverResolver = static _ => null;
        var original = ReadCodecResolver();

        try
        {
            WriteCodecResolver(null);

            var client = SharpClientBuilder.Create()
                .UseTransport(new NoopTransport())
                .UseSerializer(clientResolver)
                .Build();

            Ensure(ReferenceEquals(ReadCodecResolver(), clientResolver), "client builder should initialize codec resolver");
            (client as IDisposable)?.Dispose();

            var server = SharpLinkServerBuilder.Create()
                .UseTransport(new NoopTransport())
                .UseSerializer(serverResolver)
                .Build();

            Ensure(ReferenceEquals(ReadCodecResolver(), serverResolver), "server builder should initialize codec resolver");
            (server as IDisposable)?.Dispose();
            return Task.CompletedTask;
        }
        finally
        {
            WriteCodecResolver(original);
        }
    }

    private static Func<Type, IRpcCodec?>? ReadCodecResolver()
        => (Func<Type, IRpcCodec?>?)CodecResolverField.GetValue(null);

    private static void WriteCodecResolver(Func<Type, IRpcCodec?>? resolver)
        => CodecResolverField.SetValue(null, resolver);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class NoopTransport : ITransport
    {
        public Task<IRpcSession> ConnectAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
