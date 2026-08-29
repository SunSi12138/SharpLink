using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ManagedRouteShouldOverrideGeneratedDtoButNotFrameworkPrimitive()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public sealed class ManagedPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IManagedRouteContract : SharpLink.Sdk.IService
{
    ValueTask<ManagedPayload> Echo(int id, ManagedPayload value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.managed/v1";
    public override string WireFormatId => "route-managed-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.managed/v1\", \"route-managed-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("CreateCodec<int>()", StringComparison.Ordinal),
            "framework primitive int must remain on the fixed SharpLink wire path");
        Ensure(generated.Contains("CreateCodec<global::ManagedPayload>()", StringComparison.Ordinal),
            "ordinary DTOs remain configurable and must be eligible for a Managed route");
        Ensure(!generated.Contains("__codec_id = codecs.GetCodec<int>();", StringComparison.Ordinal),
            "fixed framework primitive request fields must remain on the inline native path");
        Ensure(generated.Contains("route-managed-wire/v1", StringComparison.Ordinal),
            "the selected configurable DTO route identity must enter generated metadata");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnmanagedRouteShouldOverrideUnsafeBlitFallbackOnlyWhenDeclared()
    {
        const string contract = """
public readonly struct Point
{
    public int X { get; init; }
    public int Y { get; init; }
}

[SharpLink.Sdk.RpcContract]
public interface IPointRouteContract : SharpLink.Sdk.IService
{
    ValueTask<Point> Echo(Point value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.unmanaged/v1";
    public override string WireFormatId => "route-unmanaged-wire/v1";
}
""";
        var withoutRoute = string.Join("\n", RunGeneratorAndGetSources(BuildRouteSource(contract)));
        Ensure(!withoutRoute.Contains("CreateCodec<global::Point>()", StringComparison.Ordinal),
            "without a route a custom unmanaged payload must retain the UnsafeBlit fallback");

        var routed = AddAssemblyAttributes(BuildRouteSource(contract),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.unmanaged/v1\", \"route-unmanaged-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Unmanaged, typeof(RouteAdapter))]");
        var generated = string.Join("\n", RunGeneratorAndGetSources(routed));
        Ensure(generated.Contains("CreateCodec<global::Point>()", StringComparison.Ordinal),
            "Unmanaged route must override the UnsafeBlit fallback for a user-defined struct");
        return Task.CompletedTask;
    }

    [Test]
    public Task ManagedRouteShouldHandleCyclicAndThirdPartyManagedPayloads()
    {
        var thirdParty = CreateMetadataReference(
            "ThirdParty.Managed",
            "namespace Vendor { public sealed class ExternalGraph { public string Name { get; set; } = string.Empty; } }");
        var source = AddAssemblyAttributes(BuildRouteSource("""
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IManagedRouteContract : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value, CancellationToken cancellationToken);
    ValueTask<Vendor.ExternalGraph> EchoExternal(Vendor.ExternalGraph value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.managed/v1";
    public override string WireFormatId => "route-managed-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.managed/v1\", \"route-managed-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source, thirdParty);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id is "SHARPLINK009" or "SHARPLINK010"),
            "Managed route must run before unsupported/cycle rejection");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source, thirdParty));
        Ensure(generated.Contains("CreateCodec<global::Graph>()", StringComparison.Ordinal),
            "Managed route must handle a cyclic owner payload");
        Ensure(generated.Contains("CreateCodec<global::Vendor.ExternalGraph>()", StringComparison.Ordinal),
            "Managed route must handle a direct third-party managed payload");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnmanagedRouteShouldHandleDirectThirdPartyUnmanagedPayload()
    {
        var thirdParty = CreateMetadataReference(
            "ThirdParty.Unmanaged",
            "namespace Vendor { public struct ExternalPoint { public int X; public int Y; } }");
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcContract]
public interface IExternalPointContract : SharpLink.Sdk.IService
{
    ValueTask<Vendor.ExternalPoint> Echo(Vendor.ExternalPoint value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.external-unmanaged/v1";
    public override string WireFormatId => "route-external-unmanaged-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.external-unmanaged/v1\", \"route-external-unmanaged-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Unmanaged, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source, thirdParty);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK009"),
            "third-party unmanaged payload must be classified before unsupported rejection");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source, thirdParty));
        Ensure(generated.Contains("CreateCodec<global::Vendor.ExternalPoint>()", StringComparison.Ordinal),
            "Unmanaged route must bind the third-party closed payload type");
        return Task.CompletedTask;
    }

    [Test]
    public Task AllRouteShouldHandleIndirectThirdPartyPayloadWithoutPerTypeBinding()
    {
        var thirdParty = CreateMetadataReference(
            "ThirdParty.Indirect",
            """
namespace Vendor
{
    public sealed class ExternalGraph { public string Name { get; set; } = string.Empty; }
    public struct ExternalPoint { public int X; public int Y; }
}
""");
        var source = AddAssemblyAttributes(BuildRouteSource("""
public sealed class Envelope
{
    public Vendor.ExternalGraph Graph { get; set; } = new();
    public Vendor.ExternalPoint Point { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IEnvelopeContract : SharpLink.Sdk.IService
{
    ValueTask<Envelope> Echo(Envelope value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.all/v1";
    public override string WireFormatId => "route-all-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.all/v1\", \"route-all-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source, thirdParty);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id is "SHARPLINK009" or "SHARPLINK010"),
            "All route must make an indirect third-party payload graph compilable without per-type bindings");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source, thirdParty));
        Ensure(generated.Contains("CreateCodec<global::Envelope>()", StringComparison.Ordinal),
            "All route must select the adapter for the closed graph root");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitPerTypeAdapterShouldOverrideAssemblyRoute()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(ExplicitAdapter))]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IExplicitRouteContract : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value, CancellationToken cancellationToken);
}

public sealed class ExplicitAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "explicit/v1";
    public override string WireFormatId => "explicit-wire/v1";
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route/v1";
    public override string WireFormatId => "route-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ExplicitAdapter), \"explicit/v1\", \"explicit-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route/v1\", \"route-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("explicit-wire/v1", StringComparison.Ordinal),
            "explicit per-type adapter must win over the assembly route");
        Ensure(!generated.Contains("route-wire/v1", StringComparison.Ordinal),
            "the losing route must not enter the generated manifest for the explicitly bound type");
        var manifest = RunGeneratorAndGetSources(source).Single(static item =>
            item.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var globalSection = manifest.Substring(
            manifest.IndexOf("__codecs =", StringComparison.Ordinal),
            manifest.IndexOf("__contractCodecs =", StringComparison.Ordinal) - manifest.IndexOf("__codecs =", StringComparison.Ordinal));
        Ensure(!globalSection.Contains("new __SharpLinkGeneratedCodec_", StringComparison.Ordinal),
            "a Contract-reachable explicit binding must not be published to the global Codec registry");
        EnsureDoesNotHaveRule(source, "SHARPLINK045");
        return Task.CompletedTask;
    }

    [Test]
    public Task OverlappingRouteScopesShouldFailWithoutDeclarationOrderWinner()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public sealed class Graph { public Graph? Parent { get; set; } }

