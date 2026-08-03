using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentyNinthCodexReviewRegressionTests
{
    [Test]
    public async Task RestoreUnionTagShouldBindReorderedNamedArguments()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Union.cs", """
public sealed class OldCase : IResult { }
public sealed class NewCase : IResult { }

[[|SharpLink.Sdk.RpcUnionCase|](caseType: typeof(NewCase), tag: 2)]
public interface IResult { }
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK033",
            "Union.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousUnionTag"] = "1",
                ["SharpLink.PreviousUnionType"] = "OldCase"
            });
        var action = (await workspace.GetActionsAsync(diagnostic, "Union.cs"))
            .Single(static item => item.EquivalenceKey == "RestoreUnionTag");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Union.cs", changed);

        EnsureContains(source, "caseType: typeof(global::OldCase)", "named union case type");
        EnsureContains(source, "tag: 1", "named union tag");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task ConstructorSelectionShouldHonorNonInheritedBaseUsageTargets()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service(int value) { }
    public Service(string value) { }
}
"""));
        workspace.AddMetadataReferenceFromSource("NonInheritedMarkerUsage", """
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public class MethodOnlyMarkerAttribute : Attribute { }

    public sealed class ActivatorUtilitiesConstructorAttribute : MethodOnlyMarkerAttribute { }
}
""");
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 0,
            "Inherited=false must not discard a base attribute class's valid-target restriction.");
    }
}
