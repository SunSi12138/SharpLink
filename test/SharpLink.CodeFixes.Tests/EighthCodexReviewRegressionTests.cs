using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class EighthCodexReviewRegressionTests
{
    [Test]
    public async Task KeepControlParameterShouldRespectParamAndParamrefDocumentation()
    {
        var scenarios = new[]
        {
            (
                DiagnosticId: "SHARPLINK002",
                DisplayName: "CancellationToken",
                Kind: "CancellationToken",
                ParameterType: "System.Threading.CancellationToken"),
            (
                DiagnosticId: "SHARPLINK007",
                DisplayName: "SharpLinkCallOptions",
                Kind: "CallOptions",
                ParameterType: "SharpLink.Sdk.SharpLinkCallOptions")
        };

        foreach (var scenario in scenarios)
        {
            var source = $$"""
public interface IContract
{
    /// <summary>Uses <paramref name="second"/> as the effective control value.</summary>
    /// <param name="second">The effective control value.</param>
    void [|Run|]({{scenario.ParameterType}} first, {{scenario.ParameterType}} second);
}
""";
            using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(scenario.DiagnosticId, "Contract.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Count == 1
                   && actions[0].Title == $"Keep {scenario.DisplayName} 'second'"
                   && actions[0].EquivalenceKey == $"Signature:Keep:{scenario.Kind}:1",
                $"Only the Keep action that preserves XML-documented parameter 'second' is safe for {scenario.DiagnosticId}. Actual: " +
                string.Join(", ", actions.Select(static action => action.Title)));
            var changed = await workspace.ApplyAsync(actions[0]);
            var changedSource = await workspace.GetTextAsync("Contract.cs", changed);
            EnsureContains(changedSource, "<paramref name=\"second\"/>", scenario.DiagnosticId + " paramref");
            EnsureContains(changedSource, "<param name=\"second\">", scenario.DiagnosticId + " param documentation");
            EnsureContains(changedSource, $"Run({scenario.ParameterType} second)", scenario.DiagnosticId + " method");
            EnsureDoesNotContain(changedSource, scenario.ParameterType + " first", scenario.DiagnosticId + " method");
            await workspace.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task Sharplink019ShouldOnlySelectConstructorThatSetsRequiredMembers()
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
    public required string Name { get; init; }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public Service() { Name = string.Empty; }

    public Service(string name) { Name = name; }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Select constructor Service()"
               && actions[0].EquivalenceKey == "SelectConstructor:Service.Service()",
            $"Only the supported public constructor marked SetsRequiredMembers may be selected. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        var constructors = CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>()
            .Where(static constructor => constructor.Identifier.ValueText == "Service")
            .ToArray();
        var parameterless = constructors.Single(static constructor =>
            constructor.ParameterList.Parameters.Count == 0);
        var withName = constructors.Single(static constructor =>
            constructor.ParameterList.Parameters.Count == 1);
        Ensure(parameterless.AttributeLists.SelectMany(static list => list.Attributes)
            .Any(static attribute => attribute.Name.ToString().Contains(
                "ActivatorUtilitiesConstructor", StringComparison.Ordinal)),
            "The SetsRequiredMembers constructor must receive ActivatorUtilitiesConstructor.");
        Ensure(withName.AttributeLists.Count == 0,
            "The unmarked constructor must remain unselected when the service has required members.");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink018ShouldRecognizeInheritedConcreteOverride()
    {
        using (var safe = CodeFixTestWorkspace.Create(("Service.cs", """
public abstract class ServiceBase
{
    public abstract int Value { get; }
}

public class IntermediateService : ServiceBase
{
    public override int Value => 42;
}

[SharpLink.Sdk.RpcService]
public abstract class [|Service|] : IntermediateService
{
}
""")))
        {
            await safe.AssertCompilesAsync();
            var diagnostic = await safe.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");
            var actions = await safe.GetActionsAsync(diagnostic, "Service.cs");
            Ensure(actions.Count == 1
                   && actions[0].Title == "Make RPC service concrete"
                   && actions[0].EquivalenceKey == "MakeServiceConcrete",
                "A concrete override inherited through an intermediate base makes the service safely concrete.");
            var changed = await safe.ApplyAsync(actions[0]);
            var source = await safe.GetTextAsync("Service.cs", changed);
            EnsureContains(source, "public class Service : IntermediateService", "concrete RPC service");
            EnsureDoesNotContain(source, "abstract class Service : IntermediateService", "concrete RPC service");
            await safe.AssertCompilesAsync(changed);
        }

        using var unsafeService = CodeFixTestWorkspace.Create(("Service.cs", """
public abstract class ServiceBase
{
    public abstract int Value { get; }
}

[SharpLink.Sdk.RpcService]
public abstract class [|Service|] : ServiceBase
{
}
"""));
        await unsafeService.AssertCompilesAsync();
        var unsafeDiagnostic = await unsafeService.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");
        var unsafeActions = await unsafeService.GetActionsAsync(unsafeDiagnostic, "Service.cs");
        Ensure(unsafeActions.Count == 0,
            "A genuinely unimplemented abstract base member must keep MakeServiceConcrete hidden.");
    }
}
