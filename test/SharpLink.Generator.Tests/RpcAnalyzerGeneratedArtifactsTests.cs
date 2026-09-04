using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{

    [Test]
    public Task GeneratedApi4ShouldUseLiteralManifestStampAndAbstractionsOnlyServerBridge()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    public string Value { get; set; } = string.Empty;
}

[SharpLink.Sdk.RpcContract]
public interface IAbi4Service : SharpLink.Sdk.IService
{
    ValueTask<Payload> Unary(Payload value);

    [SharpLink.Sdk.Oneway]
    ValueTask Notify(int value);

    ValueTask<int> Upload(IAsyncEnumerable<Payload> values, CancellationToken cancellationToken);

    IAsyncEnumerable<Payload> Download(int count, CancellationToken cancellationToken);

    IAsyncEnumerable<Payload> Duplex(
        IAsyncEnumerable<Payload> values,
        CancellationToken cancellationToken);
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var stub = generated.Single(text => text.Contains(
            "private sealed class __Stub_",
            StringComparison.Ordinal));
        var proxy = generated.Single(text => text.Contains(
            "private sealed class __Proxy_",
            StringComparison.Ordinal));
        var manifest = generated.Single(text =>
            text.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var allGenerated = string.Join("\n", generated);

        Ensure(manifest.Contains("public int ApiVersion => 4;", StringComparison.Ordinal) &&
               manifest.Contains("public int ProtocolVersion => 2;", StringComparison.Ordinal),
            "the Generator must own literal API 4 / Protocol 2 stamps");
        Ensure(manifest.Contains("SharpLinkGeneratedAssemblyManifestAttribute(", StringComparison.Ordinal) &&
               manifest.Contains(", 4, 2,", StringComparison.Ordinal) &&
               manifest.Contains("sharplink-2.0-api4-rpcchannel-codec-provider-v4", StringComparison.Ordinal),
            "the manifest locator must describe the API, Protocol, and exact ABI identity before materialization");
        Ensure(!manifest.Contains("SharpLinkGeneratedManifestVersions", StringComparison.Ordinal),
            "producer stamps must not read consumer-owned Runtime constants");
        Ensure(stub.Contains("IRpcGeneratedServerBridge bridge", StringComparison.Ordinal),
            "API 4 stubs must depend on the whole-stream server bridge");
        Ensure(stub.Contains("IBufferWriter<byte> output", StringComparison.Ordinal),
            "response payload output must be narrowed to IBufferWriter<byte>");
        Ensure(stub.Contains("internal __Stub_", StringComparison.Ordinal) &&
               stub.Contains("IRpcCodecProvider codecs)", StringComparison.Ordinal),
            "server codecs must be resolved when the Stub is constructed");
        Ensure(stub.Contains("bridge.CreateInboundStream", StringComparison.Ordinal) &&
               stub.Contains("bridge.PumpOutboundStreamAsync", StringComparison.Ordinal),
            "inbound and outbound stream lifecycles must be delegated to Runtime");
        foreach (var forbidden in new[]
                 {
                     "SharpLink.Runtime", "IRpcSession", "RuntimeContext",
                     "PooledAsyncStreamDispatcher", "RpcSessionExtensions"
                 })
        {
            Ensure(!stub.Contains(forbidden, StringComparison.Ordinal),
                $"API 4 Stub leaked forbidden Runtime ABI token '{forbidden}'");
        }
        Ensure(!proxy.Contains("using SharpLink.Runtime;", StringComparison.Ordinal),
            "API 4 Proxy must not acquire a Runtime AssemblyRef through an unused import");
        Ensure(!allGenerated.Contains("SharpLink.Runtime", StringComparison.Ordinal),
            "no generated API 4 source may reference SharpLink.Runtime");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedProxyAndStubShouldBePrivateNestedImplementationTypes()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IPrivateNestedService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("private sealed class __Proxy_", StringComparison.Ordinal),
            "generated Proxy must be a private nested implementation type");
        Ensure(generated.Contains("private sealed class __Stub_", StringComparison.Ordinal),
            "generated Stub must be a private nested implementation type");
        Ensure(CountOccurrences(generated, "public sealed class IPrivateNestedService_Proxy") == 0 &&
               CountOccurrences(generated, "public sealed class IPrivateNestedService_Stub") == 0,
            "generated Proxy/Stub must not be public top-level contract types");
        Ensure(generated.Contains("static (channel, codecs) => __CreateProxy_", StringComparison.Ordinal) &&
               generated.Contains("static codecs => __CreateStub_", StringComparison.Ordinal),
            "the manifest must use private static factories to instantiate nested artifacts");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedStubSizeFieldsShouldRemainUniqueForSanitizedEnumNames()
    {
        var source = BuildSource("""
namespace A
{
    public static class B_C
    {
        public enum State : short { None }
    }
}

namespace A_B
{
    public static class C
    {
        public enum State : short { None }
    }
}

[SharpLink.Sdk.RpcContract]
public interface IEnumCollisionContract : SharpLink.Sdk.IService
{
    ValueTask<int> Resolve(
        A.B_C.State first,
        A_B.C.State second,
        CancellationToken cancellationToken);
}
""");

        var sizeFields = string.Join("\n", RunGeneratorAndGetSources(source))
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith(
                "private static readonly int __size_type_", StringComparison.Ordinal))
            .Select(static line => line[..line.IndexOf(" =", StringComparison.Ordinal)])
            .ToArray();
        Ensure(sizeFields.Length == 2, "both enum sizes must be cached by the generated Stub");
        Ensure(sizeFields.Distinct(StringComparer.Ordinal).Count() == sizeFields.Length,
            "distinct enum types must not emit duplicate generated size fields");
        return Task.CompletedTask;
    }

    [Test]
    public Task ProxyShouldUseFiveInvokerShapesWithoutCapturedPayloadDelegate()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Unary(int value);
    [SharpLink.Sdk.Oneway]
    ValueTask Notify(string value);
    ValueTask<int> Upload(IAsyncEnumerable<int> values);
    ValueTask<int> Merge(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right);
    IAsyncEnumerable<int> Download(int count);
    IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values);
}

