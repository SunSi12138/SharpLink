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
        => GeneratorTestHarness.Run("AnalyzerTestAssembly", source, additionalReferences).Diagnostics;

    private static string[] RunGeneratorAndGetSources(
        string source,
        params MetadataReference[] additionalReferences)
        => GeneratorTestHarness.Run("GeneratorShapeTestAssembly", source, additionalReferences)
            .GeneratedTrees
            .Select(static tree => tree.GetText().ToString())
            .ToArray();

    private static void EnsureGeneratorOutputCompiles(
        string source,
        params MetadataReference[] additionalReferences)
    {
        var compilation = GeneratorTestHarness.CreateCompilation(
            "GeneratedBootstrapCompilationTest",
            source,
            additionalReferences);

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

    private static string GetFirstGeneratedCodecHash(string source)
        => string.Join("\n", RunGeneratorAndGetSources(source))
            .Split('\n')
            .Select(static line => line.Trim())
            .First(static line => line.StartsWith("public RpcHash128 CodecHash =>", StringComparison.Ordinal));

    private static MetadataReference CreateMetadataReference(
        string assemblyName,
        string source,
        params MetadataReference[] additionalReferences)
    {
        var compilation = GeneratorTestHarness.CreateCompilation(
            assemblyName,
            source,
            additionalReferences);

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
        public SharpLinkGeneratedAssemblyManifestAttribute(
            Type manifestType,
            int apiVersion,
            int protocolVersion,
            string generatorVersion,
            string abiIdentity) { }
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

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(typeof(SharpLink.Generated.{{manifestTypeName}}), 4, 2, "2.0.0-test", "sharplink-2.0-api4-rpcchannel-codec-provider-v4")]

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
        => GeneratorTestHarness.GetPlatformReferences();

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
