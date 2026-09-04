using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task RemovedDirectLeafMustNotSuppressSurvivingNestedIdentityChange()
    {
        static string Source(string rawFieldType, bool includeDirectMember)
        {
            var directMember = includeDirectMember
                ? "public Raw Direct { get; set; }"
                : string.Empty;
            return BuildSource($$"""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct Raw
{
    public {{rawFieldType}} Value;
}

[SharpLink.Sdk.RpcSerializable]
public sealed class A
{
    {{directMember}}
}

[SharpLink.Sdk.RpcSerializable]
public sealed class B
{
    public List<Raw> Nested { get; set; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface IProjectionService : SharpLink.Sdk.IService
{
    ValueTask<A> EchoA(A value, CancellationToken cancellationToken);
    ValueTask<B> EchoB(B value, CancellationToken cancellationToken);
}
""");
        }

        var baseline = RunContractGenerator(Source("int", includeDirectMember: true));
        var changed = RunContractGenerator(Source("long", includeDirectMember: false), baseline.Json);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "removing an optional direct use must not suppress a still-reachable nested final CodecHash change");
        return Task.CompletedTask;
    }

    [Test]
    public Task RequiredReferenceNullRejectionChangeShouldFailContractBaseline()
    {
        static string Source(bool nullable) => BuildSource($$"""
#nullable enable
[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    [SharpLink.Sdk.RpcRequired]
    public string{{(nullable ? "?" : string.Empty)}} Name { get; set; } = string.Empty;
}

[SharpLink.Sdk.RpcContract]
public interface IProjectionService : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""");

        var baseline = RunContractGenerator(Source(nullable: false));
        var baselineRoot = System.Text.Json.Nodes.JsonNode.Parse(baseline.Json)!.AsObject();
        var baselineMember = baselineRoot["dtos"]!.AsArray().Single()!["members"]!.AsArray().Single()!.AsObject();
        Ensure(baselineMember["rejectNull"]?.GetValue<bool>() == true,
            "required non-nullable references must persist the effective runtime null-rejection semantic");

        var changed = RunContractGenerator(Source(nullable: true), baseline.Json);
        var changedRoot = System.Text.Json.Nodes.JsonNode.Parse(changed.Json)!.AsObject();
        var changedMember = changedRoot["dtos"]!.AsArray().Single()!["members"]!.AsArray().Single()!.AsObject();
        Ensure(changedMember["rejectNull"]?.GetValue<bool>() == false,
            "required nullable references must persist the absence of runtime null rejection");
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing effective required-reference null rejection must fail baseline comparison");
        return Task.CompletedTask;
    }

    [Test]
    public Task DtoReferenceValueEnvelopeChangeShouldFailContractBaseline()
    {
        static string Source(bool referenceType)
        {
            var declaration = referenceType ? "sealed class" : "struct";
            return BuildSource($$"""
[SharpLink.Sdk.RpcSerializable]
public {{declaration}} Payload
{
    [SharpLink.Sdk.RpcMember(1)]
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IProjectionService : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""");
        }

        var baseline = RunContractGenerator(Source(referenceType: true));
        var baselineRoot = System.Text.Json.Nodes.JsonNode.Parse(baseline.Json)!.AsObject();
        Ensure(baselineRoot["dtos"]!.AsArray().Single()!["shape"]?.GetValue<string>() == "reference",
            "reference DTOs must persist their presence-framed envelope shape");

        var changed = RunContractGenerator(Source(referenceType: false), baseline.Json);
        var changedRoot = System.Text.Json.Nodes.JsonNode.Parse(changed.Json)!.AsObject();
        Ensure(changedRoot["dtos"]!.AsArray().Single()!["shape"]?.GetValue<string>() == "value",
            "value DTOs must persist their non-presence-framed envelope shape");
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "class-to-struct DTO envelope changes must fail baseline comparison");
        return Task.CompletedTask;
    }

    [Test]
    public Task TimeoutBehaviorChangeShouldFailContractBaseline()
    {
        static string Source(int seconds) => BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IProjectionService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout({{seconds}}d)]
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""");

        var baseline = RunContractGenerator(Source(5));
        var baselineMethod = System.Text.Json.Nodes.JsonNode.Parse(baseline.Json)!.AsObject()["contracts"]!
            .AsArray().Single()!["methods"]!.AsArray().Single()!.AsObject();
        Ensure(baselineMethod["hasTimeout"]?.GetValue<bool>() == true &&
               baselineMethod["timeoutTicks"]?.GetValue<long>() == TimeSpan.FromSeconds(5).Ticks,
            "baseline must persist normalized timeout behavior independently from payload identity");

        var changed = RunContractGenerator(Source(10), baseline.Json);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing normalized method timeout behavior must fail baseline comparison");
        return Task.CompletedTask;
    }

    [Test]
    public Task IdempotencyBehaviorChangeShouldFailContractBaseline()
    {
        static string Source(bool idempotent) => BuildSource($$"""
