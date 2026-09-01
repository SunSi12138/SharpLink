using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ImplicitUnsafeBlitSourceAutoLayoutShouldReportInfo()
    {
        var source = BuildSource("""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public struct AutoPayload
{
    public byte Head;
    public long Tail;
}

[SharpLink.Sdk.RpcContract]
public interface IAutoLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<AutoPayload> Echo(AutoPayload value, CancellationToken cancellationToken);
}
""");

        var diagnostics = RunUnsafeBlitCompatibilityGenerator(source);
        var diagnostic = diagnostics.Single(static item => item.Id == "SHARPLINK064");
        Ensure(diagnostic.Severity == DiagnosticSeverity.Info,
            "AutoLayout UnsafeBlit guidance must remain informational and non-blocking");
        var message = diagnostic.GetMessage();
        Ensure(message.Contains("AutoPayload", StringComparison.Ordinal) &&
               message.Contains("LayoutKind.Sequential", StringComparison.Ordinal) &&
               message.Contains("LayoutKind.Explicit", StringComparison.Ordinal) &&
               message.Contains("custom/adapter codec", StringComparison.Ordinal),
            $"SHARPLINK064 must explain the raw-wire mitigation choices. Actual: {message}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ImplicitUnsafeBlitShouldDetectNestedSourceAutoLayout()
    {
        var source = BuildSource("""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public struct AutoLeaf
{
    public short Code;
    public long Value;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct SequentialEnvelope
{
    public int Prefix;
    public AutoLeaf Leaf;
}

[SharpLink.Sdk.RpcContract]
public interface INestedAutoLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<SequentialEnvelope> Echo(SequentialEnvelope value, CancellationToken cancellationToken);
}
""");

        var diagnostic = RunUnsafeBlitCompatibilityGenerator(source)
            .Single(static item => item.Id == "SHARPLINK064");
        Ensure(diagnostic.GetMessage().Contains("AutoLeaf", StringComparison.Ordinal) &&
               diagnostic.GetMessage().Contains("SequentialEnvelope.Leaf", StringComparison.Ordinal),
            $"nested AutoLayout evidence must identify the nested source type and field path. Actual: {diagnostic.GetMessage()}");
        return Task.CompletedTask;
    }

    [Test]
    public Task SequentialAndExplicitUnsafeBlitPayloadsShouldNotReportAutoLayoutSuggestion()
    {
        var source = BuildSource("""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct SequentialPayload
{
    public byte Head;
    public long Tail;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
public struct ExplicitPayload
{
    [System.Runtime.InteropServices.FieldOffset(0)] public byte Head;
    [System.Runtime.InteropServices.FieldOffset(8)] public long Tail;
}

[SharpLink.Sdk.RpcContract]
public interface IStableLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<SequentialPayload> Sequential(SequentialPayload value, CancellationToken cancellationToken);
    ValueTask<ExplicitPayload> Explicit(ExplicitPayload value, CancellationToken cancellationToken);
}
""");

        Ensure(!RunUnsafeBlitCompatibilityGenerator(source).Any(static item => item.Id == "SHARPLINK064"),
            "Sequential and Explicit payloads must not receive the AutoLayout-specific suggestion");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitCustomAndAdapterBindingsShouldSuppressUnsafeBlitSuggestion()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public struct CustomPayload
{
    public int Value;
}

[SharpLink.Sdk.RpcCodecImplementation("custom-auto/v1", "custom-auto-schema/v1")]
public sealed class CustomPayloadCodec : SharpLink.Abstractions.IRpcCodec<CustomPayload>
{
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public struct AdapterPayload
{
    public long Value;
}

public sealed class AdapterPayloadAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "adapter-auto/v1";
    public string WireFormatId => "adapter-auto-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

[SharpLink.Sdk.RpcContract]
public interface IExplicitCodecContract : SharpLink.Sdk.IService
{
    ValueTask<CustomPayload> Custom(CustomPayload value, CancellationToken cancellationToken);
    ValueTask<AdapterPayload> Adapted(AdapterPayload value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(CustomPayload), typeof(CustomPayloadCodec))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterPayloadAdapter), \"adapter-auto/v1\", \"adapter-auto-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(AdapterPayload), typeof(AdapterPayloadAdapter))]");

        Ensure(!RunUnsafeBlitCompatibilityGenerator(source).Any(static item => item.Id == "SHARPLINK064"),
            "valid explicit custom/adapter bindings mean the payload no longer uses implicit UnsafeBlit");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReferencedAutoLayoutShouldNotReportSourceLevelSuggestion()
    {
        var external = CreateMetadataReference(
            "ExternalAutoLayout",
            """
namespace ExternalAutoLayout
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    public struct ExternalAutoPayload
    {
        public int Value;
    }
}
""");
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IExternalAutoLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<global::ExternalAutoLayout.ExternalAutoPayload> Echo(
        global::ExternalAutoLayout.ExternalAutoPayload value,
        CancellationToken cancellationToken);
}
""");

        Ensure(!RunUnsafeBlitCompatibilityGenerator(source, external)
                .Any(static item => item.Id == "SHARPLINK064"),
            "framework/referenced AutoLayout types must not receive a source-level SharpLink suggestion");
        return Task.CompletedTask;
    }

    private static ImmutableArray<Diagnostic> RunUnsafeBlitCompatibilityGenerator(
        string source,
        params MetadataReference[] additionalReferences)
    {
        source = UseCurrentIdentitySdk(source);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            assemblyName: "UnsafeBlitCompatibilityDiagnosticTests",
            syntaxTrees: [syntaxTree],
            references: GetPlatformReferences().Concat(additionalReferences),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        IIncrementalGenerator generator = new UnsafeBlitCompatibilityDiagnosticGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Diagnostics;
    }
}
