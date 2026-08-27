using System;
using SharpLink.Abstractions;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcGeneratedCodecFactoryKindTests
{
    [Test]
    public void AdapterFreeFactoryKindShouldDistinguishNativeAndDirectWireIdentities()
    {
        IRpcGeneratedCodecFactory custom = new AdapterFreeFactory("review-custom/v1");
        IRpcGeneratedCodecFactory native = new AdapterFreeFactory("sharplink-native/v1");

        Ensure(custom.Kind == RpcGeneratedCodecFactoryKind.Direct,
            "adapter-free factories with a non-native wire identity must be treated as direct/custom construction");
        Ensure(native.Kind == RpcGeneratedCodecFactoryKind.Native,
            "the SharpLink native wire identity must retain the Native factory kind");
    }

    private sealed class AdapterFreeFactory(string wireFormatId) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(AdapterFreeFactory);
        public string SchemaId => "review-factory-schema/v1";
        public string WireFormatId { get; } = wireFormatId;
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => throw new NotSupportedException();

        public bool IsCompatibleCodec(IRpcCodec codec) => false;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