[SharpLink.Sdk.RpcService]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Unary(int value) => throw new NotImplementedException();
    public ValueTask Notify(string value) => throw new NotImplementedException();
    public ValueTask<int> Upload(IAsyncEnumerable<int> values) => throw new NotImplementedException();
    public ValueTask<int> Merge(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right) => throw new NotImplementedException();
    public IAsyncEnumerable<int> Download(int count) => throw new NotImplementedException();
    public IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values) => throw new NotImplementedException();
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var allGenerated = string.Join("\n", generated);
        var proxy = generated.FirstOrDefault(static text => text.Contains("private sealed class __Proxy_"));
        if (proxy is null)
            throw new Exception("Expected generated proxy source.");
        Ensure(proxy.Contains("InvokeUnaryAsync"), "Unary invoker");
        Ensure(proxy.Contains("InvokeOneWayAsync"), "OneWay invoker");
        Ensure(proxy.Contains("InvokeClientStreamingAsync"), "ClientStreaming invoker");
        Ensure(proxy.Contains("InvokeServerStreamingAsync"), "ServerStreaming invoker");
        Ensure(proxy.Contains("InvokeDuplexStreamingAsync"), "DuplexStreaming invoker");
        Ensure(allGenerated.Contains("readonly struct __IHelloService_SharpLinkRequest_"), "Generated request struct");
        Ensure(proxy.Contains("IRpcCodec<global::__IHelloService_SharpLinkRequest_"), "Generated request codec");
        Ensure(allGenerated.Contains("Span<byte> tmp_"), "Segmented fixed-width arguments must use stack scratch");
        Ensure(!allGenerated.Contains("byte[] tmp_"), "Segmented fixed-width arguments must not allocate arrays");
        Ensure(!proxy.Contains("Action<IBufferWriter<byte>>"), "Captured payload delegate must not be generated");
        Ensure(!proxy.Contains("InvokeCancellableWithTimeoutAsync"), "Legacy combinatorial API must not be generated");
        Ensure(allGenerated.Contains("public bool SupportsCancellation(long methodHash)"),
            "streaming stubs must publish framework cancellation support");
        Ensure(allGenerated.Contains(
                "RpcMethodKind.ClientStreaming, true, true, false, null, false, 1, false)",
                StringComparison.Ordinal),
            "single client-stream count must be generated deterministically");
        Ensure(allGenerated.Contains(
                "RpcMethodKind.ClientStreaming, true, true, false, null, false, 2, false)",
                StringComparison.Ordinal),
            "multiple client-stream count must be generated deterministically");
        var supportsCancellationCases = allGenerated.Split("=> true", StringSplitOptions.None).Length - 1;
        Ensure(supportsCancellationCases >= 3,
            "client, server, and duplex streaming methods must all support framework cancellation");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedProxyLocalsShouldNotCollideWithUserParameters()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface ILocalNameContract : SharpLink.Sdk.IService
{
    ValueTask<int> Invoke(
        int __request,
        int __request_,
        IAsyncEnumerable<int> __streams,
        IAsyncEnumerable<int> __streams_,
        CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("        var __request =", StringComparison.Ordinal),
            "generated request local must not shadow a user parameter");
        Ensure(!generated.Contains("        var __request_ =", StringComparison.Ordinal),
            "generated request local must skip chained user collisions");
        Ensure(!generated.Contains("        var __streams =", StringComparison.Ordinal),
            "generated streams local must not shadow a user parameter");
        Ensure(!generated.Contains("        var __streams_ =", StringComparison.Ordinal),
            "generated streams local must skip chained user collisions");
        return Task.CompletedTask;
    }

    [Test]
    public Task ResponseNullabilityMustParticipateInGeneratedMethodFingerprint()
    {
        var required = BuildSource("""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface IResponseFingerprintContract : SharpLink.Sdk.IService
{
    ValueTask<string> Resolve(CancellationToken cancellationToken);
}
""");

        var optional = BuildSource("""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface IResponseFingerprintContract : SharpLink.Sdk.IService
{
    ValueTask<string?> Resolve(CancellationToken cancellationToken);
}
""");

        var requiredFingerprint = GetFirstGeneratedMethodFingerprint(required);
        var optionalFingerprint = GetFirstGeneratedMethodFingerprint(optional);

        Ensure(!string.Equals(requiredFingerprint, optionalFingerprint, StringComparison.Ordinal),
            "required and nullable responses must not publish the same runtime method fingerprint");
        return Task.CompletedTask;
    }

    [Test]
    public Task RouteMarkerWithoutGeneratedManifestShouldReportSharplink040()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IOrdersService : SharpLink.Sdk.IService
{
    ValueTask<int> GetAsync(int value, CancellationToken cancellationToken);
}
""");
        source = AddAssemblyAttribute(
            source,
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"orders\", typeof(string))]");

        EnsureHasRule(source, "SHARPLINK040");
        return Task.CompletedTask;
    }
}
