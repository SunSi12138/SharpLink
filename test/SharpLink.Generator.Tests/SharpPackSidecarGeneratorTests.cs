using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpLink.Serializer.SharpPack;
using SharpLink.Sdk;
using SharpPack;

namespace SharpLink.Generator.Tests;

public class SharpPackSidecarGeneratorTests
{
    [Test]
    public void SharpPackRouteShouldGenerateTypedSidecarsForExternalManagedGraph()
    {
        var vendor = CreateVendorReference("""
using System.Collections.Generic;

namespace Vendor;

public enum ExternalMode { None, Active }

public sealed class ExternalChild
{
    public string? Name { get; set; }
}

public struct ExternalManagedStruct
{
    public string? Text { get; set; }
}

public struct ExternalRaw
{
    public int Id;
    public long Stamp;
}

public sealed class ExternalRequest
{
    public int Id { get; set; }
    public List<ExternalChild>? Children { get; set; }
    public Dictionary<string, ExternalManagedStruct>? Values { get; set; }
    public ExternalMode Mode { get; set; }
}
""");
        var source = BuildSharpPackContractSource("""
    global::System.Threading.Tasks.Task<Vendor.ExternalRequest> EchoAsync(
        Vendor.ExternalRequest request,
        global::System.Threading.CancellationToken cancellationToken);

    global::System.Threading.Tasks.Task<Vendor.ExternalManagedStruct> StructAsync(
        Vendor.ExternalManagedStruct request,
        global::System.Threading.CancellationToken cancellationToken);

    global::System.Threading.Tasks.Task<Vendor.ExternalRaw> RawAsync(
        Vendor.ExternalRaw request,
        global::System.Threading.CancellationToken cancellationToken);
""");

        var result = RunAndCompile("SharpPackExternalGraph", source, [vendor]);
        EnsureNoErrors(result);
        var generated = GetSharpPackGeneratedSource(result.RunResult);

        Ensure(generated.Contains(
                "SharpPackFormatter<global::Vendor.ExternalRequest>",
                StringComparison.Ordinal),
            "external request sidecar");
        Ensure(generated.Contains(
                "SharpPackFormatter<global::Vendor.ExternalChild>",
                StringComparison.Ordinal),
            "nested external class sidecar");
        Ensure(generated.Contains(
                "SharpPackFormatter<global::Vendor.ExternalManagedStruct>",
                StringComparison.Ordinal),
            "managed external struct sidecar");
        Ensure(!generated.Contains(
                "SharpPackFormatter<global::Vendor.ExternalRaw>",
                StringComparison.Ordinal),
            "unmanaged payload keeps SharpPack raw-copy semantics");
        Ensure(generated.Contains(
                "writer.WriteValue<global::System.Collections.Generic.List<global::Vendor.ExternalChild>>",
                StringComparison.Ordinal),
            "nested collection uses typed SharpPack writer API");
        Ensure(generated.Contains(
                "reader.ReadValue<global::System.Collections.Generic.Dictionary<string, global::Vendor.ExternalManagedStruct>>",
                StringComparison.Ordinal),
            "nested dictionary uses typed SharpPack reader API");
        Ensure(generated.Contains(
                "builder.Register<global::Vendor.ExternalRequest>",
                StringComparison.Ordinal),
            "sidecar is registered into the generated scope context");
    }

    [Test]
    public void SharpPackRouteShouldReuseExistingSharpPackSupportWithoutSidecar()
    {
        var vendor = CreateVendorReference("""
using SharpPack;

namespace Vendor;

[SharpPackable]
public partial class ExistingSharpPackPayload
{
    public int Id { get; set; }
}
""", includeSharpPack: true);
        var source = BuildSharpPackContractSource("""
    global::System.Threading.Tasks.Task<Vendor.ExistingSharpPackPayload> EchoAsync(
        Vendor.ExistingSharpPackPayload request,
        global::System.Threading.CancellationToken cancellationToken);
""");

        var result = RunAndCompile("SharpPackExistingSupport", source, [vendor]);
        EnsureNoErrors(result);
        var generated = GetSharpPackGeneratedSource(result.RunResult);

        Ensure(!generated.Contains(
                "SharpPackFormatter<global::Vendor.ExistingSharpPackPayload>",
                StringComparison.Ordinal),
            "authoritative SharpPack support must not receive a duplicate sidecar");
    }

