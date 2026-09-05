using System.Buffers;
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(SharpLink.ReferencedCodecProvider.ProviderManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "test",
    SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.ReferencedCodecProvider;

public readonly record struct Payload(int Value);

public sealed class ProviderManifest : ISharpLinkGeneratedAssemblyManifest
{
    public const ulong CodecHashHigh = 0x1122334455667788UL;
    public const ulong CodecHashLow = 0x8877665544332211UL;

    private static readonly IReadOnlyList<IRpcGeneratedCodecFactory> Factories =
        new IRpcGeneratedCodecFactory[] { new PayloadFactory() };

    public ProviderManifest() { }

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "test";
    public System.Reflection.Assembly OwnerAssembly => typeof(ProviderManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => new(0xAABBCCDDEEFF0011UL, 0x1100FFEEDDCCBBAAUL);
    public string CompileTimeDescriptor => "referenced-codec-provider";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => Factories;
    public IReadOnlyList<string> Dependencies => [];

    private sealed class PayloadFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(Payload);
        public RpcHash128 CodecHash => new(CodecHashHigh, CodecHashLow);
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        {
            _ = provider;
            if (adapterScope is not null)
                throw new ArgumentException("Native Codec factory does not accept an adapter scope.", nameof(adapterScope));
            return new PayloadCodec();
        }
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<Payload>;
    }

    private sealed class PayloadCodec : IRpcCodec<Payload>
    {
        public void Serialize(in Payload value, IBufferWriter<byte> buffer)
        {
            _ = value;
            _ = buffer;
        }

        public Payload Deserialize(in ReadOnlySequence<byte> buffer)
        {
            _ = buffer;
            return default;
        }
    }
}
