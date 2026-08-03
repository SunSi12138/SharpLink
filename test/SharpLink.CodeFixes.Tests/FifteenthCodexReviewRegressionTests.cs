using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class FifteenthCodexReviewRegressionTests
{
    [Test]
    public async Task SignatureActionsShouldRejectGeneratedRelatedMethods()
    {
        var generatedScenarios = new[]
        {
            (
                Name: "AddCancellationToken",
                Source: """
using System.Threading.Tasks;

public interface IContract { }
public sealed class AddGeneratedScenario { }
public interface IAddContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value);
}
""",
                DiagnosticId: "SHARPLINK004",
                ForbiddenKey: "Signature:AddCancellationToken",
                GeneratedType: "GeneratedAddImplementation"),
            (
                Name: "KeepControlParameter",
                Source: """
using System.Threading;

public interface IContract { }
public sealed class KeepGeneratedScenario { }
public interface IKeepContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken first, int value, CancellationToken second);
}
""",
                DiagnosticId: "SHARPLINK002",
                ForbiddenKey: "Signature:Keep:CancellationToken:0",
                GeneratedType: "GeneratedKeepImplementation"),
            (
                Name: "ReorderControlParameters",
                Source: """
using System.Threading;

public interface IContract { }
public sealed class ReorderGeneratedScenario { }
public interface IReorderContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken token, int value, SharpLink.Sdk.SharpLinkCallOptions options);
}
""",
                DiagnosticId: "SHARPLINK008",
                ForbiddenKey: "Signature:ReorderControlParameters",
                GeneratedType: "GeneratedReorderImplementation"),
            (
                Name: "MakeInstance",
                Source: """
using System.Threading.Tasks;

public interface IContract { }
public sealed class MakeInstanceGeneratedScenario { }
public interface IStaticContract<TSelf> where TSelf : IStaticContract<TSelf>
{
    static abstract ValueTask<int> [|RunAsync|](int value);
}
""",
                DiagnosticId: "SHARPLINK053",
                ForbiddenKey: "Signature:MakeInstance",
                GeneratedType: "GeneratedStaticContract")
        };

        foreach (var scenario in generatedScenarios)
        {
            using var generated = CodeFixTestWorkspace.Create(("Contract.cs", scenario.Source));
            AddGeneratedFixtures(generated);
            await generated.AssertCompilesAsync();
            await AssertGeneratedTypeIsOutsideRegularDocumentsAsync(generated, scenario.GeneratedType);
            var diagnostic = await generated.CreateDiagnosticAsync(scenario.DiagnosticId, "Contract.cs");

            var actions = await generated.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.All(action => action.EquivalenceKey != scenario.ForbiddenKey),
                $"{scenario.Name} must be withheld when a related method is source-generated. Actual: {string.Join(", ", actions.Select(static action => action.EquivalenceKey))}");
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
""",
                DiagnosticId: "SHARPLINK002",
                EquivalenceKey: "Signature:Keep:CancellationToken:0"),
            (
                Name: "ReorderControlParameters",
                Source: """
using System.Threading;

public interface IReorderContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken token, int value, SharpLink.Sdk.SharpLinkCallOptions options);
}
""",
                DiagnosticId: "SHARPLINK008",
                EquivalenceKey: "Signature:ReorderControlParameters"),
            (
                Name: "MakeInstance",
                Source: """
public sealed class Contract
{
    public static int [|Run|](int value) => value;
}
""",
                DiagnosticId: "SHARPLINK053",
                EquivalenceKey: "Signature:MakeInstance")
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
    public async Task ConstructorPublicizationShouldRejectGeneratedOnlyConstructor()
    {
        using (var generated = CodeFixTestWorkspace.Create(("Service.cs", """
public interface IContract { }
public sealed class ConstructorGeneratedScenario { }

