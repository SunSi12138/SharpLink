using SharpLink.Generator;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class DiagnosticPropertyTests
{
    [Test]
    public async Task ActualGeneratorSignatureDiagnosticShouldExposeStableFixAndSymbolProperties()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

[SharpLink.Sdk.RpcContract]
public interface IContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value);
}
"""));
        var compilation = await workspace.Solution.GetProject(workspace.ProjectId)!.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

        driver = driver.RunGenerators(compilation);
        var diagnostic = driver.GetRunResult().Diagnostics.Single(static item => item.Id == "SHARPLINK004");

        Ensure(diagnostic.Location.IsInSource, "SHARPLINK004 must point to the current method declaration.");
        Ensure(diagnostic.Properties.TryGetValue("SharpLink.FixKind", out var fixKind) &&
               fixKind == "ChooseCancellationContract",
            "SHARPLINK004 must expose a stable nonlocalized fix kind.");
        Ensure(diagnostic.Properties.TryGetValue("SharpLink.SymbolIdentity", out var identity) &&
               !string.IsNullOrWhiteSpace(identity) && identity.Contains("RunAsync", StringComparison.Ordinal),
            "SHARPLINK004 must expose a stable symbol identity for solution-aware edits.");
    }
}
