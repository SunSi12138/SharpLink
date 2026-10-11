using System.Linq;
using System.Reflection;
using SharpLink.StaticCodecOwnerTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public sealed class GeneratedManifestMethodShapeValidationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ValidatorShouldRejectUnresolvedMethodShapesWithMethodDiagnostics(bool unknownMethod)
    {
        var source = LoadGeneratedManifest();
        var valid = new ShapeOverrideManifest(source, replacement: null);
        await Assert.That(SharpLinkGeneratedManifestCompatibility.Validate(valid, source.OwnerAssembly)).IsNull();

        var invalid = new ShapeOverrideManifest(source,
            unknownMethod ? RpcMethodShape.UnknownMethod : default(RpcMethodShape));
        // This is also the shared validation entry point invoked by the dynamic assembly loader.
        var error = SharpLinkGeneratedManifestCompatibility.Validate(invalid, source.OwnerAssembly);
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Code).IsEqualTo(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest);
        await Assert.That(error.Artifact).IsEqualTo("Method");
        await Assert.That(error.ContractId).IsEqualTo(invalid.ChangedContract.ContractId);
        await Assert.That(error.MethodId).IsEqualTo(invalid.ChangedContract.Methods[0].MethodId);
        await Assert.That(error.MethodName).IsEqualTo(invalid.ChangedContract.Methods[0].Name);
        await Assert.That(error.IncomingAssembly).IsEqualTo(source.OwnerAssembly.FullName);
        await Assert.That(invalid.FactoryCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RuntimeShouldRejectUnresolvedMethodShapesBeforeFactoryPublication(bool unknownMethod)
    {
        var source = LoadGeneratedManifest();
        var invalid = new ShapeOverrideManifest(source,
            unknownMethod ? RpcMethodShape.UnknownMethod : default(RpcMethodShape));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var before = context.CreateGeneratedCodecSnapshot().Count;
        await Assert.That(() => context.PrepareGeneratedManifest(invalid))
            .Throws<InvalidOperationException>();
        await Assert.That(context.CreateGeneratedCodecSnapshot().Count).IsEqualTo(before);
        await Assert.That(invalid.FactoryCalls).IsEqualTo(0);
    }

    private static ISharpLinkGeneratedAssemblyManifest LoadGeneratedManifest()
    {
        var result = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShapeSixteenStreams).Assembly, out var manifest);
        if (!result.Succeeded || manifest is null)
            throw new InvalidOperationException($"Generated fixture manifest failed: {result.Error}");
        return manifest;
    }

    private sealed class ShapeOverrideManifest : ISharpLinkGeneratedAssemblyManifest
    {
        private readonly ISharpLinkGeneratedAssemblyManifest _source;

        public ShapeOverrideManifest(ISharpLinkGeneratedAssemblyManifest source, RpcMethodShape? replacement)
        {
            _source = source;
            var contract = source.Contracts.Single(item => item.ContractType == typeof(IShapeSixteenStreams));
            var methods = contract.Methods.ToArray();
            if (replacement is { } shape)
                methods[0] = methods[0] with { Shape = shape };
            ChangedContract = contract with
            {
                Methods = methods,
                ProxyFactory = (channel, codecs) =>
                {
                    FactoryCalls++;
                    return contract.ProxyFactory(channel, codecs);
                },
                StubFactory = codecs =>
                {
                    FactoryCalls++;
                    return contract.StubFactory(codecs);
                }
            };
            Contracts = source.Contracts.Select(item => ReferenceEquals(item, contract) ? ChangedContract : item).ToArray();
        }

        public int FactoryCalls { get; private set; }
        public SharpLinkGeneratedContractDescriptor ChangedContract { get; }
        public int ApiVersion => _source.ApiVersion;
        public int ProtocolVersion => _source.ProtocolVersion;
        public string GeneratorVersion => _source.GeneratorVersion;
        public Assembly OwnerAssembly => _source.OwnerAssembly;
        public RpcHash128 RpcAssemblyHash => _source.RpcAssemblyHash;
        public string CompileTimeDescriptor => _source.CompileTimeDescriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => _source.Services;
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => _source.Codecs;
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => _source.ContractCodecs;
        public IReadOnlyList<string> Dependencies => _source.Dependencies;
        public IReadOnlyList<string> ContractDependencies => _source.ContractDependencies;
    }
}
