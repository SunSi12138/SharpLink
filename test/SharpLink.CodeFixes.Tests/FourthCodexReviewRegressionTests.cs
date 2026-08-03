using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class FourthCodexReviewRegressionTests
{
    [Test]
    public async Task Sharplink055PublicizationShouldNotExposeInternalMemberSignatureTypes()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
internal sealed class InternalPayload { }
internal delegate void InternalEventHandler(InternalPayload value);

[SharpLink.Sdk.RpcContract]
internal interface [|IContract|] : SharpLink.Sdk.IService
{
    InternalPayload Transform(InternalPayload value);
    InternalPayload Current { get; }
    event InternalEventHandler Changed;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK055", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        await AssertOptionalSafePublicizationAsync(
            workspace,
            actions,
            "Contract.cs",
            "Make RPC contract publicly reachable",
            "MakeContractPublic");
    }

    [Test]
    public async Task Sharplink018PublicizationShouldNotExposeInternalMemberSignatureTypes()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
internal sealed class InternalPayload { }
internal delegate void InternalEventHandler(InternalPayload value);

[SharpLink.Sdk.RpcService]
internal class [|Service|]
{
    public InternalPayload Field = new();
    public InternalPayload Current { get; } = new();
    public event InternalEventHandler? Changed;
    public InternalPayload Transform(InternalPayload value) => value;
    protected InternalPayload ProtectedField = new();
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        await AssertOptionalSafePublicizationAsync(
            workspace,
            actions,
            "Service.cs",
            "Make RPC service publicly reachable",
            "MakeServicePublic");
    }

    [Test]
    public async Task Sharplink043ShouldPublicizeInternalSourceBaseWithAdapter()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class AdapterBase
{
    protected AdapterBase() { }
}

internal class Adapter : AdapterBase, SharpLink.Abstractions.IRpcCodecAdapter
{
    private Adapter() { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Fix Adapter Codec adapter shape"
               && actions[0].EquivalenceKey == "FixAdapterShape",
            "A source-base adapter should remain safely repairable as one solution action.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Adapter.cs", changed);
        EnsureContains(source, "public class AdapterBase", "adapter source base class");
        EnsureContains(source, "public sealed class Adapter", "adapter declaration");
        EnsureContains(source, "public Adapter()", "adapter constructor");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink031ShouldDistinguishRequiredModifierFromRpcRequiredAttribute()
    {
        using (var modifier = CodeFixTestWorkspace.Create(("Payload.cs", """
public sealed class Payload
{
    public required int [|Value|] { get; set; }
}
""")))
        {
            await modifier.AssertCompilesAsync();
            var diagnostic = await modifier.CreateDiagnosticAsync("SHARPLINK031", "Payload.cs");

            var actions = await modifier.GetActionsAsync(diagnostic, "Payload.cs");

            Ensure(actions.Count == 0,
                "A C# required modifier has no RpcRequired attribute for RemoveRpcRequired to remove.");
        }

        using var attribute = CodeFixTestWorkspace.Create(("Payload.cs", """
public sealed class Payload
{
    [SharpLink.Sdk.RpcRequired]
    public int [|Value|] { get; set; }
}
"""));
        await attribute.AssertCompilesAsync();
        var attributeDiagnostic = await attribute.CreateDiagnosticAsync(
            "SHARPLINK031",
            "Payload.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "RemoveRpcRequired" });

        var attributeActions = await attribute.GetActionsAsync(attributeDiagnostic, "Payload.cs");

        Ensure(attributeActions.Count == 1
               && attributeActions[0].Title == "Remove [RpcRequired]"
               && attributeActions[0].EquivalenceKey == "RemoveRpcRequired",
            "An actual RpcRequired attribute must retain its removal action.");
        var changed = await attribute.ApplyAsync(attributeActions[0]);
        var source = await attribute.GetTextAsync("Payload.cs", changed);
        EnsureDoesNotContain(source, "RpcRequired", "attribute-backed required member");
        await attribute.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task AddCancellationTokenShouldWithholdForInterfaceOrImplementationOverloadCollision()
    {
        foreach (var diagnosticId in new[] { "SHARPLINK004", "SHARPLINK014" })
        {
            foreach (var collisionInInterface in new[] { true, false })
            {
                using var workspace = CreateOverloadCollisionWorkspace(diagnosticId, collisionInInterface);
                await workspace.AssertCompilesAsync();
                var diagnostic = await workspace.CreateDiagnosticAsync(diagnosticId, "Contract.cs");

                var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

                Ensure(actions.Select(static item => (item.Title, item.EquivalenceKey)).SequenceEqual(
                        [("Annotate with [NonCancellable]", "AddNonCancellable")]),
                    $"{diagnosticId} must suppress Add CancellationToken for a collision in the " +
                    $"{(collisionInInterface ? "interface" : "implementation")} family. Actual: " +
                    string.Join(", ", actions.Select(static item => item.Title)));
                var changed = await workspace.ApplyAsync(actions[0]);
                await workspace.AssertCompilesAsync(changed);
            }
        }
    }

    private static async Task AssertOptionalSafePublicizationAsync(
        CodeFixTestWorkspace workspace,
        IReadOnlyList<CodeAction> actions,
        string documentName,
        string expectedTitle,
        string expectedKey)
    {
        Ensure(actions.Count <= 1,
            $"Public reachability must expose at most one deterministic action. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        if (actions.Count == 0)
            return;

        Ensure(actions[0].Title == expectedTitle && actions[0].EquivalenceKey == expectedKey,
            $"The publicization action must retain its stable title and key. Actual: {actions[0].Title} / {actions[0].EquivalenceKey}");
        var changed = await workspace.ApplyAsync(actions[0]);
        _ = await workspace.GetTextAsync(documentName, changed);
        await workspace.AssertCompilesAsync(changed);
    }

    private static CodeFixTestWorkspace CreateOverloadCollisionWorkspace(
        string diagnosticId,
        bool collisionInInterface)
    {
        var streaming = diagnosticId == "SHARPLINK014";
        var returnType = streaming
            ? "System.Collections.Generic.IAsyncEnumerable<int>"
            : "System.Threading.Tasks.ValueTask<int>";
        var extraInterfaceMethod = collisionInInterface
            ? $"    {returnType} RunAsync(int value, System.Threading.CancellationToken cancellationToken);"
            : string.Empty;
        var targetImplementation = streaming
            ? """
    public async System.Collections.Generic.IAsyncEnumerable<int> RunAsync(int value)
    {
        yield return value;
        await System.Threading.Tasks.Task.Yield();
    }
"""
            : """
    public System.Threading.Tasks.ValueTask<int> RunAsync(int value)
        => System.Threading.Tasks.ValueTask.FromResult(value);
""";
        var collisionImplementation = streaming
            ? """
    public async System.Collections.Generic.IAsyncEnumerable<int> RunAsync(
        int value,
        System.Threading.CancellationToken cancellationToken)
    {
        yield return value;
        await System.Threading.Tasks.Task.Yield();
    }
"""
            : """
    public System.Threading.Tasks.ValueTask<int> RunAsync(
        int value,
        System.Threading.CancellationToken cancellationToken)
        => System.Threading.Tasks.ValueTask.FromResult(value);
""";

        return CodeFixTestWorkspace.Create(
            ("Contract.cs", $$"""
public interface IContract : SharpLink.Sdk.IService
{
    {{returnType}} [|RunAsync|](int value);
{{extraInterfaceMethod}}
}
"""),
            ("Implementation.cs", $$"""
public sealed class Contract : IContract
{
{{targetImplementation}}
{{collisionImplementation}}
}
"""));
    }
}
