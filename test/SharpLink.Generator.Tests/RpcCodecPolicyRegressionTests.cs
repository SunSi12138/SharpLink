using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ExplicitBindingMatchingSelectorShouldStillBeContractOwned()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
public sealed class SelectorAttribute : System.Attribute { }

[Selector]
[SharpLink.Sdk.RpcCodecAdapter(typeof(SelectorAdapter))]
public sealed class SelectorPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface ISelectorExplicitContract : SharpLink.Sdk.IService
{
    ValueTask<SelectorPayload> Echo(SelectorPayload value, CancellationToken cancellationToken);
}

public sealed class SelectorAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "selector.explicit/v1";
    public override string WireFormatId => "selector-explicit-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SelectorAdapter), \"selector.explicit/v1\", \"selector-explicit-wire/v1\", SelectorAttributeType = typeof(SelectorAttribute))]");

        var sources = RunGeneratorAndGetSources(source);
        var generated = string.Join("\n", sources);
        Ensure(generated.Split("TargetType => typeof(global::SelectorPayload)", StringSplitOptions.None).Length - 1 == 1,
            "selector default and identical explicit Contract policy should reuse one generated factory implementation");

        var manifest = sources.Single(static item => item.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var globalStart = manifest.IndexOf("__codecs =", StringComparison.Ordinal);
        var contractStart = manifest.IndexOf("__contractCodecs =", StringComparison.Ordinal);
        Ensure(globalStart >= 0 && contractStart > globalStart,
            "generated manifest must expose separate default and Contract-owned Codec tables");
        var globalSection = manifest.Substring(globalStart, contractStart - globalStart);
        var contractSection = manifest.Substring(contractStart);
        Ensure(globalSection.Contains("new __SharpLinkGeneratedCodec_", StringComparison.Ordinal) &&
               contractSection.Contains("new __SharpLinkGeneratedCodec_", StringComparison.Ordinal),
            "explicit provenance must place the shared selector factory in ContractCodecs as well as the default table");
        Ensure(!contractSection.Contains("__SharpLinkGeneratedContractPolicyCodec_", StringComparison.Ordinal),
            "definition-identical policy should not manufacture a duplicate owner implementation type");
        return Task.CompletedTask;
    }

    [Test]
    public Task AllRouteShouldNotCaptureFrameworkEnum()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum RouteEnum : short
{
    Zero,
    One
}

[SharpLink.Sdk.RpcContract]
public interface IEnumRouteContract : SharpLink.Sdk.IService
{
    ValueTask<RouteEnum> Echo(RouteEnum value, CancellationToken cancellationToken);
}

public sealed class EnumAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.enum/v1";
    public override string WireFormatId => "route-enum-safe/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(EnumAdapter), \"route.enum/v1\", \"route-enum-safe/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(EnumAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id is "SHARPLINK043" or "SHARPLINK046"),
            "a valid All route should remain valid while skipping framework enum payloads");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("CreateCodec<global::RouteEnum>()", StringComparison.Ordinal),
            "framework enum must not become a configurable route target");
        return Task.CompletedTask;
    }
}
