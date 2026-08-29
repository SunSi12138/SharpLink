using System.Buffers;
using System.Reflection;
using System.Reflection.Emit;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcImplicitOwnerLateLoadRegressionTests
{
    [Test]
    [NotInParallel]
    public void LateLoadedImplicitOnlyOwnerShouldFailClosedInsteadOfUsingContextCodec()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec<ImplicitRaw>(new ContextOverrideCodec())
            .Build(includeGeneratedAssemblyCatalog: false);
        var owner = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("SharpLink.LateImplicitOwner." + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.RunAndCollect);
        ISharpLinkGeneratedAssemblyManifest? manifest = new EmptyGeneratedOwnerManifest(owner);
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);

        try
        {
            _ = RpcGeneratedCodecResolver.GetProvider(context, owner);
            throw new InvalidOperationException(
                "an implicit-only generated owner loaded after the context must not fall through to its context-global Codec override");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("was not adopted", StringComparison.Ordinal),
                $"implicit-only late owner must fail with the deterministic adoption diagnostic, got: {exception.Message}");
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

    private struct ImplicitRaw
    {
        public int Value { get; set; }
    }

    private sealed class ContextOverrideCodec : IRpcCodec<ImplicitRaw>
    {
        public void Serialize(in ImplicitRaw value, IBufferWriter<byte> buffer) { }
        public ImplicitRaw Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class EmptyGeneratedOwnerManifest(Assembly ownerAssembly) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "implicit-owner-regression";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "implicit-owner-regression|" + OwnerAssembly.FullName;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
