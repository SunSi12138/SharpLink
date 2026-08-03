using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ProviderAndSignatureCodeFixTests
{
    [Test]
    public Task FixableDiagnosticIdsShouldExactlyMatchIssue52AllowList()
    {
        var expected = new[]
        {
            "SHARPLINK002", "SHARPLINK004", "SHARPLINK006", "SHARPLINK007", "SHARPLINK008",
            "SHARPLINK009", "SHARPLINK014", "SHARPLINK015", "SHARPLINK016", "SHARPLINK018",
            "SHARPLINK019", "SHARPLINK020", "SHARPLINK028", "SHARPLINK031", "SHARPLINK032",
            "SHARPLINK033", "SHARPLINK037", "SHARPLINK043", "SHARPLINK049", "SHARPLINK050",
            "SHARPLINK051", "SHARPLINK053", "SHARPLINK055", "SHARPLINK056"
        };

        var actual = CreateProvider().FixableDiagnosticIds;

        Ensure(actual.SequenceEqual(expected, StringComparer.Ordinal),
            $"Fixable IDs must exactly match issue #52. Actual: {string.Join(", ", actual)}");
        Ensure(actual.Distinct(StringComparer.Ordinal).Count() == actual.Length,
            "Fixable IDs must not contain duplicates.");
        return Task.CompletedTask;
    }

    [Test]
    public async Task LocationNoneDiagnosticsShouldRegisterNoActions()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", "public interface IContract { }"));
        var diagnostic = CreateLocationNoneDiagnostic("SHARPLINK028");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 0, "Location.None diagnostics must not advertise a source fix.");
    }

    [Test]
    public async Task Sharplink004ShouldOfferOrderedStableActionsAndUpdateWholeSolution()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IContract : IService
{
    ValueTask<int> [|RunAsync|](int cancellationToken, int cancellationToken1, SharpLinkCallOptions options);
}
"""),
            ("Implementation.cs", """
using System.Threading.Tasks;
using SharpLink.Sdk;

public sealed class Contract : IContract
{
    public ValueTask<int> RunAsync(int cancellationToken, int cancellationToken1, SharpLinkCallOptions options)
        => ValueTask.FromResult(cancellationToken + cancellationToken1);
}
"""),
            ("Caller.cs", """
using System.Threading.Tasks;
using SharpLink.Sdk;

public static class Caller
{
    public static ValueTask<int> CallAsync(IContract contract)
        => contract.RunAsync(20, 22, default);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static item => item.Title).SequenceEqual(
                ["Add CancellationToken", "Annotate with [NonCancellable]"],
                StringComparer.Ordinal),
            $"Cancellation actions must have the required order. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        Ensure(actions[0].EquivalenceKey == "Signature:AddCancellationToken",
            "Add CancellationToken must have a stable signature equivalence key.");
        Ensure(actions[1].EquivalenceKey == "AddNonCancellable",
            "NonCancellable must have a stable equivalence key distinct from the signature action.");

        var changed = await workspace.ApplyAsync(actions[0]);
        var contract = await workspace.GetTextAsync("Contract.cs", changed);
        var implementation = await workspace.GetTextAsync("Implementation.cs", changed);
        var caller = await workspace.GetTextAsync("Caller.cs", changed);
        EnsureContains(contract,
            "RunAsync(int cancellationToken, int cancellationToken1, SharpLinkCallOptions options, global::System.Threading.CancellationToken cancellationToken2)",
            "contract declaration");
        EnsureContains(implementation,
            "RunAsync(int cancellationToken, int cancellationToken1, SharpLinkCallOptions options, global::System.Threading.CancellationToken cancellationToken2)",
            "implementation declaration");
        EnsureContains(caller,
            "RunAsync(cancellationToken: 20, cancellationToken1: 22, options: default, cancellationToken2: global::System.Threading.CancellationToken.None)",
            "evaluation-order-preserving call site");
        await workspace.AssertCompilesAsync(changed);

        var annotated = await GetChangedSolutionAsync(actions[1]);
        var annotatedContract = await workspace.GetTextAsync("Contract.cs", annotated);
        EnsureContains(annotatedContract,
            "[global::SharpLink.Sdk.NonCancellable]",
            "alternative cancellation contract");
        EnsureDoesNotContain(annotatedContract,
            "System.Threading.CancellationToken cancellationToken2",
            "alternative cancellation contract");
        await workspace.AssertCompilesAsync(annotated);
    }

    [Test]
    public async Task Sharplink004ShouldUpdateExplicitInterfaceImplementationAndInterfaceCall()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value);
}
"""),
            ("Implementation.cs", """
