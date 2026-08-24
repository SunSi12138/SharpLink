using System;
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
}
