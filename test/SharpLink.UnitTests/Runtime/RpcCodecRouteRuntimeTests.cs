using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public class RpcCodecRouteRuntimeTests
{
    [Test]
    public void GeneratedCodecBindingShouldOverrideBuiltinCodec()
    {
        var replacement = new RoutedInt32Codec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new BuiltinOverrideManifest(replacement));
        context.AdoptGeneratedManifest(registration);
        context.PublishGeneratedCodecs(registration.Codecs);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<int>(), replacement),
            "a compile-time routed Native payload must override the shared builtin Codec");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RoutedInt32Codec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer) => 0;
    }

    private sealed class RouteAdapter(RoutedInt32Codec codec) : IRpcCodecAdapter
    {
        public const string Id = "route-native-test/v1";
        public const string Wire = "route-native-test-wire/v1";

        public string AdapterId => Id;
        public string WireFormatId => Wire;
        public IRpcCodecAdapterScope CreateScope() => new RouteScope(codec);
    }

    private sealed class RouteScope(RoutedInt32Codec codec) : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>()
            => typeof(T) == typeof(int)
                ? (IRpcCodec<T>)(object)codec
                : throw new NotSupportedException($"Unexpected route test target '{typeof(T)}'.");

        public void Dispose()
        {
        }
    }

    private sealed class RoutedInt32Factory(RouteAdapter adapter) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(int);
        public string SchemaId => "route-native-int32-test/v1";
        public string WireFormatId => RouteAdapter.Wire;
        public string? AdapterId => RouteAdapter.Id;
        public IRpcCodecAdapter? Adapter => adapter;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope?.CreateCodec<int>() ??
               throw new ArgumentNullException(nameof(adapterScope));

        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<int>;
    }

    private sealed class BuiltinOverrideManifest(RoutedInt32Codec codec) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "route-test";
        public Assembly OwnerAssembly => typeof(BuiltinOverrideManifest).Assembly;
        public string CompileTimeDescriptor => "route-native-builtin-override-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } =
            [new RoutedInt32Factory(new RouteAdapter(codec))];
        public IReadOnlyList<string> Dependencies => [];
    }
}
