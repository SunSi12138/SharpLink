using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class DeterministicDocumentCodeFixTests
{
    [Test]
    public async Task Sharplink006ShouldAppendIServiceWithoutDisturbingExistingBases()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
public interface IBase { }

[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : IBase
{
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");

        var action = await GetOnlyActionAsync(workspace, diagnostic, "Contract.cs", "Add IService to RPC contract", "AddIService");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        EnsureContains(source, "interface IContract : IBase, global::SharpLink.Sdk.IService", "RPC contract");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink015ShouldRemoveOnlyNonCancellableAndPreserveToken()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Threading;
using System.Threading.Tasks;

public interface IContract
{
    [Obsolete]
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> [|RunAsync|](CancellationToken token);
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK015", "Contract.cs");

        var action = await GetOnlyActionAsync(workspace, diagnostic, "Contract.cs", "Remove [NonCancellable]", "RemoveNonCancellable");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        EnsureDoesNotContain(source, "NonCancellable", "fixed method");
        EnsureContains(source, "[Obsolete]", "fixed method");
        EnsureContains(source, "RunAsync(CancellationToken token)", "fixed method");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink020ShouldOfferThreeExplicitLifetimeActionsWithDistinctKeys()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService(Lifetime = (SharpLink.Sdk.SharpLinkServiceLifetime)99)]
public sealed class [|Service|]
{
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK020", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Select(static item => item.Title).SequenceEqual(
                [
                    "Set RPC service lifetime to Singleton",
                    "Set RPC service lifetime to Connection",
                    "Set RPC service lifetime to Call"
                ],
                StringComparer.Ordinal),
            $"SHARPLINK020 must expose all explicit lifetime choices. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        Ensure(actions.Select(static item => item.EquivalenceKey).SequenceEqual(
                ["SetLifetime:Singleton", "SetLifetime:Connection", "SetLifetime:Call"],
                StringComparer.Ordinal),
            "Lifetime actions must have stable, distinct keys.");

        var lifetimes = new[] { "Singleton", "Connection", "Call" };
        for (var index = 0; index < actions.Count; index++)
        {
            var changed = await GetChangedSolutionAsync(actions[index]);
            var source = await workspace.GetTextAsync("Service.cs", changed);
            EnsureContains(source,
                $"RpcService(Lifetime = global::SharpLink.Sdk.SharpLinkServiceLifetime.{lifetimes[index]})",
                $"{lifetimes[index]} service attribute");
            await workspace.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task Sharplink028ShouldUpdateRpcMemberFromStructuredPreviousId()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Dto.cs", """
public sealed class Payload
{
    [SharpLink.Sdk.RpcMember(99)]
    [System.Obsolete]
    public int [|Value|] { get; set; }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK028",
            "Dto.cs",
            new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "7" });

        var action = await GetOnlyActionAsync(workspace, diagnostic, "Dto.cs", "Preserve published member ID 7", "RestoreMemberId");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Dto.cs", changed);

        EnsureContains(source, "RpcMember(7)", "DTO member");
        EnsureDoesNotContain(source, "RpcMember(99)", "DTO member");
        EnsureContains(source, "[System.Obsolete]", "DTO member");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink031ShouldRemoveRpcRequiredOnlyForStructuredFixKind()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Dto.cs", """
public sealed class Payload
{
    [SharpLink.Sdk.RpcRequired, System.Obsolete]
    public int [|Value|] { get; set; }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK031",
            "Dto.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "RemoveRpcRequired" });

        var action = await GetOnlyActionAsync(workspace, diagnostic, "Dto.cs", "Remove [RpcRequired]", "RemoveRpcRequired");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Dto.cs", changed);

        EnsureDoesNotContain(source, "RpcRequired", "DTO member");
        EnsureContains(source, "System.Obsolete", "DTO member");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink031WithoutCurrentMemberFixKindShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Dto.cs", """
public sealed class Payload
{
    public int [|Marker|] { get; set; }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK031",
            "Dto.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "RestoreRemovedRequiredMember" });

        var actions = await workspace.GetActionsAsync(diagnostic, "Dto.cs");

        Ensure(actions.Count == 0, "A removed required member must remain diagnostic-only.");
    }

    [Test]
    public async Task Sharplink032ShouldRestorePublishedEnumUnderlyingType()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Status.cs", """
public enum [|Status|] : int
{
    None,
    Ready
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK032",
            "Status.cs",
            new Dictionary<string, string?> { ["SharpLink.PreviousEnumUnderlyingType"] = "System.Byte" });

        var action = await GetOnlyActionAsync(
            workspace,
            diagnostic,
            "Status.cs",
            "Restore published enum underlying type System.Byte",
            "RestoreEnumType");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Status.cs", changed);

        EnsureContains(source, "enum Status : byte", "enum declaration");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink033ShouldRestorePublishedUnionMappingFromStructuredProperties()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Union.cs", """
public sealed class OldCase : IResult { }
public sealed class NewCase : IResult { }

[[|SharpLink.Sdk.RpcUnionCase|](9, typeof(NewCase))]
public interface IResult { }
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK033",
            "Union.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousUnionTag"] = "7",
                ["SharpLink.PreviousUnionType"] = "OldCase"
            });

        var action = await GetOnlyActionAsync(workspace, diagnostic, "Union.cs", "Restore tag 7 to OldCase", "RestoreUnionTag");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Union.cs", changed);

        EnsureContains(source, "RpcUnionCase(7, typeof(global::OldCase))", "union mapping");
        EnsureContains(source, "RpcUnionCase(9, typeof(NewCase))", "preserved reassigned union mapping");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink049ShouldRemoveOnlyTheSourceLocatedBuiltinBinding()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Bindings.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(string))]
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int))]
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK049", "Bindings.cs");

        var action = await GetOnlyActionAsync(
            workspace,
            diagnostic,
            "Bindings.cs",
            "Remove built-in Codec adapter binding",
            "RemoveBuiltinAdapterBinding");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Bindings.cs", changed);

        EnsureDoesNotContain(source, "typeof(string)", "adapter bindings");
        EnsureContains(source, "typeof(int)", "adapter bindings");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink050ShouldOfferDefaultThenRemoveTimeout()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Threading.Tasks;