    [Test]
    public void ExplicitNonSharpPackAdapterShouldWinOverAssemblyRoute()
    {
        var vendor = CreateVendorReference("""
namespace Vendor;

public sealed class ExternalRequest
{
    public int Id { get; set; }
}
""");
        var source = """
using System;
using System.Buffers;
using SharpLink.Abstractions;
using SharpLink.Sdk;
using SharpLink.Serializer.SharpPack;

[assembly: RpcCodecAdapterRegistration(typeof(FakeAdapter), "tests/fake/v1", "tests/fake-wire/v1")]
[assembly: RpcCodecRoute(RpcCodecScope.All, typeof(SharpPackRpcCodecAdapter))]
[assembly: RpcCodecAdapter(typeof(Vendor.ExternalRequest), typeof(FakeAdapter))]

[RpcContract]
public interface IContract : IService
{
    global::System.Threading.Tasks.Task<Vendor.ExternalRequest> EchoAsync(
        Vendor.ExternalRequest request,
        global::System.Threading.CancellationToken cancellationToken);
}

[RpcCodecSemanticIdentity(1UL, 2UL)]
public sealed class FakeAdapter : IRpcCodecAdapter
{
    public string AdapterId => "tests/fake/v1";
    public IRpcCodecAdapterScope CreateScope() => new Scope();

    private sealed class Scope : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>() => new Codec<T>();
        public void Dispose() { }
    }

    private sealed class Codec<T> : IRpcCodec<T>
    {
        public void Serialize(in T value, IBufferWriter<byte> writer) { }
        public T? Deserialize(in ReadOnlySequence<byte> sequence) => default;
    }
}
""";

        var result = RunAndCompile("SharpPackExplicitOverride", source, [vendor]);
        EnsureNoErrors(result);
        Ensure(!result.RunResult.Results
                .SelectMany(static item => item.GeneratedSources)
                .Any(static item => item.HintName == "SharpLink.SharpPackIntegration.g.cs"),
            "no SharpPack binding remains after the explicit non-SharpPack override");
    }

    [Test]
    public void UnsupportedNestedConstructionShouldReportBuildTimePathDiagnostic()
    {
        var vendor = CreateVendorReference("""
namespace Vendor;

public sealed class BadChild
{
    public string Name { get; }
    public BadChild(int unrelated) => Name = unrelated.ToString();
}

public sealed class ExternalRequest
{
    public BadChild? Child { get; set; }
}
""");
        var source = BuildSharpPackContractSource("""
    global::System.Threading.Tasks.Task<Vendor.ExternalRequest> EchoAsync(
        Vendor.ExternalRequest request,
        global::System.Threading.CancellationToken cancellationToken);
""");

        var result = RunAndCompile("SharpPackUnsupportedNested", source, [vendor]);
        var diagnostic = result.DriverDiagnostics.FirstOrDefault(static item => item.Id == "SLSP0001");

        Ensure(diagnostic is not null, "unsupported SharpPack graph produces SLSP0001");
        Ensure(diagnostic.GetMessage().Contains("member 'Child'", StringComparison.Ordinal),
            "diagnostic contains the dependency member path");
        Ensure(diagnostic.GetMessage().Contains("constructor", StringComparison.OrdinalIgnoreCase),
            "diagnostic identifies construction as the unsupported capability");
    }

    private static string BuildSharpPackContractSource(string members) => $$"""
using SharpLink.Sdk;
using SharpLink.Serializer.SharpPack;

[assembly: RpcCodecRoute(RpcCodecScope.All, typeof(SharpPackRpcCodecAdapter))]

[RpcContract]
public interface IContract : IService
{
{{members}}
}
""";

    private static MetadataReference CreateVendorReference(string source, bool includeSharpPack = false)
    {
        var references = GeneratorTestHarness.GetPlatformReferences();
        if (includeSharpPack)
            references = references.Add(MetadataReference.CreateFromFile(typeof(SharpPackFormatter<>).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "Vendor.Models",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            throw new Exception(string.Join(
                Environment.NewLine,
                emit.Diagnostics.Where(static item => item.Severity == DiagnosticSeverity.Error)));
        }
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static RunResult RunAndCompile(
        string assemblyName,
        string source,
        IEnumerable<MetadataReference> additionalReferences)
    {
        var references = new List<MetadataReference>(additionalReferences)
        {
            MetadataReference.CreateFromFile(typeof(RpcContractAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(SharpPackRpcCodecAdapter).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(SharpPackFormatter<>).Assembly.Location)
        };
        var compilation = GeneratorTestHarness.CreateCompilation(assemblyName, source, references);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new RpcGenerator().AsSourceGenerator(),
            new SharpPackIntegrationGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var driverDiagnostics);
        return new RunResult(driver.GetRunResult(), outputCompilation, driverDiagnostics);
    }

    private static string GetSharpPackGeneratedSource(GeneratorDriverRunResult result)
        => result.Results
            .SelectMany(static item => item.GeneratedSources)
            .Single(static item => item.HintName == "SharpLink.SharpPackIntegration.g.cs")
            .SourceText
            .ToString();

    private static void EnsureNoErrors(RunResult result)
    {
        var errors = result.DriverDiagnostics
            .Concat(result.OutputCompilation.GetDiagnostics())
            .Where(static item => item.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
            throw new Exception(string.Join(Environment.NewLine, errors.Select(static item => item.ToString())));
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed record RunResult(
        GeneratorDriverRunResult RunResult,
        Compilation OutputCompilation,
        ImmutableArray<Diagnostic> DriverDiagnostics);
}
