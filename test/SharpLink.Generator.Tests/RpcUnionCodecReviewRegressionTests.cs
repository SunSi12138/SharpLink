using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void NativeUnionCodecShouldRejectUserDefinedConversionOnlyCase()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(ConvertibleCase))]
public class ConversionUnion { }

public sealed class ConvertibleCase
{
    public static implicit operator ConversionUnion(ConvertibleCase value) => new();
}

[SharpLink.Sdk.RpcContract]
public interface IConversionUnionContract : SharpLink.Sdk.IService
{
    ValueTask<ConversionUnion> Echo(
        ConversionUnion value,
        CancellationToken cancellationToken);
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains(
                    "user-defined conversions cannot define runtime union cases",
                    StringComparison.Ordinal)),
            $"user-defined conversions must not qualify a native runtime union case: {FormatDiagnostics(diagnostics)}");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("case global::ConvertibleCase", StringComparison.Ordinal),
            "a conversion-only case must never reach generated runtime type-pattern dispatch");
    }

    [Test]
    public void NativeUnionCodecShouldAllowRuntimeSizedCaseWithExplicitAdapter()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(VectorCase))]
public interface IVectorUnion { }

[SharpLink.Sdk.RpcCodecAdapter(typeof(VectorCaseAdapter))]
public struct VectorCase : IVectorUnion
{
    public System.Numerics.Vector<int> Value;
}

public sealed class VectorCaseAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "vector.case/v1";
    public string WireFormatId => "vector.case.wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

[SharpLink.Sdk.RpcContract]
public interface IVectorUnionContract : SharpLink.Sdk.IService
{
    ValueTask<IVectorUnion> Echo(
        IVectorUnion value,
        CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(VectorCaseAdapter), \"vector.case/v1\", \"vector.case.wire/v1\")]");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            $"an explicit Adapter must make a runtime-sized unmanaged union case valid: {FormatDiagnostics(diagnostics)}");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("case global::VectorCase", StringComparison.Ordinal),
            "the runtime-sized union case must remain in generated type dispatch when explicitly adapted");
        Ensure(generated.Contains("CreateCodec<global::VectorCase>()", StringComparison.Ordinal),
            "the union child must resolve through the explicit Adapter Codec");
    }

    [Test]
    public void NativeUnionCodecShouldRejectSelfCaseWithoutGeneratorFailure()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(SelfUnion))]
public sealed class SelfUnion { }

[SharpLink.Sdk.RpcContract]
public interface ISelfUnionContract : SharpLink.Sdk.IService
{
    ValueTask<SelfUnion> Echo(
        SelfUnion value,
        CancellationToken cancellationToken);
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains(
                    "cannot declare itself as a case",
                    StringComparison.Ordinal)),
            $"a native union self-case must produce a controlled diagnostic: {FormatDiagnostics(diagnostics)}");
        Ensure(!diagnostics.Any(static diagnostic =>
                string.Equals(diagnostic.Id, "CS8785", StringComparison.Ordinal) ||
                string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal) ||
                diagnostic.GetMessage().Contains("InvalidOperationException", StringComparison.Ordinal)),
            $"a native union self-case must not fail the generator: {FormatDiagnostics(diagnostics)}");
    }

    [Test]
    public void NativeUnionCodecShouldFailClosedForReachableFinalPlanRecursion()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(RecursiveCase))]
public interface IRecursiveUnion { }

[SharpLink.Sdk.RpcSerializable]
public sealed class RecursiveCase : IRecursiveUnion
{
    public IRecursiveUnion? Next { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IRecursiveUnionContract : SharpLink.Sdk.IService
{
    ValueTask<IRecursiveUnion> Echo(
        IRecursiveUnion value,
        CancellationToken cancellationToken);
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains(
                    "recursive final Codec dependency",
                    StringComparison.Ordinal)),
            $"a native union reachable final-plan cycle must produce a controlled diagnostic: {FormatDiagnostics(diagnostics)}");
        Ensure(!diagnostics.Any(static diagnostic =>
                string.Equals(diagnostic.Id, "CS8785", StringComparison.Ordinal) ||
                string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal) ||
                diagnostic.GetMessage().Contains("InvalidOperationException", StringComparison.Ordinal)),
            $"a native union reachable final-plan cycle must not fail the generator: {FormatDiagnostics(diagnostics)}");
    }
}
