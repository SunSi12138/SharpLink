using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ContractManifestShouldBeDeterministicAndContainTheWireSchema()
    {
        var source = BuildSource("""
public enum Status : byte { Unknown, Ready }

[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    [SharpLink.Sdk.RpcMember(7)]
    public required string Name { get; set; }
    public Status Status { get; set; }
}

[SharpLink.Sdk.RpcUnionCase(1, typeof(Payload))]
public interface IResultUnion { }

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
    IAsyncEnumerable<Status> Watch(CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class HelloService : IHelloService
{
    public ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken) => new(value);
    public async IAsyncEnumerable<Status> Watch(CancellationToken cancellationToken) { yield break; }
}
""");

        var first = RunContractGenerator(source);
        var second = RunContractGenerator(source);
        Ensure(first.Json == second.Json, "identical source must emit byte-identical contract JSON");
        Ensure(first.Json.Contains("\"format\": \"SharpLink.Contracts\"", StringComparison.Ordinal),
            "Manifest format marker");
        Ensure(first.Json.Contains("\"shape\": \"Unary\"", StringComparison.Ordinal),
            "RPC call shape");
        Ensure(first.Json.Contains("\"wireType\": \"LengthDelimited\"", StringComparison.Ordinal),
            "DTO wire type");
        Ensure(first.Json.Contains("\"required\": true", StringComparison.Ordinal),
            "required DTO member");
        Ensure(first.Json.Contains("\"underlyingType\": \"byte\"", StringComparison.Ordinal),
            "enum underlying type");
        Ensure(first.Json.Contains("\"tag\": 1", StringComparison.Ordinal), "union tag");
        Ensure(first.Json.Contains("\"schemaFingerprint\":", StringComparison.Ordinal),
            "schema fingerprint");
        Ensure(!first.Json.Contains(Directory.GetCurrentDirectory(), StringComparison.Ordinal),
            "Manifest must not contain absolute paths");
        return Task.CompletedTask;
    }

    [Test]
    public Task NullableRequestResponseAndStreamItemsShouldBeRecorded()
    {
        var source = BuildSource("""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface INullableService : SharpLink.Sdk.IService
{
    ValueTask<string?> Maybe(string? value, CancellationToken cancellationToken);
    ValueTask<int> Upload(IAsyncEnumerable<string?> values, CancellationToken cancellationToken);
    IAsyncEnumerable<string?> Watch(CancellationToken cancellationToken);
}
""");

        var current = RunContractGenerator(source);
        var nullableValues = current.Json.Split(
            "\"nullable\": true",
            StringSplitOptions.None).Length - 1;
        Ensure(nullableValues == 4, "nullable request, response, request stream item, and response stream item");

        var changed = RunContractGenerator(
            source.Replace("string?", "string", StringComparison.Ordinal),
            current.Json);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "payload nullability compatibility diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task NoBaselineAndValidBaselineShouldNotReportCompatibilityErrors()
    {
        var source = SimpleContract("ValueTask<int> Echo(int value, CancellationToken cancellationToken);");
        var current = RunContractGenerator(source);
        Ensure(!current.Diagnostics.Any(IsCompatibilityDiagnostic), "no baseline only emits current Manifest");

        var compared = RunContractGenerator(source, current.Json);
        Ensure(!compared.Diagnostics.Any(IsCompatibilityDiagnostic),
            "an identical valid baseline is compatible");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAndUnsupportedBaselinesShouldReportStableDiagnostics()
    {
        var source = SimpleContract("ValueTask<int> Echo(int value, CancellationToken cancellationToken);");
        var invalid = RunContractGenerator(source, "{");
        Ensure(invalid.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK024"),
            "damaged baseline diagnostic");

        var baseline = RunContractGenerator(source).Json.Replace(
            "\"version\": 1", "\"version\": 99", StringComparison.Ordinal);
        var unsupported = RunContractGenerator(source, baseline);
        Ensure(unsupported.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK025"),
            "unsupported baseline version diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractAndMethodIdentityChangesShouldBeRejected()
    {
        var baselineSource = SimpleContract(
            "ValueTask<int> Echo(int value, CancellationToken cancellationToken);");
        var baseline = RunContractGenerator(baselineSource).Json;

        var renamedContract = RunContractGenerator(
            baselineSource.Replace("IHelloService", "IRenamedService", StringComparison.Ordinal), baseline);
        Ensure(renamedContract.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK026"),
            "contract ID change diagnostic");

        var changedMethod = RunContractGenerator(SimpleContract(
            "ValueTask<int> Echo(long value, CancellationToken cancellationToken);"), baseline);
        Ensure(changedMethod.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK027"),
            "method ID change diagnostic");
        Ensure(changedMethod.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "request wire type change diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task CallShapeWireTypeAndMethodRemovalShouldBeRejected()
    {
        var baselineSource = SimpleContract("""
ValueTask<int> Echo(int value, CancellationToken cancellationToken);
ValueTask<int> Legacy(int value, CancellationToken cancellationToken);
""");
        var baseline = RunContractGenerator(baselineSource).Json;

        var changed = RunContractGenerator(SimpleContract("""
IAsyncEnumerable<long> Echo(int value, CancellationToken cancellationToken);
"""), baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK029"),
            "call shape diagnostic");
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "response or stream item wire type diagnostic");
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK034"),
            "method removal diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractAndServiceRouteRemovalShouldBeRejected()
    {
        var contractSource = SimpleContract(
            "ValueTask<int> Echo(int value, CancellationToken cancellationToken);");
        var contractBaseline = RunContractGenerator(contractSource).Json;
        var removedContract = RunContractGenerator(BuildSource("public sealed class Implementation { }"), contractBaseline);
        Ensure(removedContract.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK035"),
            "contract removal diagnostic");

        var serviceSource = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}
""");
        var serviceBaseline = RunContractGenerator(serviceSource).Json;
        var removedService = RunContractGenerator(contractSource, serviceBaseline);
        Ensure(removedService.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK037"),
            "service route removal diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task RequiredMemberChangesAndDefaultIdRenameShouldBeRejected()
    {
        var baselineSource = DtoContract("""
[SharpLink.Sdk.RpcRequired, SharpLink.Sdk.RpcMember(1)]
public string Name { get; set; } = string.Empty;
public int Count { get; set; }
""");
        var baseline = RunContractGenerator(baselineSource).Json;
        var changed = RunContractGenerator(DtoContract("""
public int Total { get; set; }
[SharpLink.Sdk.RpcRequired, SharpLink.Sdk.RpcMember(2)]
public string Code { get; set; } = string.Empty;
"""), baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK028"),
            "default member ID rename diagnostic");
        Ensure(changed.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK031") >= 2,
            "required member removal and addition diagnostics");
        return Task.CompletedTask;
    }

    [Test]
    public Task CompatibleOptionalFieldAndExplicitIdRenameShouldBeAllowed()
    {
        var baselineSource = DtoContract("""
[SharpLink.Sdk.RpcMember(7)]
public string Name { get; set; } = string.Empty;
""");
        var baseline = RunContractGenerator(baselineSource).Json;
        var compatible = RunContractGenerator(DtoContract("""
[SharpLink.Sdk.RpcMember(7)]
public string DisplayName { get; set; } = string.Empty;
[SharpLink.Sdk.RpcMember(8)]
public int OptionalCount { get; set; }
"""), baseline);
        Ensure(!compatible.Diagnostics.Any(IsCompatibilityDiagnostic),
            "explicit member ID rename and optional addition are compatible");
        return Task.CompletedTask;
    }

    [Test]
    public Task EnumAndUnionTagChangesShouldBeRejected()
    {
        var baselineSource = BuildSource("""
public enum Status : byte { None, Ready }
public sealed class FirstCase { }
public sealed class SecondCase { }
[SharpLink.Sdk.RpcUnionCase(1, typeof(FirstCase))]
public interface IResultUnion { }
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Status> Echo(Status value, CancellationToken cancellationToken);
}
""");
        var baseline = RunContractGenerator(baselineSource).Json;
        var currentSource = baselineSource
            .Replace("Status : byte", "Status : int", StringComparison.Ordinal)
            .Replace("RpcUnionCase(1, typeof(FirstCase))", "RpcUnionCase(1, typeof(SecondCase))", StringComparison.Ordinal);
        var changed = RunContractGenerator(currentSource, baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK032"),
            "enum underlying type diagnostic");
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK033"),
            "union tag reuse diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task NestedCollectionEnumUnderlyingTypeChangesShouldBeRejected()
    {
        var baselineSource = BuildSource("""
public enum NestedStatus : byte { None, Ready }

[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    public List<NestedStatus> Values { get; set; } = [];
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""");
        var baseline = RunContractGenerator(baselineSource).Json;
        var changed = RunContractGenerator(
            baselineSource.Replace("NestedStatus : byte", "NestedStatus : int", StringComparison.Ordinal),
            baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK032"),
            "nested collection enum underlying type diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConfiguredManifestOutputShouldWriteTheExactJsonArtifact()
    {
        var output = Path.Combine(Path.GetTempPath(), $"sharplink-{Guid.NewGuid():N}.json");
        try
        {
            var result = RunContractGenerator(
                SimpleContract("ValueTask<int> Echo(int value, CancellationToken cancellationToken);"),
                outputPath: output);
            Ensure(File.Exists(output), "configured Manifest artifact exists");
            Ensure(File.ReadAllText(output) == result.Json, "artifact exactly matches generated JSON");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
        return Task.CompletedTask;
    }

    [Test]
    public Task UnrelatedImplementationChangesShouldReuseContractAnalysis()
    {
        var parseOptions = CSharpParseOptions.Default;
        var contractTree = CSharpSyntaxTree.ParseText(
            SimpleContract("ValueTask<int> Echo(int value, CancellationToken cancellationToken);"),
            parseOptions,
            path: "/contracts/Contract.cs");
        var implementationTree = CSharpSyntaxTree.ParseText(
            "public static class Implementation { public static int Value => 1; }",
            parseOptions,
            path: "/implementation/Implementation.cs");
        var compilation = CSharpCompilation.Create(
            "ContractIncrementalTestAssembly",
            [contractTree, implementationTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            ImmutableArray<AdditionalText>.Empty,
            parseOptions,
            new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string>()),
            new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var changedImplementationTree = CSharpSyntaxTree.ParseText(
            "public static class Implementation { public static int Value => 2; }",
            parseOptions,
            path: "/implementation/Implementation.cs");
        compilation = compilation.ReplaceSyntaxTree(implementationTree, changedImplementationTree);
        driver = driver.RunGenerators(compilation);

        var steps = driver.GetRunResult().Results.Single().TrackedSteps["SharpLink.ContractManifestAnalysis"];
        Ensure(steps.Length > 0, "contract Manifest analysis tracking step");
        Ensure(
            steps.SelectMany(static step => step.Outputs).All(static output =>
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
            "unrelated implementation edits must not rerun contract Manifest analysis");
        return Task.CompletedTask;
    }

    private static string SimpleContract(string methods) => BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    {{methods}}
}
""");

    private static string DtoContract(string members) => BuildSource($$"""
[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    {{members}}
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""");

    private static bool IsCompatibilityDiagnostic(Diagnostic diagnostic)
        => string.CompareOrdinal(diagnostic.Id, "SHARPLINK024") >= 0 &&
           string.CompareOrdinal(diagnostic.Id, "SHARPLINK035") <= 0;

    private static ContractGeneratorResult RunContractGenerator(
        string source,
        string? baseline = null,
        string? outputPath = null)
    {
        const string baselinePath = "/contracts/previous.sharplink.json";
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            "ContractManifestTestAssembly",
            [syntaxTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var additionalTexts = ImmutableArray<AdditionalText>.Empty;
        if (baseline is not null)
        {
            properties["build_property.SharpLinkContractBaseline"] = baselinePath;
            additionalTexts = [new InMemoryAdditionalText(baselinePath, baseline)];
        }
        if (outputPath is not null)
            properties["build_property.SharpLinkContractManifestOutput"] = outputPath;

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            additionalTexts,
            CSharpParseOptions.Default,
            new TestAnalyzerConfigOptionsProvider(properties));
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        var generated = result.GeneratedTrees
            .Select(static tree => tree.GetText().ToString())
            .First(static text => text.Contains("__SharpLinkContractManifest", StringComparison.Ordinal));
        const string startMarker = "internal const string Json = @\"";
        const string endMarker = "\";\n}";
        var start = generated.IndexOf(startMarker, StringComparison.Ordinal) + startMarker.Length;
        var end = generated.LastIndexOf(endMarker, StringComparison.Ordinal);
        Ensure(start >= startMarker.Length && end > start, "generated contract Manifest constant");
        var json = generated.Substring(start, end - start).Replace("\"\"", "\"", StringComparison.Ordinal);
        return new ContractGeneratorResult(json, result.Diagnostics);
    }

    private sealed record ContractGeneratorResult(string Json, ImmutableArray<Diagnostic> Diagnostics);

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(content);
    }

    private sealed class TestAnalyzerConfigOptionsProvider(
        IReadOnlyDictionary<string, string> properties) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global = new TestAnalyzerConfigOptions(properties);
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestAnalyzerConfigOptions.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => TestAnalyzerConfigOptions.Empty;
    }

    private sealed class TestAnalyzerConfigOptions(
        IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        internal static TestAnalyzerConfigOptions Empty { get; } = new(new Dictionary<string, string>());
        public override bool TryGetValue(string key, out string value)
            => values.TryGetValue(key, out value!);
    }
}