[SharpLink.Sdk.RpcService]
public partial class [|Service|] { }
""")))
        {
            AddGeneratedFixtures(generated);
            await generated.AssertCompilesAsync();
            var project = generated.Solution.GetProject(generated.ProjectId)
                          ?? throw new InvalidOperationException("Test project was unavailable.");
            var compilation = await project.GetCompilationAsync()
                              ?? throw new InvalidOperationException("Compilation was unavailable.");
            var service = compilation.GetTypeByMetadataName("Service")
                          ?? throw new InvalidOperationException("Partial service was unavailable.");
            var constructor = service.InstanceConstructors.Single(static item =>
                !item.IsImplicitlyDeclared && item.Parameters.Length == 0);
            var regularTrees = await GetRegularTreesAsync(project);
            Ensure(constructor.DeclaringSyntaxReferences.All(reference =>
                    !regularTrees.Contains(reference.SyntaxTree)),
                "The sole usable non-public constructor must be generated outside Project.Documents.");
            var diagnostic = await generated.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

            var actions = await generated.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "MakeConstructorPublic"),
                "MakeConstructorPublic must be withheld for a generated-only constructor.");
        }

        foreach (var accessibility in new[] { "private", "protected" })
        {
            using var ordinary = CodeFixTestWorkspace.Create(("Service.cs", $$"""
[SharpLink.Sdk.RpcService]
public class [|Service|]
{
    {{accessibility}} Service() { }
}
"""));
            await ordinary.AssertCompilesAsync();
            var diagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
            var actions = await ordinary.GetActionsAsync(diagnostic, "Service.cs");
            var action = actions.Single(static item => item.EquivalenceKey == "MakeConstructorPublic");

            var changed = await ordinary.ApplyAsync(action);
            var source = await ordinary.GetTextAsync("Service.cs", changed);

            EnsureContains(source, "public Service()", $"ordinary {accessibility} constructor");
            await ordinary.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task PolicyActionsShouldRejectGeneratedEquivalentDeclarationsAndAttributes()
    {
        var generatedPolicies = new[]
        {
            (
                Name: "Timeout",
                Source: """
using System.Threading.Tasks;

public interface IContract { }
public sealed class TimeoutGeneratedScenario { }
public interface ITimeoutBase
{
    ValueTask<int> [|RunAsync|]();
}
""",
                DiagnosticIds: new[] { "SHARPLINK050" },
                ForbiddenKeys: new[] { "UseDefaultTimeout", "RemoveTimeout" },
                GeneratedType: "IGeneratedTimeoutContract"),
            (
                Name: "Oneway",
                Source: """
using System.Threading.Tasks;

public interface IContract { }
public sealed class OnewayGeneratedScenario { }
public interface IOnewayBase
{
    ValueTask [|RunAsync|]();
}
""",
                DiagnosticIds: new[] { "SHARPLINK056" },
                ForbiddenKeys: new[] { "RemoveOneway" },
                GeneratedType: "IGeneratedOnewayContract"),
            (
                Name: "AddNonCancellable",
                Source: """
using System.Threading.Tasks;

public interface IContract { }
public sealed class AddNonCancellableGeneratedScenario { }
public interface IAddNonCancellableBase
{
    ValueTask<int> [|RunAsync|]();
}
""",
                DiagnosticIds: new[] { "SHARPLINK004", "SHARPLINK014" },
                ForbiddenKeys: new[] { "AddNonCancellable" },
                GeneratedType: "IGeneratedAddNonCancellableContract"),
            (
                Name: "RemoveNonCancellable",
                Source: """
using System.Threading.Tasks;

