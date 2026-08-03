using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentySixthCodexReviewRegressionTests
{
    [Test]
    public async Task ConstructorPublicizationShouldRejectPrimaryConstructors()
    {
        var declarations = new[]
        {
            "internal class [|Service|](int value)",
            "internal record class [|Service|](int Value)"
        };

        foreach (var declaration in declarations)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Service.cs", $$"""
[SharpLink.Sdk.RpcService]
{{declaration}}
{
}
"""));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
            var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "MakeConstructorPublic"),
                "A primary constructor without ConstructorDeclarationSyntax must not receive a no-op publicization action.");
        }
    }

    [Test]
    public async Task ModifierRemovalShouldPreserveExteriorTrivia()
    {
        using (var method = CodeFixTestWorkspace.Create(("Contract.cs", """
public sealed class Contract
{
    public static // why this was static
    int [|Run|](int value) => value;
}
""")))
        {
            await method.AssertCompilesAsync();
            var diagnostic = await method.CreateDiagnosticAsync("SHARPLINK053", "Contract.cs");
            var action = (await method.GetActionsAsync(diagnostic, "Contract.cs"))
                .Single(static item => item.EquivalenceKey == "Signature:MakeInstance");
            var changed = await method.ApplyAsync(action);
            var source = await method.GetTextAsync("Contract.cs", changed);

            EnsureContains(source, "// why this was static", "removed static modifier trivia");
            await method.AssertCompilesAsync(changed);
        }

        using var service = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public abstract // service shape rationale
class [|Service|]
{
    public Service() { }
}
"""));
        await service.AssertCompilesAsync();
        var serviceDiagnostic = await service.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");
        var serviceAction = (await service.GetActionsAsync(serviceDiagnostic, "Service.cs"))
            .Single(static item => item.EquivalenceKey == "MakeServiceConcrete");
        var serviceChanged = await service.ApplyAsync(serviceAction);
        var serviceSource = await service.GetTextAsync("Service.cs", serviceChanged);

        EnsureContains(serviceSource, "// service shape rationale", "removed abstract modifier trivia");
        await service.AssertCompilesAsync(serviceChanged);
    }

    [Test]
    public async Task ConstructorPublicizationShouldValidateContainingTypeArguments()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
internal sealed class InternalPayload { }

public sealed class Outer<T>
{
    public sealed class Nested { }
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    private Service(Outer<InternalPayload>.Nested dependency) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.All(static action => action.EquivalenceKey != "MakeConstructorPublic"),
            "A nested parameter type must include its constructed containing type in accessibility checks.");
    }

    [Test]
    public async Task SignatureEditsShouldPreserveExistingNameColonTrivia()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](int value);
}

public static class Caller
{
    public static int Call(IContract contract) =>
        contract.Run(value /* binding note */: 42);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
        var action = (await workspace.GetActionsAsync(diagnostic, "Contract.cs"))
            .Single(static item => item.EquivalenceKey == "Signature:AddCancellationToken");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        EnsureContains(source, "value /* binding note */:", "existing named-argument trivia");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task ConstructorSelectionShouldHonorInheritedAttributeUsage()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service(int value) { }
    public Service(string value) { }
}
"""));
        workspace.AddMetadataReferenceFromSource("InheritedMarkerUsage", """
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Method)]
    public class MethodOnlyMarkerAttribute : Attribute { }

    public sealed class ActivatorUtilitiesConstructorAttribute : MethodOnlyMarkerAttribute { }
}
""");
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 0,
            "An inherited AttributeUsage restriction must block constructor-selection markers.");
    }
}