[SharpLink.Sdk.RpcContract]
public interface IConflictRouteContract : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value, CancellationToken cancellationToken);
}

public sealed class FirstAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "first/v1";
    public override string WireFormatId => "first-wire/v1";
}
public sealed class SecondAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "second/v1";
    public override string WireFormatId => "second-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"first/v1\", \"first-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SecondAdapter), \"second/v1\", \"second-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(FirstAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(SecondAdapter))]");

        EnsureHasRule(source, "SHARPLINK045");
        return Task.CompletedTask;
    }

    [Test]
    public Task DifferentScopesMayUseDifferentAdapters()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public sealed class Graph { public Graph? Parent { get; set; } }
public readonly struct Point { public int X { get; init; } }

[SharpLink.Sdk.RpcContract]
public interface ISplitRouteContract : SharpLink.Sdk.IService
{
    ValueTask<Graph> EchoGraph(Graph value, CancellationToken cancellationToken);
    ValueTask<Point> EchoPoint(Point value, CancellationToken cancellationToken);
}

public sealed class ManagedAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "managed/v1";
    public override string WireFormatId => "managed-wire/v1";
}
public sealed class UnmanagedAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "unmanaged/v1";
    public override string WireFormatId => "unmanaged-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ManagedAdapter), \"managed/v1\", \"managed-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(UnmanagedAdapter), \"unmanaged/v1\", \"unmanaged-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(ManagedAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Unmanaged, typeof(UnmanagedAdapter))]");

        EnsureDoesNotHaveRule(source, "SHARPLINK045");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("managed-wire/v1", StringComparison.Ordinal), "Managed route identity");
        Ensure(generated.Contains("unmanaged-wire/v1", StringComparison.Ordinal), "Unmanaged route identity");
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractRouteShouldNotClaimStandaloneRpcSerializableCodec()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class StandalonePayload
{
    public int Value { get; set; }
}