public interface IContract { }
public sealed class RemoveNonCancellableGeneratedScenario { }
public interface IRemoveNonCancellableBase
{
    ValueTask<int> [|RunAsync|]();
}
""",
                DiagnosticIds: new[] { "SHARPLINK015" },
                ForbiddenKeys: new[] { "RemoveNonCancellable" },
                GeneratedType: "IGeneratedRemoveNonCancellableContract")
        };

        foreach (var scenario in generatedPolicies)
        {
            using var generated = CodeFixTestWorkspace.Create(("Contract.cs", scenario.Source));
            AddGeneratedFixtures(generated);
            await generated.AssertCompilesAsync();
            await AssertGeneratedTypeIsOutsideRegularDocumentsAsync(generated, scenario.GeneratedType);
            foreach (var diagnosticId in scenario.DiagnosticIds)
            {
                var diagnostic = await generated.CreateDiagnosticAsync(diagnosticId, "Contract.cs");
                var actions = await generated.GetActionsAsync(diagnostic, "Contract.cs");
                Ensure(actions.All(action => !scenario.ForbiddenKeys.Contains(
                        action.EquivalenceKey, StringComparer.Ordinal)),
                    $"{scenario.Name} actions must be withheld for generated declarations/attributes. Actual: {string.Join(", ", actions.Select(static action => action.EquivalenceKey))}");
            }
        }

        await AssertOrdinaryPolicyActionAsync(
            "SHARPLINK050",
            "RemoveTimeout",
            """
using System.Threading.Tasks;

public interface IContract
{
    [SharpLink.Sdk.Timeout(-1)]
    ValueTask<int> [|RunAsync|]();
}
""");
        await AssertOrdinaryPolicyActionAsync(
            "SHARPLINK056",
            "RemoveOneway",
            """
using System.Threading.Tasks;

public interface IContract
{
    [SharpLink.Sdk.Oneway]
    ValueTask<int> [|RunAsync|]();
}
""");
        foreach (var diagnosticId in new[] { "SHARPLINK004", "SHARPLINK014" })
        {
            await AssertOrdinaryPolicyActionAsync(
                diagnosticId,
                "AddNonCancellable",
                """
using System.Threading.Tasks;

public interface IContract
{
    ValueTask<int> [|RunAsync|]();
}
""");
        }
        await AssertOrdinaryPolicyActionAsync(
            "SHARPLINK015",
            "RemoveNonCancellable",
            """
using System.Threading.Tasks;

public interface IContract
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> [|RunAsync|]();
}
""");
    }

    [Test]
    public async Task AddIServiceShouldRejectGenericContractDeclarations()
    {
        var invalidSources = new[]
        {
            """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|]<T> { }
""",
            """
