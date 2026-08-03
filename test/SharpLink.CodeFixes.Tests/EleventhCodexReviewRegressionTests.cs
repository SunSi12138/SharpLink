using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SharpLink.Generator;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class EleventhCodexReviewRegressionTests
{
    [Test]
    public async Task AttributeRemovalFixesShouldDeclineFixAllForSharedAttributeLists()
    {
        await AssertSharedAttributeListFixAllIsDeclinedAsync(
            "SHARPLINK051",
            "RemoveInvalidUnionCase",
            "Union.cs",
            """
using SharpLink.Sdk;

[RpcUnionCase(0, typeof(FirstCase)), RpcUnionCase(-1, typeof(SecondCase))]
public interface IResult { }

public sealed class FirstCase { }
public sealed class SecondCase { }
""",
            "RpcUnionCase");

        await AssertSharedAttributeListFixAllIsDeclinedAsync(
            "SHARPLINK049",
            "RemoveBuiltinAdapterBinding",
            "Bindings.cs",
            """
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(string)), SharpLink.Sdk.RpcCodecAdapter(typeof(int))]
""",
            "RpcCodecAdapter");
    }

    [Test]
    public async Task RequiredModifierCombinedWithRpcRequiredShouldNotOfferAttributeOnlyFix()
    {
        using var baselineWorkspace = CodeFixTestWorkspace.Create(("Payload.cs", DtoContract("""
[SharpLink.Sdk.RpcMember(1)]
public string Name { get; set; } = string.Empty;
""")));
        await baselineWorkspace.AssertCompilesAsync();
        var baseline = await RunContractGeneratorAsync(baselineWorkspace);

        using var changedWorkspace = CodeFixTestWorkspace.Create(("Payload.cs", DtoContract("""
[SharpLink.Sdk.RpcRequired, SharpLink.Sdk.RpcMember(1)]
public required string Name { get; set; }
""")));
        await changedWorkspace.AssertCompilesAsync();
        var changed = await RunContractGeneratorAsync(changedWorkspace, baseline.Json);
        var diagnostic = changed.Diagnostics.Single(static item => item.Id == "SHARPLINK031");

        Ensure(!diagnostic.Properties.ContainsKey("SharpLink.FixKind"),
            "Removing RpcRequired cannot fix compatibility while the C# required modifier remains.");
        var actions = await changedWorkspace.GetActionsAsync(diagnostic, "Payload.cs");
        Ensure(actions.Count == 0,
            "A real SHARPLINK031 diagnostic for required plus RpcRequired must not offer RemoveRpcRequired.");
    }

    [Test]
    public async Task Sharplink019ShouldExcludeErrorObsoleteConstructorCandidates()
    {
        using (var publicCandidates = CodeFixTestWorkspace.Create(("Service.cs", """
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    [Obsolete("Removed constructor", true)]
    public Service(int value) { }

    public Service(string value) { }
}
""")))
        {
            await publicCandidates.AssertCompilesAsync();
            var diagnostic = await publicCandidates.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

            var actions = await publicCandidates.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                    [("Select constructor Service(string)", "SelectConstructor:Service.Service(string)")]),
                $"Only the usable public constructor may be selected. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
            var changed = await publicCandidates.ApplyAsync(actions[0]);
            await publicCandidates.AssertCompilesAsync(changed);
            var source = await publicCandidates.GetTextAsync("Service.cs", changed);
            Ensure(CountMarkedConstructors(source, "ActivatorUtilitiesConstructor") == 1,
                "The valid public constructor must receive exactly one selection marker.");
        }

        using var nonPublicCandidates = CodeFixTestWorkspace.Create(("Service.cs", """
using System;

[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    [Obsolete("Removed constructor", true)]
    private Service(int value) { }

    private Service(string value) { }
}
"""));
        await nonPublicCandidates.AssertCompilesAsync();
        var nonPublicDiagnostic = await nonPublicCandidates.CreateDiagnosticAsync(
            "SHARPLINK019",
            "Service.cs");

        var nonPublicActions = await nonPublicCandidates.GetActionsAsync(
            nonPublicDiagnostic,
            "Service.cs");

        Ensure(nonPublicActions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Make Service constructor public", "MakeConstructorPublic")]),
            $"Only the usable non-public constructor may be exposed. Actual: {string.Join(", ", nonPublicActions.Select(static action => action.Title))}");
        var nonPublicChanged = await nonPublicCandidates.ApplyAsync(nonPublicActions[0]);
        await nonPublicCandidates.AssertCompilesAsync(nonPublicChanged);
        var nonPublicSource = await nonPublicCandidates.GetTextAsync("Service.cs", nonPublicChanged);
        var constructors = CSharpSyntaxTree.ParseText(nonPublicSource)
            .GetRoot()
            .DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .ToDictionary(static constructor => constructor.ParameterList.Parameters.Single().Type!.ToString());
        Ensure(constructors["string"].Modifiers.Any(SyntaxKind.PublicKeyword),
            "The valid non-public constructor must become public.");
        Ensure(constructors["int"].Modifiers.Any(SyntaxKind.PrivateKeyword),
            "The error-obsolete constructor must remain private.");
    }

    private static async Task AssertSharedAttributeListFixAllIsDeclinedAsync(
        string diagnosticId,
        string equivalenceKey,
        string documentName,
        string source,
        string attributeName)
    {
        using var workspace = CodeFixTestWorkspace.Create((documentName, source));
        await workspace.AssertCompilesAsync();
        var document = workspace.GetDocument(documentName);
        var root = await document.GetSyntaxRootAsync()
                   ?? throw new InvalidOperationException("The test document has no syntax root.");
        var attributes = root.DescendantNodes()
            .OfType<AttributeSyntax>()
            .Where(attribute => attribute.Name.ToString().Contains(attributeName, StringComparison.Ordinal))
            .ToArray();
        Ensure(attributes.Length == 2, $"Expected two {attributeName} attributes in one list.");
        var descriptor = new DiagnosticDescriptor(
            diagnosticId,
            diagnosticId,
            "Synthetic diagnostic whose message must not control the fix",
            "Tests",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        var diagnostics = attributes
            .Select(attribute => Diagnostic.Create(descriptor, attribute.GetLocation()))
            .ToImmutableArray();
        var provider = CreateProvider();
        var diagnosticProvider = new TestFixAllDiagnosticProvider(
            new Dictionary<DocumentId, ImmutableArray<Diagnostic>>
            {
                [document.Id] = diagnostics
            });
        var context = new FixAllContext(
            document,
            provider,
            FixAllScope.Document,
            equivalenceKey,
            [diagnosticId],
            diagnosticProvider,
            CancellationToken.None);

        var fixAllProvider = provider.GetFixAllProvider();
        Ensure(fixAllProvider is not null, "The provider must expose a Fix All provider.");
        var fixAllAction = await fixAllProvider!.GetFixAsync(context);
        Ensure(fixAllAction is null,
            $"{equivalenceKey} must decline Fix All rather than partially editing a shared AttributeList.");

        foreach (var diagnostic in diagnostics)
        {
            var actions = await workspace.GetActionsAsync(diagnostic, documentName);
            Ensure(actions.Count == 1 && actions[0].EquivalenceKey == equivalenceKey,
                $"Each individual {diagnosticId} diagnostic must retain its removal action.");
            var changed = await GetChangedSolutionAsync(actions[0]);
            var changedSource = await workspace.GetTextAsync(documentName, changed);
            Ensure(CountAttributes(changedSource, attributeName) == 1,
                $"An individual {diagnosticId} fix must remove exactly its selected attribute.");
            await workspace.AssertCompilesAsync(changed);
        }
    }

    private static string DtoContract(string members) => $$"""
using System.Threading;
using System.Threading.Tasks;

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
""";

    private static async Task<ContractGeneratorResult> RunContractGeneratorAsync(
        CodeFixTestWorkspace workspace,
        string? baseline = null)
    {
        const string baselinePath = "/contracts/previous.sharplink.json";
        var project = workspace.Solution.GetProject(workspace.ProjectId)
                      ?? throw new InvalidOperationException("Test project was unavailable.");
        var compilation = await project.GetCompilationAsync() as CSharpCompilation
                          ?? throw new InvalidOperationException("C# compilation was unavailable.");
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var additionalTexts = ImmutableArray<AdditionalText>.Empty;
        if (baseline is not null)
        {
            properties["build_property.SharpLinkContractBaseline"] = baselinePath;
            additionalTexts = [new InMemoryAdditionalText(baselinePath, baseline)];
        }

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            additionalTexts,
            project.ParseOptions as CSharpParseOptions,
            new TestAnalyzerConfigOptionsProvider(properties));
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        var generated = result.GeneratedTrees
            .Select(static tree => tree.GetText().ToString())
            .First(static text => text.Contains("__SharpLinkContractManifest", StringComparison.Ordinal));
        const string startMarker = "internal const string Json = @\"";
        const string endMarker = "\";";
        var start = generated.IndexOf(startMarker, StringComparison.Ordinal) + startMarker.Length;
        var end = generated.LastIndexOf(endMarker, StringComparison.Ordinal);
        Ensure(start >= startMarker.Length && end > start, "generated contract Manifest constant");
        var json = generated.Substring(start, end - start).Replace("\"\"", "\"", StringComparison.Ordinal);
        return new ContractGeneratorResult(json, result.Diagnostics);
    }

    private static int CountAttributes(string source, string attributeName)
        => CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<AttributeSyntax>()
            .Count(attribute => attribute.Name.ToString().Contains(attributeName, StringComparison.Ordinal));

    private static int CountMarkedConstructors(string source, string markerName)
        => CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Count(constructor => constructor.AttributeLists
                .SelectMany(static list => list.Attributes)
                .Any(attribute => attribute.Name.ToString().Contains(markerName, StringComparison.Ordinal)));

    private sealed record ContractGeneratorResult(
        string Json,
        ImmutableArray<Diagnostic> Diagnostics);

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

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
            => TestAnalyzerConfigOptions.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
            => TestAnalyzerConfigOptions.Empty;
    }

    private sealed class TestAnalyzerConfigOptions(
        IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        internal static TestAnalyzerConfigOptions Empty { get; } = new(new Dictionary<string, string>());

        public override bool TryGetValue(string key, out string value)
            => values.TryGetValue(key, out value!);
    }
}
