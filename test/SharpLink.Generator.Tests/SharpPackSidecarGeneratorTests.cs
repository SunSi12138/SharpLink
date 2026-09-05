using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpPack;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void SharpPackRouteShouldGenerateTypedSidecarsForExternalManagedGraph()
    {
        var vendor = CreateSharpPackVendorReference("""
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

        var result = RunSharpPackAndCompile("SharpPackExternalGraph", source, [vendor]);
        EnsureNoSharpPackErrors(result);
        var generated = GetSharpPackGeneratedSource(result.DriverRunResult);

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
        var vendor = CreateSharpPackVendorReference("""
using System;
using SharpPack;

namespace Vendor;

public sealed class ExistingSharpPackPayload : ISharpPackFormatterFactory<ExistingSharpPackPayload>
{
    public int Id { get; set; }

    public static SharpPackFormatter<ExistingSharpPackPayload> CreateFormatter()
        => throw new NotSupportedException();
}
""");
        var source = BuildSharpPackContractSource("""
    global::System.Threading.Tasks.Task<Vendor.ExistingSharpPackPayload> EchoAsync(
        Vendor.ExistingSharpPackPayload request,
        global::System.Threading.CancellationToken cancellationToken);
""");

        var result = RunSharpPackAndCompile("SharpPackExistingSupport", source, [vendor]);
        EnsureNoSharpPackErrors(result);
        var generated = GetSharpPackGeneratedSource(result.DriverRunResult);

        Ensure(!generated.Contains(
                "SharpPackFormatter<global::Vendor.ExistingSharpPackPayload>",
                StringComparison.Ordinal),
            "authoritative SharpPack support must not receive a duplicate sidecar");
    }

    [Test]
    public void ExplicitNonSharpPackAdapterShouldWinOverAssemblyRoute()
    {
        var vendor = CreateSharpPackVendorReference("""
namespace Vendor;

public sealed class ExternalRequest
{
    public int Id { get; set; }
}
""");
        var source = BuildSharpPackContractSource(
            """
    global::System.Threading.Tasks.Task<Vendor.ExternalRequest> EchoAsync(
        Vendor.ExternalRequest request,
        global::System.Threading.CancellationToken cancellationToken);
""",
            """
[SharpLink.Sdk.RpcCodecSemanticIdentity(1UL, 2UL)]
public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "tests/fake/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => new Scope();

    private sealed class Scope : SharpLink.Abstractions.IRpcCodecAdapterScope
    {
        public SharpLink.Abstractions.IRpcCodec<T> CreateCodec<T>() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
""",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"tests/fake/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(Vendor.ExternalRequest), typeof(FakeAdapter))]");

        var result = RunSharpPackAndCompile("SharpPackExplicitOverride", source, [vendor]);
        EnsureNoSharpPackErrors(result);
        Ensure(!result.DriverRunResult.Results
                .SelectMany(static item => item.GeneratedSources)
                .Any(static item => item.HintName == "SharpLink.SharpPackIntegration.g.cs"),
            "no SharpPack binding remains after the explicit non-SharpPack override");
    }

    [Test]
    public void UnsupportedNestedConstructionShouldReportBuildTimePathDiagnostic()
    {
        var vendor = CreateSharpPackVendorReference("""
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

        var result = RunSharpPackAndCompile("SharpPackUnsupportedNested", source, [vendor]);
        var diagnostic = result.DriverDiagnostics.FirstOrDefault(static item => item.Id == "SLSP0001");

        Ensure(diagnostic is not null, "unsupported SharpPack graph produces SLSP0001");
        Ensure(diagnostic!.GetMessage().Contains("member 'Child'", StringComparison.Ordinal),
            "diagnostic contains the dependency member path");
        Ensure(diagnostic.GetMessage().Contains("constructor", StringComparison.OrdinalIgnoreCase),
            "diagnostic identifies construction as the unsupported capability");
    }

    private static string BuildSharpPackContractSource(
        string members,
        string extraTypes = "",
        params string[] extraAssemblyAttributes)
    {
        var attributes = new List<string>
        {
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SharpLink.Serializer.SharpPack.SharpPackRpcCodecAdapter), \"sharplink.serializer.sharppack/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(SharpLink.Serializer.SharpPack.SharpPackRpcCodecAdapter))]"
        };
        attributes.AddRange(extraAssemblyAttributes);

        return $$"""
using System;

{{string.Join(Environment.NewLine, attributes)}}

namespace SharpLink.Sdk
{
    public interface IService { }

    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class RpcContractAttribute : Attribute { }

    [Flags]
    public enum RpcCodecScope
    {
        None = 0,
        Managed = 1,
        Unmanaged = 2,
        All = Managed | Unmanaged
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RpcCodecRouteAttribute : Attribute
    {
        public RpcCodecRouteAttribute(RpcCodecScope scope, Type adapterType) { }
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RpcCodecAdapterRegistrationAttribute : Attribute
    {
        public RpcCodecAdapterRegistrationAttribute(Type adapterType, string adapterId) { }
        public Type? SelectorAttributeType { get; init; }
    }

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public sealed class RpcCodecAdapterAttribute : Attribute
    {
        public RpcCodecAdapterAttribute(Type adapterType) { }
        public RpcCodecAdapterAttribute(Type targetType, Type adapterType) { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RpcCodecSemanticIdentityAttribute : Attribute
    {
        public RpcCodecSemanticIdentityAttribute(ulong high, ulong low) { }
    }
}

namespace SharpLink.Abstractions
{
    public interface IRpcCodec { }
    public interface IRpcCodec<T> : IRpcCodec { }

    public interface IRpcCodecAdapter
    {
        string AdapterId { get; }
        IRpcCodecAdapterScope CreateScope();
    }

    public interface IRpcCodecAdapterScope : IDisposable
    {
        IRpcCodec<T> CreateCodec<T>();
    }
}

namespace SharpLink.Serializer.SharpPack
{
    public interface ISharpPackRpcCodecAdapterScopeConfiguration
    {
        void Configure(
            string configurationId,
            Action<global::SharpPack.SharpPackSerializerContextBuilder> configure);
    }

    [SharpLink.Sdk.RpcCodecSemanticIdentity(0x3fd7540d55dfa977UL, 0xbb67b4932c1a5249UL)]
    public sealed class SharpPackRpcCodecAdapter : SharpLink.Abstractions.IRpcCodecAdapter
    {
        public string AdapterId => "sharplink.serializer.sharppack/v1";
        public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotSupportedException();
    }
}

[SharpLink.Sdk.RpcContract]
public interface IContract : SharpLink.Sdk.IService
{
{{members}}
}

{{extraTypes}}
""";
    }

    private static MetadataReference CreateSharpPackVendorReference(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Vendor.Models",
            [CSharpSyntaxTree.ParseText(source)],
            GeneratorTestHarness.GetPlatformReferences(),
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

    private static GeneratorExecution RunSharpPackAndCompile(
        string assemblyName,
        string source,
        IEnumerable<MetadataReference> additionalReferences)
    {
        var compilation = GeneratorTestHarness.CreateCompilation(assemblyName, source, additionalReferences);
        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        var runResult = driver.GetRunResult();

        var integrationCompilation = compilation;
        foreach (var generated in runResult.Results
                     .SelectMany(static item => item.GeneratedSources)
                     .Where(static item => item.HintName == "SharpLink.SharpPackIntegration.g.cs"))
        {
            integrationCompilation = integrationCompilation.AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(
                    generated.SourceText.ToString(),
                    CSharpParseOptions.Default,
                    generated.HintName));
        }

        return new GeneratorExecution(runResult, integrationCompilation, runResult.Diagnostics);
    }

    private static string GetSharpPackGeneratedSource(GeneratorDriverRunResult result)
        => result.Results
            .SelectMany(static item => item.GeneratedSources)
            .Single(static item => item.HintName == "SharpLink.SharpPackIntegration.g.cs")
            .SourceText
            .ToString();

    private static void EnsureNoSharpPackErrors(GeneratorExecution result)
    {
        var errors = result.DriverDiagnostics
            .Concat(result.OutputCompilation.GetDiagnostics())
            .Where(static item => item.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
            throw new Exception(string.Join(Environment.NewLine, errors.Select(static item => item.ToString())));
    }

    private sealed record GeneratorExecution(
        GeneratorDriverRunResult DriverRunResult,
        Compilation OutputCompilation,
        ImmutableArray<Diagnostic> DriverDiagnostics);
}
