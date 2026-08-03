using SharpLink.Generator;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ThirtyThirdCodexReviewRegressionTests
{
    [Test]
    public async Task AdapterShapeShouldReusePublicParamsConstructor()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public Adapter(params int[] values) { Count = values.Length; }

    public int Count { get; }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");
        var action = (await workspace.GetActionsAsync(diagnostic, "Adapter.cs"))
            .Single(static item => item.EquivalenceKey == "FixAdapterShape");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Adapter.cs", changed);

        EnsureContains(source, "public Adapter(params int[] values)", "params adapter constructor");
        EnsureDoesNotContain(source, "public Adapter()", "synthetic adapter constructor");
        await workspace.AssertCompilesAsync(changed);
        var compilation = await changed.GetProject(workspace.ProjectId)!.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RpcGenerator());
        var diagnostics = driver.RunGenerators(compilation).GetRunResult().Diagnostics;
        Ensure(diagnostics.All(static item => item.Id != "SHARPLINK043"),
            "The generator must accept a public params adapter constructor.");
    }
}
