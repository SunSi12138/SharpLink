using System.Linq;

namespace SharpLink.UnitTests.Abstractions;

public sealed class RpcMethodShapeGeneratedBoundaryTests
{
    [Test]
    [Arguments(16)]
    [Arguments(127)]
    public async Task CompiledGeneratedStubShouldPreserveSupportedStreamCounts(int count)
    {
        var contractType = count == 16 ? typeof(IShapeSixteenStreams) : typeof(IShapeMaximumStreams);
        var contract = GetContract(contractType);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stub = CreateStub(context, contract);
        var method = contract.Methods.Single();
        var shape = stub.ResolveMethodShape(method.MethodId);
        await Assert.That(shape.ClientStreamCount).IsEqualTo(count);
        await Assert.That(shape).IsEqualTo(method.Shape);
        await Assert.That(shape.Kind).IsEqualTo(RpcMethodKind.ClientStreaming);
        await Assert.That(shape.HasClientStreams).IsTrue();
        await Assert.That(shape.SupportsCancellation).IsTrue();
        stub.DescribeMethod(method.MethodId, shape, out var descriptor);
        await Assert.That(descriptor.ClientStreamCount).IsEqualTo(count);
    }

    [Test]
    public async Task CompiledGeneratedStubShouldPreserve257DistinctTimeoutValues()
    {
        var contract = GetContract(typeof(IShapeManyTimeouts));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stub = CreateStub(context, contract);
        await Assert.That(contract.Methods.Count).IsEqualTo(257);
        foreach (var method in contract.Methods)
        {
            var seconds = int.Parse(method.Name.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
            var shape = stub.ResolveMethodShape(method.MethodId);
            await Assert.That(shape).IsEqualTo(method.Shape);
            await Assert.That(shape.HasMethodTimeout).IsTrue();
            await Assert.That(shape.HasMethodTimeoutValue).IsTrue();
            stub.DescribeMethod(method.MethodId, shape, out var descriptor);
            await Assert.That(descriptor.MethodTimeout).IsEqualTo(TimeSpan.FromSeconds(seconds));
        }
    }

    private static IRpcStub CreateStub(SharpLinkRuntimeContext context,
        SharpLinkGeneratedContractDescriptor contract)
    {
        var result = SharpLinkAssemblyManifestLoader.TryLoad(contract.ContractType.Assembly, out var manifest);
        if (!result.Succeeded || manifest is null)
            throw new InvalidOperationException($"Generated boundary fixture manifest failed: {result.Error}");
        var registration = context.PrepareGeneratedManifest(manifest);
        context.AdoptGeneratedManifest(registration);
        return contract.StubFactory(RpcGeneratedCodecResolver.GetProvider(registration));
    }

    private static SharpLinkGeneratedContractDescriptor GetContract(Type type)
    {
        var result = SharpLinkAssemblyManifestLoader.TryLoad(type.Assembly, out var manifest);
        if (!result.Succeeded || manifest is null)
            throw new InvalidOperationException($"Generated boundary fixture manifest failed: {result.Error}");
        return manifest.Contracts.Single(contract => contract.ContractType == type);
    }
}

