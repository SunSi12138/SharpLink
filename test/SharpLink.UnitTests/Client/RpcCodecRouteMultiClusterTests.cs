
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class RpcCodecRouteMultiClusterTests
{
    [Test]
    public Task DependencyManifestViewShouldPreserveRoutedCodecOwnership()
    {
        ISharpLinkGeneratedAssemblyManifest source = new RoutedDependencyManifest();
        var viewType = typeof(SharpLinkMultiClusterClientBuilder)
            .GetNestedType("DependencyManifestView", BindingFlags.NonPublic)!;
        var constructor = viewType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        var view = (ISharpLinkGeneratedAssemblyManifest)constructor.Invoke([source]);

        Ensure(view.Codecs.Count == 0,
            "the dependency view must not republish a Contract-owned Codec globally");
        Ensure(view.ContractCodecs.Count == 1 && view.ContractCodecs[0].TargetType == typeof(ScopedPayload),
            "the dependency view must retain the Contract-owned Codec binding");
        return Task.CompletedTask;
    }

    private sealed class RoutedDependencyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(RpcCodecRouteMultiClusterTests).Assembly;
        public string CompileTimeDescriptor => string.Empty;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [new ScopedFactory()];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class ScopedFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(ScopedPayload);
        public string SchemaId => "scoped-dependency/v1";
        public string WireFormatId => "sharplink-native/v1";
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => new ScopedCodec();
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<ScopedPayload>;
    }

    private sealed class ScopedCodec : IRpcCodec<ScopedPayload>
    {
        public void Serialize(in ScopedPayload value, IBufferWriter<byte> buffer) { }
        public ScopedPayload Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class ScopedPayload { }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
