using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task NestedEnumAndNullableEnumShouldUseNativeRouteCodec()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum NestedMode : short
{
    Zero,
    One
}

public sealed class Envelope
{
    public NestedMode Mode { get; set; }
    public NestedMode? OptionalMode { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface INestedEnumContract : SharpLink.Sdk.IService
{
    ValueTask<Envelope> Echo(Envelope value, CancellationToken cancellationToken);
}

public sealed class EnumAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.nested-enum/v1";
    public override string WireFormatId => "route-nested-enum-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(EnumAdapter), \"route.nested-enum/v1\", \"route-nested-enum-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(EnumAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::NestedMode>()", StringComparison.Ordinal),
            "a DTO-only nested enum selected by a Native route must receive an owner-bound Codec factory");
        Ensure(generated.Contains("CreateCodec<global::NestedMode?>()", StringComparison.Ordinal),
            "a DTO-only nested nullable enum selected by a Native route must receive an owner-bound Codec factory");
        Ensure(generated.Contains("private readonly IRpcCodec<global::NestedMode> __codec_", StringComparison.Ordinal),
            "the Contract-policy Envelope Codec must bind its enum member through IRpcCodec instead of the fixed native member path");
        Ensure(generated.Contains("private readonly IRpcCodec<global::NestedMode?> __codec_", StringComparison.Ordinal),
            "the Contract-policy Envelope Codec must bind its nullable enum member through IRpcCodec instead of the nullable-fixed path");

        var manifest = RunContractGenerator(source).Json;
        var mode = GetEnumPolicyDtoMember(manifest, "Envelope", "Mode");
        var optionalMode = GetEnumPolicyDtoMember(manifest, "Envelope", "OptionalMode");
        Ensure(mode["wireType"]!.GetValue<string>() == "LengthDelimited" &&
               mode["wireFormatId"]!.GetValue<string>() == "route-nested-enum-wire/v1",
            "the nested enum manifest identity must describe the same routed length-delimited Codec path emitted by the DTO Codec");
        Ensure(optionalMode["wireType"]!.GetValue<string>() == "LengthDelimited" &&
               optionalMode["wireFormatId"]!.GetValue<string>() == "route-nested-enum-wire/v1",
            "the nested nullable-enum manifest identity must describe the same routed Codec path emitted by the DTO Codec");
        return Task.CompletedTask;
    }

    [Test]
    public Task DirectAndNestedEnumRouteShouldKeepEmitterAndManifestAligned()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum MixedMode : int
{
    Zero,
    One
}

public sealed class MixedEnvelope
{
    public MixedMode Mode { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IMixedEnumContract : SharpLink.Sdk.IService
{
    ValueTask<MixedMode> EchoMode(MixedMode value, CancellationToken cancellationToken);
    ValueTask<MixedEnvelope> EchoEnvelope(MixedEnvelope value, CancellationToken cancellationToken);
}

public sealed class MixedEnumAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.mixed-enum/v1";
    public override string WireFormatId => "route-mixed-enum-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(MixedEnumAdapter), \"route.mixed-enum/v1\", \"route-mixed-enum-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(MixedEnumAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::MixedMode>()", StringComparison.Ordinal),
            "the mixed direct+nested enum must have one routed Codec selection");
        Ensure(generated.Contains("private readonly IRpcCodec<global::MixedMode> __codec_", StringComparison.Ordinal),
            "the same routed enum selection must be used inside the native DTO shell");

        var manifest = RunContractGenerator(source).Json;
        var member = GetEnumPolicyDtoMember(manifest, "MixedEnvelope", "Mode");
        Ensure(member["wireType"]!.GetValue<string>() == "LengthDelimited" &&
               member["wireFormatId"]!.GetValue<string>() == "route-mixed-enum-wire/v1",
            "mixed direct+nested use must not advertise Adapter identity while emitting the old fixed native member path");
        var enumCodecs = GetEnumPolicyCodecEntries(manifest, "MixedMode");
        Ensure(enumCodecs.Any(codec =>
                codec["kind"]!.GetValue<string>() == "Adapter" &&
                codec["wireFormatId"]!.GetValue<string>() == "route-mixed-enum-wire/v1"),
            "the final manifest Codec identity for the mixed enum must remain the routed Adapter");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitEnumBindingsShouldOverrideNativeRoute()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum ExplicitMode : byte
{
    Zero,
    One
}

[SharpLink.Sdk.RpcContract]
public interface IExplicitEnumContract : SharpLink.Sdk.IService
{
    ValueTask<ExplicitMode> Echo(ExplicitMode value, CancellationToken cancellationToken);
    ValueTask<ExplicitMode?> EchoNullable(ExplicitMode? value, CancellationToken cancellationToken);
}

public sealed class RouteEnumAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.explicit-enum/a";
    public override string WireFormatId => "route-explicit-enum-a/v1";
}

public sealed class ExplicitEnumAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.explicit-enum/b";
    public override string WireFormatId => "route-explicit-enum-b/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteEnumAdapter), \"route.explicit-enum/a\", \"route-explicit-enum-a/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ExplicitEnumAdapter), \"route.explicit-enum/b\", \"route-explicit-enum-b/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(RouteEnumAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(ExplicitMode), typeof(ExplicitEnumAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(ExplicitMode?), typeof(ExplicitEnumAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("internal static readonly IRpcCodecAdapter Instance = new global::ExplicitEnumAdapter();", StringComparison.Ordinal),
            "assembly-level explicit enum binding must be accepted and win over the broader Native route");
        Ensure(!generated.Contains("internal static readonly IRpcCodecAdapter Instance = new global::RouteEnumAdapter();", StringComparison.Ordinal),
            "the broader route Adapter must not capture enum or nullable-enum targets with explicit bindings");

        var manifest = RunContractGenerator(source).Json;
        var entries = GetEnumPolicyCodecEntries(manifest, "ExplicitMode").ToArray();
        Ensure(entries.Length >= 2,
            "the regression must describe both enum and nullable-enum final Codec identities");
        Ensure(entries.All(codec =>
                codec["kind"]!.GetValue<string>() == "Adapter" &&
                codec["wireFormatId"]!.GetValue<string>() == "route-explicit-enum-b/v1"),
            "explicit enum/nullable-enum bindings must outrank the Native route in the emitted manifest as well as generated factories");
        return Task.CompletedTask;
    }

    private static System.Text.Json.Nodes.JsonObject GetEnumPolicyDtoMember(
        string json,
        string dtoName,
        string memberName)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var dto = root["dtos"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(item => string.Equals(item["name"]!.GetValue<string>(), dtoName, StringComparison.Ordinal));
        return dto["members"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(item => string.Equals(item["name"]!.GetValue<string>(), memberName, StringComparison.Ordinal));
    }

    private static System.Collections.Generic.IEnumerable<System.Text.Json.Nodes.JsonObject> GetEnumPolicyCodecEntries(
        string json,
        string typeName)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        return root["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Where(item => item["type"]!.GetValue<string>().Contains(typeName, StringComparison.Ordinal));
    }
}
