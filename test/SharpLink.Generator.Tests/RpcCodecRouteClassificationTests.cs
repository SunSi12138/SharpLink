using System;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ManagedRouteShouldCoverCollectionsAndDtosButNotFrameworkEnums()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum FixedMode : byte
{
    First,
    Second
}

public sealed class ManagedItem
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IManagedCollectionRouteContract : SharpLink.Sdk.IService
{
    ValueTask<System.Collections.Generic.List<ManagedItem>> Echo(
        System.Collections.Generic.List<ManagedItem> values,
        FixedMode mode,
        CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.managed-aggregate/v1";
    public override string WireFormatId => "route-managed-aggregate-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.managed-aggregate/v1\", \"route-managed-aggregate-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains(
                "CreateCodec<global::System.Collections.Generic.List<global::ManagedItem>>()",
                StringComparison.Ordinal),
            "ordinary collections remain configurable and must be eligible for the Managed route");
        Ensure(!generated.Contains("CreateCodec<global::FixedMode>()", StringComparison.Ordinal),
            "framework enums are fixed wire primitives and must not be routed");
        return Task.CompletedTask;
    }
}