public interface IContract
{
    [Obsolete]
    [[|SharpLink.Sdk.Timeout|](0)]
    ValueTask<int> RunAsync();
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK050", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static item => item.Title).SequenceEqual(
                ["Use generated default timeout", "Remove [Timeout]"],
                StringComparer.Ordinal),
            "Timeout actions must be ordered default then remove.");
        Ensure(actions.Select(static item => item.EquivalenceKey).SequenceEqual(
                ["UseDefaultTimeout", "RemoveTimeout"],
                StringComparer.Ordinal),
            "Timeout actions must have stable, distinct equivalence keys.");

        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Contract.cs", changed);
        EnsureContains(source, "[SharpLink.Sdk.Timeout]", "timeout attribute");
        EnsureDoesNotContain(source, "Timeout(0)", "timeout attribute");
        EnsureContains(source, "[Obsolete]", "method attributes");
        await workspace.AssertCompilesAsync(changed);

        var removed = await GetChangedSolutionAsync(actions[1]);
        var removedSource = await workspace.GetTextAsync("Contract.cs", removed);
        EnsureDoesNotContain(removedSource, "Timeout", "remove-timeout action");
        EnsureContains(removedSource, "[Obsolete]", "remove-timeout action");
        await workspace.AssertCompilesAsync(removed);
    }

    [Test]
    public async Task Sharplink051ShouldRemoveOnlyTheSpecificInvalidUnionMapping()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Union.cs", """
public sealed class FirstCase : IResult { }
public sealed class SecondCase : IResult { }

[[|SharpLink.Sdk.RpcUnionCase|](0, typeof(FirstCase))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(SecondCase))]
public interface IResult { }
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK051", "Union.cs");

        var action = await GetOnlyActionAsync(
            workspace,
            diagnostic,
            "Union.cs",
            "Remove invalid RPC union case mapping",
            "RemoveInvalidUnionCase");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Union.cs", changed);

        EnsureDoesNotContain(source, "RpcUnionCase(0", "union mappings");
        EnsureContains(source, "RpcUnionCase(2, typeof(SecondCase))", "union mappings");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink055ShouldMakeContractAndContainingTypesPublicPreservingModifiers()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
internal static partial class Container
{
    private interface [|IContract|]
    {
    }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK055", "Contract.cs");

        var action = await GetOnlyActionAsync(
            workspace,
            diagnostic,
            "Contract.cs",
            "Make RPC contract publicly reachable",
            "MakeContractPublic");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        EnsureContains(source, "public static partial class Container", "containing type");
        EnsureContains(source, "public interface IContract", "contract interface");
        EnsureDoesNotContain(source, "internal static", "containing type");
        EnsureDoesNotContain(source, "private interface", "contract interface");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink056ShouldRemoveOnewayAndPreserveReturnContract()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Threading.Tasks;

public interface IContract
{
    [Obsolete, SharpLink.Sdk.Oneway]
    ValueTask<int> [|RunAsync|]();
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK056", "Contract.cs");

        var action = await GetOnlyActionAsync(workspace, diagnostic, "Contract.cs", "Remove [Oneway]", "RemoveOneway");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        EnsureDoesNotContain(source, "Oneway", "fixed method");
        EnsureContains(source, "Obsolete", "fixed method");
        EnsureContains(source, "ValueTask<int> RunAsync()", "fixed method return contract");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink028ShouldAddRpcMemberWhenTheMemberHasNoExplicitId()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Dto.cs", """
public sealed class Payload
{
    public int [|Value|] { get; set; }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK028",
            "Dto.cs",
            new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "19" });

        var action = await GetOnlyActionAsync(
            workspace, diagnostic, "Dto.cs", "Preserve published member ID 19", "RestoreMemberId");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Dto.cs", changed);

        EnsureContains(source, "[global::SharpLink.Sdk.RpcMember(19)]", "DTO member");
        EnsureContains(source, "public int Value { get; set; }", "DTO member declaration");
        await workspace.AssertCompilesAsync(changed);
    }

    private static async Task<CodeAction> GetOnlyActionAsync(
        CodeFixTestWorkspace workspace,
        Diagnostic diagnostic,
        string documentName,
        string expectedTitle,
        string expectedEquivalenceKey)
    {
        var actions = await workspace.GetActionsAsync(diagnostic, documentName);
        Ensure(actions.Count == 1,
            $"Expected one action '{expectedTitle}', but got: {string.Join(", ", actions.Select(static item => item.Title))}");
        Ensure(actions[0].Title == expectedTitle,
            $"Expected title '{expectedTitle}', but got '{actions[0].Title}'.");
        Ensure(actions[0].EquivalenceKey == expectedEquivalenceKey,
            $"Expected key '{expectedEquivalenceKey}', but got '{actions[0].EquivalenceKey}'.");
        return actions[0];
    }
}
