using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class SixthCodexReviewRegressionTests
{
    [Test]
    public async Task Sharplink050ShouldSynchronizeEquivalentSourceInterfacePolicies()
    {
        foreach (var equivalenceKey in new[] { "UseDefaultTimeout", "RemoveTimeout" })
        {
            using var workspace = CreateTimeoutWorkspace();
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK050", "Derived.cs");
            var actions = await workspace.GetActionsAsync(diagnostic, "Derived.cs");
            Ensure(actions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                    [
                        ("Use generated default timeout", "UseDefaultTimeout"),
                        ("Remove [Timeout]", "RemoveTimeout")
                    ]),
                "Equivalent invalid Timeout policies must retain the two deterministic synchronized repairs.");

            var changed = await workspace.ApplyAsync(
                actions.Single(action => action.EquivalenceKey == equivalenceKey));

            foreach (var documentName in new[] { "Base.cs", "Derived.cs" })
            {
                var source = await workspace.GetTextAsync(documentName, changed);
                if (equivalenceKey == "UseDefaultTimeout")
                {
                    EnsureContains(source, "[SharpLink.Sdk.Timeout]", documentName + " default Timeout policy");
                    EnsureDoesNotContain(source, "Timeout(0)", documentName + " default Timeout policy");
                }
                else
                {
                    EnsureDoesNotContain(source, "Timeout", documentName + " removed Timeout policy");
                }
            }
            await workspace.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task Sharplink037ShouldRequireSetsRequiredMembersForServiceCandidate()
    {
        var unsafeScenarios = new[]
        {
            (Name: "declared required member", Declarations: """
public sealed class Service : IContract
{
    public required string Name { get; init; }
    public Service() { }
}
"""),
            (Name: "inherited required member", Declarations: """
public class ServiceBase
{
    public required string Name { get; init; }
}

public sealed class Service : ServiceBase, IContract
{
    public Service() { }
}
""")
        };

        foreach (var scenario in unsafeScenarios)
        {
            using var workspace = CreateRouteWorkspace(scenario.Declarations);
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Count == 0,
                $"A service candidate with a {scenario.Name} needs SetsRequiredMembers on its selected constructor.");
        }

        using var marked = CreateRouteWorkspace("""
public sealed class Service : IContract
{
    public required string Name { get; init; }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public Service() { Name = string.Empty; }
}
""");
        await marked.AssertCompilesAsync();
        var markedDiagnostic = await marked.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");
        var markedActions = await marked.GetActionsAsync(markedDiagnostic, "Contract.cs");
        Ensure(markedActions.Count == 1
               && markedActions[0].Title == "Add [RpcService] to Service"
               && markedActions[0].EquivalenceKey == "RestoreServiceRoute",
            "SetsRequiredMembers makes the sole service candidate safely restorable.");
        var markedChanged = await marked.ApplyAsync(markedActions[0]);
        var markedSource = await marked.GetTextAsync("Service.cs", markedChanged);
        EnsureContains(markedSource, "[global::SharpLink.Sdk.RpcService]", "restored service route");
        await marked.AssertCompilesAsync(markedChanged);
    }

    [Test]
    public async Task Sharplink032ShouldRejectIncompatibleConstantExpressionType()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Status.cs", """
public enum [|Status|] : long
{
    Negative = -1L
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK032",
            "Status.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousEnumUnderlyingType"] = "System.Int32"
            });

        var actions = await workspace.GetActionsAsync(diagnostic, "Status.cs");

        Ensure(actions.Count == 0,
            "A long-typed initializer is not implicitly convertible to int even when its numeric value is in range.");
    }

    [Test]
    public async Task Sharplink016ShouldConsiderOtherRpcServiceImplementationsBeforeAnnotatingContract()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contracts.cs", """
