using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ThirtyNinthCodexReviewRegressionTests
{
    [Test]
    public async Task RestoreUnionTagShouldAvoidEveryPublishedTag()
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
                ["SharpLink.PreviousUnionType"] = "OldCase",
                ["SharpLink.PublishedUnionTags"] = "1,2"
            });
        var action = (await workspace.GetActionsAsync(diagnostic, "Union.cs"))
            .Single(static item => item.EquivalenceKey == "RestoreUnionTag");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Union.cs", changed);

        EnsureContains(source, "RpcUnionCase(1, typeof(global::OldCase))", "restored union case");
        EnsureContains(source, "RpcUnionCase(3, typeof(NewCase))", "new tag after published range");
        EnsureDoesNotContain(source, "RpcUnionCase(2, typeof(NewCase))", "published tag reuse");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RestoreUnionTagShouldRequireCompletePublishedTagEvidence()
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

        var actions = await workspace.GetActionsAsync(diagnostic, "Union.cs");

        Ensure(actions.All(static item => item.EquivalenceKey != "RestoreUnionTag"),
            "A reassigned case needs the complete published tag set before allocating a replacement.");
    }

    [Test]
    public async Task AdapterShapeShouldBindTheAdapterConstructorParameter()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Target), typeof(Adapter))]

public sealed class Target { }

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    private Adapter() { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");
        var action = (await workspace.GetActionsAsync(diagnostic, "Adapter.cs"))
            .Single(static item => item.EquivalenceKey == "FixAdapterShape");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Adapter.cs", changed);

        EnsureContains(source, "public sealed class Target", "unchanged target type");
        EnsureContains(source, "public sealed class Adapter", "adapter accessibility and sealing");
        EnsureContains(source, "public Adapter()", "adapter constructor accessibility");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RemoveNonCancellableShouldRejectConflictingClosedConstruction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IBase<T>
{
    [NonCancellable]
    ValueTask<int> Run(T value);
}

[RpcContract]
public interface ICancellableContract : IService, IBase<CancellationToken> { }

[RpcContract]
public interface INonCancellableContract : IService, IBase<string> { }
"""));
        await workspace.AssertCompilesAsync();
        var compilation = await workspace.Solution.GetProject(workspace.ProjectId)!.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new SharpLink.Generator.RpcGenerator());
        var diagnostic = driver.RunGenerators(compilation).GetRunResult().Diagnostics
            .Single(static item => item.Id == "SHARPLINK015");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.All(static item => item.EquivalenceKey != "RemoveNonCancellable"),
            "Removing a shared annotation must not invalidate another closed generic construction.");
    }
}
