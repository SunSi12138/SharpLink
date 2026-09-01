using System;
using System.Linq;
using System.Threading.Tasks;

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
        static string Manifest(string fieldType, string extraType)
        {
            var source = BuildSource($$"""
public struct NullablePhysicalValue
{
    public int Payload;
}

{{extraType}}

[SharpLink.Sdk.RpcSerializable]
public sealed class NullablePhysicalEnvelope
{
    public {{fieldType}} Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface INullablePhysicalContract : SharpLink.Sdk.IService
{
    ValueTask<NullablePhysicalEnvelope> Echo(
        NullablePhysicalEnvelope value,
        CancellationToken cancellationToken);
}
""");

            return RunGeneratorAndGetSources(source)
                .Single(static generated => generated.Contains(
                    "ISharpLinkGeneratedAssemblyManifest",
                    StringComparison.Ordinal));
        }

        var nullable = Manifest("NullablePhysicalValue?", string.Empty);
        var fullReplica = Manifest(
            "NullablePhysicalReplica",
            """
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct NullablePhysicalReplica
{
    private bool HasValue;
    private NullablePhysicalValue Value;
}
""");
        var childOnlyReplica = Manifest(
            "NullablePhysicalChildOnly",
            """
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct NullablePhysicalChildOnly
{
    private NullablePhysicalValue Value;
}
""");

        var nullableCodec = ExtractGeneratedCodecIdentity(nullable, "NullablePhysicalEnvelope");
        Ensure(
            nullableCodec == ExtractGeneratedCodecIdentity(fullReplica, "NullablePhysicalEnvelope"),
            "raw Nullable<T> physical identity must model the CLR presence field plus the value field, not only the child T layout");
        Ensure(
            nullableCodec != ExtractGeneratedCodecIdentity(childOnlyReplica, "NullablePhysicalEnvelope"),
            "removing the Nullable<T> presence representation must change an enclosing generated CodecHash");
        return Task.CompletedTask;
    }
}
