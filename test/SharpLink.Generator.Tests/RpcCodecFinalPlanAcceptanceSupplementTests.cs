using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ImplicitAndExplicitDefaultSequentialShouldShareUnsafeBlitIdentity()
    {
        static string Manifest(bool explicitSequential)
        {
            var layout = explicitSequential
                ? "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]"
                : string.Empty;
            var source = BuildSource($$"""
{{layout}}
public struct DefaultSequentialPayload
{
    public byte Head;
    public long Tail;
}

[SharpLink.Sdk.RpcSerializable]
public sealed class DefaultSequentialEnvelope
{
    public DefaultSequentialPayload Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IDefaultSequentialContract : SharpLink.Sdk.IService
{
    ValueTask<DefaultSequentialEnvelope> Echo(
        DefaultSequentialEnvelope value,
        CancellationToken cancellationToken);
}
""");

            return RunGeneratorAndGetSources(source)
                .Single(static generated => generated.Contains(
                    "ISharpLinkGeneratedAssemblyManifest",
                    StringComparison.Ordinal));
        }

        var implicitSequential = Manifest(explicitSequential: false);
        var explicitSequential = Manifest(explicitSequential: true);
        Ensure(
            ExtractGeneratedCodecIdentity(implicitSequential, "DefaultSequentialEnvelope") ==
            ExtractGeneratedCodecIdentity(explicitSequential, "DefaultSequentialEnvelope"),
            "implicit Sequential and explicit default Sequential describe the same effective CLR layout and must propagate the same UnsafeBlit identity into an enclosing generated CodecHash");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(implicitSequential) ==
            ExtractGeneratedRpcAssemblyHash(explicitSequential),
            "source-only spelling of default Sequential layout must not perturb RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task RawNullablePhysicalIdentityShouldIncludePresenceAndValueLayout()
    {
        var source = BuildSource("""
public struct NullablePhysicalValue
{
    public int Payload;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct NullablePhysicalReplica
{
    private bool HasValue;
    private NullablePhysicalValue Value;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct NullablePhysicalChildOnly
{
    private NullablePhysicalValue Value;
}

[SharpLink.Sdk.RpcContract]
public interface INullablePhysicalContract : SharpLink.Sdk.IService
{
    ValueTask<NullablePhysicalValue?> EchoNullable(
        NullablePhysicalValue? value,
        CancellationToken cancellationToken);

    ValueTask<NullablePhysicalReplica> EchoReplica(
        NullablePhysicalReplica value,
        CancellationToken cancellationToken);

    ValueTask<NullablePhysicalChildOnly> EchoChildOnly(
        NullablePhysicalChildOnly value,
        CancellationToken cancellationToken);
}
""");

        var hashes = AnalyzeFinalCodecHashesForAcceptance(source);
        var nullableCodec = hashes
            .Single(static pair =>
                pair.Key.Contains("NullablePhysicalValue", StringComparison.Ordinal) &&
                !string.Equals(pair.Key, "global::NullablePhysicalValue", StringComparison.Ordinal))
            .Value;
        var fullReplicaCodec = hashes["global::NullablePhysicalReplica"];
        var childOnlyCodec = hashes["global::NullablePhysicalChildOnly"];

        Ensure(
            nullableCodec == fullReplicaCodec,
            "raw Nullable<T> physical identity must model the CLR presence field plus the value field, not only the child T layout");
        Ensure(
            nullableCodec != childOnlyCodec,
            "removing the Nullable<T> presence representation must change the raw UnsafeBlit CodecHash");
        return Task.CompletedTask;
    }

    private static Dictionary<string, (ulong High, ulong Low)> AnalyzeFinalCodecHashesForAcceptance(
        string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            "FinalCodecPlanNullableAcceptance",
            [syntaxTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var sourceErrors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Ensure(
            sourceErrors.Length == 0,
            "nullable acceptance source must compile: " +
            string.Join(Environment.NewLine, sourceErrors.Select(static diagnostic => diagnostic.ToString())));

        var analyze = typeof(RpcGenerator).GetMethod(
            "AnalyzeGeneratedCodecsWithPolicyOwnership",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Final Codec analysis entry point was not found.");
        var result = analyze.Invoke(null, [compilation, CancellationToken.None]) ??
                     throw new InvalidOperationException("Final Codec analysis returned no result.");
        var codecHashes = result.GetType().GetProperty("CodecHashes")?.GetValue(result) as IEnumerable ??
                          throw new InvalidOperationException("Final Codec analysis did not expose CodecHashes.");

        var hashes = new Dictionary<string, (ulong High, ulong Low)>(StringComparer.Ordinal);
        foreach (var item in codecHashes)
        {
            if (item is null)
                continue;
            var itemType = item.GetType();
            var typeName = itemType.GetProperty("TypeName")?.GetValue(item) as string ??
                           throw new InvalidOperationException("Final Codec hash entry has no TypeName.");
            var high = (ulong)(itemType.GetProperty("High")?.GetValue(item) ??
                               throw new InvalidOperationException("Final Codec hash entry has no High value."));
            var low = (ulong)(itemType.GetProperty("Low")?.GetValue(item) ??
                              throw new InvalidOperationException("Final Codec hash entry has no Low value."));
            hashes[typeName] = (high, low);
        }
        return hashes;
    }
}
