using System.Text;
using Microsoft.CodeAnalysis.Diagnostics;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ThirteenthCodexReviewRegressionTests
{
    [Test]
    public async Task MissingContractFixShouldRejectGeneratedContractOutsideRegularDocuments()
    {
        using (var generated = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : GeneratedApi.IGeneratedContract
{
    public Service() { }
}
""")))
        {
            AddGeneratedSource(generated, """
namespace GeneratedApi;

public interface IGeneratedContract : SharpLink.Sdk.IService { }
""");
            await generated.AssertCompilesAsync();
            var project = generated.Solution.GetProject(generated.ProjectId)
                          ?? throw new InvalidOperationException("Test project was unavailable.");
            var compilation = await project.GetCompilationAsync()
                              ?? throw new InvalidOperationException("Compilation was unavailable.");
            var contract = compilation.GetTypeByMetadataName("GeneratedApi.IGeneratedContract")
                           ?? throw new InvalidOperationException("Generated contract was unavailable.");
            var generatedTree = contract.DeclaringSyntaxReferences.Single().SyntaxTree;
            var regularTrees = await Task.WhenAll(project.Documents.Select(static document =>
                document.GetSyntaxTreeAsync()));
            Ensure(!regularTrees.Contains(generatedTree),
                "The contract candidate must come from a generated tree outside Project.Documents.");
            var diagnostic = await generated.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");

            var actions = await generated.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.Count == 0,
                "AnnotateRpcContract must be withheld when its sole candidate is source-generated and not editable.");
        }

        using var ordinary = CodeFixTestWorkspace.Create(("Service.cs", """
public interface IContract : SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract
{
    public Service() { }
}
"""));
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");

        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Service.cs");

        Ensure(ordinaryActions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Annotate IContract with [RpcContract]", "AnnotateRpcContract")]),
            "An ordinary source contract candidate must retain AnnotateRpcContract.");
        var ordinaryChanged = await ordinary.ApplyAsync(ordinaryActions[0]);
        var ordinarySource = await ordinary.GetTextAsync("Service.cs", ordinaryChanged);
        EnsureContains(ordinarySource, "[global::SharpLink.Sdk.RpcContract]", "ordinary source contract");
        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    [Test]
    public async Task MissingContractFixShouldRejectMixedGeneratedPartialContract()
    {
        using (var mixed = CodeFixTestWorkspace.Create(
                   ("Contract.cs", """
namespace Mixed;

public partial interface IContract { }
"""),
                   ("Service.cs", """
namespace Mixed;

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract
{
    public Service() { }

    public System.Threading.Tasks.ValueTask<int> RunAsync() =>
        System.Threading.Tasks.ValueTask.FromResult(42);
}
""")))
        {
            AddGeneratedSource(mixed, """
namespace Mixed;

public partial interface IContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    System.Threading.Tasks.ValueTask<int> RunAsync();
}
""");
            await mixed.AssertCompilesAsync();
            var project = mixed.Solution.GetProject(mixed.ProjectId)
                          ?? throw new InvalidOperationException("Test project was unavailable.");
            var compilation = await project.GetCompilationAsync()
                              ?? throw new InvalidOperationException("Compilation was unavailable.");
            var contract = compilation.GetTypeByMetadataName("Mixed.IContract")
                           ?? throw new InvalidOperationException("Mixed partial contract was unavailable.");
            var regularTrees = await Task.WhenAll(project.Documents.Select(static document =>
                document.GetSyntaxTreeAsync()));
            Ensure(contract.DeclaringSyntaxReferences.Any(reference => regularTrees.Contains(reference.SyntaxTree)) &&
                   contract.DeclaringSyntaxReferences.Any(reference => !regularTrees.Contains(reference.SyntaxTree)),
                "The fixture must combine regular and generated partial declarations.");
            var diagnostic = await mixed.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");

            var actions = await mixed.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "AnnotateRpcContract"),
                "AnnotateRpcContract must be withheld when generated partial declarations provide the candidate shape.");
        }

        using var regular = CodeFixTestWorkspace.Create(
            ("Contract.Declaration.cs", """
namespace Regular;

public partial interface IContract { }
"""),
            ("Contract.Shape.cs", """
namespace Regular;

public partial interface IContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    System.Threading.Tasks.ValueTask<int> RunAsync();
}
"""),
            ("Service.cs", """
namespace Regular;

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract
{
    public Service() { }

    public System.Threading.Tasks.ValueTask<int> RunAsync() =>
        System.Threading.Tasks.ValueTask.FromResult(42);
}
"""));
        await regular.AssertCompilesAsync();
        var regularDiagnostic = await regular.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");

        var regularActions = await regular.GetActionsAsync(regularDiagnostic, "Service.cs");
        var regularAction = regularActions.Single(static action =>
            action.EquivalenceKey == "AnnotateRpcContract");
        var regularChanged = await regular.ApplyAsync(regularAction);
        var regularContract = await regular.GetTextAsync("Contract.Declaration.cs", regularChanged) +
                              await regular.GetTextAsync("Contract.Shape.cs", regularChanged);

        EnsureContains(regularContract, "[global::SharpLink.Sdk.RpcContract]", "regular partial contract");
        await regular.AssertCompilesAsync(regularChanged);
    }

    [Test]
    public async Task RestoreServiceRouteShouldExcludeErrorObsoleteImplementations()
    {
        using (var obsoleteOnly = CodeFixTestWorkspace.Create(("Services.cs", """
using System;

[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }

[Obsolete("Removed service", true)]
public sealed class ObsoleteService : IContract
{
    public ObsoleteService() { }
}
""")))
        {
            await obsoleteOnly.AssertCompilesAsync();
            var diagnostic = await obsoleteOnly.CreateDiagnosticAsync("SHARPLINK037", "Services.cs");

            var actions = await obsoleteOnly.GetActionsAsync(diagnostic, "Services.cs");

            Ensure(actions.Count == 0,
                "A sole error-obsolete implementation must not receive [RpcService].");
        }

        using var validSibling = CodeFixTestWorkspace.Create(("Services.cs", """
using System;

[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }

[Obsolete("Removed service", true)]
public sealed class ObsoleteService : IContract
{
    public ObsoleteService() { }
}

public sealed class ValidService : IContract
{
    public ValidService() { }
}
"""));
        await validSibling.AssertCompilesAsync();
        var validDiagnostic = await validSibling.CreateDiagnosticAsync("SHARPLINK037", "Services.cs");

        var validActions = await validSibling.GetActionsAsync(validDiagnostic, "Services.cs");

        Ensure(validActions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Add [RpcService] to ValidService", "RestoreServiceRoute")]),
            "Filtering an error-obsolete implementation must leave the sole valid sibling repairable.");
        var validChanged = await validSibling.ApplyAsync(validActions[0]);
        var validSource = await validSibling.GetTextAsync("Services.cs", validChanged);
        var markedTypes = CSharpSyntaxTree.ParseText(validSource)
            .GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
            .Where(static type => type.AttributeLists.SelectMany(static list => list.Attributes)
                .Any(static attribute => attribute.Name.ToString().Contains("RpcService", StringComparison.Ordinal)))
            .Select(static type => type.Identifier.ValueText)
            .ToArray();
        Ensure(markedTypes.SequenceEqual(["ValidService"]),
            $"Only the valid sibling may receive RpcService. Actual: {string.Join(", ", markedTypes)}");
        await validSibling.AssertCompilesAsync(validChanged);
    }

    [Test]
    public async Task AddCancellationTokenShouldWithholdForCSharp13ExpressionTreeInvocation()
    {
        using (var expressionTree = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Linq.Expressions;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](int value);
}

public static class Calls
{
    public static Expression<Func<IContract, int>> Call { get; } = contract => contract.Run(42);
}
""")))
        {
            SetLanguageVersion(expressionTree, LanguageVersion.CSharp13);
            await expressionTree.AssertCompilesAsync();
            var diagnostic = await expressionTree.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");

            var actions = await expressionTree.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                    [("Annotate with [NonCancellable]", "AddNonCancellable")]),
                $"C# 13 expression trees must suppress only the unsafe signature action. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
            var annotated = await expressionTree.ApplyAsync(actions[0]);
            await expressionTree.AssertCompilesAsync(annotated);
        }

        using var ordinary = CodeFixTestWorkspace.Create(("Contract.cs", """
public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](int value);
}

public static class Calls
{
    public static int Call(IContract contract) => contract.Run(42);
}
"""));
        SetLanguageVersion(ordinary, LanguageVersion.CSharp13);
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Contract.cs");
        var signatureAction = ordinaryActions.Single(static action =>
            action.EquivalenceKey == "Signature:AddCancellationToken");

        var ordinaryChanged = await ordinary.ApplyAsync(signatureAction);

        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    [Test]
    public async Task ReorderControlParametersShouldWithholdForCSharp13ExpressionTreeInvocation()
    {
        using (var expressionTree = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Linq.Expressions;
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken token, int value, SharpLink.Sdk.SharpLinkCallOptions options);
}

