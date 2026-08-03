using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TenthCodexReviewRegressionTests
{
    [Test]
    public async Task AddCancellationTokenShouldWithholdWhenHandlerDependsOnCallOptions()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;
using SharpLink.Sdk;

[System.Runtime.CompilerServices.InterpolatedStringHandler]
public ref struct Handler
{
    public Handler(int literalLength, int formattedCount, SharpLinkCallOptions options) { }
    public void AppendLiteral(string value) { }
}

public interface IContract : IService
{
    ValueTask<int> [|RunAsync|](
        SharpLinkCallOptions options,
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument("options")] Handler handler);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                [("Annotate with [NonCancellable]", "AddNonCancellable")]),
            $"A handler dependency on CallOptions must suppress only Add CancellationToken. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink019ShouldNotSelectClassPrimaryConstructor()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|](string name)
{
    public string Name { get; } = name;
    public Service() : this(string.Empty) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Select constructor Service()"
               && actions[0].EquivalenceKey == "SelectConstructor:Service.Service()",
            $"Only the ordinary constructor may receive a marker; a class primary constructor has no valid attribute target. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        var constructor = CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>()
            .Single(static item => item.Identifier.ValueText == "Service");
        Ensure(constructor.AttributeLists.SelectMany(static list => list.Attributes)
            .Any(static attribute => attribute.Name.ToString().Contains(
                "ActivatorUtilitiesConstructor", StringComparison.Ordinal)),
            "The ordinary constructor must receive ActivatorUtilitiesConstructor.");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task SealingShouldWithholdForDeclaredProtectedMembers()
    {
        foreach (var accessibility in new[] { "protected", "protected internal", "private protected" })
        {
            using (var dto = CodeFixTestWorkspace.Create(("Payload.cs", $$"""
[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]
{
    {{accessibility}} int Value => 42;
}
""")))
            {
                await dto.AssertCompilesAsync();
                var diagnostic = await dto.CreateDiagnosticAsync(
                    "SHARPLINK009",
                    "Payload.cs",
                    new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });
                var actions = await dto.GetActionsAsync(diagnostic, "Payload.cs");
                Ensure(actions.Count == 0,
                    $"DTO sealing must be hidden for a declared non-override {accessibility} member.");
            }

            using var adapter = CodeFixTestWorkspace.Create(("Adapter.cs", $$"""
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

public class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public Adapter() { }
    {{accessibility}} int Value => 42;
}
"""));
            await adapter.AssertCompilesAsync();
            var adapterDiagnostic = await adapter.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");
            var adapterActions = await adapter.GetActionsAsync(adapterDiagnostic, "Adapter.cs");
            Ensure(adapterActions.Count == 0,
                $"Adapter sealing must be hidden for a declared non-override {accessibility} member.");
        }

        using (var ordinaryDto = CodeFixTestWorkspace.Create(("Payload.cs", """
[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]
{
    public int Value => 42;
}
""")))
        {
            await ordinaryDto.AssertCompilesAsync();
            var diagnostic = await ordinaryDto.CreateDiagnosticAsync(
                "SHARPLINK009",
                "Payload.cs",
                new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });
            var actions = await ordinaryDto.GetActionsAsync(diagnostic, "Payload.cs");
            Ensure(actions.Count == 1
                   && actions[0].Title == "Seal DTO for generated Codec"
                   && actions[0].EquivalenceKey == "SealDto",
                "An ordinary source DTO must retain its sealing action.");
            var changed = await ordinaryDto.ApplyAsync(actions[0]);
            await ordinaryDto.AssertCompilesAsync(changed);
        }

        using var ordinaryAdapter = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

public class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public Adapter() { }
    public int Value => 42;
}
"""));
        await ordinaryAdapter.AssertCompilesAsync();
        var ordinaryAdapterDiagnostic = await ordinaryAdapter.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");
        var ordinaryAdapterActions = await ordinaryAdapter.GetActionsAsync(
            ordinaryAdapterDiagnostic,
            "Adapter.cs");
        Ensure(ordinaryAdapterActions.Count == 1
               && ordinaryAdapterActions[0].Title == "Fix Adapter Codec adapter shape"
               && ordinaryAdapterActions[0].EquivalenceKey == "FixAdapterShape",
            "An ordinary source adapter must retain its shape action.");
        var ordinaryAdapterChanged = await ordinaryAdapter.ApplyAsync(ordinaryAdapterActions[0]);
        await ordinaryAdapter.AssertCompilesAsync(ordinaryAdapterChanged);
    }

    [Test]
    public async Task Sharplink031ShouldRemovePropertyTargetedRpcRequiredFromPositionalRecord()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", """
[SharpLink.Sdk.RpcSerializable]
public sealed record Payload(
    [property: SharpLink.Sdk.RpcRequired] int [|Value|]);
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK031",
            "Payload.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "RemoveRpcRequired" });

        var actions = await workspace.GetActionsAsync(diagnostic, "Payload.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Remove [RpcRequired]"
               && actions[0].EquivalenceKey == "RemoveRpcRequired",
            "A property-targeted RpcRequired on a positional record must retain its removal action.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Payload.cs", changed);
        EnsureDoesNotContain(source, "RpcRequired", "positional record property");
        EnsureContains(source, "record Payload(int Value)", "positional record declaration");
        await workspace.AssertCompilesAsync(changed);
    }
}
