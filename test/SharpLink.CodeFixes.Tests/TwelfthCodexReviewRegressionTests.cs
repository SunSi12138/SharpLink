using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwelfthCodexReviewRegressionTests
{
    [Test]
    public async Task AdapterShapeFixShouldRejectErrorObsoleteParameterlessConstructor()
    {
        using (var obsolete = CodeFixTestWorkspace.Create(("Adapter.cs", """
using System;

[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    [Obsolete("Removed constructor", true)]
    private Adapter() { }
}
""")))
        {
            await obsolete.AssertCompilesAsync();
            var diagnostic = await obsolete.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

            var actions = await obsolete.GetActionsAsync(diagnostic, "Adapter.cs");

            Ensure(actions.Count == 0,
                "The adapter shape fix must not expose an error-obsolete parameterless constructor.");
        }

        using var valid = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    private Adapter() { }
}
"""));
        await valid.AssertCompilesAsync();
        var validDiagnostic = await valid.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var validActions = await valid.GetActionsAsync(validDiagnostic, "Adapter.cs");

        Ensure(validActions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Fix Adapter Codec adapter shape", "FixAdapterShape")]),
            "A usable private parameterless constructor must retain adapter shape repair.");
        var changed = await valid.ApplyAsync(validActions[0]);
        var source = await valid.GetTextAsync("Adapter.cs", changed);
        EnsureContains(source, "public Adapter()", "usable parameterless adapter constructor");
        await valid.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task AddCancellationTokenShouldPreserveDeclarationSeparatorTrivia()
    {
        const string triviaTag = "add-declaration-separator";
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IContract : IService
{
    ValueTask<int> [|RunAsync|](
        int value, // add-declaration-separator
        SharpLinkCallOptions options);
}
"""));
        await workspace.AssertCompilesAsync();
        var original = await workspace.GetTextAsync("Contract.cs");
        Ensure(ParameterSeparatorHasTrivia(original, triviaTag),
            "The fixture comment must belong to a parameter comma.");
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "Signature:AddCancellationToken");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        Ensure(ParameterSeparatorHasTrivia(source, triviaTag),
            $"Adding CancellationToken must preserve // trivia attached to an existing parameter comma. Actual: {source}");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task KeepControlParameterShouldPreserveDeclarationSeparatorTrivia()
    {
        const string triviaTag = "keep-declaration-separator";
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken first, /* keep-declaration-separator */ int value, CancellationToken second);
}
"""));
        await workspace.AssertCompilesAsync();
        var original = await workspace.GetTextAsync("Contract.cs");
        Ensure(ParameterSeparatorHasTrivia(original, triviaTag),
            "The fixture comment must belong to a parameter comma.");
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK002", "Contract.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");
        var action = actions.Single(static item =>
            item.EquivalenceKey == "Signature:Keep:CancellationToken:0");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        Ensure(ParameterSeparatorHasTrivia(source, triviaTag),
            $"Keeping one control parameter must preserve /* */ trivia on the surviving parameter comma. Actual: {source}");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task ReorderControlParametersShouldPreserveDeclarationAndInvocationSeparatorTrivia()
    {
        const string declarationTag = "reorder-declaration-separator";
        const string invocationTag = "reorder-invocation-separator";
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading;
using SharpLink.Sdk;

public interface IContract : IService
{
    int [|Run|](CancellationToken token, /* reorder-declaration-separator */ int value, SharpLinkCallOptions options);
}
"""),
            ("Caller.cs", """
using System.Threading;
using SharpLink.Sdk;

public static class Caller
{
    public static int Call(IContract contract)
        => contract.Run(default, /* reorder-invocation-separator */ 42, default);
}
"""));
        await workspace.AssertCompilesAsync();
        var originalDeclaration = await workspace.GetTextAsync("Contract.cs");
        var originalInvocation = await workspace.GetTextAsync("Caller.cs");
        Ensure(ParameterSeparatorHasTrivia(originalDeclaration, declarationTag),
            "The fixture comment must belong to a parameter comma.");
        Ensure(ArgumentSeparatorHasTrivia(originalInvocation, invocationTag),
            "The fixture comment must belong to an argument comma.");
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");
        var action = actions.Single(static item =>
            item.EquivalenceKey == "Signature:ReorderControlParameters");

        var changed = await workspace.ApplyAsync(action);
        var declaration = await workspace.GetTextAsync("Contract.cs", changed);
        var invocation = await workspace.GetTextAsync("Caller.cs", changed);

        Ensure(ParameterSeparatorHasTrivia(declaration, declarationTag) &&
               ArgumentSeparatorHasTrivia(invocation, invocationTag),
            "Reordering declarations and rewriting calls must preserve block trivia attached to their commas. " +
            $"Actual declaration: {declaration} Actual invocation: {invocation}");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task PublicizationShouldRejectGeneratedDependencyOutsideRegularSolutionDocuments()
    {
        using (var generated = CodeFixTestWorkspace.Create(("Service.cs", """
using GeneratedApi;

[SharpLink.Sdk.RpcService]
internal sealed class [|Service|] : GeneratedBase
{
    public Service() { }
}
""")))
        {
            AddGeneratedSource(generated, """
namespace GeneratedApi;

internal class GeneratedBase { }
""");
            await generated.AssertCompilesAsync();
            var project = generated.Solution.GetProject(generated.ProjectId)
                          ?? throw new InvalidOperationException("Test project was unavailable.");
            var compilation = await project.GetCompilationAsync()
                              ?? throw new InvalidOperationException("Compilation was unavailable.");
            var dependency = compilation.GetTypeByMetadataName("GeneratedApi.GeneratedBase")
                             ?? throw new InvalidOperationException("Generated dependency was unavailable.");
            var generatedTree = dependency.DeclaringSyntaxReferences.Single().SyntaxTree;
            var regularTrees = await Task.WhenAll(project.Documents.Select(static document =>
                document.GetSyntaxTreeAsync()));
            Ensure(!regularTrees.Contains(generatedTree),
                "The fixture dependency must come from a generated tree outside Project.Documents.");
            var diagnostic = await generated.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

            var actions = await generated.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.Count == 0,
                "Publicization must be withheld when its source-generated dependency cannot be edited through the Solution.");
        }

        using var ordinary = CodeFixTestWorkspace.Create(("Service.cs", """
internal class SourceBase { }

[SharpLink.Sdk.RpcService]
internal sealed class [|Service|] : SourceBase
{
    public Service() { }
}
"""));
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Service.cs");

        Ensure(ordinaryActions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Make RPC service publicly reachable", "MakeServicePublic")]),
            "A publicization closure made entirely of ordinary source documents must remain repairable.");
        var ordinaryChanged = await ordinary.ApplyAsync(ordinaryActions[0]);
        var ordinarySource = await ordinary.GetTextAsync("Service.cs", ordinaryChanged);
        EnsureContains(ordinarySource, "public class SourceBase", "ordinary source dependency");
        EnsureContains(ordinarySource, "public sealed class Service", "ordinary source RPC service");
        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    private static bool ParameterSeparatorHasTrivia(string source, string triviaTag)
        => CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<ParameterListSyntax>()
            .SelectMany(static list => list.Parameters.GetSeparators())
            .Any(separator => SeparatorHasTrivia(separator, triviaTag));

    private static bool ArgumentSeparatorHasTrivia(string source, string triviaTag)
        => CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<ArgumentListSyntax>()
            .SelectMany(static list => list.Arguments.GetSeparators())
            .Any(separator => SeparatorHasTrivia(separator, triviaTag));

    private static bool SeparatorHasTrivia(SyntaxToken separator, string triviaTag)
        => separator.LeadingTrivia.Concat(separator.TrailingTrivia)
            .Any(trivia => trivia.ToFullString().Contains(triviaTag, StringComparison.Ordinal));

    private static void AddGeneratedSource(CodeFixTestWorkspace workspace, string source)
    {
        var updated = workspace.Solution.AddAnalyzerReference(
            workspace.ProjectId,
            new TestGeneratorReference(new FixedSourceGenerator(source)));
        var solutionProperty = typeof(CodeFixTestWorkspace).GetProperty(
            nameof(CodeFixTestWorkspace.Solution),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Test workspace Solution property was unavailable.");
        solutionProperty.SetValue(workspace, updated);
    }

    private sealed class TestGeneratorReference(ISourceGenerator generator) : AnalyzerReference
    {
        public override string? FullPath => null;

        public override string Display => "TwelfthCodexReviewGeneratedDependency";

        public override object Id { get; } = new();

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language)
            => [];

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages()
            => [];

        public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
            => language == LanguageNames.CSharp ? [generator] : [];

        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages()
            => [generator];
    }

#pragma warning disable RS1042 // This test-only generator intentionally runs in the host test process.
    private sealed class FixedSourceGenerator(string source) : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context) { }

        public void Execute(GeneratorExecutionContext context)
            => context.AddSource("GeneratedDependency.g.cs", SourceText.From(source, Encoding.UTF8));
    }
#pragma warning restore RS1042
}