public interface ITargetContract : SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcContract]
public interface IExistingContract : SharpLink.Sdk.IService { }
"""),
            ("Services.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|TargetService|] : ITargetContract { }

[SharpLink.Sdk.RpcService]
public sealed class ExistingService : ITargetContract, IExistingContract { }
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK016", "Services.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Services.cs");

        Ensure(actions.Count == 0,
            "Annotating ITargetContract would make ExistingService implement two annotated RPC contracts.");
    }

    [Test]
    public async Task SignatureQualifiedCrefShouldSuppressSignatureEditButSimpleCrefShouldNot()
    {
        using (var qualified = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    /// <summary>See <see cref="RunAsync(int)"/>.</summary>
    ValueTask<int> [|RunAsync|](int value);
}
""")))
        {
            await qualified.AssertCompilesAsync();
            var diagnostic = await qualified.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
            var actions = await qualified.GetActionsAsync(diagnostic, "Contract.cs");
            Ensure(actions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                    [("Annotate with [NonCancellable]", "AddNonCancellable")]),
                $"A signature-qualified cref must conservatively suppress the signature edit. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
            var changed = await qualified.ApplyAsync(actions[0]);
            await qualified.AssertCompilesAsync(changed);
        }

        using var simple = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    /// <summary>See <see cref="RunAsync"/>.</summary>
    ValueTask<int> [|RunAsync|](int value);
}
"""));
        await simple.AssertCompilesAsync();
        var simpleDiagnostic = await simple.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
        var simpleActions = await simple.GetActionsAsync(simpleDiagnostic, "Contract.cs");
        Ensure(simpleActions.Select(static action => action.EquivalenceKey).SequenceEqual(
                ["Signature:AddCancellationToken", "AddNonCancellable"],
                StringComparer.Ordinal),
            $"An unqualified cref must not suppress a safe signature edit. Actual: {string.Join(", ", simpleActions.Select(static action => action.Title))}");
        var simpleChanged = await simple.ApplyAsync(simpleActions[0]);
        var simpleSource = await simple.GetTextAsync("Contract.cs", simpleChanged);
        EnsureContains(simpleSource, "cref=\"RunAsync\"", "unqualified cref");
        await simple.AssertCompilesAsync(simpleChanged);
    }

    [Test]
    public async Task FileLocalPublicizationShouldWithholdForSameNamespaceTypeCollision()
    {
        using (var contract = CodeFixTestWorkspace.Create(
                   ("Contract.File.cs", """
namespace Contracts;

[SharpLink.Sdk.RpcContract]
file interface [|IContract|] : SharpLink.Sdk.IService { }
"""),
                   ("Contract.Other.cs", """
namespace Contracts;

internal interface IContract { }
""")))
        {
            await contract.AssertCompilesAsync();
            var diagnostic = await contract.CreateDiagnosticAsync("SHARPLINK055", "Contract.File.cs");
            var actions = await contract.GetActionsAsync(diagnostic, "Contract.File.cs");
            Ensure(actions.Count == 0,
                "Removing file-local from IContract would collide with the namespace's other IContract.");
        }

        using var service = CodeFixTestWorkspace.Create(
            ("Service.File.cs", """
namespace Services;

[SharpLink.Sdk.RpcService]
file sealed class [|Service|] { }
"""),
            ("Service.Other.cs", """
namespace Services;

internal class Service { }
"""));
        await service.AssertCompilesAsync();
        var serviceDiagnostic = await service.CreateDiagnosticAsync("SHARPLINK018", "Service.File.cs");
        var serviceActions = await service.GetActionsAsync(serviceDiagnostic, "Service.File.cs");
        Ensure(serviceActions.Count == 0,
            "Removing file-local from Service would collide with the namespace's other Service.");
    }

    private static CodeFixTestWorkspace CreateTimeoutWorkspace()
        => CodeFixTestWorkspace.Create(
            ("Base.cs", """
using System.Threading.Tasks;

public interface IBaseContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout(0)]
    ValueTask<int> RunAsync(int value);
}
"""),
            ("Derived.cs", """
using System.Threading.Tasks;

public interface IDerivedContract : IBaseContract
{
    [SharpLink.Sdk.Timeout(0)]
    new ValueTask<int> [|RunAsync|](int value);
}
"""));

    private static CodeFixTestWorkspace CreateRouteWorkspace(string serviceDeclarations)
        => CodeFixTestWorkspace.Create(
            ("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }
"""),
            ("Service.cs", serviceDeclarations));
}
