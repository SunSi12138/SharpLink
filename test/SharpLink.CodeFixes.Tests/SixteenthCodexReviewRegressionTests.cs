using Microsoft.CodeAnalysis.Diagnostics;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class SixteenthCodexReviewRegressionTests
{
    [Test]
    public async Task MissingContractFixShouldRequireExactlyOneCancellationPolicyPerRpcMethod()
    {
        var invalidScenarios = new[]
        {
            (
                Name: "streaming method without a cancellation policy",
                ContractMethod: "System.Collections.Generic.IAsyncEnumerable<int> Stream();",
                ImplementationMethod:
                    "public System.Collections.Generic.IAsyncEnumerable<int> Stream() => StreamCore();",
                Helper: """
    private static async System.Collections.Generic.IAsyncEnumerable<int> StreamCore()
    {
        await System.Threading.Tasks.Task.Yield();
        yield return 42;
    }
"""),
            (
                Name: "method with both CancellationToken and NonCancellable",
                ContractMethod: "[SharpLink.Sdk.NonCancellable] System.Threading.Tasks.ValueTask<int> Run(System.Threading.CancellationToken cancellationToken);",
                ImplementationMethod:
                    "public System.Threading.Tasks.ValueTask<int> Run(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.ValueTask.FromResult(42);",
                Helper: string.Empty),
            (
                Name: "non-streaming method without a cancellation policy",
                ContractMethod: "System.Threading.Tasks.ValueTask<int> Run();",
                ImplementationMethod:
                    "public System.Threading.Tasks.ValueTask<int> Run() => System.Threading.Tasks.ValueTask.FromResult(42);",
                Helper: string.Empty)
        };

        foreach (var scenario in invalidScenarios)
        {
            using var invalid = CodeFixTestWorkspace.Create(("Service.cs", $$"""
public interface IContract : SharpLink.Sdk.IService
{
    {{scenario.ContractMethod}}
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract
{
    public Service() { }
    {{scenario.ImplementationMethod}}
{{scenario.Helper}}
}
"""));
            await invalid.AssertCompilesAsync();
            var diagnostic = await invalid.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");

            var actions = await invalid.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "AnnotateRpcContract"),
                $"AnnotateRpcContract must be withheld for a {scenario.Name}. Actual: {string.Join(", ", actions.Select(static action => action.EquivalenceKey))}");
        }

        using var valid = CodeFixTestWorkspace.Create(("Service.cs", """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    ValueTask<int> WithCancellation(CancellationToken cancellationToken);
    IAsyncEnumerable<int> StreamWithCancellation(CancellationToken cancellationToken);

    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> WithoutCancellation();

    [SharpLink.Sdk.NonCancellable]
    IAsyncEnumerable<int> StreamWithoutCancellation();
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract
{
    public Service() { }

    public ValueTask<int> WithCancellation(CancellationToken cancellationToken) =>
        ValueTask.FromResult(42);

    public IAsyncEnumerable<int> StreamWithCancellation(CancellationToken cancellationToken) =>
        StreamCore();

    public ValueTask<int> WithoutCancellation() => ValueTask.FromResult(42);

    public IAsyncEnumerable<int> StreamWithoutCancellation() => StreamCore();

    private static async IAsyncEnumerable<int> StreamCore()
    {
        await Task.Yield();
        yield return 42;
    }
}
"""));
        await valid.AssertCompilesAsync();
        var validDiagnostic = await valid.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");
        var validActions = await valid.GetActionsAsync(validDiagnostic, "Service.cs");
        var validAction = validActions.Single(static action =>
            action.EquivalenceKey == "AnnotateRpcContract");

        var validChanged = await valid.ApplyAsync(validAction);
        var validSource = await valid.GetTextAsync("Service.cs", validChanged);

        EnsureContains(validSource, "[global::SharpLink.Sdk.RpcContract]", "valid XOR cancellation policies");
        await valid.AssertCompilesAsync(validChanged);
    }

    [Test]
    public async Task RestoreServiceRouteShouldOnlyConsiderImplementationsInDiagnosticProject()
    {
        using (var crossProject = CodeFixTestWorkspace.Create(("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }
""")))
        {
            AddDownstreamImplementationProject(crossProject);
            await crossProject.AssertCompilesAsync();
            Ensure(crossProject.Solution.ProjectIds.Count == 2,
                "The regression fixture must contain two real Roslyn projects.");
            var diagnostic = await crossProject.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");

            var actions = await crossProject.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "RestoreServiceRoute"),
                "RestoreServiceRoute must be withheld when the only implementation is in a downstream project.");
        }

        using var sameProject = CodeFixTestWorkspace.Create(("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }

public sealed class Service : IContract
{
    public Service() { }
}
"""));
        await sameProject.AssertCompilesAsync();
        var sameProjectDiagnostic = await sameProject.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");
        var sameProjectActions = await sameProject.GetActionsAsync(sameProjectDiagnostic, "Contract.cs");
        var sameProjectAction = sameProjectActions.Single(static action =>
            action.EquivalenceKey == "RestoreServiceRoute");

        var sameProjectChanged = await sameProject.ApplyAsync(sameProjectAction);
        var sameProjectSource = await sameProject.GetTextAsync("Contract.cs", sameProjectChanged);

        EnsureContains(sameProjectSource, "[global::SharpLink.Sdk.RpcService]", "same-project implementation");
        await sameProject.AssertCompilesAsync(sameProjectChanged);
    }

    [Test]
    public async Task SignatureActionsShouldRejectSourceGeneratedInvocationCallers()
    {
        var generatedScenarios = new[]
        {
            (
                Name: "AddCancellationToken",
                Source: """
using System.Threading.Tasks;

public interface IContract { }
public sealed class SixteenthAddCallerScenario { }
public interface IAddContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value);
}
""",
                DiagnosticId: "SHARPLINK004",
                ForbiddenKey: "Signature:AddCancellationToken",
                GeneratedType: "SixteenthGeneratedAddCaller"),
            (
                Name: "KeepControlParameter",
                Source: """
using System.Threading;

public interface IContract { }
public sealed class SixteenthKeepCallerScenario { }
public interface IKeepContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken first, int value, CancellationToken second);
}
""",
                DiagnosticId: "SHARPLINK002",
                ForbiddenKey: "Signature:Keep:CancellationToken:0",
                GeneratedType: "SixteenthGeneratedKeepCaller"),
            (
                Name: "ReorderControlParameters",
                Source: """
using System.Threading;

public interface IContract { }
public sealed class SixteenthReorderCallerScenario { }
public interface IReorderContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken cancellationToken, int value, SharpLink.Sdk.SharpLinkCallOptions options);
}
""",
                DiagnosticId: "SHARPLINK008",
                ForbiddenKey: "Signature:ReorderControlParameters",
                GeneratedType: "SixteenthGeneratedReorderCaller")
        };

        foreach (var scenario in generatedScenarios)
        {
            using var generated = CodeFixTestWorkspace.Create(("Contract.cs", scenario.Source));
            AddGeneratedInvocationCallers(generated);
            await generated.AssertCompilesAsync();
            await AssertGeneratedTypeIsOutsideRegularDocumentsAsync(generated, scenario.GeneratedType);
            var diagnostic = await generated.CreateDiagnosticAsync(scenario.DiagnosticId, "Contract.cs");

            var actions = await generated.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.All(action => action.EquivalenceKey != scenario.ForbiddenKey),
                $"{scenario.Name} must be withheld for a source-generated invocation caller. Actual: {string.Join(", ", actions.Select(static action => action.EquivalenceKey))}");
        }

        var ordinaryScenarios = new[]
        {
            (
                Name: "AddCancellationToken",
                Source: """
using System.Threading.Tasks;

public interface IAddContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value);
}

public static class Caller
{
    public static ValueTask<int> Call(IAddContract contract) => contract.RunAsync(42);
}
""",
                DiagnosticId: "SHARPLINK004",
                EquivalenceKey: "Signature:AddCancellationToken"),
            (
                Name: "KeepControlParameter",
                Source: """
using System.Threading;

public interface IKeepContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken first, int value, CancellationToken second);
}

public static class Caller
{
    public static int Call(IKeepContract contract) => contract.Run(default, 42, default);
}
""",
                DiagnosticId: "SHARPLINK002",
                EquivalenceKey: "Signature:Keep:CancellationToken:0"),
            (
                Name: "ReorderControlParameters",
                Source: """
using System.Threading;

public interface IReorderContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken cancellationToken, int value, SharpLink.Sdk.SharpLinkCallOptions options);
}

public static class Caller
{
    public static int Call(IReorderContract contract) => contract.Run(default, 42, default);
}
""",
                DiagnosticId: "SHARPLINK008",
                EquivalenceKey: "Signature:ReorderControlParameters")
        };

        foreach (var scenario in ordinaryScenarios)
        {
            using var ordinary = CodeFixTestWorkspace.Create(("Contract.cs", scenario.Source));
            await ordinary.AssertCompilesAsync();
            var diagnostic = await ordinary.CreateDiagnosticAsync(scenario.DiagnosticId, "Contract.cs");
            var actions = await ordinary.GetActionsAsync(diagnostic, "Contract.cs");
            var action = actions.Single(item => item.EquivalenceKey == scenario.EquivalenceKey);

            var changed = await ordinary.ApplyAsync(action);

            await ordinary.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task ServiceLifetimeFixShouldRejectErrorObsoleteService()
    {
        using (var obsolete = CodeFixTestWorkspace.Create(("Service.cs", """
using System;

[Obsolete("Removed service", true)]
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service() { }
}
""")))
        {
            await obsolete.AssertCompilesAsync();
            var diagnostic = await obsolete.CreateDiagnosticAsync("SHARPLINK020", "Service.cs");

            var actions = await obsolete.GetActionsAsync(diagnostic, "Service.cs");

            var lifetimeActions = actions.Where(static action =>
                action.EquivalenceKey?.StartsWith("SetLifetime:", StringComparison.Ordinal) == true).ToArray();
            Ensure(lifetimeActions.Length == 0,
                $"An error-obsolete service must not offer lifetime actions. Actual: {string.Join(", ", actions.Select(static action => action.EquivalenceKey))}");
        }

        using var ordinary = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service() { }
}
"""));
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK020", "Service.cs");
        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Service.cs");

        Ensure(ordinaryActions.Select(static action => action.EquivalenceKey).SequenceEqual(
                ["SetLifetime:Singleton", "SetLifetime:Connection", "SetLifetime:Call"],
                StringComparer.Ordinal),
            "An ordinary service must retain all three lifetime actions.");
        var ordinaryChanged = await ordinary.ApplyAsync(
            ordinaryActions.Single(static action => action.EquivalenceKey == "SetLifetime:Call"));
        var ordinarySource = await ordinary.GetTextAsync("Service.cs", ordinaryChanged);

        EnsureContains(
            ordinarySource,
            "RpcService(Lifetime = global::SharpLink.Sdk.SharpLinkServiceLifetime.Call)",
            "ordinary service lifetime");
        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    private static void AddDownstreamImplementationProject(CodeFixTestWorkspace workspace)
    {
        var upstream = workspace.Solution.GetProject(workspace.ProjectId)
                       ?? throw new InvalidOperationException("Upstream project was unavailable.");
        var downstreamId = ProjectId.CreateNewId("DownstreamImplementations");
        var downstreamInfo = ProjectInfo.Create(
            downstreamId,
            VersionStamp.Create(),
            "DownstreamImplementations",
            "DownstreamImplementations",
            LanguageNames.CSharp,
            parseOptions: upstream.ParseOptions,
            compilationOptions: upstream.CompilationOptions);
        var downstreamDocumentId = DocumentId.CreateNewId(downstreamId, "Implementation.cs");
        var updated = workspace.Solution
            .AddProject(downstreamInfo)
            .AddMetadataReferences(downstreamId, upstream.MetadataReferences)
            .AddProjectReference(downstreamId, new ProjectReference(workspace.ProjectId))
            .AddDocument(downstreamDocumentId, "Implementation.cs", SourceText.From("""
public sealed class DownstreamService : IContract
{
    public DownstreamService() { }
}
"""));
        SetSolution(workspace, updated);
    }

    private static void AddGeneratedInvocationCallers(CodeFixTestWorkspace workspace)
    {
        var updated = workspace.Solution.AddAnalyzerReference(
            workspace.ProjectId,
            new AnalyzerFileReference(
                typeof(SixteenthGeneratedInvocationCallerGenerator).Assembly.Location,
                CurrentAssemblyLoader.Instance));
        SetSolution(workspace, updated);
    }

    private static void SetSolution(CodeFixTestWorkspace workspace, Solution solution)
    {
        var solutionProperty = typeof(CodeFixTestWorkspace).GetProperty(
            nameof(CodeFixTestWorkspace.Solution),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Test workspace Solution property was unavailable.");
        solutionProperty.SetValue(workspace, solution);
    }

    private static async Task AssertGeneratedTypeIsOutsideRegularDocumentsAsync(
        CodeFixTestWorkspace workspace,
        string metadataName)
    {
        var project = workspace.Solution.GetProject(workspace.ProjectId)
                      ?? throw new InvalidOperationException("Test project was unavailable.");
        var compilation = await project.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        var type = compilation.GetTypeByMetadataName(metadataName)
                   ?? throw new InvalidOperationException($"Generated type '{metadataName}' was unavailable.");
        var regularTrees = await Task.WhenAll(project.Documents.Select(static document =>
            document.GetSyntaxTreeAsync()));
        Ensure(type.DeclaringSyntaxReferences.Length != 0 &&
               type.DeclaringSyntaxReferences.All(reference => !regularTrees.Contains(reference.SyntaxTree)),
            $"'{metadataName}' must be generated outside Project.Documents.");
    }

    private sealed class CurrentAssemblyLoader : IAnalyzerAssemblyLoader
    {
        internal static CurrentAssemblyLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath) { }

        public Assembly LoadFromPath(string fullPath)
            => typeof(SixteenthGeneratedInvocationCallerGenerator).Assembly;
    }
}

#pragma warning disable RS1036, RS1038, RS1041, RS1042 // Test-only generator runs in the host test process.
[Generator]
public sealed class SixteenthGeneratedInvocationCallerGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        if (HasType("SixteenthAddCallerScenario"))
        {
            Add("SixteenthAddCaller.g.cs", """
public static class SixteenthGeneratedAddCaller
{
    public static System.Threading.Tasks.ValueTask<int> Call(IAddContract contract) =>
        contract.RunAsync(42);
}
""");
        }
        if (HasType("SixteenthKeepCallerScenario"))
        {
            Add("SixteenthKeepCaller.g.cs", """
public static class SixteenthGeneratedKeepCaller
{
    public static int Call(IKeepContract contract) => contract.Run(default, 42, default);
}
""");
        }
        if (HasType("SixteenthReorderCallerScenario"))
        {
            Add("SixteenthReorderCaller.g.cs", """
public static class SixteenthGeneratedReorderCaller
{
    public static int Call(IReorderContract contract) => contract.Run(default, 42, default);
}
""");
        }

        bool HasType(string metadataName)
            => context.Compilation.GetTypeByMetadataName(metadataName) is not null;

        void Add(string hintName, string source)
            => context.AddSource(hintName, source);
    }
}
#pragma warning restore RS1036, RS1038, RS1041, RS1042