public static class Calls
{
    public static Expression<Func<IContract, int>> Call { get; } =
        contract => contract.Run(default, 42, default);
}
""")))
        {
            SetLanguageVersion(expressionTree, LanguageVersion.CSharp13);
            await expressionTree.AssertCompilesAsync();
            var diagnostic = await expressionTree.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");

            var actions = await expressionTree.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Count == 0,
                "ReorderControlParameters must be withheld when it would introduce named arguments into a C# 13 expression tree.");
        }

        using var ordinary = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken token, int value, SharpLink.Sdk.SharpLinkCallOptions options);
}

public static class Calls
{
    public static int Call(IContract contract) => contract.Run(default, 42, default);
}
"""));
        SetLanguageVersion(ordinary, LanguageVersion.CSharp13);
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");
        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Contract.cs");

        Ensure(ordinaryActions.Select(static action => action.EquivalenceKey).SequenceEqual(
                ["Signature:ReorderControlParameters"]),
            "An ordinary C# 13 invocation must retain ReorderControlParameters.");
        var ordinaryChanged = await ordinary.ApplyAsync(ordinaryActions[0]);
        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    private static void SetLanguageVersion(CodeFixTestWorkspace workspace, LanguageVersion languageVersion)
        => SetSolution(
            workspace,
            workspace.Solution.WithProjectParseOptions(
                workspace.ProjectId,
                new CSharpParseOptions(languageVersion)));

    private static void AddGeneratedSource(CodeFixTestWorkspace workspace, string source)
        => SetSolution(
            workspace,
            workspace.Solution.AddAnalyzerReference(
                workspace.ProjectId,
                new TestGeneratorReference(new FixedSourceGenerator(source))));

    private static void SetSolution(CodeFixTestWorkspace workspace, Solution solution)
    {
        var solutionProperty = typeof(CodeFixTestWorkspace).GetProperty(
            nameof(CodeFixTestWorkspace.Solution),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Test workspace Solution property was unavailable.");
        solutionProperty.SetValue(workspace, solution);
    }

    private sealed class TestGeneratorReference(ISourceGenerator generator) : AnalyzerReference
    {
        public override string? FullPath => null;

        public override string Display => "ThirteenthCodexReviewGeneratedContract";

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
            => context.AddSource("GeneratedContract.g.cs", SourceText.From(source, Encoding.UTF8));
    }
#pragma warning restore RS1042
}
