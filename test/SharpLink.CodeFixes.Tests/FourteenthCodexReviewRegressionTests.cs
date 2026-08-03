using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class FourteenthCodexReviewRegressionTests
{
    [Test]
    public async Task RestoreServiceRouteShouldRejectGeneratedServiceOutsideRegularDocuments()
    {
        using (var generated = CodeFixTestWorkspace.Create(("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }
""")))
        {
            AddGeneratedService(generated);
            await generated.AssertCompilesAsync();
            var project = generated.Solution.GetProject(generated.ProjectId)
                          ?? throw new InvalidOperationException("Test project was unavailable.");
            var compilation = await project.GetCompilationAsync()
                              ?? throw new InvalidOperationException("Compilation was unavailable.");
            var service = compilation.GetTypeByMetadataName("GeneratedApi.GeneratedService")
                          ?? throw new InvalidOperationException("Generated service was unavailable.");
            var generatedTree = service.DeclaringSyntaxReferences.Single().SyntaxTree;
            var regularTrees = await Task.WhenAll(project.Documents.Select(static document =>
                document.GetSyntaxTreeAsync()));
            Ensure(!regularTrees.Contains(generatedTree),
                "The candidate service must come from a generated tree outside Project.Documents.");
            var diagnostic = await generated.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");

            var actions = await generated.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Count == 0,
                "RestoreServiceRoute must be withheld when its sole service candidate is source-generated.");
        }

        using var ordinary = CodeFixTestWorkspace.Create(("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }

public sealed class Service : IContract
{
    public Service() { }
}
"""));
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");

        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Contract.cs");

        Ensure(ordinaryActions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Add [RpcService] to Service", "RestoreServiceRoute")]),
            "An ordinary source service candidate must retain RestoreServiceRoute.");
        var ordinaryChanged = await ordinary.ApplyAsync(ordinaryActions[0]);
        var ordinarySource = await ordinary.GetTextAsync("Contract.cs", ordinaryChanged);
        EnsureContains(ordinarySource, "[global::SharpLink.Sdk.RpcService]", "ordinary source service");
        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    [Test]
    public async Task RestoreServiceRouteShouldRejectMixedGeneratedPartialService()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }
"""),
            ("Service.cs", """
public partial class Service
{
    public Service() { }
}

internal sealed class FourteenthMixedServiceScenario { }
"""));
        AddGeneratedService(workspace);
        await workspace.AssertCompilesAsync();
        var project = workspace.Solution.GetProject(workspace.ProjectId)
                      ?? throw new InvalidOperationException("Test project was unavailable.");
        var compilation = await project.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        var service = compilation.GetTypeByMetadataName("Service")
                      ?? throw new InvalidOperationException("Mixed partial service was unavailable.");
        var regularTrees = await Task.WhenAll(project.Documents.Select(static document =>
            document.GetSyntaxTreeAsync()));
        Ensure(service.DeclaringSyntaxReferences.Any(reference => regularTrees.Contains(reference.SyntaxTree)) &&
               service.DeclaringSyntaxReferences.Any(reference => !regularTrees.Contains(reference.SyntaxTree)),
            "The fixture must combine regular and generated service declarations.");
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.All(static action => action.EquivalenceKey != "RestoreServiceRoute"),
            "RestoreServiceRoute must be withheld when a generated partial declaration supplies the contract implementation.");
    }

    [Test]
    public async Task MakeServiceConcreteShouldRequireUsablePublicActivationConstructor()
    {
        using (var unavailable = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public abstract class [|Service|]
{
    private Service() { }
}
""")))
        {
            await unavailable.AssertCompilesAsync();
            var diagnostic = await unavailable.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

            var actions = await unavailable.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.Count == 0,
                "Making an abstract service concrete must be withheld without a usable public activation constructor.");
        }

        using var valid = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public abstract class [|Service|]
{
    public Service() { }
    public int Value => 42;
}
"""));
        await valid.AssertCompilesAsync();
        var validDiagnostic = await valid.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

        var validActions = await valid.GetActionsAsync(validDiagnostic, "Service.cs");

        Ensure(validActions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Make RPC service concrete", "MakeServiceConcrete")]),
            "An abstract service with a valid public activation constructor must remain repairable.");
        var validChanged = await valid.ApplyAsync(validActions[0]);
        var validSource = await valid.GetTextAsync("Service.cs", validChanged);
        EnsureContains(validSource, "public class Service", "concrete RPC service");
        EnsureDoesNotContain(validSource, "abstract class Service", "concrete RPC service");
        await valid.AssertCompilesAsync(validChanged);
    }

    [Test]
    public async Task MissingContractFixShouldRequireSupportedRpcMethodShapes()
    {
        var invalidScenarios = new[]
        {
            (
                Name: "by-ref parameter",
                ContractMethod: "System.Threading.Tasks.Task<int> Run(ref int value);",
                ImplementationMethod:
                    "public System.Threading.Tasks.Task<int> Run(ref int value) => System.Threading.Tasks.Task.FromResult(value);"),
            (
                Name: "unsupported return",
                ContractMethod: "int Run(int value);",
                ImplementationMethod: "public int Run(int value) => value;")
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
}
"""));
            await invalid.AssertCompilesAsync();
            var diagnostic = await invalid.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");

            var actions = await invalid.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.Count == 0,
                $"AnnotateRpcContract must be withheld for a contract with a {scenario.Name}.");
        }

        using var valid = CodeFixTestWorkspace.Create(("Service.cs", """
using System.Collections.Generic;
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    Task Ping();
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> Echo(int value);
    [SharpLink.Sdk.NonCancellable]
    IAsyncEnumerable<int> Stream();
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract
{
    public Service() { }
    public Task Ping() => Task.CompletedTask;
    public ValueTask<int> Echo(int value) => ValueTask.FromResult(value);
    public IAsyncEnumerable<int> Stream() => StreamCore();

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

        Ensure(validActions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Annotate IContract with [RpcContract]", "AnnotateRpcContract")]),
            "A contract using supported Task, ValueTask, and IAsyncEnumerable returns must remain repairable.");
        var validChanged = await valid.ApplyAsync(validActions[0]);
        var validSource = await valid.GetTextAsync("Service.cs", validChanged);
        EnsureContains(validSource, "[global::SharpLink.Sdk.RpcContract]", "valid RPC contract");
        await valid.AssertCompilesAsync(validChanged);
    }

    [Test]
    public async Task ConstructorSelectionShouldAtomicallyReplaceMultipleActivationMarkers()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public Service() { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public Service(string name) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [
                    ("Select constructor Service()", "SelectConstructor:Service.Service()"),
                    ("Select constructor Service(string)", "SelectConstructor:Service.Service(string)")
                ]),
            $"Each supported constructor must remain an atomic selection choice. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
        foreach (var action in actions)
        {
            var changed = await GetChangedSolutionAsync(action);
            var source = await workspace.GetTextAsync("Service.cs", changed);
            var constructors = CSharpSyntaxTree.ParseText(source)
                .GetRoot()
                .DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>()
                .ToArray();
            var marked = constructors.Where(static constructor => constructor.AttributeLists
                .SelectMany(static list => list.Attributes)
                .Any(static attribute => attribute.Name.ToString().Contains(
                    "ActivatorUtilitiesConstructor", StringComparison.Ordinal))).ToArray();
            Ensure(marked.Length == 1,
                $"Selecting a constructor must atomically leave one activation marker. Actual source: {source}");
            var expectedParameterCount = action.Title == "Select constructor Service()" ? 0 : 1;
            Ensure(marked[0].ParameterList.Parameters.Count == expectedParameterCount,
                $"The selected constructor must own the sole remaining marker. Action: {action.Title}");
            await workspace.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task AddCancellationTokenShouldAvoidIdentifiersAcrossRelatedDeclarations()
    {
        using (var collisions = CodeFixTestWorkspace.Create(
                   ("Contract.cs", """
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value)
    {
        var cancellationToken = value;
        return ValueTask.FromResult(cancellationToken);
    }
}
"""),
                   ("Implementations.cs", """
