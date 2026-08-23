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
        var replacement = new GeneratedInt32Codec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new BuiltinOverrideManifest(replacement));
        context.AdoptGeneratedManifest(registration);
        context.PublishGeneratedCodecs(registration.Codecs);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<int>(), replacement),
            "a compile-time generated binding for a Native payload must override the shared builtin Codec");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class GeneratedInt32Codec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer) => 0;
    }

    private sealed class GeneratedInt32Factory(GeneratedInt32Codec codec) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(int);
        public string SchemaId => "route-native-int32-test/v1";
        public string WireFormatId => "route-native-test-wire/v1";
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? codec
                : throw new ArgumentException("Native factory does not accept an Adapter Scope.", nameof(adapterScope));

        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<int>;
    }

    private sealed class BuiltinOverrideManifest(GeneratedInt32Codec codec) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "route-test";
        public Assembly OwnerAssembly => typeof(BuiltinOverrideManifest).Assembly;
        public string CompileTimeDescriptor => "route-native-builtin-override-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [new GeneratedInt32Factory(codec)];
        public IReadOnlyList<string> Dependencies => [];
    }
}
