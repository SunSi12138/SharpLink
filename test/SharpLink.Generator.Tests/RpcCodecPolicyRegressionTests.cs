using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task DirectCodecFactoryShouldEmitExplicitRuntimeKind()
    {
        var source = BuildDirectCodecSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(DirectPayloadCodec), WireFormatId = "direct.payload/v1")]
public sealed class DirectPayload { }
public sealed class DirectPayloadCodec : SharpLink.Abstractions.IRpcCodec<DirectPayload> { }

[SharpLink.Sdk.RpcContract]
public interface IDirectPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<DirectPayload> Echo(DirectPayload value, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains(
                "public RpcGeneratedCodecFactoryKind Kind => RpcGeneratedCodecFactoryKind.Direct;",
                StringComparison.Ordinal),
            "generated direct Codec factories must carry an explicit runtime-discriminable kind");
        return Task.CompletedTask;
    }

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

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("CreateCodec<global::RouteEnum>()", StringComparison.Ordinal),
            "framework enum must not become a configurable route target");
        var root = System.Text.Json.Nodes.JsonNode.Parse(RunContractGenerator(source).Json)!.AsObject();
        var enumCodec = root["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(item => item["type"]!.GetValue<string>().Contains("RouteEnum", StringComparison.Ordinal));
        Ensure(enumCodec["kind"]!.GetValue<string>() == "Native" &&
               enumCodec["wireFormatId"]!.GetValue<string>() == "sharplink-native/v1",
            "framework enum must retain the fixed SharpLink native identity");
        return Task.CompletedTask;
    }

    [Test]
    public Task DirectCodecImplementationChangeWithSameWireFormatShouldBeWireBreak()
    {
        var baselineSource = BuildDirectCodecSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(PayloadCodecA), WireFormatId = "payload.same-wire/v1")]
public sealed class Payload { public int Value { get; set; } }
public sealed class PayloadCodecA : SharpLink.Abstractions.IRpcCodec<Payload> { }
public sealed class PayloadCodecB : SharpLink.Abstractions.IRpcCodec<Payload> { }

[SharpLink.Sdk.RpcContract]
public interface IPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""");
        var currentSource = BuildDirectCodecSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(PayloadCodecB), WireFormatId = "payload.same-wire/v1")]
public sealed class Payload { public int Value { get; set; } }
public sealed class PayloadCodecA : SharpLink.Abstractions.IRpcCodec<Payload> { }
public sealed class PayloadCodecB : SharpLink.Abstractions.IRpcCodec<Payload> { }

[SharpLink.Sdk.RpcContract]
public interface IPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""");

        var baseline = RunContractGenerator(baselineSource);
        var compared = RunContractGenerator(currentSource, baseline.Json);
        Ensure(compared.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"same-Kind direct Codec implementation changes must compare SchemaId even when WireFormatId is unchanged. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterImplementationChangeWithSameWireFormatShouldChangeSchemaIdentity()
    {
        const string contract = """
public sealed class AdapterPayload { public int Value { get; set; } }

[SharpLink.Sdk.RpcContract]
public interface IAdapterPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<AdapterPayload> Echo(AdapterPayload value, CancellationToken cancellationToken);
}

public sealed class AdapterA : TestRouteAdapterBase
{
    public override string AdapterId => "adapter.same-wire/a";
    public override string WireFormatId => "adapter.same-wire/v1";
}
public sealed class AdapterB : TestRouteAdapterBase
{
    public override string AdapterId => "adapter.same-wire/b";
    public override string WireFormatId => "adapter.same-wire/v1";
}
""";
        const string registrationA =
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterA), \"adapter.same-wire/a\", \"adapter.same-wire/v1\")]";
        const string registrationB =
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterB), \"adapter.same-wire/b\", \"adapter.same-wire/v1\")]";
        var baselineSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registrationA,
            registrationB,
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(AdapterPayload), typeof(AdapterA))]");
        var currentSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registrationA,
            registrationB,
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(AdapterPayload), typeof(AdapterB))]");

        var baseline = RunContractGenerator(baselineSource);
        var compared = RunContractGenerator(currentSource, baseline.Json);
        Ensure(compared.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"Adapter identity changes must alter format-2 SchemaId even when Kind and WireFormatId are unchanged. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }
}