using System.Threading.Tasks;

public sealed class PatternContract : IContract
{
    public ValueTask<int> RunAsync(int value)
    {
        object boxed = value;
        return boxed is int cancellationToken
            ? ValueTask.FromResult(cancellationToken)
            : ValueTask.FromResult(0);
    }
}

public sealed class LocalFunctionContract : IContract
{
    public ValueTask<int> RunAsync(int value)
    {
        int cancellationToken() => value;
        return ValueTask.FromResult(cancellationToken());
    }
}
"""),
                   ("Caller.cs", """
using System.Threading.Tasks;

public static class Caller
{
    public static ValueTask<int> ViaInterface(IContract contract) => contract.RunAsync(40);
    public static ValueTask<int> ViaPattern(PatternContract contract) => contract.RunAsync(41);
    public static ValueTask<int> ViaLocalFunction(LocalFunctionContract contract) => contract.RunAsync(42);
}
""")))
        {
            await collisions.AssertCompilesAsync();
            var diagnostic = await collisions.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
            var actions = await collisions.GetActionsAsync(diagnostic, "Contract.cs");
            var action = actions.Single(static item =>
                item.EquivalenceKey == "Signature:AddCancellationToken");

            var changed = await collisions.ApplyAsync(action);

            var declarations = changed.Projects
                .SelectMany(static project => project.Documents)
                .Select(document => document.GetSyntaxRootAsync())
                .ToArray();
            var roots = await Task.WhenAll(declarations);
            var methods = roots.Where(static root => root is not null)
                .SelectMany(static root => root!.DescendantNodes().OfType<MethodDeclarationSyntax>())
                .Where(static method => method.Identifier.ValueText == "RunAsync")
                .ToArray();
            Ensure(methods.Length == 3 && methods.All(static method =>
                    method.ParameterList.Parameters.Last().Identifier.ValueText == "cancellationToken1"),
                "Every related declaration must use collision-free parameter name cancellationToken1.");
            var callers = await collisions.GetTextAsync("Caller.cs", changed);
            Ensure(CountOccurrences(
                    callers,
                    "cancellationToken1: global::System.Threading.CancellationToken.None") == 3,
                $"Every related invocation must use cancellationToken1. Actual: {callers}");
            await collisions.AssertCompilesAsync(changed);
        }

        using var ordinary = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value);
}
"""),
            ("Implementation.cs", """
using System.Threading.Tasks;

public sealed class Contract : IContract
{
    public ValueTask<int> RunAsync(int value) => ValueTask.FromResult(value);
}
"""),
            ("Caller.cs", """
using System.Threading.Tasks;

public static class Caller
{
    public static ValueTask<int> Call(IContract contract) => contract.RunAsync(42);
}
"""));
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Contract.cs");
        var ordinaryAction = ordinaryActions.Single(static item =>
            item.EquivalenceKey == "Signature:AddCancellationToken");

        var ordinaryChanged = await ordinary.ApplyAsync(ordinaryAction);

        var ordinaryContract = await ordinary.GetTextAsync("Contract.cs", ordinaryChanged);
        EnsureContains(
            ordinaryContract,
            "CancellationToken cancellationToken",
            "non-conflicting cancellation parameter");
        EnsureDoesNotContain(
            ordinaryContract,
            "CancellationToken cancellationToken1",
            "non-conflicting cancellation parameter");
        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    private static int CountOccurrences(string value, string fragment)
        => value.Split(fragment, StringSplitOptions.None).Length - 1;

    private static void AddGeneratedService(CodeFixTestWorkspace workspace)
    {
        var updated = workspace.Solution.AddAnalyzerReference(
            workspace.ProjectId,
            new AnalyzerFileReference(
                typeof(FourteenthGeneratedServiceGenerator).Assembly.Location,
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
            => typeof(FourteenthGeneratedServiceGenerator).Assembly;
    }
}

#pragma warning disable RS1036, RS1038, RS1041, RS1042 // Test-only generator runs in the host test process.
[Generator]
public sealed class FourteenthGeneratedServiceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        if (context.Compilation.GetTypeByMetadataName("FourteenthMixedServiceScenario") is not null)
        {
            context.AddSource("MixedService.g.cs", """
public partial class Service : IContract { }
""");
            return;
        }

        context.AddSource("GeneratedService.g.cs", """
namespace GeneratedApi;

public sealed class GeneratedService : IContract
{
    public GeneratedService() { }
}
""");
    }
}
#pragma warning restore RS1036, RS1038, RS1041, RS1042
