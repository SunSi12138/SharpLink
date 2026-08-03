using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentySecondGenerationPreflightTests
{
    [Test]
    public async Task ContractActivationShouldRejectDirectAndInheritedErrorObsoleteMethods()
    {
        using (var addIService = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;

[SharpLink.Sdk.RpcContract]
public interface [|IContract|]
{
    [Obsolete("Removed method", true)]
    int Ping();
}
""")))
        {
            await addIService.AssertCompilesAsync();
            var diagnostic = await addIService.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");
            var actions = await addIService.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "AddIService"),
                "AddIService must reject direct error-obsolete RPC methods.");
        }

        using var annotate = CodeFixTestWorkspace.Create(("Service.cs", """
using System;

public interface IBaseContract
{
    [Obsolete("Removed method", true)]
    int Ping();
}

public interface IContract : IBaseContract, SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract
{
    public int Ping() => 42;
}
"""));
        await annotate.AssertCompilesAsync();
        var annotateDiagnostic = await annotate.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");
        var annotateActions = await annotate.GetActionsAsync(annotateDiagnostic, "Service.cs");

        Ensure(annotateActions.All(static action => action.EquivalenceKey != "AnnotateRpcContract"),
            "AnnotateRpcContract must reject inherited error-obsolete RPC methods.");
    }

    [Test]
    public async Task SealDtoShouldRejectErrorObsoleteSerializableMembers()
    {
        var members = new[]
        {
            "[Obsolete(\"Removed field\", true)] public int Value;",
            "[Obsolete(\"Removed property\", true)] public int Value { get; set; }"
        };

        foreach (var member in members)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", $$"""
using System;

[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]
{
    {{member}}
}
"""));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(
                "SHARPLINK009",
                "Payload.cs",
                new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });
            var actions = await workspace.GetActionsAsync(diagnostic, "Payload.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "SealDto"),
                "SealDto must reject every error-obsolete member the Generator would serialize.");
        }
    }

    [Test]
    public async Task MethodShapeRepairsShouldRejectErrorObsoleteTargets()
    {
        var scenarios = new[]
        {
            (
                DiagnosticId: "SHARPLINK004",
                Source: """
using System;

public interface IContract : SharpLink.Sdk.IService
{
    [Obsolete("Removed method", true)]
    int [|Run|](int value);
}
"""),
            (
                DiagnosticId: "SHARPLINK002",
                Source: """
using System;
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    [Obsolete("Removed method", true)]
    int [|Run|](CancellationToken first, CancellationToken second);
}
"""),
            (
                DiagnosticId: "SHARPLINK008",
                Source: """
using System;
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    [Obsolete("Removed method", true)]
    int [|Run|](CancellationToken token, int value, SharpLink.Sdk.SharpLinkCallOptions options);
}
"""),
            (
                DiagnosticId: "SHARPLINK015",
                Source: """
using System;
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    [Obsolete("Removed method", true), SharpLink.Sdk.NonCancellable]
    int [|Run|](CancellationToken token);
}
"""),
            (
                DiagnosticId: "SHARPLINK050",
                Source: """
using System;

public interface IContract : SharpLink.Sdk.IService
{
    [Obsolete("Removed method", true), SharpLink.Sdk.Timeout(-1)]
    int [|Run|]();
}
"""),
            (
                DiagnosticId: "SHARPLINK056",
                Source: """
using System;
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    [Obsolete("Removed method", true), SharpLink.Sdk.Oneway]
    ValueTask<int> [|RunAsync|]();
}
"""),
            (
                DiagnosticId: "SHARPLINK053",
                Source: """
using System;

[Obsolete("Removed container", true)]
public static class RemovedContainer
{
    public static int [|Run|](int value) => value;
}
""")
        };

        foreach (var scenario in scenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Target.cs", scenario.Source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(scenario.DiagnosticId, "Target.cs");
            var actions = await workspace.GetActionsAsync(diagnostic, "Target.cs");

            Ensure(actions.Count == 0,
                $"{scenario.DiagnosticId} must not activate generated references to an error-obsolete method.");
        }
    }
}
