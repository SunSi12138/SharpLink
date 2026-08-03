using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentyEighthCodexReviewRegressionTests
{
    [Test]
    public async Task MakeDtoAccessibleShouldValidateNestedClosedGenericUses()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", """
internal sealed class Outer<T>
{
    [SharpLink.Sdk.RpcSerializable]
    internal sealed class [|Payload|]
    {
        public T Value { get; set; } = default!;
    }
}

internal interface IContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    Outer<int>.Payload Get();
}

internal sealed class GenericConsumer<T>
{
    public Outer<T>.Payload Echo(Outer<T>.Payload value) => value;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK009",
            "Payload.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "MakeDtoAccessible" });
        var action = (await workspace.GetActionsAsync(diagnostic, "Payload.cs"))
            .Single(static item => item.EquivalenceKey == "MakeDtoAccessible");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Payload.cs", changed);

        EnsureContains(source, "public sealed class Outer<T>", "generic DTO container");
        EnsureContains(source, "public sealed class Payload", "nested DTO");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RestoreUnionTagShouldRejectAnotherCaseUsingPublishedTag()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Union.cs", """
public sealed class OldCase : IResult { }
public sealed class NewCase : IResult { }
public sealed class OtherCase : IResult { }

[[|SharpLink.Sdk.RpcUnionCase|](1, typeof(NewCase))]
[SharpLink.Sdk.RpcUnionCase(1, typeof(OtherCase))]
public interface IResult { }
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK033",
            "Union.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousUnionTag"] = "1",
                ["SharpLink.PreviousUnionType"] = "OldCase"
            });
        var actions = await workspace.GetActionsAsync(diagnostic, "Union.cs");

        Ensure(actions.All(static item => item.EquivalenceKey != "RestoreUnionTag"),
            "Restoring a tag must not leave another union case mapped to that tag.");
    }

    [Test]
    public async Task SignatureCollisionShouldCompareConstructedContainingTypes()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;

public sealed class Outer<T>
{
    public sealed class Nested { }
}

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](Outer<string>.Nested value);
    int Run(Outer<int>.Nested value, CancellationToken cancellationToken);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
        var action = (await workspace.GetActionsAsync(diagnostic, "Contract.cs"))
            .Single(static item => item.EquivalenceKey == "Signature:AddCancellationToken");

        await workspace.AssertCompilesAsync(await workspace.ApplyAsync(action));
    }
}
