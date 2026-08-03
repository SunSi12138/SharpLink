using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentyFourthCodexReviewRegressionTests
{
    [Test]
    public async Task PublicizationShouldPreserveAccessibilityModifierTrivia()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
[SharpLink.Sdk.RpcContract]
internal // publicization rationale
interface [|IContract|] : SharpLink.Sdk.IService { }
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK055", "Contract.cs");
        var action = (await workspace.GetActionsAsync(diagnostic, "Contract.cs"))
            .Single(static item => item.EquivalenceKey == "MakeContractPublic");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        EnsureContains(source, "public // publicization rationale", "accessibility replacement trivia");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task CSharp13CustomExpressionQueryShouldWithholdNamedArgumentEdits()
    {
        using (var add = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Linq.Expressions;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](int value);
}

public sealed class CustomQuery<T>
{
    public CustomQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> selector) => new();
}

public static class Calls
{
    public static CustomQuery<int> Project(CustomQuery<IContract> contracts) =>
        from contract in contracts
        select contract.Run(42);
}
""")))
        {
            SetLanguageVersion(add, LanguageVersion.CSharp13);
            await add.AssertCompilesAsync();
            var diagnostic = await add.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
            var actions = await add.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Select(static action => action.EquivalenceKey).SequenceEqual(
                    ["AddNonCancellable"],
                    StringComparer.Ordinal),
                "Custom expression-tree query translation must suppress AddCancellationToken.");
        }

        using var reorder = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Linq.Expressions;
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken token, int value, SharpLink.Sdk.SharpLinkCallOptions options);
}

public sealed class CustomQuery<T>
{
    public CustomQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> selector) => new();
}

public static class Calls
{
    public static CustomQuery<int> Project(CustomQuery<IContract> contracts) =>
        from contract in contracts
        select contract.Run(default, 42, default);
}
"""));
        SetLanguageVersion(reorder, LanguageVersion.CSharp13);
        await reorder.AssertCompilesAsync();
        var reorderDiagnostic = await reorder.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");
        var reorderActions = await reorder.GetActionsAsync(reorderDiagnostic, "Contract.cs");

        Ensure(reorderActions.Count == 0,
            "Custom expression-tree query translation must suppress ReorderControlParameters.");
    }

    [Test]
    public async Task DtoPublicizationShouldRequireACompletePostEditGeneratorModel()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", """
public static class Container
{
    [SharpLink.Sdk.RpcSerializable]
    private class [|Payload|]
    {
        public int Value { get; set; }
    }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK009",
            "Payload.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "MakeDtoAccessible" });
        var actions = await workspace.GetActionsAsync(diagnostic, "Payload.cs");

        Ensure(actions.All(static action => action.EquivalenceKey != "MakeDtoAccessible"),
            "DTO publicization must be withheld when the publicized class would remain unsealed.");
    }

    [Test]
    public async Task SealDtoShouldRejectErrorObsoleteAccessorsUsedByGeneratedCode()
    {
        var accessors = new[]
        {
            "[Obsolete(\"Removed getter\", true)] get; set;",
            "get; [Obsolete(\"Removed setter\", true)] set;"
        };

        foreach (var accessor in accessors)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", $$"""
using System;

[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]
{
    public int Value { {{accessor}} }
}
"""));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(
                "SHARPLINK009",
                "Payload.cs",
                new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });
            var actions = await workspace.GetActionsAsync(diagnostic, "Payload.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "SealDto"),
                "SealDto must reject every error-obsolete accessor the generated Codec would invoke.");
        }
    }

    private static void SetLanguageVersion(
        CodeFixTestWorkspace workspace,
        LanguageVersion languageVersion)
    {
        var solutionProperty = typeof(CodeFixTestWorkspace).GetProperty(
            nameof(CodeFixTestWorkspace.Solution),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Test workspace Solution property was unavailable.");
        solutionProperty.SetValue(
            workspace,
            workspace.Solution.WithProjectParseOptions(
                workspace.ProjectId,
                new CSharpParseOptions(languageVersion)));
    }
}
