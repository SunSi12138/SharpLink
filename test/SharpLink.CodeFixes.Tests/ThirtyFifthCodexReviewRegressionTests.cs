using SharpLink.Generator;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ThirtyFifthCodexReviewRegressionTests
{
    [Test]
    public async Task RemoveNonCancellableShouldResolveConstructedInheritedMethod()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IBase<T>
{
    [NonCancellable]
    ValueTask<int> Run(T token);
}

[RpcContract]
public interface IContract : IService, IBase<CancellationToken> { }
"""));
        await workspace.AssertCompilesAsync();
        var compilation = await workspace.Solution.GetProject(workspace.ProjectId)!.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RpcGenerator());
        var diagnostic = driver.RunGenerators(compilation).GetRunResult().Diagnostics
            .Single(static item => item.Id == "SHARPLINK015");
        var action = (await workspace.GetActionsAsync(diagnostic, "Contract.cs"))
            .Single(static item => item.EquivalenceKey == "RemoveNonCancellable");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        EnsureDoesNotContain(source, "NonCancellable", "constructed inherited method policy");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RestoreUnionTagShouldPreserveReassignedCase()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Union.cs", """
public sealed class OldCase : IResult { }
public sealed class NewCase : IResult { }

[[|SharpLink.Sdk.RpcUnionCase|](1, typeof(NewCase))]
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

        EnsureContains(source, "RpcUnionCase(1, typeof(global::OldCase))", "restored union case");
        EnsureContains(source, "RpcUnionCase(2, typeof(NewCase))", "preserved reassigned union case");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RestoreEnumTypeShouldRejectInvalidatedExternalConversion()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Status.cs", """
public enum [|Status|] : int
{
    Ready = 1
}

public sealed class Holder
{
    public Status Value { get; } = (Status)300;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK032",
            "Status.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousEnumUnderlyingType"] = "System.Byte"
            });

        var actions = await workspace.GetActionsAsync(diagnostic, "Status.cs");

        Ensure(actions.All(static item => item.EquivalenceKey != "RestoreEnumType"),
            "Enum restoration must be withheld when it invalidates a source conversion outside the declaration.");
    }
}
