using Microsoft.CodeAnalysis.CSharp.Syntax;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class EighteenthCodexReviewRegressionTests
{
    [Test]
    public async Task ConstructorRepairsShouldRejectErrorObsoleteServiceTypes()
    {
        var scenarios = new[]
        {
            (
                Name: "ambiguous public constructors",
                Source: """
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}

[Obsolete("Removed service", true)]
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service(int value) { }
    public Service(string value) { }
}
"""),
            (
                Name: "single non-public constructor",
                Source: """
using System;

[Obsolete("Removed service", true)]
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    private Service(int value) { }
}
""")
        };

        foreach (var scenario in scenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Service.cs", scenario.Source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.Count == 0,
                $"Constructor repairs must be withheld for an error-obsolete service with {scenario.Name}.");
        }
    }

    [Test]
    public async Task ConstructorSelectionShouldReplaceSoleInvalidMarker()
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
    public Service(ref int value) { }

    public Service(string value) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Select constructor Service(string)", "SelectConstructor:Service.Service(string)")]),
            $"The sole invalid marker must be movable to the supported constructor. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        var constructors = CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<ConstructorDeclarationSyntax>().ToArray();
        var invalid = constructors.Single(static constructor =>
            constructor.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.RefKeyword));
        var valid = constructors.Single(static constructor =>
            constructor.ParameterList.Parameters[0].Type?.ToString() == "string");

        Ensure(!HasSelectionMarker(invalid),
            "The unsupported ref constructor must lose its selection marker.");
        Ensure(HasSelectionMarker(valid),
            "The supported constructor must receive the sole selection marker.");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RemoveRpcRequiredShouldFindPartialPropertyImplementationAttribute()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Payload.Definition.cs", """
public sealed partial class Payload
{
    public partial int [|Value|] { get; set; }
}
"""),
            ("Payload.Implementation.cs", """
public sealed partial class Payload
{
    private int _value;

    [SharpLink.Sdk.RpcRequired]
    public partial int Value
    {
        get => _value;
        set => _value = value;
    }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK031",
            "Payload.Definition.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "RemoveRpcRequired" });
        var actions = await workspace.GetActionsAsync(diagnostic, "Payload.Definition.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "RemoveRpcRequired");

        var changed = await workspace.ApplyAsync(action);
        var definition = await workspace.GetTextAsync("Payload.Definition.cs", changed);
        var implementation = await workspace.GetTextAsync("Payload.Implementation.cs", changed);

        EnsureDoesNotContain(definition, "RpcRequired", "partial property definition");
        EnsureDoesNotContain(implementation, "RpcRequired", "partial property implementation");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task OrderedServiceFixesShouldPreflightLaterGenerationStages()
    {
        var missingContractScenarios = new[]
        {
            (
                Name: "abstract service",
                Declaration: "public abstract class [|Service|] : IContract { public Service() { } }",
                Attribute: "[SharpLink.Sdk.RpcService]"),
            (
                Name: "invalid lifetime",
                Declaration: "public sealed class [|Service|] : IContract { public Service() { } }",
                Attribute: "[SharpLink.Sdk.RpcService(Lifetime = (SharpLink.Sdk.SharpLinkServiceLifetime)99)]"),
            (
                Name: "invalid activation",
                Declaration: "public class [|Service|] : IContract { protected Service() { } }",
                Attribute: "[SharpLink.Sdk.RpcService]")
        };

        foreach (var scenario in missingContractScenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Service.cs", $$"""
public interface IContract : SharpLink.Sdk.IService { }

{{scenario.Attribute}}
{{scenario.Declaration}}
"""));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "AnnotateRpcContract"),
                $"AnnotateRpcContract must be withheld for a {scenario.Name} because it would reveal the next service diagnostic.");
        }

        using (var invalidLifetime = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcContract]
public interface IContract : SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcService(Lifetime = (SharpLink.Sdk.SharpLinkServiceLifetime)99)]
internal sealed class [|Service|] : IContract
{
    public Service() { }
}
""")))
        {
            await invalidLifetime.AssertCompilesAsync();
            var diagnostic = await invalidLifetime.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

            var actions = await invalidLifetime.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "MakeServicePublic"),
                "MakeServicePublic must be withheld when it would reveal an invalid lifetime diagnostic.");
        }

        using var invalidActivation = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcContract]
public interface IContract : SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcService(Lifetime = (SharpLink.Sdk.SharpLinkServiceLifetime)99)]
public class [|Service|] : IContract
{
    protected Service() { }
}
"""));
        await invalidActivation.AssertCompilesAsync();
        var invalidActivationDiagnostic = await invalidActivation.CreateDiagnosticAsync(
            "SHARPLINK020", "Service.cs");

        var invalidActivationActions = await invalidActivation.GetActionsAsync(
            invalidActivationDiagnostic, "Service.cs");

        Ensure(invalidActivationActions.All(static action =>
                action.EquivalenceKey?.StartsWith("SetLifetime:", StringComparison.Ordinal) != true),
            "Lifetime fixes must be withheld when they would reveal an invalid activation diagnostic.");
    }

    [Test]
    public async Task AddIServiceShouldPreflightTheResultingContractShape()
    {
        var invalidSources = new[]
        {
            """
[SharpLink.Sdk.RpcContract]
internal interface [|IContract|] { }
""",
            """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|]
{
    int Run();
}
"""
        };

        foreach (var source in invalidSources)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "AddIService"),
                "AddIService must be withheld when inheritance would activate another blocking contract diagnostic.");
        }
    }

    private static bool HasSelectionMarker(ConstructorDeclarationSyntax constructor)
        => constructor.AttributeLists.SelectMany(static list => list.Attributes)
            .Any(static attribute => attribute.Name.ToString()
                .Contains("ActivatorUtilitiesConstructor", StringComparison.Ordinal));
}
