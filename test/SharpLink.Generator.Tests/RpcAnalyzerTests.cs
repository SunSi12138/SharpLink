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
               manifest.Contains(", 4, 2,", StringComparison.Ordinal),
            "the manifest locator must describe compatibility before materialization");
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
        Ensure(generated.Contains("static channel => __CreateProxy_", StringComparison.Ordinal) &&
               generated.Contains("static codecs => __CreateStub_", StringComparison.Ordinal),
            "the manifest must use private static factories to instantiate nested artifacts");
        return Task.CompletedTask;
    }

    [Test]
    public Task SemanticFixedRequestValuesShouldUseValidatedBuiltInCodecs()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IValidatedValueService : SharpLink.Sdk.IService
{
    ValueTask<int> Validate(
        bool enabled,
        decimal amount,
        DateOnly day,
        DateTime timestamp,
        DateTimeOffset offset,
        TimeOnly time,
        System.Text.Rune rune,
        CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(CountOccurrences(generated, "marker_enabled is not (0 or 1)") == 2,
            "proxy and stub request decoders must reject non-canonical Boolean markers");
        Ensure(generated.Contains("value.enabled ? (byte)1 : (byte)0", StringComparison.Ordinal),
            "the request encoder must canonicalize Boolean values");
        foreach (var type in new[]
                 {
                     "decimal", "global::System.DateOnly", "global::System.DateTime",
                     "global::System.DateTimeOffset", "global::System.TimeOnly", "global::System.Text.Rune"
                 })
        {
            Ensure(generated.Contains($"codecs.GetCodec<{type}>()", StringComparison.Ordinal),
                $"request value {type} must use its validating built-in Codec");
        }
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcContractShouldGenerateInheritedBaseMethods()
    {
        var source = BuildSource("""
public interface IBaseOperations
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
    ValueTask<int> Ping(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IDerivedService : SharpLink.Sdk.IService, IBaseOperations
{
    new ValueTask<int> Echo(int value, CancellationToken cancellationToken);
    ValueTask<int> Add(int left, int right, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("public global::System.Threading.Tasks.ValueTask<int> Ping(", StringComparison.Ordinal),
            "proxy should implement an inherited-only RPC method");
        Ensure(generated.Contains("impl.Ping(", StringComparison.Ordinal),
            "stub should dispatch an inherited-only RPC method");
        Ensure(CountOccurrences(generated, "public global::System.Threading.Tasks.ValueTask<int> Echo(") == 1,
            "a directly redeclared base method should be generated exactly once");
        return Task.CompletedTask;
    }

    [Test]
    public Task IncompatibleInheritedRpcRoutesShouldReportASpecificDiagnostic()
    {
        var source = BuildSource("""
public interface INumericBase
{
    ValueTask<int> Resolve(CancellationToken cancellationToken);
}

public interface ITextBase
{
    ValueTask<string> Resolve(CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingContract : SharpLink.Sdk.IService, INumericBase, ITextBase
{
}
""");

        EnsureRuleCount(source, "SHARPLINK057", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IConflictingContractProxy",
                StringComparison.Ordinal),
            "a conflicting inherited contract must not emit a broken Proxy");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingInheritedOnewayShapesShouldReportASpecificDiagnostic()
    {
        var source = BuildSource("""
public interface IFireAndForgetBase
{
    [SharpLink.Sdk.Oneway]
    ValueTask Notify(CancellationToken cancellationToken);
}

public interface IAcknowledgedBase
{
    ValueTask Notify(CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingOnewayContract : SharpLink.Sdk.IService, IFireAndForgetBase, IAcknowledgedBase
{
}
""");

        EnsureRuleCount(source, "SHARPLINK057", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IConflictingOnewayContractProxy",
                StringComparison.Ordinal),
            "a conflicting inherited Oneway shape must not emit contract artifacts");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingInheritedRpcPoliciesShouldReportASpecificDiagnostic()
    {
        var source = BuildSource("""
public interface IRetryingBase
{
    [SharpLink.Sdk.Timeout(1)]
    [SharpLink.Sdk.Idempotent]
    ValueTask<int> Resolve(int value, CancellationToken cancellationToken);
}

public interface INonRetryingBase
{
    [SharpLink.Sdk.Timeout(2)]
    ValueTask<int> Resolve(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingPolicyContract : SharpLink.Sdk.IService, IRetryingBase, INonRetryingBase
{
}
""");

        EnsureRuleCount(source, "SHARPLINK057", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IConflictingPolicyContractProxy",
                StringComparison.Ordinal),
            "conflicting inherited RPC policies must not emit contract artifacts");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingInheritedRequestSchemasShouldReportASpecificDiagnostic()
    {
        var nameAndTopLevelNullability = BuildSource("""
#nullable enable
public interface IRequiredNameBase
{
    ValueTask<int> Resolve(string requiredName, CancellationToken cancellationToken);
}

public interface IOptionalAliasBase
{
    ValueTask<int> Resolve(string? optionalAlias, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingRequestSchemaContract : SharpLink.Sdk.IService, IRequiredNameBase, IOptionalAliasBase
{
}
""");

        EnsureRuleCount(nameAndTopLevelNullability, "SHARPLINK057", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(nameAndTopLevelNullability)).Contains(
                "IConflictingRequestSchemaContractProxy",
                StringComparison.Ordinal),
            "conflicting inherited request schemas must not emit contract artifacts");

        var nestedNullability = BuildSource("""
#nullable enable
public interface IRequiredItemsBase
{
    ValueTask<int> Resolve(List<string> items, CancellationToken cancellationToken);
}

public interface IOptionalItemsBase
{
    ValueTask<int> Resolve(List<string?> items, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingNestedSchemaContract : SharpLink.Sdk.IService, IRequiredItemsBase, IOptionalItemsBase
{
}
""");
        EnsureRuleCount(nestedNullability, "SHARPLINK057", 1);

        var parameterNameOnly = BuildSource("""
public interface IPrimaryNameBase
{
    ValueTask<int> Resolve(string primaryName, CancellationToken cancellationToken);
}

public interface IAliasNameBase
{
    ValueTask<int> Resolve(string aliasName, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingNameSchemaContract : SharpLink.Sdk.IService, IPrimaryNameBase, IAliasNameBase
{
}
""");
        EnsureRuleCount(parameterNameOnly, "SHARPLINK057", 1);

        var controlParameterNames = BuildSource("""
public interface IFirstControlBase
{
    ValueTask<int> Resolve(string value, CancellationToken firstToken);
}

public interface ISecondControlBase
{
    ValueTask<int> Resolve(string value, CancellationToken secondToken);
}

[SharpLink.Sdk.RpcContract]
public interface ICompatibleControlNamesContract : SharpLink.Sdk.IService, IFirstControlBase, ISecondControlBase
{
}
""");
        EnsureDoesNotHaveRule(controlParameterNames, "SHARPLINK057");
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
    public Task DtoMemberNullabilityMustParticipateInRuntimeCodecSchemaIdentity()
    {
        var required = BuildSource("""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface IDtoSchemaContract : SharpLink.Sdk.IService
{
    ValueTask<Payload> Resolve(CancellationToken cancellationToken);
}
public sealed class Payload { public string Name { get; set; } = string.Empty; }
""");
        var optional = BuildSource("""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface IDtoSchemaContract : SharpLink.Sdk.IService
{
    ValueTask<Payload> Resolve(CancellationToken cancellationToken);
}
public sealed class Payload { public string? Name { get; set; } }
""");

        var requiredSchema = GetFirstGeneratedCodecSchema(required);
        var optionalSchema = GetFirstGeneratedCodecSchema(optional);
        Ensure(!string.Equals(requiredSchema, optionalSchema, StringComparison.Ordinal),
            "required and nullable DTO members must not publish the same runtime Codec schema");
        return Task.CompletedTask;
    }

    [Test]
    public Task DirectRedeclarationShouldCanonicalizeInheritedRpcSemantics()
    {
        var source = BuildSource("""
public interface IFireAndForgetBase
{
    [SharpLink.Sdk.Oneway]
    ValueTask Notify(CancellationToken cancellationToken);
}

public interface IAcknowledgedBase
{
    ValueTask Notify(CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface ICanonicalContract : SharpLink.Sdk.IService, IFireAndForgetBase, IAcknowledgedBase
{
    new ValueTask Notify(CancellationToken cancellationToken);
}
""");

        EnsureDoesNotHaveRule(source, "SHARPLINK057");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                ": global::ICanonicalContract",
                StringComparison.Ordinal),
            "an explicit derived declaration must remain the canonical generated route");
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
    public Task InvalidReturnTypeShouldReportSharplink001()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    int Echo(int value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK001");
        return Task.CompletedTask;
    }

    [Test]
    public Task TaskPayloadNamedValueTaskShouldKeepOuterTaskSemantics()
    {
        var source = BuildSource("""
public sealed class ValueTaskPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface ITaskPayloadContract : SharpLink.Sdk.IService
{
    Task<ValueTaskPayload> Echo(ValueTaskPayload value, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        var proxyStart = generated.IndexOf(
            "public global::System.Threading.Tasks.Task<global::ValueTaskPayload> Echo(",
            StringComparison.Ordinal);
        var proxyEnd = proxyStart < 0
            ? -1
            : generated.IndexOf("\n    }", proxyStart, StringComparison.Ordinal);
        Ensure(proxyStart >= 0 && proxyEnd > proxyStart &&
               generated.AsSpan(proxyStart, proxyEnd - proxyStart).Contains(".AsTask();", StringComparison.Ordinal),
            "Task<T> Proxy emission must convert the channel ValueTask using outer Task semantics");
        Ensure(generated.Contains(
                "__SerializeResponse(pending.GetAwaiter().GetResult(), false, __responseCodec_",
                StringComparison.Ordinal),
            "Task<T> Stub emission must use Task result semantics even when T contains 'ValueTask'");
        Ensure(generated.Contains(
                "return __AwaitTaskResultAsync(pending, false, __responseCodec_",
                StringComparison.Ordinal),
            "Task<T> Stub emission must await the outer Task type");
        Ensure(!generated.Contains("Serialize(pending.Result, output)", StringComparison.Ordinal),
            "Task<T> must not use the ValueTask-only Result path");
        return Task.CompletedTask;
    }

    [Test]
    public Task MultipleCancellationTokensShouldReportSharplink002()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken ct1, CancellationToken ct2);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK002");
        return Task.CompletedTask;
    }

    [Test]
    public Task TooManyStreamParametersShouldReportSharplink003()
    {
        var parameters = string.Join(", ",
            Enumerable.Range(0, 128).Select(i => $"IAsyncEnumerable<int> p{i}"));
        var source = BuildSource($$"""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo({{parameters}});
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK003");
        return Task.CompletedTask;
    }

    [Test]
    public Task MissingCancellationTokenShouldReportSharplink004()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout(1)]
    ValueTask<int> Echo(int value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK004");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitNonCancellableShouldSuppressSharplink004()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> Echo(int value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureDoesNotHaveRule(source, "SHARPLINK004");
        return Task.CompletedTask;
    }

    [Test]
    public Task StreamingWithoutCancellationTokenShouldReportSharplink014()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    IAsyncEnumerable<int> Download(int count);
}
""");

        EnsureHasRule(source, "SHARPLINK014");
        EnsureDoesNotHaveRule(source, "SHARPLINK004");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitNonCancellableShouldSuppressSharplink014()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    IAsyncEnumerable<int> Download(int count);
}
""");

        EnsureDoesNotHaveRule(source, "SHARPLINK014");
        return Task.CompletedTask;
    }

    [Test]
    public Task NonCancellableWithCancellationTokenShouldReportSharplink015()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""");

        EnsureHasRule(source, "SHARPLINK015");
        return Task.CompletedTask;
    }

    [Test]
    public Task MultipleCallOptionsShouldReportSharplink007()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, SharpLink.Sdk.SharpLinkCallOptions first, SharpLink.Sdk.SharpLinkCallOptions second);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK007");
        return Task.CompletedTask;
    }

    [Test]
    public Task MisplacedControlParameterShouldReportSharplink008()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(SharpLink.Sdk.SharpLinkCallOptions options, int value, CancellationToken cancellationToken);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK008");
        return Task.CompletedTask;
    }

    [Test]
    public Task GenericMethodInIServiceShouldReportSharplink005Once()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<T> Echo<T>(T value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        var diagnostics = RunGenerator(source);
        var hits = diagnostics.Where(d => d.Id == "SHARPLINK005").ToArray();
        Ensure(hits.Length == 1, $"Expected exactly one SHARPLINK005, but got {hits.Length}.");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcContractWithoutIServiceShouldReportSharplink006()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService
{
    ValueTask<int> Echo(int value);
}
""");

        EnsureHasRule(source, "SHARPLINK006");
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
    public Task ReachableDtoShouldGenerateCodecAndManifest()
    {
        var source = BuildSource("""
public sealed record Address([property: SharpLink.Sdk.RpcMember(7)] string City);

public sealed class Person
{
    [SharpLink.Sdk.RpcRequired]
    public string Name { get; init; } = string.Empty;
    public int Age { get; init; }
    public Address Address { get; init; } = new Address(string.Empty);
    public List<string> Tags { get; init; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Person> Echo(Person value);
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var codecs = generated.FirstOrDefault(static text => text.Contains("Missing required RPC member 'Name'"));
        if (codecs is null)
            throw new Exception("Expected generated DTO codec source.");
        var manifest = generated.FirstOrDefault(static text => text.Contains("__SharpLinkGeneratedAssemblyManifest"));
        if (manifest is null)
            throw new Exception("Expected generated assembly manifest source.");
        Ensure(codecs.Contains("IRpcCodec<global::Person>"), "Person codec");
        Ensure(codecs.Contains("IRpcCodec<global::Address>"), "nested record codec");
        Ensure(codecs.Contains("IRpcCodec<global::System.Collections.Generic.List<string>>"), "collection codec");
        Ensure(manifest.Contains("SharpLinkGeneratedAssemblyCatalog.Register"), "manifest registration");
        Ensure(manifest.Contains(".Factory()"), "codec factories belong to the assembly manifest");
        Ensure(codecs.Contains("case 7U:"), "explicit field ID");
        Ensure(codecs.Contains("Missing required RPC member 'Name'"), "required member validation");
        return Task.CompletedTask;
    }

    [Test]
    public Task DirectStringDtosShouldCacheExactUtf8SizesAndPreReserveOnce()
    {
        var source = BuildDirectStringDtoSource(1, 4, 16, 64);
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));

        Ensure(CountOccurrences(generated, "internal static class __SharpLinkGeneratedUtf8") == 1,
            "one assembly-private UTF-8 helper must be shared by all eligible generated Codecs");
        Ensure(CountOccurrences(generated, "__SharpLinkGeneratedUtf8.GetByteCount(__string_") == 85,
            "each direct string must be counted exactly once across the 1/4/16/64-field shapes");
        Ensure(CountOccurrences(generated, "StrictEncoding.GetByteCount(") == 1,
            "the known-size write helper must never traverse UTF-16 again");
        Ensure(CountOccurrences(generated, "__SharpLinkGeneratedUtf8.WriteStringKnownSize(writer, __string_") == 85,
            "each direct string must reuse its cached value and byte count");
        Ensure(CountOccurrences(generated, "if (writer is IRpcByteBufferWriter __rpcWriter)") == 4,
            "each eligible DTO must gate whole-payload reservation on the SharpLink packet writer");
        Ensure(CountOccurrences(generated, "__rpcWriter.GetSpan(checked(__encodedSize + 4));") == 4,
            "each eligible DTO must make one capacity request including existing varuint request slack");
        Ensure(CountOccurrences(generated, "__rpcWriter.Advance(0);") == 4,
            "the discarded reservation must complete its buffer lease");
        Ensure(CountOccurrences(generated, "var __encodedSize =") == 4,
            "each eligible DTO must compute one checked encoded size");
        Ensure(!generated.Contains("RpcGeneratedCodecWire.WriteString(writer, value.Field", StringComparison.Ordinal),
            "eligible DTOs must not call the byte-counting public string primitive after pre-sizing");
        Ensure(generated.Contains("new global::System.Text.UTF8Encoding(false, true)", StringComparison.Ordinal),
            "the generated helper must preserve strict UTF-8 encoder semantics");
        Ensure(generated.Contains("global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian", StringComparison.Ordinal) &&
               generated.Contains("var payload = writer.GetSpan(byteCount);", StringComparison.Ordinal),
            "known-size writes must preserve the little-endian prefix and separate payload request");
        return Task.CompletedTask;
    }

    [Test]
    public Task DtosWithComplexMembersShouldKeepTheExistingStreamingWritePath()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class MixedPayload
{
    public string Name { get; set; } = string.Empty;
    public NestedPayload Nested { get; set; } = new();
}

[SharpLink.Sdk.RpcSerializable]
public sealed class NestedPayload
{
    public int Value { get; set; }
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("internal static class __SharpLinkGeneratedUtf8", StringComparison.Ordinal) &&
               !generated.Contains("var __encodedSize =", StringComparison.Ordinal),
            "a nested DTO graph must not claim an exact top-level size");
        Ensure(generated.Contains("RpcGeneratedCodecWire.WriteString(writer, value.Name);", StringComparison.Ordinal),
            "ineligible DTOs must retain the existing string write path");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedManifestShouldExposeAnAssemblyOwnedBootstrapForInternalServices()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IInternalService : SharpLink.Sdk.IService
{
    ValueTask<string> Identify();
}

[SharpLink.Sdk.RpcService]
internal sealed class InternalService : IInternalService
{
    public InternalService() { }
    public ValueTask<string> Identify() => new("internal");
}
""");

        var manifest = GetGeneratedManifest(source);
        Ensure(manifest.Contains("typeof(global::InternalService)", StringComparison.Ordinal),
            "the assembly-owned manifest must retain its internal service implementation");
        Ensure(manifest.Contains("public static void Register()", StringComparison.Ordinal),
            "the generated manifest must expose a public static bootstrap entry point");
        Ensure(manifest.Contains(
                "=> SharpLinkGeneratedAssemblyCatalog.Register(Instance);",
                StringComparison.Ordinal),
            "the public bootstrap must register the assembly-owned manifest instance");
        Ensure(manifest.Contains("=> __SharpLinkGeneratedAssemblyManifest_", StringComparison.Ordinal) &&
               manifest.Contains(".Register();", StringComparison.Ordinal),
            "the producer module initializer must delegate to the public bootstrap");
        Ensure(CountOccurrences(manifest, "SharpLinkGeneratedAssemblyCatalog.Register") == 1,
            "registration logic must have one assembly-owned implementation");
        Ensure(!manifest.Contains("Register(global::InternalService", StringComparison.Ordinal),
            "the public bootstrap must not expose the internal implementation type");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReferencedAssemblyManifestsShouldEmitDeterministicStaticBootstrapCalls()
    {
        var infrastructure = CreateManifestInfrastructureReference();
        var alpha = CreateGeneratedManifestReference(
            "AlphaServices",
            "AlphaManifest",
            "HiddenAlphaService",
            infrastructure);
        var zeta = CreateGeneratedManifestReference(
            "ZetaServices",
            "ZetaManifest",
            "HiddenZetaService",
            infrastructure);
        var legacy = CreateLegacyGeneratedManifestReference(infrastructure);
        var malformed = CreateMalformedManifestReference(infrastructure);
        var ordinary = CreateMetadataReference(
            "OrdinaryDependency",
            "namespace OrdinaryDependency { public sealed class OrdinaryType { } }");
        const string consumer = "namespace Consumer { internal sealed class Marker { } }";

        var first = GetReferencedManifestBootstrap(
            RunGeneratorAndGetSources(consumer, infrastructure, zeta, ordinary, legacy, malformed, alpha));
        var second = GetReferencedManifestBootstrap(
            RunGeneratorAndGetSources(consumer, infrastructure, alpha, malformed, legacy, ordinary, zeta));

        Ensure(string.Equals(first, second, StringComparison.Ordinal),
            "referenced manifest bootstrap output must not depend on metadata-reference order");
        Ensure(CountOccurrences(first, ".Register();") == 2,
            "each current referenced generated manifest must receive exactly one bootstrap call");
        var alphaCall = first.IndexOf("global::SharpLink.Generated.AlphaManifest.Register();", StringComparison.Ordinal);
        var zetaCall = first.IndexOf("global::SharpLink.Generated.ZetaManifest.Register();", StringComparison.Ordinal);
        Ensure(alphaCall >= 0 && zetaCall > alphaCall,
            "bootstrap calls must use public fully qualified entry points in assembly-identity order");
        Ensure(!first.Contains("LegacyManifest", StringComparison.Ordinal),
            "legacy API 3 locators must not be bootstrapped into an API 4 process");
        Ensure(first.Contains("ModuleInitializer", StringComparison.Ordinal),
            "the consumer bootstrap must execute before application entry and server Build");
        Ensure(!first.Contains("OrdinaryDependency", StringComparison.Ordinal) &&
               !first.Contains("MalformedManifest", StringComparison.Ordinal) &&
               !first.Contains("HiddenAlphaService", StringComparison.Ordinal) &&
               !first.Contains("HiddenZetaService", StringComparison.Ordinal),
            "ordinary references and internal implementation types must not leak into the bootstrap");
        foreach (var forbidden in new[]
                 {
                     "Assembly.Load", "Assembly.LoadFrom", "GetCustomAttributes", "Directory.", "GetFiles("
                 })
        {
            Ensure(!first.Contains(forbidden, StringComparison.Ordinal),
                $"the static bootstrap must not use runtime discovery token '{forbidden}'");
        }

        EnsureGeneratorOutputCompiles(consumer, infrastructure, zeta, ordinary, legacy, malformed, alpha);
        return Task.CompletedTask;
    }

    [Test]
    public Task SemanticDtoMembersShouldUseValidatedCodecs()
    {
        var source = BuildSource("""
public sealed record SemanticPayload(
    [property: SharpLink.Sdk.RpcMember(1)] bool Boolean,
    [property: SharpLink.Sdk.RpcMember(2)] System.Text.Rune Rune,
    [property: SharpLink.Sdk.RpcMember(3)] decimal Decimal,
    [property: SharpLink.Sdk.RpcMember(4)] System.DateOnly DateOnly,
    [property: SharpLink.Sdk.RpcMember(5)] System.DateTime DateTime,
    [property: SharpLink.Sdk.RpcMember(6)] System.TimeOnly TimeOnly,
    [property: SharpLink.Sdk.RpcMember(7)] System.DateTimeOffset DateTimeOffset,
    [property: SharpLink.Sdk.RpcMember(8)] bool? NullableBoolean,
    [property: SharpLink.Sdk.RpcMember(9)] System.Text.Rune? NullableRune,
    [property: SharpLink.Sdk.RpcMember(10)] decimal? NullableDecimal,
    [property: SharpLink.Sdk.RpcMember(11)] System.DateOnly? NullableDateOnly,
    [property: SharpLink.Sdk.RpcMember(12)] System.DateTime? NullableDateTime,
    [property: SharpLink.Sdk.RpcMember(13)] System.TimeOnly? NullableTimeOnly,
    [property: SharpLink.Sdk.RpcMember(14)] System.DateTimeOffset? NullableDateTimeOffset);

[SharpLink.Sdk.RpcContract]
public interface ISemanticService : SharpLink.Sdk.IService
{
    ValueTask<SemanticPayload> Echo(SemanticPayload value);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("RpcGeneratedCodecWire.WriteBoolean(writer, value.Boolean)", StringComparison.Ordinal),
            "generated Boolean encoder must emit its canonical marker");
        Ensure(generated.Contains("RpcGeneratedCodecWire.ReadBoolean(ref reader)", StringComparison.Ordinal),
            "generated Boolean decoder must validate its marker");
        Ensure(generated.Contains("RpcGeneratedCodecWire.ReadRune(ref reader)", StringComparison.Ordinal),
            "Rune member must use its validated fixed reader");
        Ensure(generated.Contains("RpcGeneratedCodecWire.ReadDecimal(ref reader)", StringComparison.Ordinal),
            "decimal member must use its validated fixed reader");
        Ensure(generated.Contains("RpcGeneratedCodecWire.ReadDateOnly(ref reader)", StringComparison.Ordinal) &&
               generated.Contains("RpcGeneratedCodecWire.ReadDateTime(ref reader)", StringComparison.Ordinal) &&
               generated.Contains("RpcGeneratedCodecWire.ReadTimeOnly(ref reader)", StringComparison.Ordinal),
            "temporal members must use their validated fixed readers");
        Ensure(generated.Contains("RpcGeneratedCodecWire.WriteDateTimeOffset(writer, value.DateTimeOffset)", StringComparison.Ordinal) &&
               generated.Contains("RpcGeneratedCodecWire.ReadDateTimeOffset(ref reader)", StringComparison.Ordinal),
            "DateTimeOffset member must use its canonical fixed writer and validated reader");
        Ensure(CountOccurrences(generated, "RpcGeneratedCodecWire.ReadBoolean(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadRune(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadDecimal(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadDateOnly(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadDateTime(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadTimeOnly(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadDateTimeOffset(ref reader)") == 2,
            "nullable semantic members must use the same validated readers");
        return Task.CompletedTask;
    }

    [Test]
    public Task CodecOnlyManifestShouldBeOwnedByTheGeneratedAssembly()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var contract = CreateMetadataReference(
            "ReferencedDtoContract",
            """
using System.Threading.Tasks;

namespace ReferencedDtoContract
{
    public sealed class Payload
    {
        public int Value { get; set; }
    }

    [SharpLink.Sdk.RpcContract]
    public interface ICodecContract : SharpLink.Sdk.IService
    {
        ValueTask<Payload> Echo(Payload value);
    }
}
""",
            sdk);

        var generated = RunGeneratorAndGetSources(
            "namespace CodecConsumer { public sealed class Marker; }",
            sdk,
            contract);
        var manifest = generated.FirstOrDefault(static text =>
            text.Contains("__SharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal))
            ?? throw new Exception("Expected a codec-only assembly manifest source.");
        Ensure(manifest.Contains(
                "public Assembly OwnerAssembly => typeof(__SharpLinkGeneratedAssemblyManifest_",
                StringComparison.Ordinal),
            "Codec-only manifests must identify the assembly containing the generated manifest.");
        Ensure(!manifest.Contains(
                "OwnerAssembly => typeof(global::ReferencedDtoContract.Payload).Assembly",
                StringComparison.Ordinal),
            "Codec-only manifests must not identify a referenced DTO assembly as their owner.");
        Ensure(manifest.Contains("ReferencedDtoContract, Version=0.0.0.0", StringComparison.Ordinal),
            "Codec-only manifests must depend on the assembly that owns referenced DTO types.");
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractsWithMatchingMethodHashesShouldGenerateDistinctHelperTypes()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IFirstService : SharpLink.Sdk.IService
{
    ValueTask<int> Add(int left, int right);
}

[SharpLink.Sdk.RpcContract]
public interface ISecondService : SharpLink.Sdk.IService
{
    ValueTask<int> Add(int left, int right);
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var all = string.Join("\n", generated);
        Ensure(all.Contains("__IFirstService_SharpLinkRequest_"), "first contract helper type");
        Ensure(all.Contains("__ISecondService_SharpLinkRequest_"), "second contract helper type");
        return Task.CompletedTask;
    }

    [Test]
    public Task CyclicDtoGraphShouldReportSharplink010()
    {
        var source = BuildSource("""
public sealed class Node
{
    public Node? Next { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Node> Echo(Node value);
}
""");

        EnsureHasRule(source, "SHARPLINK010");
        return Task.CompletedTask;
    }

    [Test]
    public Task DuplicateDtoMemberIdShouldReportSharplink011()
    {
        var source = BuildSource("""
public sealed class Collision
{
    [SharpLink.Sdk.RpcMember(1)] public int First { get; set; }
    [SharpLink.Sdk.RpcMember(1)] public int Second { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Collision> Echo(Collision value);
}
""");

        EnsureHasRule(source, "SHARPLINK011");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcServiceWithoutExplicitLifetimeShouldGenerateSingletonManifestEntry()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        var manifest = GetGeneratedManifest(source);
        Ensure(manifest.Contains("public const string CompileTimeDescriptor", StringComparison.Ordinal),
            "Manifest must expose its compile-time descriptor.");
        Ensure(manifest.Contains("global::HelloService", StringComparison.Ordinal),
            "Manifest must identify the service implementation.");
        Ensure(manifest.Contains("SharpLinkServiceLifetime.Singleton", StringComparison.Ordinal),
            "RpcService without an explicit lifetime must be generated as Singleton.");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcServiceExplicitLifetimesShouldBePreservedInManifest()
    {
        foreach (var lifetime in new[] { "Singleton", "Connection", "Call" })
        {
            var source = BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcService(Lifetime = SharpLink.Sdk.SharpLinkServiceLifetime.{{lifetime}})]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Get(int value) => new(value);
}
""");

            var manifest = GetGeneratedManifest(source);
            Ensure(manifest.Contains("global::HelloService", StringComparison.Ordinal),
                "Manifest must identify the service implementation.");
            Ensure(manifest.Contains($"SharpLinkServiceLifetime.{lifetime}", StringComparison.Ordinal),
                $"Manifest must preserve explicit {lifetime} lifetime.");
        }

        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidRpcServiceLifetimeShouldReportSharplink020()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService(Lifetime = (SharpLink.Sdk.SharpLinkServiceLifetime)99)]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK020", "99");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcServiceWithoutRpcContractShouldReportSharplink016()
    {
        var source = BuildSource("""
public interface IOrdinaryService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class OrdinaryService : IOrdinaryService
{
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK016", "OrdinaryService");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcServiceImplementingMultipleContractsShouldReportSharplink017()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IFirstService : SharpLink.Sdk.IService
{
    ValueTask<int> First(int value);
}

[SharpLink.Sdk.RpcContract]
public interface ISecondService : SharpLink.Sdk.IService
{
    ValueTask<int> Second(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class AmbiguousService : IFirstService, ISecondService
{
    public ValueTask<int> First(int value) => new(value);
    public ValueTask<int> Second(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK017", "AmbiguousService");
        return Task.CompletedTask;
    }

    [Test]
    public Task AbstractAndOpenGenericRpcServicesShouldReportSharplink018()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IAbstractContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcContract]
public interface IGenericContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcService]
public abstract class AbstractService : IAbstractContract
{
    public abstract ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class GenericService<T> : IGenericContract
{
    public ValueTask<int> Get(int value) => new(value);
}
""");

        EnsureRuleCount(source, "SHARPLINK018", 2);
        return Task.CompletedTask;
    }

    [Test]
    public Task AmbiguousAndInaccessibleConstructorsShouldReportSharplink019()
    {
        var source = BuildSource("""
public sealed class FirstDependency;
public sealed class SecondDependency;

[SharpLink.Sdk.RpcContract]
public interface IAmbiguousContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcContract]
public interface IInaccessibleContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class AmbiguousConstructorService : IAmbiguousContract
{
    public AmbiguousConstructorService(FirstDependency dependency) { }
    public AmbiguousConstructorService(SecondDependency dependency) { }
    public ValueTask<int> Get(int value) => new(value);
}

[SharpLink.Sdk.RpcService]
public sealed class InaccessibleConstructorService : IInaccessibleContract
{
    private InaccessibleConstructorService() { }
    public ValueTask<int> Get(int value) => new(value);
}
""");

        EnsureRuleCount(source, "SHARPLINK019", 2);
        return Task.CompletedTask;
    }

    [Test]
    public Task DuplicateStaticContractOwnersShouldReportSharplink021()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var first = CreateMetadataReference("ContractOwnerA", BuildReferencedContractSource("ValueTask<int> Echo(int value);"), sdk);
        var second = CreateMetadataReference("ContractOwnerB", BuildReferencedContractSource("ValueTask<int> Echo(int value);"), sdk);

        EnsureHasRule(
            "namespace Consumer { public sealed class Marker; }",
            "SHARPLINK021",
            sdk,
            first,
            second);
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitContractAssemblyFilterShouldExcludeUnselectedStaticConflicts()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var first = CreateMetadataReference(
            "ContractOwnerA",
            BuildReferencedContractSource("ValueTask<int> Echo(int value);") +
            "\nnamespace ContractOwnerA { public sealed class Marker; }",
            sdk);
        var second = CreateMetadataReference(
            "ContractOwnerB",
            BuildReferencedContractSource("ValueTask<string> Echo(int value);") +
            "\nnamespace ContractOwnerB { public sealed class Marker; }",
            sdk);

        var diagnostics = RunGenerator(
            "[assembly: SharpLink.Sdk.SharpLinkRpcContracts(typeof(ContractOwnerA.Marker))]\n" +
            "namespace Consumer { public sealed class Marker; }",
            sdk,
            first,
            second);
        Ensure(!diagnostics.Any(static diagnostic =>
                diagnostic.Id is "SHARPLINK021" or "SHARPLINK022" or "SHARPLINK023"),
            $"Explicit contract scan filter must exclude unselected assemblies. Actual: {FormatDiagnostics(diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitEmptyContractAssemblyFilterShouldDisableReferencedContractScanning()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var first = CreateMetadataReference("ContractOwnerA", BuildReferencedContractSource("ValueTask<int> Echo(int value);"), sdk);
        var second = CreateMetadataReference("ContractOwnerB", BuildReferencedContractSource("ValueTask<string> Echo(int value);"), sdk);

        var diagnostics = RunGenerator(
            "[assembly: SharpLink.Sdk.SharpLinkRpcContracts()]\n" +
            "namespace Consumer { public sealed class Marker; }",
            sdk,
            first,
            second);
        Ensure(!diagnostics.Any(static diagnostic =>
                diagnostic.Id is "SHARPLINK021" or "SHARPLINK022" or "SHARPLINK023"),
            $"An explicit empty contract filter must not fall back to automatic reference scanning. Actual: {FormatDiagnostics(diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task SanitizedHintNamesShouldRemainUnique()
    {
        var source = BuildSource("""
namespace A.B
{
    [SharpLink.Sdk.RpcContract]
    public interface IC : SharpLink.Sdk.IService
    {
        ValueTask<int> Invoke(CancellationToken cancellationToken);
    }
}

namespace A
{
    [SharpLink.Sdk.RpcContract]
    public interface B_IC : SharpLink.Sdk.IService
    {
        ValueTask<int> Invoke(CancellationToken cancellationToken);
    }
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "CS8785"),
            $"Distinct fully-qualified contracts must not collide after hint-name sanitization. Actual: {FormatDiagnostics(diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task NestedContractsShouldReceiveUniqueGeneratedPeerNames()
    {
        var source = BuildSource("""
namespace Nested
{
    public sealed class First
    {
        [SharpLink.Sdk.RpcContract]
        public interface IInner : SharpLink.Sdk.IService
        {
            ValueTask<int> Invoke(CancellationToken cancellationToken);
        }
    }

    public sealed class Second
    {
        [SharpLink.Sdk.RpcContract]
        public interface IInner : SharpLink.Sdk.IService
        {
            ValueTask<int> Invoke(CancellationToken cancellationToken);
        }
    }
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(CountOccurrences(generated, "IInner_Proxy") == 0,
            "nested contracts with the same simple name must not emit colliding top-level Proxy types");
        Ensure(generated.Contains(" : global::Nested.First.IInner", StringComparison.Ordinal) &&
               generated.Contains(" : global::Nested.Second.IInner", StringComparison.Ordinal),
            "both nested contracts must retain generated peers");
        return Task.CompletedTask;
    }

    [Test]
    public Task KeywordRpcIdentifiersShouldEmitValidCSharpSyntax()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IKeywordContract : SharpLink.Sdk.IService
{
    ValueTask<int> @class(int @event, SharpLink.Sdk.SharpLinkCallOptions @params, CancellationToken @default);
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var syntaxErrors = generated
            .SelectMany(static text => CSharpSyntaxTree.ParseText(text).GetDiagnostics())
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Ensure(syntaxErrors.Length == 0,
            $"Keyword RPC identifiers must remain escaped in generated source. Actual: {FormatDiagnostics(syntaxErrors)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ByRefRpcSignaturesShouldReportSharplink052()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IByRefContract : SharpLink.Sdk.IService
{
    ValueTask<int> Ref(ref int value, CancellationToken cancellationToken);
    ValueTask<int> Out(out int value, CancellationToken cancellationToken);
    ValueTask<int> In(in int value, CancellationToken cancellationToken);
    ref ValueTask<int> RefReturn(CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK052", 4);
        return Task.CompletedTask;
    }

    [Test]
    public Task StaticAbstractRpcMethodsShouldReportSharplink053()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IStaticContract : SharpLink.Sdk.IService
{
    static abstract ValueTask<int> Invoke(int value, CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK053", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task AbstractNonMethodContractMembersShouldReportSharplink054()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IMemberContract : SharpLink.Sdk.IService
{
    int Version { get; }
    string this[int index] { get; }
    event Action Changed;
}
""");

        EnsureRuleCount(source, "SHARPLINK054", 3);
        return Task.CompletedTask;
    }

    [Test]
    public Task InaccessibleAndOpenNestedContractsShouldBeRejected()
    {
        var inaccessible = BuildSource("""
[SharpLink.Sdk.RpcContract]
interface IInternalContract : SharpLink.Sdk.IService
{
    ValueTask<int> Invoke(CancellationToken cancellationToken);
}

public sealed class Container
{
    [SharpLink.Sdk.RpcContract]
    private interface IPrivateContract : SharpLink.Sdk.IService
    {
        ValueTask<int> Invoke(CancellationToken cancellationToken);
    }
}
""");
        EnsureRuleCount(inaccessible, "SHARPLINK055", 2);

        var openNested = BuildSource("""
public sealed class GenericContainer<T>
{
    [SharpLink.Sdk.RpcContract]
    public interface IOpenContract : SharpLink.Sdk.IService
    {
        ValueTask<int> Invoke(CancellationToken cancellationToken);
    }
}
""");
        EnsureRuleCount(openNested, "SHARPLINK005", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task DefaultInterfaceMembersShouldNotBeRejectedAsRpcRoutes()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IDefaultMemberContract : SharpLink.Sdk.IService
{
    int Version => 1;
    event Action Changed { add { } remove { } }
    ValueTask<int> Invoke(CancellationToken cancellationToken);
}
""");

        EnsureDoesNotHaveRule(source, "SHARPLINK054");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidOnewayReturnShapesShouldReportSharplink056()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IInvalidOnewayContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Oneway]
    Task<int> TaskResult(CancellationToken cancellationToken);

    [SharpLink.Sdk.Oneway]
    ValueTask<int> ValueTaskResult(CancellationToken cancellationToken);

    [SharpLink.Sdk.Oneway]
    IAsyncEnumerable<int> StreamResult(CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK056", 3);

        var valid = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IValidOnewayContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Oneway]
    Task Fire(CancellationToken cancellationToken);

    [SharpLink.Sdk.Oneway]
    ValueTask Send(CancellationToken cancellationToken);
}
""");
        EnsureDoesNotHaveRule(valid, "SHARPLINK056");
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
    public Task CaseInsensitiveDtoMemberAmbiguityShouldReportSharplink012()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class AmbiguousCaseDto
{
    public string Name { get; set; } = string.Empty;
    public string name { get; set; } = string.Empty;
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "CS8785"),
            $"case-insensitive member names must not crash the Generator. Actual: {FormatDiagnostics(diagnostics)}");
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK012"),
            $"an assignable DTO with case-distinct members should remain supported. Actual: {FormatDiagnostics(diagnostics)}");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("value.Name", StringComparison.Ordinal) &&
               generated.Contains("value.name", StringComparison.Ordinal),
            "both case-distinct members must remain in the generated Codec");

        var ambiguousConstructor = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class AmbiguousConstructorDto
{
    public string Name { get; }
    public string name { get; }

    public AmbiguousConstructorDto(string NAME)
    {
        Name = NAME;
        name = NAME;
    }
}
""");
        var constructorDiagnostics = RunGenerator(ambiguousConstructor);
        Ensure(!constructorDiagnostics.Any(static diagnostic => diagnostic.Id == "CS8785"),
            $"ambiguous constructor mapping must not crash the Generator. Actual: {FormatDiagnostics(constructorDiagnostics)}");
        EnsureRuleCount(ambiguousConstructor, "SHARPLINK012", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedDictionaryReaderShouldRejectNullKeysAsDataLoss()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IDictionaryContract : SharpLink.Sdk.IService
{
    ValueTask<Dictionary<string, string>> Echo(
        Dictionary<string, string> values,
        CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("Generated dictionary contains a null key.", StringComparison.Ordinal),
            "generated dictionary readers must reject null keys before Dictionary.TryAdd");
        return Task.CompletedTask;
    }

    [Test]
    public Task NonPublicDefaultInterfaceHelpersShouldNotBecomeRpcRoutes()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelperContract : SharpLink.Sdk.IService
{
    ValueTask<int> Invoke(int value, CancellationToken cancellationToken);

    private ValueTask<int> Normalize(int value) => new(value);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains(" Normalize(", StringComparison.Ordinal) &&
               !generated.Contains(".Normalize(", StringComparison.Ordinal) &&
               !generated.Contains("\"Normalize\"", StringComparison.Ordinal),
            "non-public default interface helpers must not become generated routes");

        var nonPublicAbstract = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface INonPublicAbstractContract : SharpLink.Sdk.IService
{
    protected abstract ValueTask<int> Hidden(int value, CancellationToken cancellationToken);
}
""");
        EnsureRuleCount(nonPublicAbstract, "SHARPLINK054", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingStaticMethodDescriptorsShouldReportSharplink022()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var first = CreateMetadataReference("MethodOwnerA", BuildReferencedContractSource("ValueTask<int> Echo(int value);"), sdk);
        var second = CreateMetadataReference("MethodOwnerB", BuildReferencedContractSource("ValueTask<string> Echo(int value);"), sdk);

        EnsureHasRule(
            "namespace Consumer { public sealed class Marker; }",
            "SHARPLINK022",
            sdk,
            first,
            second);
        return Task.CompletedTask;
    }

    [Test]
    public Task MultipleStaticServicesForContractShouldReportSharplink023()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class FirstHelloService : IHelloService
{
    public ValueTask<int> Echo(int value) => new(value);
}

[SharpLink.Sdk.RpcService]
public sealed class SecondHelloService : IHelloService
{
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK023", "IHelloService");
        return Task.CompletedTask;
    }

    [Test]
    public Task MarkedServiceConstructorsShouldParticipateInStaticConflictAnalysis()
    {
        var source = BuildSource("""
namespace Microsoft.Extensions.DependencyInjection
{
    [System.AttributeUsage(System.AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : System.Attribute { }
}

[SharpLink.Sdk.RpcContract]
public interface IMarkedContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class FirstMarkedService : IMarkedContract
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public FirstMarkedService() { }
    public FirstMarkedService(string ignored) { }
    public ValueTask<int> Echo(int value) => new(value);
}

[SharpLink.Sdk.RpcService]
public sealed class SecondMarkedService : IMarkedContract
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SecondMarkedService() { }
    public SecondMarkedService(string ignored) { }
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK023", "IMarkedContract");
        return Task.CompletedTask;
    }

    [Test]
    public Task ClusterRouteShouldGenerateDeterministicSeparateManifest()
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
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"orders\", typeof(IOrdersService))]");

        var generated = RunGeneratorAndGetSources(source);
        var route = generated.Single(text => text.Contains("GeneratedClusterRouteManifest", StringComparison.Ordinal));
        Ensure(route.Contains("new SharpLinkClusterKey(\"orders\")", StringComparison.Ordinal),
            "cluster route should preserve the declared key");
        Ensure(route.Contains("SharpLinkGeneratedClusterRouteCatalog.Register", StringComparison.Ordinal),
            "cluster route manifest should register from a module initializer");
        Ensure(route.Contains("System.Array.AsReadOnly(__routes)", StringComparison.Ordinal),
            "cluster route manifest must not expose its generated array");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidClusterRouteKeyShouldReportSharplink038()
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
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"bad key\", typeof(IOrdersService))]");

        EnsureHasRule(source, "SHARPLINK038");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingClusterRouteShouldReportSharplink039()
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
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"orders\", typeof(IOrdersService))]\n" +
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"payments\", typeof(IOrdersService))]");

        EnsureHasRule(source, "SHARPLINK039");
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

    [Test]
    public Task NullRouteMarkerShouldReportSharplink041()
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
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"orders\", null)]");

        EnsureHasRule(source, "SHARPLINK041");
        return Task.CompletedTask;
    }

    [Test]
    public Task RegisteredSelectorShouldGenerateClosedAdapterFactoryWithoutReflection()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[FakePackable]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class FakePackableAttribute : Attribute { }

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("adapterScope.CreateCodec<global::Graph>()", StringComparison.Ordinal),
            "Adapter factory must emit a closed generic Codec creation");
        Ensure(generated.Contains("public Type TargetType => typeof(global::Graph);", StringComparison.Ordinal),
            "Adapter factory target type");
        Ensure(generated.Contains("fake.adapter/v1", StringComparison.Ordinal), "Adapter ID");
        Ensure(generated.Contains("fake-wire/v1", StringComparison.Ordinal), "Wire Format ID");
        Ensure(!generated.Contains("FakeAdapter, Version=", StringComparison.Ordinal),
            "Adapter implementation assemblies are normal runtime references, not dynamic Manifest dependencies");
        Ensure(!generated.Contains("MakeGenericType", StringComparison.Ordinal), "no MakeGenericType");
        Ensure(!generated.Contains("Activator.CreateInstance", StringComparison.Ordinal), "no Activator");
        Ensure(!generated.Contains("Serialize(Type", StringComparison.Ordinal), "no non-generic Serialize API");
        Ensure(!generated.Contains("Deserialize(Type", StringComparison.Ordinal), "no non-generic Deserialize API");
        EnsureDoesNotHaveRule(source, "SHARPLINK010");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitAdapterBindingShouldSelectRegisteredAdapter()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(FakeAdapter))]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\")]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::Graph>()", StringComparison.Ordinal),
            "explicit binding generates Adapter factory");
        EnsureDoesNotHaveRule(source, "SHARPLINK010");
        return Task.CompletedTask;
    }

    [Test]
    public Task NamedTupleAssemblyBindingShouldSelectRegisteredAdapter()
    {
        var source = AddAssemblyAttribute(AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface ITupleService : SharpLink.Sdk.IService
{
    ValueTask<(int Index, string Label)> Echo((int Index, string Label) value);
}

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\")]"),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(ValueTuple<int, string>), typeof(FakeAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<(int Index, string Label)>()", StringComparison.Ordinal),
            "named tuple resolves through its underlying ValueTuple binding");
        EnsureDoesNotHaveRule(source, "SHARPLINK009");
        return Task.CompletedTask;
    }

    [Test]
    public Task SelectorShouldOverrideUnmanagedNativeFallback()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[FakePackable]
public readonly struct Point
{
    public int X { get; init; }
    public int Y { get; init; }
}

[SharpLink.Sdk.RpcContract]
public interface IPointService : SharpLink.Sdk.IService
{
    ValueTask<Point> Echo(Point value);
}

[AttributeUsage(AttributeTargets.Struct)]
public sealed class FakePackableAttribute : Attribute { }

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::Point>()", StringComparison.Ordinal),
            "a selected Adapter must win for an unmanaged user-defined struct");
        Ensure(generated.Contains("__codec_value = codecs.GetCodec<global::Point>();", StringComparison.Ordinal),
            "an unmanaged request must resolve the selected Adapter Codec");
        Ensure(generated.Contains("__codec_value.Serialize(value.value, writer);", StringComparison.Ordinal),
            "an unmanaged request must be length-delimited through the selected Adapter Codec");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAdapterRegistrationShouldReportSharplink043()
    {
        var source = AddAssemblyAttribute(BuildSource("""
public sealed class InvalidAdapter { }
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(InvalidAdapter), \"invalid/v1\", \"wire/v1\")]");
        EnsureHasRule(source, "SHARPLINK043");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAdapterRegistrationShapesShouldReportSharplink042()
    {
        var declarations = """
public sealed class ValidAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "valid.adapter/v1";
    public string WireFormatId => "valid-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

public sealed class NotAnAttribute { }
""";
        var invalidAttributes = new[]
        {
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ValidAdapter), \"\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ValidAdapter), \"adapter/v1\", \"\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ValidAdapter), \"adapter/v1\", \"wire/é\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ValidAdapter), \"adapter/v1\", \"wire/v1\", SelectorAttributeType = typeof(NotAnAttribute))]"
        };

        foreach (var attribute in invalidAttributes)
            EnsureHasRule(AddAssemblyAttribute(BuildSource(declarations), attribute), "SHARPLINK042");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAdapterTypeShapesShouldReportSharplink043()
    {
        var source = AddAssemblyAttributes(BuildSource("""
public class NonSealedAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "nonsealed/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

internal sealed class NonPublicAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "nonpublic/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

public sealed class NoPublicConstructorAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    private NoPublicConstructorAdapter() { }
    public string AdapterId => "no-ctor/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

public sealed class DoesNotImplementAdapter { }
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(NonSealedAdapter), \"nonsealed/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(NonPublicAdapter), \"nonpublic/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(NoPublicConstructorAdapter), \"no-ctor/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(DoesNotImplementAdapter), \"no-interface/v1\", \"wire/v1\")]");

        EnsureRuleCount(source, "SHARPLINK043", 4);
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterNestedInNonPublicTypeShouldReportSharplink043()
    {
        var source = AddAssemblyAttribute(BuildSource("""
internal static class HiddenContainer
{
    public sealed class NestedAdapter : SharpLink.Abstractions.IRpcCodecAdapter
    {
        public string AdapterId => "nested/v1";
        public string WireFormatId => "wire/v1";
        public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
    }
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(HiddenContainer.NestedAdapter), \"nested/v1\", \"wire/v1\")]");

        EnsureHasRule(source, "SHARPLINK043");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingSelectorRegistrationsShouldReportSharplink044()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[AttributeUsage(AttributeTargets.Class)]
public sealed class SharedSelectorAttribute : Attribute { }

public sealed class FirstAdapter : AdapterBase { }
public sealed class SecondAdapter : AdapterBase { }
public abstract class AdapterBase : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => GetType().Name;
    public string WireFormatId => GetType().Name;
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"first/v1\", \"wire/v1\", SelectorAttributeType = typeof(SharedSelectorAttribute))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SecondAdapter), \"second/v1\", \"wire/v1\", SelectorAttributeType = typeof(SharedSelectorAttribute))]");

        EnsureRuleCount(source, "SHARPLINK044", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingAdapterSelectionShouldReportSharplink045()
    {
        var source = AddAssemblyAttribute(AddAssemblyAttribute(BuildSource("""
[FirstSelector]
[SharpLink.Sdk.RpcCodecAdapter(typeof(SecondAdapter))]
public sealed class Graph { }

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

[AttributeUsage(AttributeTargets.Class)] public sealed class FirstSelectorAttribute : Attribute { }
public sealed class FirstAdapter : TestAdapterBase { }
public sealed class SecondAdapter : TestAdapterBase { }
public abstract class TestAdapterBase : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => GetType().Name;
    public string WireFormatId => GetType().Name;
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"first/v1\", \"first-wire/v1\", SelectorAttributeType = typeof(FirstSelectorAttribute))]"),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SecondAdapter), \"second/v1\", \"second-wire/v1\")]");
        EnsureHasRule(source, "SHARPLINK045");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAdapterAttributeFormsShouldReportSharplink046()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(Graph), typeof(FakeAdapter))]
public sealed class Graph { }

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

public sealed class FakeAdapter { }
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(FakeAdapter))]");

        EnsureRuleCount(source, "SHARPLINK046", 2);
        return Task.CompletedTask;
    }

    [Test]
    public Task OpenGenericAdapterTargetShouldReportSharplink047()
    {
        var source = AddAssemblyAttribute(BuildSource("public sealed class FakeAdapter { }"),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(List<>), typeof(FakeAdapter))]");

        EnsureHasRule(source, "SHARPLINK047");
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterIdentityConflictsShouldReportSharplink048()
    {
        var sameTypeDifferentIdentity = AddAssemblyAttributes(BuildSource("""
public sealed class FirstAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "first/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"first/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"second/v1\", \"wire/v1\")]");
        EnsureHasRuleContaining(sameTypeDifferentIdentity, "SHARPLINK048", "same Adapter type");

        var sameIdDifferentType = AddAssemblyAttributes(BuildSource("""
public sealed class FirstAdapter : AdapterBase { }
public sealed class SecondAdapter : AdapterBase { }
public abstract class AdapterBase : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "shared/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"shared/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SecondAdapter), \"shared/v1\", \"wire/v1\")]");
        EnsureHasRuleContaining(sameIdDifferentType, "SHARPLINK048", "Adapter ID 'shared/v1'");

        var sameIdDifferentWire = AddAssemblyAttributes(BuildSource("""
public sealed class FirstAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "shared/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"shared/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"shared/v1\", \"other-wire/v1\")]");
        EnsureHasRuleContaining(sameIdDifferentWire, "SHARPLINK048", "same Adapter type");
        return Task.CompletedTask;
    }

    [Test]
    public Task BuiltinAdapterBindingShouldReportSharplink049()
    {
        var source = AddAssemblyAttribute(BuildSource("public sealed class FakeAdapter { }"),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(FakeAdapter))]");

        EnsureHasRule(source, "SHARPLINK049");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnregisteredSelectedAdapterShouldReportSharplink042()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(FakeAdapter))]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

public sealed class FakeAdapter { }
""");

        EnsureHasRuleContaining(source, "SHARPLINK042", "no valid RpcCodecAdapterRegistration");
        return Task.CompletedTask;
    }

    [Test]
    public Task EquivalentAdapterCandidatesShouldBeIdempotent()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[FakePackable]
[SharpLink.Sdk.RpcCodecAdapter(typeof(FakeAdapter))]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class FakePackableAttribute : Attribute { }

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(Graph), typeof(FakeAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        EnsureDoesNotHaveRule(source, "SHARPLINK045");
        Ensure(CountOccurrences(generated, "CreateCodec<global::Graph>()") == 1,
            "equivalent type, assembly, and selector candidates emit one factory");
        return Task.CompletedTask;
    }

    [Test]
    public Task RegisteredAdapterShouldNotReplaceSupportedNativeDto()
    {
        var source = AddAssemblyAttribute(BuildSource("""
public sealed class NativePayload
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[SharpLink.Sdk.RpcContract]
public interface INativeService : SharpLink.Sdk.IService
{
    ValueTask<NativePayload> Echo(NativePayload value);
}

public sealed class InstalledAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "installed/v1";
    public string WireFormatId => "installed-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(InstalledAdapter), \"installed/v1\", \"installed-wire/v1\")]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("IRpcCodec<global::NativePayload>", StringComparison.Ordinal),
            "supported DTO retains its native generated Codec");
        Ensure(generated.Contains("WireFormatId => \"sharplink-native/v1\"", StringComparison.Ordinal),
            "supported DTO retains the native wire identity");
        Ensure(!generated.Contains("CreateCodec<global::NativePayload>()", StringComparison.Ordinal),
            "installed Adapter is not an automatic fallback");
        Ensure(!generated.Contains("installed-wire/v1", StringComparison.Ordinal),
            "unused Adapter metadata is not emitted");
        return Task.CompletedTask;
    }

    [Test]
    public Task InstalledUnselectedAdapterShouldNotFallbackForUnsupportedDto()
    {
        var source = AddAssemblyAttribute(BuildSource("""
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

public sealed class InstalledAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "installed/v1";
    public string WireFormatId => "installed-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(InstalledAdapter), \"installed/v1\", \"installed-wire/v1\")]");

        EnsureHasRule(source, "SHARPLINK010");
        return Task.CompletedTask;
    }

    [Test]
    public Task TransitiveAdapterRegistrationShouldBeDiscoveredFromMetadata()
    {
        var sdk = CreateMetadataReference("AdapterMetadataSdk", BuildSource(string.Empty));
        var adapter = CreateAdapterPackageReference(
            "MetadataAdapterPackage",
            "MetadataAdapterPackage",
            "MetadataAdapter",
            "MetadataSelectorAttribute",
            "metadata.adapter/v1",
            "metadata-wire/v1",
            sdk);
        var bridge = CreateMetadataReference(
            "MetadataAdapterBridge",
            "namespace MetadataAdapterBridge { public sealed class Marker { public MetadataAdapterPackage.MetadataAdapter Adapter { get; } = new(); } }",
            sdk,
            adapter);
        var source = """
using System.Threading.Tasks;
using MetadataAdapterPackage;

[MetadataSelector]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

public sealed class CompileReference
{
    public MetadataAdapterBridge.Marker? Marker { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}
""";

        var generated = string.Join("\n", RunGeneratorAndGetSources(source, sdk, bridge, adapter));
        Ensure(generated.Contains("CreateCodec<global::Graph>()", StringComparison.Ordinal),
            "registration from the transitive compilation reference closure selects the Adapter");
        Ensure(generated.Contains("metadata.adapter/v1", StringComparison.Ordinal), "metadata Adapter ID");
        Ensure(generated.Contains("metadata-wire/v1", StringComparison.Ordinal), "metadata Wire Format ID");
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterOutputShouldBeDeterministicAcrossReferenceAndAttributeOrder()
    {
        var sdk = CreateMetadataReference("DeterministicAdapterSdk", BuildSource(string.Empty));
        var firstAdapter = CreateAdapterPackageReference(
            "FirstAdapterPackage", "FirstAdapterPackage", "FirstAdapter", "FirstSelectorAttribute",
            "first.adapter/v1", "first-wire/v1", sdk);
        var secondAdapter = CreateAdapterPackageReference(
            "SecondAdapterPackage", "SecondAdapterPackage", "SecondAdapter", "SecondSelectorAttribute",
            "second.adapter/v1", "second-wire/v1", sdk);
        const string body = """
[FirstSelector]
public sealed class FirstGraph { public FirstGraph? Parent { get; set; } }

[SecondSelector]
public sealed class SecondGraph { public SecondGraph? Parent { get; set; } }

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<FirstGraph> EchoFirst(FirstGraph value);
    ValueTask<SecondGraph> EchoSecond(SecondGraph value);
}
""";
        var firstSource = $$"""
using System.Threading.Tasks;
using FirstAdapterPackage;
using SecondAdapterPackage;
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(FirstGraph), typeof(FirstAdapterPackage.FirstAdapter))]
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(SecondGraph), typeof(SecondAdapterPackage.SecondAdapter))]
{{body}}
""";
        var secondSource = $$"""
using System.Threading.Tasks;
using FirstAdapterPackage;
using SecondAdapterPackage;
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(SecondGraph), typeof(SecondAdapterPackage.SecondAdapter))]
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(FirstGraph), typeof(FirstAdapterPackage.FirstAdapter))]
{{body}}
""";

        var first = RunGeneratorAndGetSources(firstSource, sdk, firstAdapter, secondAdapter);
        var second = RunGeneratorAndGetSources(secondSource, secondAdapter, firstAdapter, sdk);

        Ensure(first.SequenceEqual(second, StringComparer.Ordinal),
            "reference and equivalent Attribute ordering must not change generated output");
        return Task.CompletedTask;
    }

    [Test]
    public Task MultipleTargetsShouldShareOneGeneratedAdapterHolder()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[FakePackable]
public sealed class FirstGraph { public FirstGraph? Parent { get; set; } }

[FakePackable]
public sealed class SecondGraph { public SecondGraph? Parent { get; set; } }

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<FirstGraph> EchoFirst(FirstGraph value);
    ValueTask<SecondGraph> EchoSecond(SecondGraph value);
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class FakePackableAttribute : Attribute { }

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(CountOccurrences(generated, "new global::FakeAdapter();") == 1,
            "one Manifest emits one Adapter holder for all targets sharing an Adapter ID");
        Ensure(CountOccurrences(generated, "CreateCodec<global::FirstGraph>()") == 1, "first closed target");
        Ensure(CountOccurrences(generated, "CreateCodec<global::SecondGraph>()") == 1, "second closed target");
        return Task.CompletedTask;
    }

    [Test]
    public Task InaccessibleGeneratedServiceAndDtoTypesShouldReportSharpLinkDiagnostics()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHiddenArtifactContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

public static class HiddenArtifactContainer
{
    [SharpLink.Sdk.RpcService]
    private sealed class HiddenService : IHiddenArtifactContract
    {
        public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
    }

    [SharpLink.Sdk.RpcSerializable]
    private sealed class HiddenDto
    {
        public int Value { get; set; }
    }
}
""");

        EnsureRuleCount(source, "SHARPLINK018", 1);
        EnsureRuleCount(source, "SHARPLINK009", 1);

        var allowed = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IAllowedArtifactContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

public class AllowedArtifactContainer
{
    [SharpLink.Sdk.RpcService]
    protected internal sealed class AllowedService : IAllowedArtifactContract
    {
        public AllowedService() { }
        public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
    }
}

[SharpLink.Sdk.RpcSerializable]
internal sealed class InternalDto
{
    public int Value { get; set; }
}
""");
        EnsureDoesNotHaveRule(allowed, "SHARPLINK018");
        EnsureDoesNotHaveRule(allowed, "SHARPLINK009");
        var generated = string.Join("\n", RunGeneratorAndGetSources(allowed));
        Ensure(generated.Contains("global::AllowedArtifactContainer.AllowedService", StringComparison.Ordinal),
            "protected-internal services must remain accessible to sibling generated code");
        Ensure(generated.Contains("global::InternalDto", StringComparison.Ordinal),
            "internal DTOs must remain accessible to sibling generated code");
        return Task.CompletedTask;
    }

    [Test]
    public Task KeywordDtoMembersShouldUseSafeGeneratedLocalNames()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class KeywordDto
{
    [SharpLink.Sdk.RpcRequired]
    public int @class { get; set; }
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("local_@class", StringComparison.Ordinal),
            "escaped member syntax must not be embedded inside a generated local identifier");
        Ensure(!generated.Contains("seen_@class", StringComparison.Ordinal),
            "escaped member syntax must not be embedded inside a generated presence identifier");
        Ensure(generated.Contains("local_class", StringComparison.Ordinal) &&
               generated.Contains("seen_class", StringComparison.Ordinal) &&
               generated.Contains("value.@class", StringComparison.Ordinal),
            "generated locals and escaped member access must remain distinct");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnsealedRecordDtoShouldBeRejectedBeforeDerivedStateCanBeSliced()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public record BasePayload(int Value);

public sealed record DerivedPayload(int Value, int Extra) : BasePayload(Value);
""");

        EnsureRuleCount(source, "SHARPLINK009", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task RefLikeDtoShouldBeRejectedWithoutEmittingBrokenContractArtifacts()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(RefPayloadAdapter))]
[SharpLink.Sdk.RpcSerializable]
public ref struct RefPayload
{
    public int Value;
}

[SharpLink.Sdk.RpcContract]
public interface IRefPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<int> Send(RefPayload payload, CancellationToken cancellationToken);
}

public sealed class RefPayloadAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "ref.adapter/v1";
    public string WireFormatId => "ref-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RefPayloadAdapter), \"ref.adapter/v1\", \"ref-wire/v1\")]");

        EnsureRuleCount(source, "SHARPLINK009", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IRefPayloadContract",
                StringComparison.Ordinal),
            "a ref-like payload must suppress contract artifacts that cannot use it as a generic argument");
        return Task.CompletedTask;
    }

    [Test]
    public Task StaticAbstractOperatorsShouldRejectRpcContractGeneration()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IOperatorContract : SharpLink.Sdk.IService
{
    static abstract IOperatorContract operator +(IOperatorContract left, IOperatorContract right);
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK054", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IOperatorContract",
                StringComparison.Ordinal),
            "a contract with an unimplementable static abstract operator must not emit a Proxy");
        return Task.CompletedTask;
    }

    [Test]
    public Task ServiceConstructorsMustBeRepresentableByGeneratedDiActivation()
    {
        var source = BuildSource("""
public sealed class Dependency;
public ref struct StackDependency;

[SharpLink.Sdk.RpcContract]
public interface IRefConstructorService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class RefConstructorService : IRefConstructorService
{
    public RefConstructorService(ref Dependency dependency) { }
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}

[SharpLink.Sdk.RpcContract]
public interface IStackConstructorService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class StackConstructorService : IStackConstructorService
{
    public StackConstructorService(StackDependency dependency) { }
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}

[SharpLink.Sdk.RpcContract]
public interface IPointerConstructorService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class PointerConstructorService : IPointerConstructorService
{
    public unsafe PointerConstructorService(int* dependency) { }
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}

[SharpLink.Sdk.RpcContract]
public interface IRefReadonlyConstructorService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class RefReadonlyConstructorService : IRefReadonlyConstructorService
{
    public RefReadonlyConstructorService(ref readonly Dependency dependency) { }
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}
""");

        EnsureRuleCount(source, "SHARPLINK019", 4);
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("typeof(global::RefConstructorService)", StringComparison.Ordinal),
            "a ref dependency must suppress its generated service descriptor");
        Ensure(!generated.Contains("typeof(global::StackConstructorService)", StringComparison.Ordinal),
            "a ref-like dependency must suppress its generated service descriptor");
        Ensure(!generated.Contains("typeof(global::PointerConstructorService)", StringComparison.Ordinal),
            "a pointer dependency must suppress its generated service descriptor");
        Ensure(!generated.Contains("typeof(global::RefReadonlyConstructorService)", StringComparison.Ordinal),
            "a ref-readonly dependency must suppress a generated call that requires addressable storage");
        return Task.CompletedTask;
    }

    [Test]
    public Task IgnoredRequiredDtoMembersNeedACompilerValidConstructionPlan()
    {
        var invalid = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class IgnoredRequiredDto
{
    public int Value { get; set; }

    [SharpLink.Sdk.RpcIgnore]
    public required string Secret { get; init; }
}
""");

        EnsureRuleCount(invalid, "SHARPLINK012", 1);

        var valid = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class RequiredMembersSatisfiedDto
{
    public int Value { get; set; }

    [SharpLink.Sdk.RpcIgnore]
    public required string Secret { get; init; }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public RequiredMembersSatisfiedDto() => Secret = string.Empty;
}
""");
        EnsureDoesNotHaveRule(valid, "SHARPLINK012");

        var requiredField = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class RequiredFieldDto
{
    public required int Value;

    public RequiredFieldDto(int value) => Value = value;
}
""");
        EnsureDoesNotHaveRule(requiredField, "SHARPLINK012");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(requiredField)).Contains(
                "Value = local_Value",
                StringComparison.Ordinal),
            "a compiler-required field must remain in the generated object initializer even when constructor-bound");
        return Task.CompletedTask;
    }

    [Test]
    public Task ByReferenceDtoConstructorsMustNotBeSelectedForGeneratedCalls()
    {
        var invalid = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class RefConstructorDto
{
    public int Value { get; }

    public RefConstructorDto(ref int value) => Value = value;
}

[SharpLink.Sdk.RpcSerializable]
public sealed class RefReadonlyConstructorDto
{
    public int Value { get; }

    public RefReadonlyConstructorDto(ref readonly int value) => Value = value;
}
""");
        EnsureRuleCount(invalid, "SHARPLINK012", 2);

        var validFallback = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class FallbackConstructorDto
{
    public int Value { get; }

    public FallbackConstructorDto(ref int value) => Value = value;
    public FallbackConstructorDto(int value) => Value = value;
}
""");
        EnsureDoesNotHaveRule(validFallback, "SHARPLINK012");
        return Task.CompletedTask;
    }

    [Test]
    public Task PointerPayloadDiagnosticsMustSuppressBrokenContractArtifacts()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public unsafe interface IPointerPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<int> SendPointer(int* value, CancellationToken cancellationToken);
    ValueTask<int> SendFunction(delegate*<int, int> callback, CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK009", 2);
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("IPointerPayloadContract", StringComparison.Ordinal),
            "pointer payloads must suppress all contract artifacts that cannot represent them");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedRequestWireFailuresMustUseStructuredDataLoss()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IRequestDataLossContract : SharpLink.Sdk.IService
{
    ValueTask<int> Validate(
        bool enabled,
        string name,
        CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("throw new InvalidDataException", StringComparison.Ordinal),
            "peer-controlled generated request wire failures must not leak unstructured InvalidDataException");
        Ensure(CountOccurrences(generated, "throw RpcGeneratedCodecWire.DataLoss(") >= 8,
            "request Codec and Stub must classify marker, truncation, length, null, and trailing failures as DataLoss");
        return Task.CompletedTask;
    }

    [Test]
    public Task EmptyInvocationCategoriesMustUseStructuredUnimplemented()
    {
        var responseOnly = string.Join("\n", RunGeneratorAndGetSources(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IResponseOnlyContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(CancellationToken cancellationToken);
}
""")));
        var noResponseOnly = string.Join("\n", RunGeneratorAndGetSources(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface INoResponseOnlyContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Oneway]
    ValueTask Notify(CancellationToken cancellationToken);
}
""")));

        Ensure(!responseOnly.Contains("RpcException", StringComparison.Ordinal) &&
               !noResponseOnly.Contains("RpcException", StringComparison.Ordinal),
            "empty invocation categories must not emit the legacy unstructured exception");
        Ensure(responseOnly.Contains("SharpLinkErrorCode.Unimplemented", StringComparison.Ordinal) &&
               noResponseOnly.Contains("SharpLinkErrorCode.Unimplemented", StringComparison.Ordinal),
            "both empty invocation categories must return structured Unimplemented");
        return Task.CompletedTask;
    }

    [Test]
    public Task CustomRpcCodecShouldEmitAStableGeneratedFactory()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcCodec(typeof(MoneyCodec))]
public sealed record Money(decimal Value);

[SharpLink.Sdk.RpcCodecImplementation("money-wire/v1", "money-schema/v1")]
public sealed class MoneyCodec : SharpLink.Abstractions.IRpcCodec<Money>
{
}

[SharpLink.Sdk.RpcContract]
public interface IMoneyService : SharpLink.Sdk.IService
{
    ValueTask<Money> Convert(Money value, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("internal sealed class Factory : IRpcGeneratedCodecFactory", StringComparison.Ordinal),
            "custom Codec binding must emit an IRpcGeneratedCodecFactory");
        Ensure(generated.Contains("new global::MoneyCodec()", StringComparison.Ordinal),
            "custom Codec factory must construct the bound implementation directly");
        Ensure(generated.Contains("\"money-wire/v1\"", StringComparison.Ordinal) &&
               generated.Contains("SchemaId => \"global::Money:", StringComparison.Ordinal),
            "custom Codec wire/schema identity must be emitted into the manifest");
        return Task.CompletedTask;
    }

    [Test]
    public Task CustomRpcCodecWithoutStableIdentityShouldReportSharplink061()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcCodec(typeof(MoneyCodec))]
public sealed record Money(decimal Value);

public sealed class MoneyCodec : SharpLink.Abstractions.IRpcCodec<Money>
{
}

[SharpLink.Sdk.RpcContract]
public interface IMoneyService : SharpLink.Sdk.IService
{
    ValueTask<Money> Convert(Money value, CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK061", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task AssemblyLevelCustomRpcCodecShouldBindExternalType()
    {
        var source = AddAssemblyAttribute(BuildSource("""
