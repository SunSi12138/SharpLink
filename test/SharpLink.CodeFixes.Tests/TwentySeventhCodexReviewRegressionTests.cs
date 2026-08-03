using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentySeventhCodexReviewRegressionTests
{
    [Test]
    public async Task KeepParameterShouldRejectUserDefinedConversions()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;

public readonly struct TokenWrapper
{
    public static explicit operator CancellationToken(TokenWrapper value) => default;
}

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken first, CancellationToken second);
}

public static class Caller
{
    public static int Call(IContract contract, TokenWrapper wrapper, CancellationToken token) =>
        contract.Run((CancellationToken)wrapper, token);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK002", "Contract.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static action => action.EquivalenceKey).SequenceEqual(
                ["Signature:Keep:CancellationToken:0"], StringComparer.Ordinal),
            "Dropping an operator-backed conversion must be withheld.");
    }

    [Test]
    public async Task SealDtoShouldSupportClosedGenericPayloadUses()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", """
[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]<T>
{
    public T Value { get; set; } = default!;
}

public interface IContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    Payload<int> Get();
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK009", "Payload.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });
        var action = (await workspace.GetActionsAsync(diagnostic, "Payload.cs"))
            .Single(static item => item.EquivalenceKey == "SealDto");
        var changed = await workspace.ApplyAsync(action);

        EnsureContains(await workspace.GetTextAsync("Payload.cs", changed),
            "public sealed class Payload<T>", "closed generic DTO definition");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task SealDtoShouldSupportNestedClosedGenericPayloadUses()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", """
public sealed class Outer<T>
{
    [SharpLink.Sdk.RpcSerializable]
    public class [|Payload|]
    {
        public T Value { get; set; } = default!;
    }
}

public interface IContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    Outer<int>.Payload Get();
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK009", "Payload.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });
        var action = (await workspace.GetActionsAsync(diagnostic, "Payload.cs"))
            .Single(static item => item.EquivalenceKey == "SealDto");
        await workspace.AssertCompilesAsync(await workspace.ApplyAsync(action));
    }
}