public class Container<T>
{
    [SharpLink.Sdk.RpcContract]
    public interface [|IContract|] { }
}
"""
        };

        foreach (var source in invalidSources)
        {
            using var invalid = CodeFixTestWorkspace.Create(("Contract.cs", source));
            await invalid.AssertCompilesAsync();
            var diagnostic = await invalid.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");

            var actions = await invalid.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "AddIService"),
                "AddIService must be withheld for generic contracts and contracts nested in generic types.");
        }

        using var ordinary = CodeFixTestWorkspace.Create(("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] { }
"""));
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");
        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Contract.cs");
        var ordinaryAction = ordinaryActions.Single(static action => action.EquivalenceKey == "AddIService");

        var ordinaryChanged = await ordinary.ApplyAsync(ordinaryAction);

        var ordinarySource = await ordinary.GetTextAsync("Contract.cs", ordinaryChanged);
        EnsureContains(
            ordinarySource,
            "interface IContract : global::SharpLink.Sdk.IService",
            "ordinary non-generic RPC contract");
        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    [Test]
    public async Task RestoreUnionTagShouldRejectErrorObsoleteCaseType()
    {
        using (var obsolete = CodeFixTestWorkspace.Create(("Union.cs", """
using System;

[Obsolete("Removed case", true)]
public sealed class OldCase : IResult { }
public sealed class NewCase : IResult { }

[[|SharpLink.Sdk.RpcUnionCase|](9, typeof(NewCase))]
public interface IResult { }
""")))
        {
            await obsolete.AssertCompilesAsync();
            var diagnostic = await obsolete.CreateDiagnosticAsync(
                "SHARPLINK033",
                "Union.cs",
                UnionRestoreProperties());

            var actions = await obsolete.GetActionsAsync(diagnostic, "Union.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "RestoreUnionTag"),
                "RestoreUnionTag must be withheld for an error-obsolete published case type.");
        }

        using var ordinary = CodeFixTestWorkspace.Create(("Union.cs", """
public sealed class OldCase : IResult { }
public sealed class NewCase : IResult { }

[[|SharpLink.Sdk.RpcUnionCase|](9, typeof(NewCase))]
public interface IResult { }
"""));
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync(
            "SHARPLINK033",
            "Union.cs",
            UnionRestoreProperties());
        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Union.cs");
        var ordinaryAction = ordinaryActions.Single(static action =>
            action.EquivalenceKey == "RestoreUnionTag");

        var ordinaryChanged = await ordinary.ApplyAsync(ordinaryAction);

        var ordinarySource = await ordinary.GetTextAsync("Union.cs", ordinaryChanged);
        EnsureContains(
            ordinarySource,
            "RpcUnionCase(7, typeof(global::OldCase))",
            "ordinary restored union mapping");
        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    private static IReadOnlyDictionary<string, string?> UnionRestoreProperties()
        => new Dictionary<string, string?>
        {
            ["SharpLink.PreviousUnionTag"] = "7",
            ["SharpLink.PreviousUnionType"] = "OldCase"
        };

    private static async Task AssertOrdinaryPolicyActionAsync(
        string diagnosticId,
        string equivalenceKey,
        string source)
    {
        using var ordinary = CodeFixTestWorkspace.Create(("Contract.cs", source));
        await ordinary.AssertCompilesAsync();
        var diagnostic = await ordinary.CreateDiagnosticAsync(diagnosticId, "Contract.cs");
        var actions = await ordinary.GetActionsAsync(diagnostic, "Contract.cs");
        var action = actions.Single(item => item.EquivalenceKey == equivalenceKey);

        var changed = await ordinary.ApplyAsync(action);

        var changedSource = await ordinary.GetTextAsync("Contract.cs", changed);
        if (equivalenceKey == "AddNonCancellable")
        {
            EnsureContains(
                changedSource,
                "[global::SharpLink.Sdk.NonCancellable]",
                $"ordinary {diagnosticId} policy action");
        }
        else
        {
            var removedName = equivalenceKey switch
            {
                "RemoveTimeout" => "Timeout",
                "RemoveOneway" => "Oneway",
                "RemoveNonCancellable" => "NonCancellable",
                _ => throw new InvalidOperationException($"Unexpected policy action '{equivalenceKey}'.")
            };
            EnsureDoesNotContain(
                changedSource,
                removedName,
                $"ordinary {diagnosticId} policy action");
        }
        await ordinary.AssertCompilesAsync(changed);
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
        var regularTrees = await GetRegularTreesAsync(project);
        Ensure(type.DeclaringSyntaxReferences.Length != 0 &&
               type.DeclaringSyntaxReferences.All(reference => !regularTrees.Contains(reference.SyntaxTree)),
            $"'{metadataName}' must be generated outside Project.Documents.");
    }

    private static async Task<SyntaxTree?[]> GetRegularTreesAsync(Project project)
        => await Task.WhenAll(project.Documents.Select(static document => document.GetSyntaxTreeAsync()));

    private static void AddGeneratedFixtures(CodeFixTestWorkspace workspace)
    {
        var updated = workspace.Solution.AddAnalyzerReference(
            workspace.ProjectId,
            new AnalyzerFileReference(
                typeof(FifteenthGeneratedFixtureGenerator).Assembly.Location,
                CurrentAssemblyLoader.Instance));
        var solutionProperty = typeof(CodeFixTestWorkspace).GetProperty(
            nameof(CodeFixTestWorkspace.Solution),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Test workspace Solution property was unavailable.");
        solutionProperty.SetValue(workspace, updated);
    }

    private sealed class CurrentAssemblyLoader : IAnalyzerAssemblyLoader
    {
        internal static CurrentAssemblyLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath) { }

        public Assembly LoadFromPath(string fullPath)
            => typeof(FifteenthGeneratedFixtureGenerator).Assembly;
    }
}