public sealed class ContractPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IStandaloneIsolationContract : SharpLink.Sdk.IService
{
    ValueTask<ContractPayload> Echo(ContractPayload value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.managed.contract-only/v1";
    public override string WireFormatId => "route-managed-contract-only-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.managed.contract-only/v1\", \"route-managed-contract-only-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::ContractPayload>()", StringComparison.Ordinal),
            "the Managed route must apply to the Contract payload root");
        Ensure(!generated.Contains("CreateCodec<global::StandalonePayload>()", StringComparison.Ordinal),
            "a standalone RpcSerializable codec must remain on normal generated-codec resolution");
        Ensure(generated.Contains("typeof(global::StandalonePayload)", StringComparison.Ordinal),
            "the standalone RpcSerializable codec must still be emitted");
        return Task.CompletedTask;
    }

    [Test]
    public Task ManagedRouteShouldNotCaptureDynamicPayload()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcContract]
public interface IDynamicRouteContract : SharpLink.Sdk.IService
{
    ValueTask<dynamic> Echo(dynamic value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.dynamic/v1";
    public override string WireFormatId => "route-dynamic-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.dynamic/v1\", \"route-dynamic-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK009"),
            "dynamic payloads must retain the SharpLink unsupported diagnostic instead of being routed");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("CreateCodec<dynamic>()", StringComparison.Ordinal),
            "dynamic must not enter a routed Codec factory");
        Ensure(!generated.Contains("typeof(dynamic)", StringComparison.Ordinal),
            "generated manifests must not contain illegal typeof(dynamic)");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcSerializableContractPayloadShouldKeepIndependentBindings()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IDualRoleContract : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.dual-role/v1";
    public override string WireFormatId => "route-dual-role-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.dual-role/v1\", \"route-dual-role-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var sources = RunGeneratorAndGetSources(source);
        var generated = string.Join("\n", sources);
        Ensure(generated.Split("TargetType => typeof(global::Payload)", StringSplitOptions.None).Length - 1 == 2,
            "dual-role Payload must have independent standalone-generated and Contract-routed factories");
        var manifest = sources.Single(static item => item.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        Ensure(manifest.Contains("ContractCodecs => __readOnlyContractCodecs", StringComparison.Ordinal),
            "generated manifest must expose a separate Contract Codec binding table");
        return Task.CompletedTask;
    }

    private static string BuildRouteSource(string contract)
        => BuildSource(contract) + """

namespace SharpLink.Sdk
{
    [Flags]
    public enum RpcCodecScope
    {
        None = 0,
        Managed = 1 << 0,
        Unmanaged = 1 << 1,
        All = Managed | Unmanaged
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
    public sealed class RpcCodecRouteAttribute : Attribute
    {
        public RpcCodecRouteAttribute(RpcCodecScope scope, Type adapterType) { }
    }
}

public abstract class TestRouteAdapterBase : SharpLink.Abstractions.IRpcCodecAdapter
{
    public abstract string AdapterId { get; }
    public abstract string WireFormatId { get; }
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
""";
}
