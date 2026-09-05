using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task SdkUsingPayloadWithoutManifestShouldNotBecomeModuleDependency()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));
        var payloads = CreateMetadataReference(
            "SdkAnnotatedPayloads",
            """
namespace SdkAnnotatedPayloads
{
    public sealed class Payload
    {
        public int Value { get; set; }
    }

    public sealed class SdkReferenceMarker
    {
        public SharpLink.Sdk.IService? Service { get; set; }
    }
}
""",
            sdk);
        const string source = """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;
using SdkAnnotatedPayloads;

[RpcContract]
public interface IPayloadContract : IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""";

        var manifest = RunGeneratorAndGetSources(source, sdk, payloads)
            .Single(static text => text.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        Ensure(!manifest.Contains("SdkAnnotatedPayloads, Version=", StringComparison.Ordinal),
            "an ordinary CLR payload assembly must not become a generated-module dependency merely because it references SharpLink.Sdk");
        return Task.CompletedTask;
    }

    [Test]
    public Task ManagedRouteShouldRecognizeNestedCustomCodecDependency()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public sealed class Envelope
{
    public Child Value { get; set; } = new();
}

[SharpLink.Sdk.RpcCodec(typeof(ChildCodec))]
public class Child
{
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x3100000000000001UL, 0x4100000000000001UL)]
public sealed class ChildCodec : SharpLink.Abstractions.IRpcCodec<Child>
{
}

[SharpLink.Sdk.RpcContract]
public interface INestedCustomRouteContract : SharpLink.Sdk.IService
{
    ValueTask<Envelope> Echo(Envelope value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x3100000000000002UL, 0x4100000000000002UL)]
public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.nested-custom/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.nested-custom/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Managed, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id is "SHARPLINK009" or "SHARPLINK010"),
            "a valid nested custom Codec must keep the parent graph eligible for Managed routing");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::Envelope>()", StringComparison.Ordinal),
            "the Managed route must select the configurable parent whose child is resolved by a custom Codec");
        Ensure(generated.Contains("public string? AdapterId => \"route.nested-custom/v1\";", StringComparison.Ordinal),
            "the selected Managed route must use the registered Adapter");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReferencedAssemblyCustomBindingShouldNotLeakIntoOwnerPolicy()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));
        var policyA = CreateMetadataReference(
            "PolicyA",
            """
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: RpcCodec(typeof(PolicyA.SharedPayload), typeof(PolicyA.CodecA))]

namespace PolicyA
{
    public sealed class SharedPayload
    {
        public int Value { get; set; }
    }

    [RpcCodecSemanticIdentity(0x3100000000000003UL, 0x4100000000000003UL)]
    public sealed class CodecA : IRpcCodec<SharedPayload>
    {
    }
}
""",
            sdk);
        const string source = """
using System.Threading;
using System.Threading.Tasks;
using PolicyA;
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: RpcCodec(typeof(SharedPayload), typeof(CodecB))]

[RpcCodecSemanticIdentity(0x3100000000000004UL, 0x4100000000000004UL)]
public sealed class CodecB : IRpcCodec<SharedPayload>
{
}

[RpcContract]
public interface IOwnerPolicyContract : IService
{
    ValueTask<SharedPayload> Echo(SharedPayload value, CancellationToken cancellationToken);
}
""";

        var diagnostics = RunGenerator(source, sdk, policyA);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK062"),
            "the current owner must not conflict with an assembly-level custom binding declared by a referenced assembly");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source, sdk, policyA));
        Ensure(generated.Contains("new global::CodecB()", StringComparison.Ordinal),
            "the current Contract assembly must retain its own custom Codec binding");
        Ensure(!generated.Contains("new global::PolicyA.CodecA()", StringComparison.Ordinal),
            "a referenced assembly-level custom Codec binding must not be inherited into the current owner policy");
        return Task.CompletedTask;
    }

    [Test]
    public Task FrameworkPrimitiveAdapterBindingShouldBeRejectedEvenWithAllRoute()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcContract]
public interface IFixedIntContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x3100000000000005UL, 0x4100000000000005UL)]
public sealed class ExplicitAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "explicit.int/v1";
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x3100000000000006UL, 0x4100000000000006UL)]
public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.all/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ExplicitAdapter), \"explicit.int/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.all/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(ExplicitAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK049"),
            "framework primitive int must reject explicit Adapter/direct rebinding regardless of lower-precedence routes");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("public string? AdapterId => \"explicit.int/v1\";", StringComparison.Ordinal),
            "a rejected framework primitive binding must not enter the final Codec graph");
        Ensure(!generated.Contains("CreateCodec<int>()", StringComparison.Ordinal),
            "All route must not capture framework primitive int");
        return Task.CompletedTask;
    }

    [Test]
    public Task FrameworkPrimitiveCustomCodecBindingShouldBeRejectedEvenWithAllRoute()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcContract]
public interface IFixedIntCustomContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x3100000000000007UL, 0x4100000000000007UL)]
public sealed class IntCodec : SharpLink.Abstractions.IRpcCodec<int>
{
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x3100000000000008UL, 0x4100000000000008UL)]
public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.all/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.all/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(int), typeof(IntCodec))]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK063"),
            "framework primitive int must reject custom Codec rebinding regardless of lower-precedence routes");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("new global::IntCodec()", StringComparison.Ordinal),
            "a rejected framework primitive custom Codec must not enter the final Codec graph");
        Ensure(!generated.Contains("CreateCodec<int>()", StringComparison.Ordinal),
            "All route must not capture framework primitive int");
        return Task.CompletedTask;
    }
}