public sealed record ThirdPartyMoney(decimal Value);

[SharpLink.Sdk.RpcCodecImplementation("third-party/v1", "third-party-schema/v1")]
public sealed class ThirdPartyMoneyCodec : SharpLink.Abstractions.IRpcCodec<ThirdPartyMoney>
{
}

[SharpLink.Sdk.RpcContract]
public interface IThirdPartyMoneyService : SharpLink.Sdk.IService
{
    ValueTask<ThirdPartyMoney> Convert(ThirdPartyMoney value, CancellationToken cancellationToken);
}
"""), "[assembly: SharpLink.Sdk.RpcCodec(typeof(ThirdPartyMoney), typeof(ThirdPartyMoneyCodec))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("new global::ThirdPartyMoneyCodec()", StringComparison.Ordinal),
            "assembly-level custom Codec binding must be used for the external payload type");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReferencedContractAssemblyCustomCodecBindingShouldBeDiscovered()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));
        var external = CreateMetadataReference(
            "ExternalCustomCodec",
            """
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: RpcCodec(typeof(ExternalMoney), typeof(ExternalMoneyCodec))]

public sealed record ExternalMoney(decimal Value);

[RpcCodecImplementation("external-wire/v1", "external-schema/v1")]
public sealed class ExternalMoneyCodec : IRpcCodec<ExternalMoney>
{
}
""",
            sdk);
        var source = """