#pragma warning disable RS1036, RS1038, RS1041, RS1042 // Test-only generator runs in the host test process.
[Generator]
public sealed class FifteenthGeneratedFixtureGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        if (HasType("AddGeneratedScenario"))
            Add("GeneratedAdd.g.cs", """
public sealed class GeneratedAddImplementation : IAddContract
{
    public System.Threading.Tasks.ValueTask<int> RunAsync(int value) =>
        System.Threading.Tasks.ValueTask.FromResult(value);
}
""");
        if (HasType("KeepGeneratedScenario"))
            Add("GeneratedKeep.g.cs", """
public sealed class GeneratedKeepImplementation : IKeepContract
{
    public int Run(System.Threading.CancellationToken first, int value,
        System.Threading.CancellationToken second) => value;
}
""");
        if (HasType("ReorderGeneratedScenario"))
            Add("GeneratedReorder.g.cs", """
public sealed class GeneratedReorderImplementation : IReorderContract
{
    public int Run(System.Threading.CancellationToken token, int value,
        SharpLink.Sdk.SharpLinkCallOptions options) => value;
}
""");
        if (HasType("MakeInstanceGeneratedScenario"))
            Add("GeneratedStatic.g.cs", """
public sealed class GeneratedStaticContract : IStaticContract<GeneratedStaticContract>
{
    public static System.Threading.Tasks.ValueTask<int> RunAsync(int value) =>
        System.Threading.Tasks.ValueTask.FromResult(value);
}
""");
        if (HasType("ConstructorGeneratedScenario"))
            Add("GeneratedConstructor.g.cs", """
public partial class Service
{
    protected Service() { }
}
""");
        if (HasType("TimeoutGeneratedScenario"))
            Add("GeneratedTimeout.g.cs", """
[SharpLink.Sdk.RpcContract]
public interface IGeneratedTimeoutContract : ITimeoutBase
{
    [SharpLink.Sdk.Timeout(1)]
    new System.Threading.Tasks.ValueTask<int> RunAsync();
}
""");
        if (HasType("OnewayGeneratedScenario"))
            Add("GeneratedOneway.g.cs", """
[SharpLink.Sdk.RpcContract]
public interface IGeneratedOnewayContract : IOnewayBase
{
    [SharpLink.Sdk.Oneway]
    new System.Threading.Tasks.ValueTask RunAsync();
}
""");
        if (HasType("AddNonCancellableGeneratedScenario"))
            Add("GeneratedAddNonCancellable.g.cs", """
[SharpLink.Sdk.RpcContract]
public interface IGeneratedAddNonCancellableContract : IAddNonCancellableBase
{
    new System.Threading.Tasks.ValueTask<int> RunAsync();
}
""");
        if (HasType("RemoveNonCancellableGeneratedScenario"))
            Add("GeneratedRemoveNonCancellable.g.cs", """
[SharpLink.Sdk.RpcContract]
public interface IGeneratedRemoveNonCancellableContract : IRemoveNonCancellableBase
{
    [SharpLink.Sdk.NonCancellable]
    new System.Threading.Tasks.ValueTask<int> RunAsync();
}
""");

        bool HasType(string metadataName)
            => context.Compilation.GetTypeByMetadataName(metadataName) is not null;

        void Add(string hintName, string source)
            => context.AddSource(hintName, source);
    }
}
#pragma warning restore RS1036, RS1038, RS1041, RS1042