namespace SharpLink.Sdk
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class IdempotentAttribute : System.Attribute
    {
    }
}

[SharpLink.Sdk.RpcContract]
public interface IProjectionService : SharpLink.Sdk.IService
{
    {{(idempotent ? "[SharpLink.Sdk.Idempotent]" : string.Empty)}}
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""");

        var baseline = RunContractGenerator(Source(idempotent: false));
        var baselineMethod = System.Text.Json.Nodes.JsonNode.Parse(baseline.Json)!.AsObject()["contracts"]!
            .AsArray().Single()!["methods"]!.AsArray().Single()!.AsObject();
        Ensure(baselineMethod["idempotent"]?.GetValue<bool>() == false,
            "baseline must persist non-idempotent behavior");

        var changed = RunContractGenerator(Source(idempotent: true), baseline.Json);
        var changedMethod = System.Text.Json.Nodes.JsonNode.Parse(changed.Json)!.AsObject()["contracts"]!
            .AsArray().Single()!["methods"]!.AsArray().Single()!.AsObject();
        Ensure(changedMethod["idempotent"]?.GetValue<bool>() == true,
            "current manifest must persist idempotent behavior");
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing method idempotency behavior must fail baseline comparison");
        return Task.CompletedTask;
    }

    [Test]
    public Task CancellabilityBehaviorChangeShouldFailContractBaseline()
    {
        static string Source(bool cancellable) => BuildSource(cancellable
            ? """
[SharpLink.Sdk.RpcContract]
public interface IProjectionService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
"""
            : """
[SharpLink.Sdk.RpcContract]
public interface IProjectionService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> Echo(int value);
}
""");

        var baseline = RunContractGenerator(Source(cancellable: true));
        var changed = RunContractGenerator(Source(cancellable: false), baseline.Json);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing method cancellability behavior must fail baseline comparison");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitFullyOverlappingIdenticalAliasShouldPreserveUnsafeBlitIdentity()
    {
        static string Source(bool includeAlias) => BuildSource($$"""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
public struct Raw
{
    [System.Runtime.InteropServices.FieldOffset(0)]
    public int Value;
    {{(includeAlias ? "[System.Runtime.InteropServices.FieldOffset(0)] public int Alias;" : string.Empty)}}
}

[SharpLink.Sdk.RpcContract]
public interface IProjectionService : SharpLink.Sdk.IService
{
    ValueTask<Raw> Echo(Raw value, CancellationToken cancellationToken);
}
""");

        var baseline = RunContractGenerator(Source(includeAlias: false));
        var changedWithoutBaseline = RunContractGenerator(Source(includeAlias: true));
        var baselineHash = GetFinalCodecHash(baseline.Json, "Raw");
        var changedHash = GetFinalCodecHash(changedWithoutBaseline.Json, "Raw");
        Ensure(string.Equals(baselineHash, changedHash, StringComparison.Ordinal),
            "a fully overlapping identical explicit alias must not change UnsafeBlit physical identity");

        var changed = RunContractGenerator(Source(includeAlias: true), baseline.Json);
        Ensure(!changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "an identical fully overlapping explicit alias must remain baseline-compatible");
        return Task.CompletedTask;
    }

    private static string GetFinalCodecHash(string json, string typeName)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        return root["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(item => item["type"]!.GetValue<string>() == typeName)["codecHash"]!
            .GetValue<string>();
    }
}
