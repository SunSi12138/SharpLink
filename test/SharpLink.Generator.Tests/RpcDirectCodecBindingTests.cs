using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task SourceOwnedDirectCodecShouldBindWithoutRuntimeUseCodec()
    {
        var source = BuildDirectCodecSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(MoneyCodec), WireFormatId = "money/v1")]
public readonly record struct Money(decimal Value);

public sealed class MoneyCodec : SharpLink.Abstractions.IRpcCodec<Money> { }

[SharpLink.Sdk.RpcContract]
public interface IMoneyContract : SharpLink.Sdk.IService
{
    ValueTask<Money> Echo(Money value, CancellationToken cancellationToken);
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error),
            "valid source-owned direct Codec binding must not report generator errors");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("return new global::MoneyCodec();", StringComparison.Ordinal),
            "direct Codec factory must instantiate the selected closed Codec at registration time");
        Ensure(generated.Contains("money/v1", StringComparison.Ordinal),
            "direct Codec WireFormatId must enter generated wire metadata");
        return Task.CompletedTask;
    }

    [Test]
    public Task AssemblyDirectCodecShouldBindThirdPartyClosedType()
    {
        var thirdParty = CreateMetadataReference(
            "Vendor.DirectCodec",
            "namespace Vendor { public sealed class ExternalValue { public int Value { get; set; } } }");
        var source = AddAssemblyAttribute(BuildDirectCodecSource("""
public sealed class ExternalValueCodec : SharpLink.Abstractions.IRpcCodec<Vendor.ExternalValue> { }

[SharpLink.Sdk.RpcContract]
public interface IExternalValueContract : SharpLink.Sdk.IService
{
    ValueTask<Vendor.ExternalValue> Echo(Vendor.ExternalValue value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(Vendor.ExternalValue), typeof(ExternalValueCodec), WireFormatId = \"vendor.external-value/v1\")]");

        var diagnostics = RunGenerator(source, thirdParty);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error),
            "valid assembly direct Codec binding for a third-party closed type must compile in generator analysis");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source, thirdParty));
        Ensure(generated.Contains("TargetType => typeof(global::Vendor.ExternalValue)", StringComparison.Ordinal),
            "third-party direct binding must emit a factory for the exact closed target");
        Ensure(generated.Contains("return new global::ExternalValueCodec();", StringComparison.Ordinal),
            "third-party direct binding must instantiate the selected Codec directly");
        return Task.CompletedTask;
    }

    [Test]
    public Task DirectCodecShouldRequireStableWireFormatId()
    {
        var source = BuildDirectCodecSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(MoneyCodec))]
public readonly record struct Money(decimal Value);
public sealed class MoneyCodec : SharpLink.Abstractions.IRpcCodec<Money> { }

[SharpLink.Sdk.RpcContract]
public interface IMoneyContract : SharpLink.Sdk.IService
{
    ValueTask<Money> Echo(Money value, CancellationToken cancellationToken);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK046", "requires a non-empty stable ASCII WireFormatId");
        return Task.CompletedTask;
    }

    [Test]
    public Task MismatchedAndOpenDirectCodecTypesShouldFailAtCompileTime()
    {
        var mismatched = BuildDirectCodecSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(OtherCodec), WireFormatId = "money/v1")]
public readonly record struct Money(decimal Value);
public readonly record struct Other(decimal Value);
public sealed class OtherCodec : SharpLink.Abstractions.IRpcCodec<Other> { }

[SharpLink.Sdk.RpcContract]
public interface IMoneyContract : SharpLink.Sdk.IService
{
    ValueTask<Money> Echo(Money value, CancellationToken cancellationToken);
}
""");
        EnsureHasRuleContaining(mismatched, "SHARPLINK043", "IRpcCodec<global::Money>");

        var open = BuildDirectCodecSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(GenericCodec<>), WireFormatId = "money/v1")]
public readonly record struct Money(decimal Value);
public sealed class GenericCodec<T> : SharpLink.Abstractions.IRpcCodec<T> { }

