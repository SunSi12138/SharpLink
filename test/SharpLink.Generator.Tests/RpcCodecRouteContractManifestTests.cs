using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task RouteShouldNotChangeFrameworkPrimitiveRequestFraming()
    {
        const string contract = """
[SharpLink.Sdk.RpcContract]
public interface IFixedRouteContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.fixed-framing/v1";
    public override string WireFormatId => "route-fixed/v1";
}
""";
        const string registration =
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.fixed-framing/v1\", \"route-fixed/v1\")]";
        var baselineSource = AddAssemblyAttributes(BuildRouteSource(contract), registration);
        var routedSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registration,
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(RouteAdapter))]");

        var baseline = RunContractGenerator(baselineSource);
        var routed = RunContractGenerator(routedSource);
        var baselineRequest = GetFirstRequestValue(baseline.Json);
        var routedRequest = GetFirstRequestValue(routed.Json);

        Ensure(baselineRequest["wireType"]!.GetValue<string>() == "Fixed4" &&
               routedRequest["wireType"]!.GetValue<string>() == "Fixed4",
            "framework primitive int must retain the fixed SharpLink framing even under an All route");
        Ensure(routedRequest["wireFormatId"]!.GetValue<string>() == "sharplink-native/v1",
            "framework primitive int must retain the fixed SharpLink wire identity");

        var compared = RunContractGenerator(routedSource, baseline.Json);
        Ensure(!compared.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"adding a route must not mutate a framework primitive wire contract. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task RouteShouldNotChangeFrameworkEnumRequestFraming()
    {
        const string contract = """
public enum RouteState : int
{
    Ready = 1
}

[SharpLink.Sdk.RpcContract]
public interface IEnumRouteContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(RouteState value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.enum-framing/v1";
    public override string WireFormatId => "route-enum/v1";
}
""";
        const string registration =
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.enum-framing/v1\", \"route-enum/v1\")]";
        var baselineSource = AddAssemblyAttributes(BuildRouteSource(contract), registration);
        var routedSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registration,
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(RouteAdapter))]");

        var baselineRequest = GetFirstRequestValue(RunContractGenerator(baselineSource).Json);
        var routedRequest = GetFirstRequestValue(RunContractGenerator(routedSource).Json);

        Ensure(baselineRequest["wireType"]!.GetValue<string>() == "Fixed4" &&
               routedRequest["wireType"]!.GetValue<string>() == "Fixed4",
            "framework enum must retain its fixed SharpLink framing under an All route");
        Ensure(routedRequest["wireFormatId"]!.GetValue<string>() == "sharplink-native/v1",
            "framework enum must retain the fixed SharpLink wire identity");
        return Task.CompletedTask;
    }

    [Test]
    public Task ManagedRouteOnDtoShouldBeWireBreakWhenWireFormatIdIsUnchanged()
    {
        const string contract = """
public sealed class Payload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IDtoRouteContract : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.dto-kind/v1";
    public override string WireFormatId => "sharplink-native/v1";
}
""";
        const string registration =
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.dto-kind/v1\", \"sharplink-native/v1\")]";
        var baselineSource = AddAssemblyAttributes(BuildRouteSource(contract), registration);
        var routedSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registration,
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var baseline = RunContractGenerator(baselineSource);
        var compared = RunContractGenerator(routedSource, baseline.Json);
        Ensure(compared.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"changing a configurable DTO from generated Codec to routed Adapter must be a wire break even with the same WireFormatId. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ManagedRouteOnCollectionShouldBeWireBreakWhenWireFormatIdIsUnchanged()
    {
        const string contract = """
public sealed class ManagedItem
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface ICollectionRouteContract : SharpLink.Sdk.IService
{
    ValueTask<System.Collections.Generic.List<ManagedItem>> Echo(
        System.Collections.Generic.List<ManagedItem> value,
        CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.collection-kind/v1";
    public override string WireFormatId => "sharplink-native/v1";
}
""";
        const string registration =
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.collection-kind/v1\", \"sharplink-native/v1\")]";
        var baselineSource = AddAssemblyAttributes(BuildRouteSource(contract), registration);
        var routedSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registration,
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var baseline = RunContractGenerator(baselineSource);
        var compared = RunContractGenerator(routedSource, baseline.Json);
        Ensure(compared.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"changing a configurable List Codec to a routed Adapter must be a wire break even with the same WireFormatId. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task NestedAdapterIdentityChangeShouldBeWireBreakWhenWireFormatIdIsUnchanged()
    {
        const string contract = """
public sealed class Inner
{
    public int Value { get; set; }
}

public sealed class Envelope
{
    public System.Collections.Generic.List<Inner> Items { get; set; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface INestedCodecIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<Envelope> Echo(Envelope value, CancellationToken cancellationToken);
}

public sealed class AdapterA : TestRouteAdapterBase
{
    public override string AdapterId => "route.nested-a/v1";
    public override string WireFormatId => "route.nested-wire/v1";
}

public sealed class AdapterB : TestRouteAdapterBase
{
    public override string AdapterId => "route.nested-b/v1";
    public override string WireFormatId => "route.nested-wire/v1";
}
""";
        const string registrations = """
[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterA), "route.nested-a/v1", "route.nested-wire/v1")]
[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterB), "route.nested-b/v1", "route.nested-wire/v1")]
""";
        var baselineSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registrations,
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(Inner), typeof(AdapterA))]");
        var changedSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registrations,
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(Inner), typeof(AdapterB))]");

        var baseline = RunContractGenerator(baselineSource);
        var compared = RunContractGenerator(changedSource, baseline.Json);
        Ensure(compared.Diagnostics.Any(static diagnostic =>
                diagnostic.Id == "SHARPLINK030" &&
                diagnostic.GetMessage().Contains("nested Codec selection changed", StringComparison.Ordinal)),
            $"changing a nested Adapter identity must be a wire break even when WireFormatId is unchanged. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ImplicitUnsafeBlitAndAdapterTransitionsShouldBeWireBreaks()
    {
        const string contract = """
public struct BlitPayload
{
    public int X;
    public int Y;
}

[SharpLink.Sdk.RpcContract]
public interface IBlitCodecIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<BlitPayload> Echo(BlitPayload value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.blit-identity/v1";
    public override string WireFormatId => "sharplink-native/v1";
}
""";
        const string registration =
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.blit-identity/v1\", \"sharplink-native/v1\")]";
        var implicitSource = AddAssemblyAttributes(BuildRouteSource(contract), registration);
        var adapterSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registration,
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Unmanaged, typeof(RouteAdapter))]");

        var implicitManifest = RunContractGenerator(implicitSource);
        var adapterManifest = RunContractGenerator(adapterSource);
        var implicitKinds = System.Text.Json.Nodes.JsonNode.Parse(implicitManifest.Json)!["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Where(static item => item["type"]!.GetValue<string>().Contains("BlitPayload", StringComparison.Ordinal))
            .Select(static item => item["kind"]!.GetValue<string>())
            .ToArray();
        Ensure(implicitKinds.Contains("UnsafeBlit", StringComparer.Ordinal),
            "format-2 compatibility manifests must explicitly record the implicit UnsafeBlit selection");

        var toAdapter = RunContractGenerator(adapterSource, implicitManifest.Json);
        Ensure(toAdapter.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"implicit UnsafeBlit -> Adapter must be a wire break even with a stable WireFormatId. Diagnostics: {FormatDiagnostics(toAdapter.Diagnostics)}");

        var toImplicit = RunContractGenerator(implicitSource, adapterManifest.Json);
        Ensure(toImplicit.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"Adapter -> implicit UnsafeBlit must be a wire break even with a stable WireFormatId. Diagnostics: {FormatDiagnostics(toImplicit.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task LegacyCollectionBaselineWithoutCodecKindShouldRequireRegeneration()
    {
        const string contract = """
public sealed class ManagedItem
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface ILegacyCollectionContract : SharpLink.Sdk.IService
{
    ValueTask<System.Collections.Generic.List<ManagedItem>> Echo(
        System.Collections.Generic.List<ManagedItem> value,
        CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.legacy-collection/v1";
    public override string WireFormatId => "sharplink-native/v1";
}
""";
        const string registration =
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.legacy-collection/v1\", \"sharplink-native/v1\")]";
        var baselineSource = AddAssemblyAttributes(BuildRouteSource(contract), registration);
        var routedSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registration,
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var baselineNode = System.Text.Json.Nodes.JsonNode.Parse(RunContractGenerator(baselineSource).Json)!.AsObject();
        baselineNode["version"] = 1;
        foreach (var codec in baselineNode["codecs"]!.AsArray().Select(static item => item!.AsObject()))
        {
            codec.Remove("kind");
            codec.Remove("schemaId");
        }
        var compared = RunContractGenerator(routedSource, baselineNode.ToJsonString());
        Ensure(compared.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK025"),
            $"legacy format-1 baselines without codec kind/schema identity must require regeneration. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    private static System.Text.Json.Nodes.JsonObject GetFirstRequestValue(string json)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        return root["contracts"]!.AsArray()[0]!.AsObject()["methods"]!.AsArray()[0]!
            .AsObject()["request"]!.AsArray()[0]!.AsObject();
    }
}
