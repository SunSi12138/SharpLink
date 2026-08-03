using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class NinthCodexReviewRegressionTests
{
    [Test]
    public async Task KeepControlParameterShouldPreserveParametersReferencedByRelatedMethodBodies()
    {
        var scenarios = new[]
        {
            (
                Name: "concrete implementation",
                Documents: new[]
                {
                    ("Contract.cs", """
public interface IContract
{
    int [|Run|](System.Threading.CancellationToken first, System.Threading.CancellationToken second);
}
"""),
                    ("Implementation.cs", """
public sealed class Contract : IContract
{
    public int Run(System.Threading.CancellationToken first, System.Threading.CancellationToken second)
        => first.CanBeCanceled ? 1 : 0;
}
""")
                }),
            (
                Name: "default interface implementation",
                Documents: new[]
                {
                    ("Contract.cs", """
public interface IContract
{
    int [|Run|](System.Threading.CancellationToken first, System.Threading.CancellationToken second)
        => first.CanBeCanceled ? 1 : 0;
}
""")
                })
        };

        foreach (var scenario in scenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(scenario.Documents);
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK002", "Contract.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Count == 1
                   && actions[0].Title == "Keep CancellationToken 'first'"
                   && actions[0].EquivalenceKey == "Signature:Keep:CancellationToken:0",
                $"Only the Keep action preserving the parameter used by the {scenario.Name} body is safe. Actual: " +
                string.Join(", ", actions.Select(static action => action.Title)));
            var changed = await workspace.ApplyAsync(actions[0]);
            foreach (var documentName in scenario.Documents.Select(static document => document.Item1))
            {
                var source = await workspace.GetTextAsync(documentName, changed);
                EnsureContains(source,
                    "Run(System.Threading.CancellationToken first)",
                    scenario.Name + " declaration");
                EnsureDoesNotContain(source,
                    "System.Threading.CancellationToken second",
                    scenario.Name + " declaration");
            }
            await workspace.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task Sharplink016ShouldWithholdGenericContractCandidates()
    {
        var unsafeScenarios = new[]
        {
            (Name: "generic interface", Source: """
public interface IContract<T> : SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract<int> { }
"""),
            (Name: "interface nested in generic container", Source: """
public static class Container<T>
{
    public interface IContract : SharpLink.Sdk.IService { }
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : Container<int>.IContract { }
""")
        };

        foreach (var scenario in unsafeScenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Service.cs", scenario.Source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");
            var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");
            Ensure(actions.Count == 0,
                $"Annotating a {scenario.Name} cannot produce a valid non-generic RPC contract.");
        }

        using var publicCandidate = CodeFixTestWorkspace.Create(("Service.cs", """
public interface IContract : SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract { }
"""));
        await publicCandidate.AssertCompilesAsync();
        var publicDiagnostic = await publicCandidate.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");
        var publicActions = await publicCandidate.GetActionsAsync(publicDiagnostic, "Service.cs");
        Ensure(publicActions.Count == 1
               && publicActions[0].Title == "Annotate IContract with [RpcContract]"
               && publicActions[0].EquivalenceKey == "AnnotateRpcContract",
            "A public non-generic sole contract candidate must remain annotatable.");
        var publicChanged = await publicCandidate.ApplyAsync(publicActions[0]);
        await publicCandidate.AssertCompilesAsync(publicChanged);
    }

    [Test]
    public async Task Sharplink031ShouldWithholdAttributeRemovalForMultiVariableField()
    {
        using (var multiVariable = CodeFixTestWorkspace.Create(("Payload.cs", """
public sealed class Payload
{
    [SharpLink.Sdk.RpcRequired]
    public int [|First|], Second;
}
""")))
        {
            await multiVariable.AssertCompilesAsync();
            var diagnostic = await multiVariable.CreateDiagnosticAsync(
                "SHARPLINK031",
                "Payload.cs",
                new Dictionary<string, string?> { ["SharpLink.FixKind"] = "RemoveRpcRequired" });
            var actions = await multiVariable.GetActionsAsync(diagnostic, "Payload.cs");
            Ensure(actions.Count == 0,
                "Removing a field-level RpcRequired attribute would also alter the field's other variables.");
        }

        using var singleVariable = CodeFixTestWorkspace.Create(("Payload.cs", """
public sealed class Payload
{
    [SharpLink.Sdk.RpcRequired]
    public int [|Value|];
}
"""));
        await singleVariable.AssertCompilesAsync();
        var singleDiagnostic = await singleVariable.CreateDiagnosticAsync(
            "SHARPLINK031",
            "Payload.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "RemoveRpcRequired" });
        var singleActions = await singleVariable.GetActionsAsync(singleDiagnostic, "Payload.cs");
        Ensure(singleActions.Count == 1
               && singleActions[0].Title == "Remove [RpcRequired]"
               && singleActions[0].EquivalenceKey == "RemoveRpcRequired",
            "A single-variable field must retain the deterministic RpcRequired removal action.");
        var changed = await singleVariable.ApplyAsync(singleActions[0]);
        var source = await singleVariable.GetTextAsync("Payload.cs", changed);
        EnsureDoesNotContain(source, "RpcRequired", "single-variable field");
        await singleVariable.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink028ShouldRestorePositionalRecordPropertyId()
    {
        using (var missingAttribute = CodeFixTestWorkspace.Create(("Payload.cs", """
[SharpLink.Sdk.RpcSerializable]
public sealed record Payload(int [|Value|]);
""")))
        {
            await missingAttribute.AssertCompilesAsync();
            var diagnostic = await missingAttribute.CreateDiagnosticAsync(
                "SHARPLINK028",
                "Payload.cs",
                new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "7" });
            var actions = await missingAttribute.GetActionsAsync(diagnostic, "Payload.cs");
            Ensure(actions.Count == 1
                   && actions[0].Title == "Preserve published member ID 7"
                   && actions[0].EquivalenceKey == "RestoreMemberId",
                "A positional record property without RpcMember must be safely restorable.");
            var changed = await missingAttribute.ApplyAsync(actions[0]);
            var source = await missingAttribute.GetTextAsync("Payload.cs", changed);
            EnsureContains(source,
                "record Payload([property: global::SharpLink.Sdk.RpcMember(7)] int Value)",
                "positional record property");
            await missingAttribute.AssertCompilesAsync(changed);
        }

        using (var existingAttribute = CodeFixTestWorkspace.Create(("Payload.cs", """
[SharpLink.Sdk.RpcSerializable]
public sealed record Payload(
    [property: SharpLink.Sdk.RpcMember(99)] int [|Value|]);
""")))
        {
            await existingAttribute.AssertCompilesAsync();
            var diagnostic = await existingAttribute.CreateDiagnosticAsync(
                "SHARPLINK028",
                "Payload.cs",
                new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "7" });
            var actions = await existingAttribute.GetActionsAsync(diagnostic, "Payload.cs");
            Ensure(actions.Count == 1,
                "An existing property-targeted RpcMember on a positional record must be updatable.");
            var changed = await existingAttribute.ApplyAsync(actions[0]);
            var source = await existingAttribute.GetTextAsync("Payload.cs", changed);
            EnsureContains(source, "[property: SharpLink.Sdk.RpcMember(7)]", "updated positional property ID");
            EnsureDoesNotContain(source, "RpcMember(99)", "updated positional property ID");
            await existingAttribute.AssertCompilesAsync(changed);
        }

        using var occupied = CodeFixTestWorkspace.Create(("Payload.cs", """
[SharpLink.Sdk.RpcSerializable]
public sealed record Payload(
    [property: SharpLink.Sdk.RpcMember(7)] int Existing,
    int [|Value|]);
"""));
        await occupied.AssertCompilesAsync();
        var occupiedDiagnostic = await occupied.CreateDiagnosticAsync(
            "SHARPLINK028",
            "Payload.cs",
            new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "7" });
        var occupiedActions = await occupied.GetActionsAsync(occupiedDiagnostic, "Payload.cs");
        Ensure(occupiedActions.Count == 0,
            "A positional property cannot restore an ID already occupied by another synthesized property.");
    }
}