[SharpLink.Sdk.RpcContract]
public interface IMoneyContract : SharpLink.Sdk.IService
{
    ValueTask<Money> Echo(Money value, CancellationToken cancellationToken);
}
""");
        EnsureHasRule(open, "SHARPLINK043");
        return Task.CompletedTask;
    }

    [Test]
    public Task DirectCodecShouldOverrideAssemblyRouteAtExplicitTier()
    {
        var source = AddAssemblyAttributes(BuildDirectCodecRouteSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(GraphCodec), WireFormatId = "graph.direct/v1")]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}
public sealed class GraphCodec : SharpLink.Abstractions.IRpcCodec<Graph> { }

[SharpLink.Sdk.RpcContract]
public interface IGraphContract : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : DirectCodecRouteAdapterBase
{
    public override string AdapterId => "route.graph/v1";
    public override string WireFormatId => "route-graph-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.graph/v1\", \"route-graph-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("return new global::GraphCodec();", StringComparison.Ordinal),
            "explicit direct Codec must win over a matching assembly route");
        Ensure(!generated.Contains("CreateCodec<global::Graph>()", StringComparison.Ordinal),
            "route Adapter must not replace an explicit direct Codec selection");
        return Task.CompletedTask;
    }

    [Test]
    public Task DualRoleExplicitDirectCodecShouldRemainContractOwnedDelta()
    {
        var source = BuildDirectCodecSource("""
[SharpLink.Sdk.RpcSerializable]
[SharpLink.Sdk.RpcCodecAdapter(typeof(PayloadCodec), WireFormatId = "payload.direct/v1")]
public sealed class Payload
{
    public int Value { get; set; }
}
public sealed class PayloadCodec : SharpLink.Abstractions.IRpcCodec<Payload> { }

[SharpLink.Sdk.RpcContract]
public interface IDualRoleDirectContract : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""");

        var sources = RunGeneratorAndGetSources(source);
        var generated = string.Join("\n", sources);
        Ensure(generated.Split("TargetType => typeof(global::Payload)", StringSplitOptions.None).Length - 1 == 2,
            "dual-role payload must keep one global/default factory and one Contract-owned direct factory");
        Ensure(generated.Contains("return new global::PayloadCodec();", StringComparison.Ordinal),
            "Contract-owned factory must instantiate the explicit direct Codec");
        Ensure(generated.Contains("__SharpLinkGeneratedContractPolicyCodec_", StringComparison.Ordinal),
            "Contract policy factory must have a distinct generated type from the global/default Codec");
        var manifest = sources.Single(static item => item.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        Ensure(manifest.Contains("ContractCodecs => __readOnlyContractCodecs", StringComparison.Ordinal),
            "dual-role explicit binding must be emitted in the Contract-owned Codec table");
        return Task.CompletedTask;
    }

    private static string BuildDirectCodecSource(string contract)
        => BuildSource(contract).Replace(
            "        public RpcCodecAdapterAttribute(Type targetType, Type adapterType) { }\n    }",
            "        public RpcCodecAdapterAttribute(Type targetType, Type adapterType) { }\n        public string? WireFormatId { get; set; }\n    }",
            StringComparison.Ordinal);

    private static string BuildDirectCodecRouteSource(string contract)
        => BuildDirectCodecSource(contract) + """

namespace SharpLink.Sdk
{
    [Flags]
    public enum RpcCodecScope
    {
        None = 0,
        Managed = 1 << 0,
        Unmanaged = 1 << 1,
        Native = 1 << 2,
        All = Managed | Unmanaged | Native
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
    public sealed class RpcCodecRouteAttribute : Attribute
    {
        public RpcCodecRouteAttribute(RpcCodecScope scope, Type adapterType) { }
    }
}

public abstract class DirectCodecRouteAdapterBase : SharpLink.Abstractions.IRpcCodecAdapter
{
    public abstract string AdapterId { get; }
    public abstract string WireFormatId { get; }
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
""";
}
