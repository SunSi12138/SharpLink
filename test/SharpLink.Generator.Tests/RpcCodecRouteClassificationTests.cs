using System;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task NativeRouteShouldClassifyCollectionsAsNativeButLeaveEnumsUnmanaged()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum NativeMode : byte
{
    First,
    Second
}

public sealed class NativeItem
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface INativeCollectionRouteContract : SharpLink.Sdk.IService
{
    ValueTask<System.Collections.Generic.List<NativeItem>> Echo(
        System.Collections.Generic.List<NativeItem> values,
        NativeMode mode,
        CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.native-aggregate/v1";
    public override string WireFormatId => "route-native-aggregate-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.native-aggregate/v1\", \"route-native-aggregate-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(RouteAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains(
                "CreateCodec<global::System.Collections.Generic.List<global::NativeItem>>()",
                StringComparison.Ordinal),
            "generated collection paths must classify as Native and bind to the route");
        Ensure(!generated.Contains("CreateCodec<global::NativeMode>()", StringComparison.Ordinal),
            "a Native route must not capture enum paths that use the unmanaged fallback domain");
        Ensure(!generated.Contains("__codec_mode = codecs.GetCodec<global::NativeMode>();", StringComparison.Ordinal),
            "an enum without an Unmanaged route must keep its existing non-routed request path");
        return Task.CompletedTask;
    }
}