using System.Threading;
using System.Threading.Tasks;
using ExternalCustomCodec;
using SharpLink.Sdk;

[RpcContract]
public interface IExternalMoneyService : IService
{
    ValueTask<ExternalMoney> Convert(ExternalMoney value, CancellationToken cancellationToken);
}
""";

        var generated = string.Join("\n", RunGeneratorAndGetSources(source, sdk, external));
        Ensure(generated.Contains("new global::ExternalMoneyCodec()", StringComparison.Ordinal),
            "referenced Contract assembly custom Codec binding must be discovered from the compilation reference closure");
        Ensure(generated.Contains("\"external-wire/v1\"", StringComparison.Ordinal),
            "referenced custom Codec wire identity must be emitted into the manifest");
        return Task.CompletedTask;
    }

    private static string BuildSource(string contract)
    {
        return $$"""
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Sdk
{
    public interface IService
    {
    }

    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class RpcContractAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class SharpLinkClusterContractAssemblyAttribute : Attribute
    {
        public SharpLinkClusterContractAssemblyAttribute(string cluster, Type assemblyMarker)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TimeoutAttribute : Attribute
    {
        public TimeoutAttribute(double seconds)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OnewayAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NonCancellableAttribute : Attribute
    {
    }

    public enum SharpLinkServiceLifetime
    {
        Singleton,
        Connection,
        Call
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcServiceAttribute : Attribute
    {
        public SharpLinkServiceLifetime Lifetime { get; set; } = SharpLinkServiceLifetime.Singleton;
    }

    public readonly record struct SharpLinkCallOptions;

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RpcSerializableAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RpcMemberAttribute(int id) : Attribute
    {
        public int Id { get; } = id;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RpcIgnoreAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RpcRequiredAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class RpcUnionCaseAttribute(int tag, Type caseType) : Attribute
    {
        public int Tag { get; } = tag;
        public Type CaseType { get; } = caseType;
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RpcCodecAdapterRegistrationAttribute : Attribute
    {
        public RpcCodecAdapterRegistrationAttribute(Type adapterType, string adapterId, string wireFormatId) { }
        public Type? SelectorAttributeType { get; set; }
    }

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public sealed class RpcCodecAdapterAttribute : Attribute
    {
        public RpcCodecAdapterAttribute(Type adapterType) { }
        public RpcCodecAdapterAttribute(Type targetType, Type adapterType) { }
    }

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public sealed class RpcCodecAttribute : Attribute
    {
        public RpcCodecAttribute(Type codecType) { }
        public RpcCodecAttribute(Type targetType, Type codecType) { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RpcCodecImplementationAttribute : Attribute
    {
        public RpcCodecImplementationAttribute(string wireFormatId, string schemaId) { }
    }
}

namespace SharpLink.Abstractions
{
    public interface IRpcCodec { }
    public interface IRpcCodec<T> : IRpcCodec { }
    public interface IRpcCodecAdapter
    {
        string AdapterId { get; }
        string WireFormatId { get; }
        IRpcCodecAdapterScope CreateScope();
    }
    public interface IRpcCodecAdapterScope : IDisposable
    {
        IRpcCodec<T> CreateCodec<T>();
    }
}

{{contract}}
""";
    }

    private static string BuildDirectStringDtoSource(params int[] fieldCounts)
    {
        var source = new StringBuilder();
        foreach (var fieldCount in fieldCounts)
        {
            source.AppendLine("[SharpLink.Sdk.RpcSerializable]");
            source.Append("public sealed class DirectStrings").Append(fieldCount).AppendLine();
            source.AppendLine("{");
            for (var fieldId = 1; fieldId <= fieldCount; fieldId++)
            {
                source.Append("    [SharpLink.Sdk.RpcMember(").Append(fieldId).Append(")] public string Field")
                    .Append(fieldId.ToString("D2"))
                    .AppendLine(" { get; set; } = string.Empty;");
            }
            source.AppendLine("}");
        }
        return BuildSource(source.ToString());
    }

    private static string AddAssemblyAttribute(string source, string attribute)
        => source.Replace("namespace SharpLink.Sdk", attribute + "\n\nnamespace SharpLink.Sdk", StringComparison.Ordinal);

    private static string AddAssemblyAttributes(string source, params string[] attributes)
    {
        foreach (var attribute in attributes)
            source = AddAssemblyAttribute(source, attribute);
        return source;
    }

    private static void EnsureHasRule(string source, string ruleId)
    {
        var diagnostics = RunGenerator(source);
        var has = diagnostics.Any(d => d.Id == ruleId);
        Ensure(has, $"Expected diagnostic {ruleId}, but it was not reported.");
    }

    private static void EnsureHasRule(
        string source,
        string ruleId,
        params MetadataReference[] additionalReferences)
    {
        var diagnostics = RunGenerator(source, additionalReferences);
        var has = diagnostics.Any(d => d.Id == ruleId);
        Ensure(has, $"Expected diagnostic {ruleId}, but it was not reported. Actual: {FormatDiagnostics(diagnostics)}");
    }

    private static void EnsureHasRuleContaining(string source, string ruleId, string expectedText)
    {
        var diagnostics = RunGenerator(source);
        var hit = diagnostics.FirstOrDefault(d => d.Id == ruleId);
        if (hit is null)
            throw new Exception($"Expected diagnostic {ruleId}, but it was not reported. Actual: {FormatDiagnostics(diagnostics)}");
        Ensure(hit.GetMessage().Contains(expectedText, StringComparison.Ordinal),
            $"Expected diagnostic {ruleId} to mention '{expectedText}', but got '{hit.GetMessage()}'.");
    }

    private static void EnsureRuleCount(string source, string ruleId, int expectedCount)
    {
        var diagnostics = RunGenerator(source);
        var hits = diagnostics.Count(d => d.Id == ruleId);
        Ensure(hits == expectedCount,
            $"Expected {expectedCount} diagnostic(s) for {ruleId}, but got {hits}. Actual: {FormatDiagnostics(diagnostics)}");
    }

    private static void EnsureDoesNotHaveRule(string source, string ruleId)
    {
        var diagnostics = RunGenerator(source);
        var has = diagnostics.Any(d => d.Id == ruleId);
        Ensure(!has, $"Did not expect diagnostic {ruleId}.");
    }

    private static ImmutableArray<Diagnostic> RunGenerator(
        string source,
        params MetadataReference[] additionalReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerTestAssembly",
            syntaxTrees: [syntaxTree],
            references: GetPlatformReferences().Concat(additionalReferences),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Diagnostics;
    }

    private static string[] RunGeneratorAndGetSources(
        string source,
        params MetadataReference[] additionalReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorShapeTestAssembly",
            syntaxTrees: [syntaxTree],
            references: GetPlatformReferences().Concat(additionalReferences),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().GeneratedTrees
            .Select(static tree => tree.GetText().ToString())
            .ToArray();
    }

    private static void EnsureGeneratorOutputCompiles(
        string source,
        params MetadataReference[] additionalReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratedBootstrapCompilationTest",
            syntaxTrees: [syntaxTree],
            references: GetPlatformReferences().Concat(additionalReferences),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        var errors = generatorDiagnostics
            .Concat(outputCompilation.GetDiagnostics())
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Ensure(errors.Length == 0,
            $"Generated consumer bootstrap did not compile: {FormatDiagnostics(errors)}");
    }

    private static string GetReferencedManifestBootstrap(string[] generated)
        => generated.FirstOrDefault(static text =>
                text.Contains("__SharpLinkGeneratedReferencedAssemblyBootstrap", StringComparison.Ordinal))
            ?? throw new Exception("Expected a referenced-assembly bootstrap source.");

    private static string GetGeneratedManifest(string source)
    {
        var generated = RunGeneratorAndGetSources(source);
        return generated.FirstOrDefault(static text => text.Contains("__SharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal))
            ?? throw new Exception("Expected generated assembly manifest source.");
    }

    private static string GetFirstGeneratedMethodFingerprint(string source)
    {
        var manifest = GetGeneratedManifest(source);
        const string marker = "new SharpLinkGeneratedMethodDescriptor(";
        var start = manifest.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new Exception("Expected generated method descriptor.");
        var end = manifest.IndexOf("),", start, StringComparison.Ordinal);
        if (end < 0)
            throw new Exception("Expected generated method descriptor terminator.");
        var quotedLines = manifest[start..end]
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith("\"", StringComparison.Ordinal))
            .ToArray();
        if (quotedLines.Length < 4)
            throw new Exception("Expected generated method fingerprint line.");
        return quotedLines[^1].TrimEnd(',').Trim('"');
    }

    private static string GetFirstGeneratedCodecSchema(string source)
        => string.Join("\n", RunGeneratorAndGetSources(source))
            .Split('\n')
            .Select(static line => line.Trim())
            .First(static line => line.StartsWith("public string SchemaId =>", StringComparison.Ordinal));

    private static MetadataReference CreateMetadataReference(
        string assemblyName,
        string source,
        params MetadataReference[] additionalReferences)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default)],
            GetPlatformReferences().Concat(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        Ensure(emit.Success,
            $"Failed to build metadata fixture '{assemblyName}': {FormatDiagnostics(emit.Diagnostics)}");
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static MetadataReference CreateManifestInfrastructureReference()
        => CreateMetadataReference(
            "SharpLink.ManifestFixture.Abstractions",
            """
using System;

namespace SharpLink.Abstractions
{
    public interface ISharpLinkGeneratedAssemblyManifest { }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class SharpLinkGeneratedAssemblyManifestAttribute : Attribute
    {
        public SharpLinkGeneratedAssemblyManifestAttribute(Type manifestType) { }
        public SharpLinkGeneratedAssemblyManifestAttribute(
            Type manifestType,
            int apiVersion,
            int protocolVersion,
            string generatorVersion) { }
    }

    public static class SharpLinkGeneratedAssemblyCatalog
    {
        public static void Register(ISharpLinkGeneratedAssemblyManifest manifest) { }
    }
}
""");

    private static MetadataReference CreateGeneratedManifestReference(
        string assemblyName,
        string manifestTypeName,
        string internalServiceTypeName,
        MetadataReference infrastructure)
        => CreateMetadataReference(
            assemblyName,
            $$"""
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(typeof(SharpLink.Generated.{{manifestTypeName}}), 4, 2, "2.0.0-test")]

namespace SharpLink.Generated
{
    public sealed class {{manifestTypeName}} : ISharpLinkGeneratedAssemblyManifest
    {
        public static readonly {{manifestTypeName}} Instance = new();
        public static void Register() => SharpLinkGeneratedAssemblyCatalog.Register(Instance);
    }
}

namespace {{assemblyName}}
{
    internal sealed class {{internalServiceTypeName}} { }
}
""",
            infrastructure);

    private static MetadataReference CreateLegacyGeneratedManifestReference(MetadataReference infrastructure)
        => CreateMetadataReference(
            "LegacyServices",
            """
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(typeof(SharpLink.Generated.LegacyManifest))]

namespace SharpLink.Generated
{
    public sealed class LegacyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public static readonly LegacyManifest Instance = new();
    }
}
""",
            infrastructure);

    private static MetadataReference CreateMalformedManifestReference(MetadataReference infrastructure)
        => CreateMetadataReference(
            "MalformedServices",
            """
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(typeof(SharpLink.Generated.MalformedManifest), 4, 2, "2.0.0-test")]

namespace SharpLink.Generated
{
    public sealed class MalformedManifest : ISharpLinkGeneratedAssemblyManifest { }
}
""",
            infrastructure);

    private static MetadataReference CreateAdapterPackageReference(
        string assemblyName,
        string adapterNamespace,
        string adapterType,
        string selectorType,
        string adapterId,
        string wireFormatId,
        MetadataReference sdk)
        => CreateMetadataReference(
            assemblyName,
            $$"""
using System;
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: RpcCodecAdapterRegistration(
    typeof({{adapterNamespace}}.{{adapterType}}),
    "{{adapterId}}",
    "{{wireFormatId}}",
    SelectorAttributeType = typeof({{adapterNamespace}}.{{selectorType}}))]

namespace {{adapterNamespace}}
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class {{selectorType}} : Attribute { }

    public sealed class {{adapterType}} : IRpcCodecAdapter
    {
        public string AdapterId => "{{adapterId}}";
        public string WireFormatId => "{{wireFormatId}}";
        public IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
    }
}
""",
            sdk);

    private static string BuildReferencedContractSource(string method)
    {
        return $$"""
using System.Threading.Tasks;

namespace ConflictingContracts
{
    [SharpLink.Sdk.RpcContract]
    public interface ISharedContract : SharpLink.Sdk.IService
    {
        {{method}}
    }
}
""";
    }

    private static string BuildSdkSource()
    {
        return """
using System;

namespace SharpLink.Sdk
{
    public interface IService { }

    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class RpcContractAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcServiceAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class SharpLinkRpcContractsAttribute : Attribute
    {
        public SharpLinkRpcContractsAttribute(params Type[] contractTypes) { }
    }
}
""";
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
        => string.Join(" | ", diagnostics.Select(static d => $"{d.Id}: {d.GetMessage()}"));

    private static int CountOccurrences(string text, string value)
        => text.Split(value, StringSplitOptions.None).Length - 1;

    private static IEnumerable<MetadataReference> GetPlatformReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrWhiteSpace(tpa))
            throw new Exception("TRUSTED_PLATFORM_ASSEMBLIES is unavailable.");

        return tpa.Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => MetadataReference.CreateFromFile(p));
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

}
