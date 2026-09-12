using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.StaticCodecOwnerTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcCodecPolicyRegressionTests
{
    [Test]
    public void CustomFactoryWithSemanticIdentityShouldPrepareAndResolve()
    {
        var manifest = new TestManifest(
            typeof(IContractA).Assembly,
            [new CustomPayloadFactory()]);

        using var context = new SharpLinkRuntimeContextBuilder().Build([manifest]);
        var ownerProvider = RpcGeneratedCodecResolver.GetProvider(context, manifest.OwnerAssembly);

        Ensure(ownerProvider.GetCodec<CustomPayload>() is CustomPayloadCodec,
            "a compile-time custom Codec must survive RuntimeContext preparation and owner resolution");
    }

    [Test]
    [NotInParallel]
    public void LateLoadedPolicyOwnerShouldFailClosedInsteadOfUsingGlobalCodecs()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var owner = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("SharpLink.LatePolicyOwner." + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.RunAndCollect);
        ISharpLinkGeneratedAssemblyManifest? manifest = new TestManifest(
            owner,
            [new CustomPayloadFactory()]);
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);

        try
        {
            _ = RpcGeneratedCodecResolver.GetProvider(context, owner);
            throw new InvalidOperationException(
                "a runtime context that predates a loaded policy owner must not silently return the global Codec provider");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("was not adopted", StringComparison.Ordinal),
                $"late policy owner must fail with the deterministic adoption diagnostic, got: {exception.Message}");
        }
        finally
        {
            RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
            manifest = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed class CustomPayload
    {
    }

    private sealed class CustomPayloadCodec : IRpcCodec<CustomPayload>
    {
        public void Serialize(in CustomPayload value, IBufferWriter<byte> buffer) { }
        public CustomPayload Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class CustomPayloadFactory : ITestGeneratedCodecFactory
    {
        public Type TargetType => typeof(CustomPayload);
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? new CustomPayloadCodec()
                : throw new ArgumentException("Custom factory does not accept an adapter scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<CustomPayload>;
    }

    private sealed class TestManifest(
        Assembly ownerAssembly,
        IReadOnlyList<IRpcGeneratedCodecFactory> contractCodecs) : ITestGeneratedManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "policy-regression";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "policy-regression|" + OwnerAssembly.FullName;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; } = contractCodecs;
        public IReadOnlyList<string> Dependencies => [];
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
