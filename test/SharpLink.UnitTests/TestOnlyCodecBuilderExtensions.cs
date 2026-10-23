// Test-project-only generated Codec factories. Not distributed in any SharpLink NuGet package.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SharpLink.Abstractions;

namespace SharpLink.Runtime;

internal static class TestOnlyCodecBuilderExtensions
{
    private static readonly ConditionalWeakTable<SharpLinkRuntimeContextBuilder, TestCodecManifestSource> Sources = new();

    // Keep unit/benchmark payload tests on the same generated-codec registration path as production.
    internal static SharpLinkRuntimeContextBuilder AddCodec<T>(
        this SharpLinkRuntimeContextBuilder builder, IRpcCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(codec);
        if (BuiltinRpcCodecs.TryGet(typeof(T), out _))
            throw new InvalidOperationException("The built-in codec is immutable and cannot be replaced.");

        var source = Sources.GetValue(builder, static item =>
        {
            var created = new TestCodecManifestSource();
            item.UseGeneratedManifestSource(created);
            return created;
        });
        source.Add(new TestCodecFactory<T>(codec));
        return builder;
    }

    private sealed class TestCodecManifestSource : IGeneratedManifestSource
    {
        private readonly List<IRpcGeneratedCodecFactory> _factories = [];

        internal void Add(IRpcGeneratedCodecFactory factory)
        {
            if (_factories.Exists(item => item.TargetType == factory.TargetType))
                throw new InvalidOperationException("A codec for this closed Type is already registered.");
            _factories.Add(factory);
        }

        public IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateSnapshot()
            => [new TestCodecManifest([.. _factories])];
    }

    private sealed class TestCodecManifest(IReadOnlyList<IRpcGeneratedCodecFactory> factories)
        : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "issue-801-test-only-generated-codecs";
        public System.Reflection.Assembly OwnerAssembly => typeof(TestCodecManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => new(0x6973737565383031UL, 0x636f646563766572UL);
        public string CompileTimeDescriptor => "issue-801-test-only-generated-codecs";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = factories;
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class TestCodecFactory<T>(IRpcCodec<T> codec) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => new(0x6973737565383031UL, 0x746573742d636f64UL);
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? scope)
        {
            if (scope is not null)
                throw new ArgumentException("A native Codec must not be provided an adapter scope.");
            return codec;
        }
        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<T>;
    }
}