using System.Threading.Tasks;

public sealed class Contract : IContract
{
    ValueTask<int> IContract.RunAsync(int value) => ValueTask.FromResult(value);
}
"""),
            ("Caller.cs", """
using System.Threading.Tasks;

public static class Caller
{
    public static ValueTask<int> CallAsync(IContract contract) => contract.RunAsync(42);
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 2,
            "A source explicit implementation and ordinary interface invocation must be safely rewritable.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var implementation = await workspace.GetTextAsync("Implementation.cs", changed);
        var caller = await workspace.GetTextAsync("Caller.cs", changed);
        EnsureContains(implementation,
            "IContract.RunAsync(int value, global::System.Threading.CancellationToken cancellationToken)",
            "explicit interface implementation");
        EnsureContains(caller,
            "RunAsync(value: 42, cancellationToken: global::System.Threading.CancellationToken.None)",
            "interface call site");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink002ShouldOfferOneKeepActionPerTokenAndUpdateDeclarationsAndCalls()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IContract : IService
{
    ValueTask<int> [|RunAsync|](int value, CancellationToken firstToken, CancellationToken secondToken);
}
"""),
            ("Implementation.cs", """
using System.Threading;
using System.Threading.Tasks;

public sealed class Contract : IContract
{
    public ValueTask<int> RunAsync(int value, CancellationToken firstToken, CancellationToken secondToken)
        => ValueTask.FromResult(value);
}
"""),
            ("Caller.cs", """
using System.Threading;
using System.Threading.Tasks;

public static class Caller
{
    public static ValueTask<int> CallAsync(IContract contract)
        => contract.RunAsync(42, CancellationToken.None, CancellationToken.None);
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK002", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static item => item.Title).SequenceEqual(
                ["Keep CancellationToken 'firstToken'", "Keep CancellationToken 'secondToken'"],
                StringComparer.Ordinal),
            $"SHARPLINK002 must offer one action per declared token. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        Ensure(actions.Select(static item => item.EquivalenceKey).SequenceEqual(
                ["Signature:Keep:CancellationToken:1", "Signature:Keep:CancellationToken:2"],
                StringComparer.Ordinal),
            "Keep-token actions must have stable, distinct ordinal equivalence keys.");

        var changed = await workspace.ApplyAsync(actions[0]);
        var contract = await workspace.GetTextAsync("Contract.cs", changed);
        var implementation = await workspace.GetTextAsync("Implementation.cs", changed);
        var caller = await workspace.GetTextAsync("Caller.cs", changed);
        EnsureContains(contract, "RunAsync(int value, CancellationToken firstToken)", "contract declaration");
        EnsureContains(implementation, "RunAsync(int value, CancellationToken firstToken)", "implementation declaration");
        EnsureContains(caller, "RunAsync(42, CancellationToken.None)", "call site");
        EnsureDoesNotContain(contract, "secondToken", "contract declaration");
        EnsureDoesNotContain(implementation, "secondToken", "implementation declaration");
        await workspace.AssertCompilesAsync(changed);

        var keepSecond = await GetChangedSolutionAsync(actions[1]);
        var secondContract = await workspace.GetTextAsync("Contract.cs", keepSecond);
        var secondImplementation = await workspace.GetTextAsync("Implementation.cs", keepSecond);
        EnsureContains(secondContract,
            "RunAsync(int value, CancellationToken secondToken)",
            "second keep-token contract declaration");
        EnsureContains(secondImplementation,
            "RunAsync(int value, CancellationToken secondToken)",
            "second keep-token implementation declaration");
        EnsureDoesNotContain(secondContract, "firstToken", "second keep-token contract declaration");
        await workspace.AssertCompilesAsync(keepSecond);
    }

    [Test]
    public async Task Sharplink007ShouldOfferOneKeepActionPerCallOptionsAndPreserveTokenOrdering()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IContract : IService
{
    ValueTask<int> [|RunAsync|](SharpLinkCallOptions firstOptions, int value, SharpLinkCallOptions secondOptions, CancellationToken token);
}
"""),
            ("Implementation.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public sealed class Contract : IContract
{
    public ValueTask<int> RunAsync(SharpLinkCallOptions firstOptions, int value, SharpLinkCallOptions secondOptions, CancellationToken token)
        => ValueTask.FromResult(value);
}
"""),
            ("Caller.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public static class Caller
{
    public static ValueTask<int> CallAsync(IContract contract)
        => contract.RunAsync(default, 42, default, CancellationToken.None);
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK007", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static item => item.Title).SequenceEqual(
                ["Keep SharpLinkCallOptions 'firstOptions'", "Keep SharpLinkCallOptions 'secondOptions'"],
                StringComparer.Ordinal),
            $"SHARPLINK007 must offer one action per options parameter. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        Ensure(actions.Select(static item => item.EquivalenceKey).Distinct(StringComparer.Ordinal).Count() == 2,
            "Keep-options actions must have distinct equivalence keys.");

        var changed = await workspace.ApplyAsync(actions[0]);
        var contract = await workspace.GetTextAsync("Contract.cs", changed);
        var implementation = await workspace.GetTextAsync("Implementation.cs", changed);
        var caller = await workspace.GetTextAsync("Caller.cs", changed);
        EnsureContains(contract,
            "RunAsync(SharpLinkCallOptions firstOptions, int value, CancellationToken token)",
            "contract declaration");
        EnsureContains(implementation,
            "RunAsync(SharpLinkCallOptions firstOptions, int value, CancellationToken token)",
            "implementation declaration");
        EnsureContains(caller, "RunAsync(default, 42, CancellationToken.None)", "call site");
        EnsureDoesNotContain(contract, "secondOptions", "contract declaration");
        await workspace.AssertCompilesAsync(changed);

        var keepSecond = await GetChangedSolutionAsync(actions[1]);
        var secondContract = await workspace.GetTextAsync("Contract.cs", keepSecond);
        var secondImplementation = await workspace.GetTextAsync("Implementation.cs", keepSecond);
        EnsureContains(secondContract,
            "RunAsync(int value, SharpLinkCallOptions secondOptions, CancellationToken token)",
            "second keep-options contract declaration");
        EnsureContains(secondImplementation,
            "RunAsync(int value, SharpLinkCallOptions secondOptions, CancellationToken token)",
            "second keep-options implementation declaration");
        EnsureDoesNotContain(secondContract, "firstOptions", "second keep-options contract declaration");
        await workspace.AssertCompilesAsync(keepSecond);
    }

    [Test]
    public async Task Sharplink008ShouldReorderDeclarationsAndPositionalCalls()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IContract : IService
{
    ValueTask<int> [|RunAsync|](CancellationToken token, int value, SharpLinkCallOptions options, string name);
}
"""),
            ("Implementation.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public sealed class Contract : IContract
{
    public ValueTask<int> RunAsync(CancellationToken token, int value, SharpLinkCallOptions options, string name)
        => ValueTask.FromResult(value + name.Length);
}
"""),
            ("Caller.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public static class Caller
{
    public static ValueTask<int> CallAsync(IContract contract)
        => contract.RunAsync(CancellationToken.None, 40, default, "ok");
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Reorder RPC control parameters",
            "SHARPLINK008 must expose one deterministic reorder action.");
        Ensure(actions[0].EquivalenceKey == "Signature:ReorderControlParameters",
            "The reorder action must have a stable signature equivalence key.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var contract = await workspace.GetTextAsync("Contract.cs", changed);
        var implementation = await workspace.GetTextAsync("Implementation.cs", changed);
        var caller = await workspace.GetTextAsync("Caller.cs", changed);
        EnsureContains(contract,
            "RunAsync(int value, string name, SharpLinkCallOptions options, CancellationToken token)",
            "contract declaration");
        EnsureContains(implementation,
            "RunAsync(int value, string name, SharpLinkCallOptions options, CancellationToken token)",
            "implementation declaration");
        EnsureContains(caller,
            "RunAsync(token: CancellationToken.None, value: 40, options: default, name: \"ok\")",
            "evaluation-order-preserving call site");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink014StreamingMethodShouldOfferOrderedCancellationContractActions()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Collections.Generic;
using SharpLink.Sdk;

public interface IContract : IService
{
    IAsyncEnumerable<int> [|StreamAsync|](SharpLinkCallOptions options);
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK014", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static item => item.Title).SequenceEqual(
                ["Add CancellationToken", "Annotate with [NonCancellable]"],
                StringComparer.Ordinal),
            "SHARPLINK014 must expose Add CancellationToken before NonCancellable.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Contract.cs", changed);
        EnsureContains(source,
            "StreamAsync(SharpLinkCallOptions options, global::System.Threading.CancellationToken cancellationToken)",
            "streaming contract");
        await workspace.AssertCompilesAsync(changed);
    }
}
