using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class NineteenthCodexReviewRegressionTests
{
    [Test]
    public async Task ContractAnnotationShouldRejectASecondRpcServiceOwner()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Services.cs", """
public interface ISharedContract : SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcService]
public sealed class [|FirstService|] : ISharedContract { }

[SharpLink.Sdk.RpcService]
public sealed class SecondService : ISharedContract { }
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK016", "Services.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Services.cs");

        Ensure(actions.All(static action => action.EquivalenceKey != "AnnotateRpcContract"),
            "Annotating a shared IService interface must not create a second RPC service owner.");
    }

    [Test]
    public async Task AdapterRepairShouldPublicizeItsProtectedParameterlessConstructor()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal delegate void AdapterChanged(int value);

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    protected Adapter() { }

    public event AdapterChanged? Changed;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "FixAdapterShape");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Adapter.cs", changed);

        EnsureContains(source, "public sealed class Adapter", "adapter declaration");
        EnsureContains(source, "public Adapter()", "adapter constructor");
        EnsureContains(source, "public delegate void AdapterChanged", "adapter delegate dependency");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task PublicizationShouldIncludeEditableDelegateDependencies()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
[SharpLink.Sdk.RpcContract]
internal interface [|IContract|] : SharpLink.Sdk.IService
{
    event ContractChanged Changed;
}
"""),
            ("Dependencies.cs", """
internal delegate void ContractChanged(int value);
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK055", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "MakeContractPublic");
        var changed = await workspace.ApplyAsync(action);
        var contract = await workspace.GetTextAsync("Contract.cs", changed);
        var dependencies = await workspace.GetTextAsync("Dependencies.cs", changed);

        EnsureContains(contract, "public interface IContract", "contract declaration");
        EnsureContains(dependencies, "public delegate void ContractChanged", "delegate dependency");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task CombinedServiceRepairShouldPublicizeDelegateDependencies()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
internal delegate void ServiceChanged(int value);

[SharpLink.Sdk.RpcService]
internal abstract class [|Service|]
{
    public Service() { }

    public event ServiceChanged? Changed;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "MakeServiceConcreteAndPublic");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Service.cs", changed);

        EnsureContains(source, "public class Service", "service declaration");
        EnsureContains(source, "public delegate void ServiceChanged", "service delegate dependency");
        EnsureDoesNotContain(source, "abstract class Service", "service declaration");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task PublicizationShouldRejectErrorObsoleteContractsAndDtos()
    {
        var scenarios = new[]
        {
            (
                Name: "RPC contract",
                DiagnosticId: "SHARPLINK055",
                Source: """
using System;

[Obsolete("Removed contract", true)]
[SharpLink.Sdk.RpcContract]
internal interface [|IContract|] : SharpLink.Sdk.IService { }
""",
                Properties: (IReadOnlyDictionary<string, string?>?)null),
            (
                Name: "DTO",
                DiagnosticId: "SHARPLINK009",
                Source: """
using System;

[Obsolete("Removed DTO", true)]
[SharpLink.Sdk.RpcSerializable]
internal sealed class [|Payload|] { }
""",
                Properties: (IReadOnlyDictionary<string, string?>?)new Dictionary<string, string?>
                {
                    ["SharpLink.FixKind"] = "MakeDtoAccessible"
                })
        };

        foreach (var scenario in scenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Target.cs", scenario.Source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(
                scenario.DiagnosticId,
                "Target.cs",
                scenario.Properties);

            var actions = await workspace.GetActionsAsync(diagnostic, "Target.cs");

            Ensure(actions.Count == 0,
                $"Publicization must be withheld for an error-obsolete {scenario.Name}.");
        }
    }
}
