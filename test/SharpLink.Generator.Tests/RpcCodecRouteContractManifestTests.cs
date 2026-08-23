using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task NativeRouteOnFixedRequestShouldAdvertiseLengthDelimitedOuterFraming()
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
    public override string WireFormatId => "sharplink-native/v1";
}
""";
        const string registration =
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.fixed-framing/v1\", \"sharplink-native/v1\")]";
        var baselineSource = AddAssemblyAttributes(BuildRouteSource(contract), registration);
        var routedSource = AddAssemblyAttributes(
            BuildRouteSource(contract),
            registration,
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(RouteAdapter))]");

        var baseline = RunContractGenerator(baselineSource);
        var routed = RunContractGenerator(routedSource);
        var baselineRequest = GetFirstRequestValue(baseline.Json);
        var routedRequest = GetFirstRequestValue(routed.Json);

        Ensure(baselineRequest["wireType"]!.GetValue<string>() == "Fixed4",
            "an unrouted int request must retain the inline Fixed4 field path");
        Ensure(routedRequest["wireType"]!.GetValue<string>() == "LengthDelimited",
            "a routed int request must advertise the generated length-delimited field path");
        Ensure(routedRequest["wireFormatId"]!.GetValue<string>() == "sharplink-native/v1",
            "the regression must keep the inner wire-format identity stable so framing is the detected break");

        var compared = RunContractGenerator(routedSource, baseline.Json);
        Ensure(compared.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"adding a Native route to a fixed request must be a baseline wire break. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task NativeRouteOnDtoShouldBeWireBreakWhenWireFormatIdIsUnchanged()
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
  "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(RouteAdapter))]");

        var baseline = RunContractGenerator(baselineSource);
        var compared = RunContractGenerator(routedSource, baseline.Json);
        Ensure(compared.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
  $"changing a direct payload from native DTO to routed adapter must be a wire break even with the same WireFormatId. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    private static System.Text.Json.Nodes.JsonObject GetFirstRequestValue(string json)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        return root["contracts"]!.AsArray()[0]!.AsObject()["methods"]!.AsArray()[0]!
            .AsObject()["request"]!.AsArray()[0]!.AsObject();
    }
}
