using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task RoutedCodecsShouldBeOwnerBoundAcrossProxyStubAndStreams()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcContract]
public interface IOwnerRouteContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
    System.Collections.Generic.IAsyncEnumerable<int> Stream(CancellationToken cancellationToken);
    ValueTask<int> Sum(System.Collections.Generic.IAsyncEnumerable<int> values, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.owner/v1";
    public override string WireFormatId => "route-owner-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.owner/v1\", \"route-owner-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(RouteAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("ContractCodecs => __readOnlyContractCodecs", StringComparison.Ordinal),
            "route-selected targets must be emitted into the Contract-owned binding table");
        Ensure(generated.Contains("static (channel, codecs) =>", StringComparison.Ordinal),
            "the generated proxy factory must receive the Contract owner Codec provider from registration");
        Ensure(generated.Contains("static codecs => new", StringComparison.Ordinal),
            "the generated stub factory must receive the Contract owner Codec provider from registration");
        Ensure(generated.Contains("internal IOwnerRouteContract_Stub(IRpcCodecProvider codecs)", StringComparison.Ordinal),
            "generated stubs must bind owner Codecs during construction rather than through a later mutation");
        Ensure(generated.Contains("_values, __codec_values, cancellationToken", StringComparison.Ordinal),
            "generated client streams must pass the owner-bound item Codec directly to the sink");
        Ensure(!generated.Contains("RpcCodecBoundAsyncEnumerable", StringComparison.Ordinal),
            "client stream routing must not rely on a runtime wrapper/type predicate");
        Ensure(generated.Contains("SendBoundStreamChunkAsync", StringComparison.Ordinal),
            "generated server streams must send with the owner-bound item Codec");
        Ensure(!generated.Contains("session.RuntimeContext.Codecs.GetCodec", StringComparison.Ordinal),
            "generated stubs must not resolve response or stream Codecs from the context-global provider per call");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedRoutedProxyShouldRequireContractAwareCustomRuntimeResolution()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcContract]
public interface ICustomRuntimeRouteContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.custom-runtime/v1";
    public override string WireFormatId => "route-custom-runtime-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.custom-runtime/v1\", \"route-custom-runtime-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(RouteAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains(
                "RpcGeneratedCodecResolver.GetProvider(channel.RuntimeContext, typeof(global::ICustomRuntimeRouteContract))",
                StringComparison.Ordinal),
            "the public generated proxy constructor must resolve the exact Contract policy for any IRpcRuntimeContext implementation");
        Ensure(!generated.Contains("channel.RuntimeContext.Codecs.GetCodec", StringComparison.Ordinal),
            "a custom runtime must never silently downgrade a routed proxy to the context-global Codec provider");
        return Task.CompletedTask;
    }

    [Test]
    public Task IntrinsicSelectorAdapterShouldRemainInDefaultProvider()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
public sealed class BaselineSelectorAttribute : System.Attribute
{
}

[BaselineSelector]
public sealed class SelectorPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface ISelectorBaselineContract : SharpLink.Sdk.IService
{
    ValueTask<SelectorPayload> Echo(SelectorPayload value, CancellationToken cancellationToken);
}

public sealed class SelectorAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "selector.baseline/v1";
    public override string WireFormatId => "selector-baseline-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SelectorAdapter), \"selector.baseline/v1\", \"selector-baseline-wire/v1\", SelectorAttributeType = typeof(BaselineSelectorAttribute))]");

        var sources = RunGeneratorAndGetSources(source);
        var generated = string.Join("\n", sources);
        Ensure(generated.Contains("selector.baseline/v1", StringComparison.Ordinal),
            "the selector Adapter must be selected for the payload");
        var manifest = sources.Single(static item => item.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var globalStart = manifest.IndexOf("__codecs =", StringComparison.Ordinal);
        var contractStart = manifest.IndexOf("__contractCodecs =", StringComparison.Ordinal);
        Ensure(globalStart >= 0 && contractStart > globalStart,
            "generated manifest must expose separate global/default and Contract-owned Codec tables");
        var globalSection = manifest.Substring(globalStart, contractStart - globalStart);
        var contractSection = manifest.Substring(contractStart);
        Ensure(globalSection.Contains("new __SharpLinkGeneratedCodec_", StringComparison.Ordinal),
            "an intrinsic selector Adapter factory must stay in the default generated provider so runtime UseCodec<T> can still override it");
        Ensure(!contractSection.Contains("__SharpLinkGeneratedContractPolicyCodec_", StringComparison.Ordinal),
            "an intrinsic selector Adapter must not be mistaken for owner-specific Contract policy");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitAdapterThatOverridesRouteShouldRemainContractOwned()
    {
        var thirdParty = CreateMetadataReference(
            "Vendor.OwnerScopedExplicit",
            "namespace Vendor { public sealed class SharedValue { public int Value { get; set; } } }");
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcContract]
public interface IExplicitOwnerContract : SharpLink.Sdk.IService
{
    ValueTask<Vendor.SharedValue> Echo(Vendor.SharedValue value, CancellationToken cancellationToken);
}

public sealed class ExplicitAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "explicit.owner-a/v1";
    public override string WireFormatId => "explicit-owner-a-wire/v1";
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.owner-fallback/v1";
    public override string WireFormatId => "route-owner-fallback-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ExplicitAdapter), \"explicit.owner-a/v1\", \"explicit-owner-a-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.owner-fallback/v1\", \"route-owner-fallback-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(Vendor.SharedValue), typeof(ExplicitAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var sources = RunGeneratorAndGetSources(source, thirdParty);
        var generated = string.Join("\n", sources);
        Ensure(generated.Contains("explicit.owner-a/v1", StringComparison.Ordinal),
            "the explicit Adapter must win over the matching route");
        Ensure(!generated.Contains("route.owner-fallback/v1\";", StringComparison.Ordinal),
            "the fallback route must not become the selected Codec for the explicitly-bound target");

        var manifest = sources.Single(static item => item.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var globalStart = manifest.IndexOf("__codecs =", StringComparison.Ordinal);
        var contractStart = manifest.IndexOf("__contractCodecs =", StringComparison.Ordinal);
        Ensure(globalStart >= 0 && contractStart > globalStart,
            "generated manifest must expose separate global/default and Contract-owned Codec tables");
        var globalSection = manifest.Substring(globalStart, contractStart - globalStart);
        var contractSection = manifest.Substring(contractStart);
        Ensure(!globalSection.Contains("__SharpLinkGeneratedContractPolicyCodec_", StringComparison.Ordinal),
            "a Contract-reachable explicit selection must not be published into the context-global Codec registry");
        Ensure(contractSection.Contains("__SharpLinkGeneratedContractPolicyCodec_", StringComparison.Ordinal),
            "a Contract-reachable explicit selection must be emitted into the owner-scoped Contract table");
        return Task.CompletedTask;
    }
}
