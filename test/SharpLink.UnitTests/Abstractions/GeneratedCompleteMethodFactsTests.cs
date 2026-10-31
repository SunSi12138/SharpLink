using System.Linq;
using SharpLink.StaticCodecOwnerTest.Contracts;

namespace SharpLink.UnitTests.Abstractions;

public class GeneratedCompleteMethodFactsTests
{
    [Test]
    [Arguments(16)]
    [Arguments(127)]
    public async Task GeneratedFactsShouldPreserveFullClientStreamCount(int count)
    {
        var type = count == 16 ? typeof(IFactsSixteenStreams) : typeof(IFactsMaximumStreams);
        var (contract, context, stub) = CreateStub(type);
        using (context)
        {
            var method = contract.Methods.Single();
            var facts = stub.ResolveMethodFacts(method.MethodId);
            await Assert.That(facts.ClientStreamCount).IsEqualTo(count);
            await Assert.That(facts.Kind).IsEqualTo(RpcMethodKind.ClientStreaming);
            await Assert.That(facts.SupportsCancellation && facts.HasClientStreams).IsTrue();
            await Assert.That(facts.ToDescriptor().ClientStreamCount).IsEqualTo(count);
            await Assert.That(ReferenceEquals(facts, stub.ResolveMethodFacts(method.MethodId))).IsTrue();
        }
    }

    [Test]
    public async Task CompleteGeneratedFactsShouldCarry257DistinctTimeoutsWithoutAnotherQuery()
    {
        var (contract, context, stub) = CreateStub(typeof(IFactsManyTimeouts));
        using (context)
        {
            await Assert.That(contract.Methods.Count).IsEqualTo(257);
            foreach (var method in contract.Methods)
            {
                var seconds = int.Parse(method.Name.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
                var facts = stub.ResolveMethodFacts(method.MethodId);
                var expected = TimeSpan.FromSeconds(seconds);
                await Assert.That(facts.ContractId).IsEqualTo(contract.ContractId);
                await Assert.That(facts.MethodId).IsEqualTo(method.MethodId);
                await Assert.That(facts.MethodTimeout).IsEqualTo(expected);
                await Assert.That(facts.ToDescriptor().MethodTimeout).IsEqualTo(expected);
                await Assert.That(ReferenceEquals(facts, stub.ResolveMethodFacts(method.MethodId))).IsTrue();
            }
            await Assert.That(contract.Methods.Any(method => method.MethodId == long.MinValue)).IsFalse();
            await Assert.That(ReferenceEquals(stub.ResolveMethodFacts(long.MinValue), RpcMethodFacts.UnknownMethod)).IsTrue();
        }
    }

    private static (SharpLinkGeneratedContractDescriptor Contract, SharpLinkRuntimeContext Context, IRpcStub Stub)
        CreateStub(Type type)
    {
        var result = SharpLinkAssemblyManifestLoader.TryLoad(type.Assembly, out var manifest);
        if (!result.Succeeded || manifest is null)
            throw new InvalidOperationException($"Generated facts fixture failed to load: {result.Error}");
        var contract = manifest.Contracts.Single(item => item.ContractType == type);
        var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        try
        {
            var registration = context.PrepareGeneratedManifest(manifest);
            context.AdoptGeneratedManifest(registration);
            var provider = RpcGeneratedCodecResolver.GetProvider(registration);
            var first = contract.StubFactory(provider);
            var second = contract.StubFactory(provider);
            if (!ReferenceEquals(first.ResolveMethodFacts(contract.Methods[0].MethodId),
                    second.ResolveMethodFacts(contract.Methods[0].MethodId)))
                throw new InvalidOperationException("Generated instances must share one cold fact object per method.");
            return (contract, context, first);
        }
        catch { context.Dispose(); throw; }
    }
}
