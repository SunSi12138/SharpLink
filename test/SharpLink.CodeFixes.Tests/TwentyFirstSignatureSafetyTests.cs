using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentyFirstSignatureSafetyTests
{
    [Test]
    public async Task CSharp13QueryableSyntaxShouldWithholdNamedArgumentSignatureEdits()
    {
        using (var add = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Linq;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](int value);
}

public static class Calls
{
    public static IQueryable<int> Project(IQueryable<IContract> contracts) =>
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
                "C# 13 IQueryable query syntax must suppress AddCancellationToken.");
        }

        using var reorder = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Linq;
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken token, int value, SharpLink.Sdk.SharpLinkCallOptions options);
}

public static class Calls
{
    public static IQueryable<int> Project(IQueryable<IContract> contracts) =>
        from contract in contracts
        select contract.Run(default, 42, default);
}
"""));
        SetLanguageVersion(reorder, LanguageVersion.CSharp13);
        await reorder.AssertCompilesAsync();
        var reorderDiagnostic = await reorder.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");
        var reorderActions = await reorder.GetActionsAsync(reorderDiagnostic, "Contract.cs");

        Ensure(reorderActions.Count == 0,
            "C# 13 IQueryable query syntax must suppress ReorderControlParameters.");
    }

    [Test]
    public async Task KeepParameterShouldHonorMethodAndReturnAttributeReferences()
    {
        var attributeTargets = new[]
        {
            "[ReferencesParameter(\"secondToken\")]",
            "[return: ReferencesParameter(Name = \"secondToken\")]"
        };

        foreach (var attributeTarget in attributeTargets)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", $$"""
using System;
using System.Threading;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.ReturnValue)]
public sealed class ReferencesParameterAttribute : Attribute
{
    public ReferencesParameterAttribute() { }
    public ReferencesParameterAttribute(string name) => Name = name;
    public string? Name { get; set; }
}

public interface IContract : SharpLink.Sdk.IService
{
    {{attributeTarget}}
    int [|Run|](CancellationToken firstToken, int value, CancellationToken secondToken);
}
"""));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK002", "Contract.cs");
            var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Select(static action => action.EquivalenceKey).SequenceEqual(
                    ["Signature:Keep:CancellationToken:2"],
                    StringComparer.Ordinal),
                "A signature repair must retain parameters referenced by method or return attributes.");
            var changed = await workspace.ApplyAsync(actions[0]);
            await workspace.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task AddCancellationTokenShouldPreserveValidNonCancellableBasePolicy()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Base.cs", """
public interface IBaseContract
{
    [SharpLink.Sdk.NonCancellable]
    int Run(int value);
}
"""),
            ("Derived.cs", """
[SharpLink.Sdk.RpcContract]
public interface IDerivedContract : IBaseContract, SharpLink.Sdk.IService
{
    new int [|Run|](int value);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Derived.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Derived.cs");

        Ensure(actions.Select(static action => action.EquivalenceKey).SequenceEqual(
                ["AddNonCancellable"],
                StringComparer.Ordinal),
            "A valid inherited NonCancellable policy must suppress AddCancellationToken only.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var baseSource = await workspace.GetTextAsync("Base.cs", changed);
        var derivedSource = await workspace.GetTextAsync("Derived.cs", changed);

        EnsureContains(baseSource, "NonCancellable", "valid base cancellation policy");
        EnsureContains(derivedSource, "NonCancellable", "derived cancellation repair");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task KeepParameterShouldNotDiscardEffectfulControlArguments()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken first, CancellationToken second);
}
"""),
            ("Caller.cs", """
using System.Threading;

public static class Caller
{
    public static int Call(IContract contract, CancellationToken cancellationToken) =>
        contract.Run(GetAndLogToken(), cancellationToken);

    private static CancellationToken GetAndLogToken() => default;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK002", "Contract.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static action => action.EquivalenceKey).SequenceEqual(
                ["Signature:Keep:CancellationToken:0"],
                StringComparer.Ordinal),
            "The keep choice that would discard an effectful argument must be withheld.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var caller = await workspace.GetTextAsync("Caller.cs", changed);

        EnsureContains(caller, "contract.Run(GetAndLogToken())", "retained effectful control argument");
        await workspace.AssertCompilesAsync(changed);
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
