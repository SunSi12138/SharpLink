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
    public Task NativeRouteShouldRecognizeNestedCustomCodecDependency()
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

[SharpLink.Sdk.RpcCodecImplementation("child-wire/v1", "child-schema/v1")]
public sealed class ChildCodec : SharpLink.Abstractions.IRpcCodec<Child>
{
}

[SharpLink.Sdk.RpcContract]
public interface INestedCustomRouteContract : SharpLink.Sdk.IService
{
    ValueTask<Envelope> Echo(Envelope value, CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.nested-custom/v1";
    public override string WireFormatId => "route-nested-custom-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.nested-custom/v1\", \"route-nested-custom-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id is "SHARPLINK009" or "SHARPLINK010"),
            "a valid nested custom Codec must keep the parent graph eligible for Native routing");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::Envelope>()", StringComparison.Ordinal),
            "the Native route must classify and select the parent whose child is resolved by a custom Codec");
        Ensure(generated.Contains("route-nested-custom-wire/v1", StringComparison.Ordinal),
            "the selected Native route identity must be emitted for the parent graph");
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

    [RpcCodecImplementation("policy-a-wire/v1", "policy-a-schema/v1")]
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

[RpcCodecImplementation("policy-b-wire/v1", "policy-b-schema/v1")]
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
    public Task ExplicitBuiltinAdapterShouldOverrideNativeRoute()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcContract]
public interface IExplicitBuiltinContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

public sealed class ExplicitAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "explicit.int/v1";
    public override string WireFormatId => "explicit-int-wire/v1";
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.int/v1";
    public override string WireFormatId => "route-int-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ExplicitAdapter), \"explicit.int/v1\", \"explicit-int-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.int/v1\", \"route-int-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(ExplicitAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK049"),
            "an explicit owner binding must be legal for a builtin when the same Native route could override it");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("explicit-int-wire/v1", StringComparison.Ordinal),
            "the explicit builtin Adapter must win over the Native route");
        Ensure(!generated.Contains("route-int-wire/v1", StringComparison.Ordinal),
            "the losing Native route must not enter the generated binding graph for the explicitly bound builtin");
        Ensure(generated.Contains("codecs.GetCodec<int>()", StringComparison.Ordinal),
            "the fixed request path must bind the explicit Codec instead of retaining inline builtin framing");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitBuiltinCustomCodecShouldOverrideNativeRoute()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcContract]
public interface IExplicitBuiltinCustomContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecImplementation("custom-int-wire/v1", "custom-int-schema/v1")]
public sealed class IntCodec : SharpLink.Abstractions.IRpcCodec<int>
{
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.custom-int/v1";
    public override string WireFormatId => "route-custom-int-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.custom-int/v1\", \"route-custom-int-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(int), typeof(IntCodec))]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK060"),
            "an explicit custom Codec must be legal for a builtin when a Native route could override it");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("new global::IntCodec()", StringComparison.Ordinal),
            "the explicit custom builtin Codec must win over the Native route");
        Ensure(!generated.Contains("route-custom-int-wire/v1", StringComparison.Ordinal),
            "the losing Native route must not enter the generated binding graph for the custom-bound builtin");
        Ensure(generated.Contains("codecs.GetCodec<int>()", StringComparison.Ordinal),
            "the fixed request path must bind the custom Codec instead of retaining inline builtin framing");
        return Task.CompletedTask;
    }
}
