using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class FixAllCodeFixTests
{
    [Test]
    public async Task SignatureActionsShouldExplicitlyDeclineFixAll()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

public interface IContract
{
    ValueTask<int> [|RunAsync|]();
}
"""));
        var provider = CreateProvider();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
        var document = workspace.GetDocument("Contract.cs");
        var diagnosticProvider = new TestFixAllDiagnosticProvider(
            new Dictionary<DocumentId, ImmutableArray<Diagnostic>>
            {
                [document.Id] = [diagnostic]
            });
        var context = new FixAllContext(
            document,
            provider,
            FixAllScope.Document,
            "Signature:AddCancellationToken",
            ["SHARPLINK004"],
            diagnosticProvider,
            CancellationToken.None);

        var fixAllProvider = provider.GetFixAllProvider();
        Ensure(fixAllProvider is not null, "The provider must expose a Fix All provider.");
        var action = await fixAllProvider!.GetFixAsync(context);

        Ensure(action is null,
            "Solution-wide signature actions must explicitly decline Fix All rather than partially applying overlapping edits.");
    }

    [Test]
    public async Task IndependentDocumentActionsShouldUseBatchFixerAcrossProject()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("First.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IFirst|]
{
}
"""),
            ("Second.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|ISecond|]
{
}
"""));
        var provider = CreateProvider();
        var firstDiagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK006", "First.cs");
        var secondDiagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK006", "Second.cs");
        var firstDocument = workspace.GetDocument("First.cs");
        var secondDocument = workspace.GetDocument("Second.cs");
        var diagnosticProvider = new TestFixAllDiagnosticProvider(
            new Dictionary<DocumentId, ImmutableArray<Diagnostic>>
            {
                [firstDocument.Id] = [firstDiagnostic],
                [secondDocument.Id] = [secondDiagnostic]
            });
        var context = new FixAllContext(
            workspace.Solution.GetProject(workspace.ProjectId)
            ?? throw new InvalidOperationException("Test project was unavailable."),
            provider,
            FixAllScope.Project,
            "AddIService",
            ["SHARPLINK006"],
            diagnosticProvider,
            CancellationToken.None);

        var fixAllProvider = provider.GetFixAllProvider();
        Ensure(fixAllProvider is not null, "The provider must expose a Fix All provider.");
        var action = await fixAllProvider!.GetFixAsync(context);

        Ensure(action is not null, "Independent document edits must use BatchFixer for project Fix All.");
        var changed = await workspace.ApplyAsync(action!);
        var first = await workspace.GetTextAsync("First.cs", changed);
        var second = await workspace.GetTextAsync("Second.cs", changed);
        EnsureContains(first, "interface IFirst : global::SharpLink.Sdk.IService", "first Fix All document");
        EnsureContains(second, "interface ISecond : global::SharpLink.Sdk.IService", "second Fix All document");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task StructuredRestorationActionsWithoutRequiredPropertiesShouldNotParticipateInFixAll()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Dto.cs", """
public sealed class Payload
{
    public int [|Value|] { get; set; }
}
"""));
        foreach (var id in new[] { "SHARPLINK028", "SHARPLINK032", "SHARPLINK033" })
        {
            var diagnostic = await workspace.CreateDiagnosticAsync(id, "Dto.cs");
            var actions = await workspace.GetActionsAsync(diagnostic, "Dto.cs");
            Ensure(actions.Count == 0,
                $"{id} must not parse its English message or invent missing structured restoration data.");
        }
    }
}
