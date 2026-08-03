using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class SeventhCodexReviewRegressionTests
{
    [Test]
    public async Task Sharplink008ShouldRespectInterpolatedStringHandlerParameterDependencies()
    {
        using (var unsafeMove = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;

[System.Runtime.CompilerServices.InterpolatedStringHandler]
public ref struct Handler
{
    public Handler(int literalLength, int formattedCount, CancellationToken token) { }
    public void AppendLiteral(string value) { }
}

public interface IContract
{
    void [|Run|](
        CancellationToken token,
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument("token")] Handler handler,
        int value);
}
""")))
        {
            await unsafeMove.AssertCompilesAsync();
            var diagnostic = await unsafeMove.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");
            var actions = await unsafeMove.GetActionsAsync(diagnostic, "Contract.cs");
            Ensure(actions.Count == 0,
                "Reordering token after a handler that references token would invalidate the handler dependency.");
        }

        using var safeMove = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;

[System.Runtime.CompilerServices.InterpolatedStringHandler]
public ref struct Handler
{
    public Handler(int literalLength, int formattedCount, int value) { }
    public void AppendLiteral(string text) { }
}

public interface IContract
{
    void [|Run|](
        CancellationToken token,
        int value,
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument("value")] Handler handler);
}
"""));
        await safeMove.AssertCompilesAsync();
        var safeDiagnostic = await safeMove.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");
        var safeActions = await safeMove.GetActionsAsync(safeDiagnostic, "Contract.cs");
        Ensure(safeActions.Count == 1
               && safeActions[0].Title == "Reorder RPC control parameters"
               && safeActions[0].EquivalenceKey == "Signature:ReorderControlParameters",
            "A handler whose non-control dependency remains before it must retain the reorder action.");
        var changed = await safeMove.ApplyAsync(safeActions[0]);
        var source = await safeMove.GetTextAsync("Contract.cs", changed);
        EnsureContains(source,
            "Run(int value, [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument(\"value\")] Handler handler, CancellationToken token)",
            "safely reordered handler method");
        await safeMove.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink028ShouldWithholdRestorationForOccupiedMemberId()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", """
public sealed class Payload
{
    [SharpLink.Sdk.RpcMember(7)]
    public int Existing { get; set; }

    [SharpLink.Sdk.RpcMember(99)]
    public int [|Value|] { get; set; }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK028",
            "Payload.cs",
            new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "7" });

        var actions = await workspace.GetActionsAsync(diagnostic, "Payload.cs");

        Ensure(actions.Count == 0,
            "A published member ID already occupied by another DTO member cannot be restored safely.");
    }

    [Test]
    public async Task Sharplink033ShouldWithholdRestorationForAlreadyMappedCaseType()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Union.cs", """
public sealed class PreviousCase : IResult { }
public sealed class CurrentCase : IResult { }

[SharpLink.Sdk.RpcUnionCase(3, typeof(PreviousCase))]
[[|SharpLink.Sdk.RpcUnionCase|](9, typeof(CurrentCase))]
public interface IResult { }
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK033",
            "Union.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousUnionTag"] = "7",
                ["SharpLink.PreviousUnionType"] = "PreviousCase"
            });

        var actions = await workspace.GetActionsAsync(diagnostic, "Union.cs");

        Ensure(actions.Count == 0,
            "A union case type already registered by another mapping cannot be restored under a second tag.");
    }

    [Test]
    public async Task Sharplink056ShouldSynchronizeEditablePoliciesAndWithholdForMetadataPolicy()
    {
        using (var editable = CodeFixTestWorkspace.Create(
                   ("Base.cs", """
using System.Threading.Tasks;

public interface IBaseContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Oneway]
    ValueTask RunAsync();
}
"""),
                   ("Derived.cs", """
using System.Threading.Tasks;

public interface IDerivedContract : IBaseContract
{
    [SharpLink.Sdk.Oneway]
    new ValueTask [|RunAsync|]();
}
""")))
        {
            await editable.AssertCompilesAsync();
            var diagnostic = await editable.CreateDiagnosticAsync("SHARPLINK056", "Derived.cs");
            var actions = await editable.GetActionsAsync(diagnostic, "Derived.cs");
            Ensure(actions.Count == 1
                   && actions[0].Title == "Remove [Oneway]"
                   && actions[0].EquivalenceKey == "RemoveOneway",
                "Editable equivalent Oneway policies need one synchronized removal action.");
            var changed = await editable.ApplyAsync(actions[0]);
            var baseSource = await editable.GetTextAsync("Base.cs", changed);
            var derivedSource = await editable.GetTextAsync("Derived.cs", changed);
            EnsureDoesNotContain(baseSource, "Oneway", "base interface Oneway policy");
            EnsureDoesNotContain(derivedSource, "Oneway", "derived interface Oneway policy");
            await editable.AssertCompilesAsync(changed);
        }

        using var metadata = CodeFixTestWorkspace.Create(("Derived.cs", """
using System.Threading.Tasks;

public interface IDerivedContract : External.IBaseContract
{
    [SharpLink.Sdk.Oneway]
    new ValueTask [|RunAsync|]();
}
"""));
        metadata.AddMetadataReferenceFromSource("External.Oneway.Contracts", """
namespace SharpLink.Sdk
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class OnewayAttribute : System.Attribute { }
}

namespace External
{
    public interface IBaseContract
    {
        [SharpLink.Sdk.Oneway]
        System.Threading.Tasks.ValueTask RunAsync();
    }
}
""");
        await metadata.AssertCompilesAsync();
        var metadataDiagnostic = await metadata.CreateDiagnosticAsync("SHARPLINK056", "Derived.cs");
        var metadataActions = await metadata.GetActionsAsync(metadataDiagnostic, "Derived.cs");
        Ensure(metadataActions.Count == 0,
            "Oneway removal must be hidden when an equivalent metadata policy cannot be edited.");
    }

    [Test]
    public async Task Sharplink016ShouldRequireEffectivelyPublicContractCandidate()
    {
        var unsafeScenarios = new[]
        {
            (Name: "internal top-level contract", Source: """
internal interface IContract : SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcService]
internal sealed class [|Service|] : IContract { }
"""),
            (Name: "public nested contract in internal container", Source: """
internal static class Container
{
    public interface IContract : SharpLink.Sdk.IService { }
}

[SharpLink.Sdk.RpcService]
internal sealed class [|Service|] : Container.IContract { }
""")
        };

        foreach (var scenario in unsafeScenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Service.cs", scenario.Source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");
            var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");
            Ensure(actions.Count == 0,
                $"Annotating an {scenario.Name} cannot restore a publicly reachable RPC contract.");
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
            "An effectively public sole contract candidate must remain safely annotatable.");
        var publicChanged = await publicCandidate.ApplyAsync(publicActions[0]);
        var publicSource = await publicCandidate.GetTextAsync("Service.cs", publicChanged);
        EnsureContains(publicSource, "[global::SharpLink.Sdk.RpcContract]", "public contract candidate");
        await publicCandidate.AssertCompilesAsync(publicChanged);
    }
}
