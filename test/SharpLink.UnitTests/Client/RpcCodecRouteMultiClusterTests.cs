using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class RpcCodecRouteMultiClusterTests
{
    [Test]
    public Task DependencyManifestViewShouldHideContractPolicy()
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
        Ensure(view.ContractCodecs.Count == 0,
            "the dependency view must hide Contract-owned policy when its Contracts are hidden");

        using var context = new SharpLinkRuntimeContextBuilder().Build([view]);
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
        public string WireFormatId => HiddenPolicyAdapter.Instance.WireFormatId;
        public string? AdapterId => HiddenPolicyAdapter.Instance.AdapterId;
        public IRpcCodecAdapter? Adapter => HiddenPolicyAdapter.Instance;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => throw new InvalidOperationException("hidden Contract policy factory must not be created by a dependency view");
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<ScopedPayload>;
    }

    private sealed class HiddenPolicyAdapter : IRpcCodecAdapter
    {
        internal static readonly HiddenPolicyAdapter Instance = new();
        public string AdapterId => "hidden-dependency-policy/v1";
        public string WireFormatId => "hidden-dependency-wire/v1";
        public IRpcCodecAdapterScope CreateScope()
            => throw new InvalidOperationException("hidden Contract policy adapter scope must not be created by a dependency view");
    }

    private sealed class ScopedPayload { }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
