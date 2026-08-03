using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class FifthCodexReviewRegressionTests
{
    [Test]
    public async Task Sharplink008ShouldPreserveSideEffectArgumentEvaluationOrder()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading;
using SharpLink.Sdk;

public interface IContract : IService
{
    int [|Run|](CancellationToken cancellationToken, int value, SharpLinkCallOptions options);
}
"""),
            ("Implementation.cs", """
using System.Threading;
using SharpLink.Sdk;

public sealed class Contract : IContract
{
    public int Run(CancellationToken cancellationToken, int value, SharpLinkCallOptions options) => value;
}
"""),
            ("Caller.cs", """
using System.Threading;
using SharpLink.Sdk;

public static class Caller
{
    private static int _sequence;

    public static int Call(IContract contract)
        => contract.Run(GetToken(), GetValue(), GetOptions());

    private static CancellationToken GetToken() { _sequence++; return default; }
    private static int GetValue() { _sequence++; return 42; }
    private static SharpLinkCallOptions GetOptions() { _sequence++; return default; }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Reorder RPC control parameters"
               && actions[0].EquivalenceKey == "Signature:ReorderControlParameters",
            "The side-effecting invocation must retain one safe reorder action.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var caller = await workspace.GetTextAsync("Caller.cs", changed);
        var arguments = GetInvocationArguments(caller, "Run");
        Ensure(arguments.Select(static argument => argument.Expression.ToString()).SequenceEqual(
                ["GetToken()", "GetValue()", "GetOptions()"],
                StringComparer.Ordinal),
            $"Argument evaluation order must remain token, value, options. Actual: {string.Join(", ", arguments.Select(static argument => argument.Expression))}");
        Ensure(arguments.Select(static argument => argument.NameColon?.Name.Identifier.ValueText).SequenceEqual(
                ["cancellationToken", "value", "options"],
                StringComparer.Ordinal),
            $"Preserved lexical arguments must be named to retain binding. Actual call: {GetInvocation(caller, "Run")}");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task AddCancellationTokenShouldPreserveNamedSideEffectArgumentOrder()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int first, int second);
}
"""),
            ("Implementation.cs", """
using System.Threading.Tasks;

public sealed class Contract : IContract
{
    public ValueTask<int> RunAsync(int first, int second) => ValueTask.FromResult(first + second);
}
"""),
            ("Caller.cs", """
using System.Threading.Tasks;

public static class Caller
{
    private static int _sequence;

    public static ValueTask<int> CallAsync(IContract contract)
        => contract.RunAsync(second: GetSecond(), first: GetFirst());

    private static int GetSecond() { _sequence++; return 2; }
    private static int GetFirst() { _sequence++; return 1; }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");
        var addToken = actions.Single(static action =>
            action.EquivalenceKey == "Signature:AddCancellationToken");

        var changed = await workspace.ApplyAsync(addToken);

        var caller = await workspace.GetTextAsync("Caller.cs", changed);
        var arguments = GetInvocationArguments(caller, "RunAsync");
        Ensure(arguments.Take(2).Select(static argument => argument.Expression.ToString()).SequenceEqual(
                ["GetSecond()", "GetFirst()"],
                StringComparer.Ordinal),
            $"Existing side effects must retain second-before-first evaluation. Actual: {GetInvocation(caller, "RunAsync")}");
        Ensure(arguments.Select(static argument => argument.NameColon?.Name.Identifier.ValueText).SequenceEqual(
                ["second", "first", "cancellationToken"],
                StringComparer.Ordinal),
            $"All arguments must retain explicit semantic binding after appending the token. Actual: {GetInvocation(caller, "RunAsync")}");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink019ShouldTargetRecordPrimaryConstructorAsMethod()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}

[SharpLink.Sdk.RpcService]
public sealed record class [|Service|](string Name)
{
    public Service() : this(string.Empty) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");
        var selectPrimary = actions.Single(static action =>
            action.Title == "Select constructor Service(string)");
        Ensure(selectPrimary.EquivalenceKey == "SelectConstructor:Service.Service(string)",
            $"The primary constructor selection must retain its stable key. Actual: {selectPrimary.EquivalenceKey}");

        var changed = await workspace.ApplyAsync(selectPrimary);

        var source = await workspace.GetTextAsync("Service.cs", changed);
        var record = CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax>()
            .Single(static declaration => declaration.Identifier.ValueText == "Service");
        var activationAttributes = record.AttributeLists
            .SelectMany(static list => list.Attributes.Select(attribute => (list.Target, Attribute: attribute)))
            .Where(static item => item.Attribute.Name.ToString().Contains(
                "ActivatorUtilitiesConstructor", StringComparison.Ordinal))
            .ToArray();
        Ensure(activationAttributes.Length == 1
               && activationAttributes[0].Target?.Identifier.ValueText == "method",
            $"A record primary constructor attribute must use the method target. Actual source: {source}");
        var ordinaryConstructor = record.Members
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>()
            .Single();
        Ensure(ordinaryConstructor.AttributeLists.Count == 0,
            "Selecting the primary constructor must not annotate the secondary constructor.");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink043ShouldRequireSetsRequiredMembersForParameterlessConstruction()
    {
        var unsafeScenarios = new[]
        {
            (Name: "declared required member", Source: """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public required string Name { get; init; }
    public Adapter() { }
}
"""),
            (Name: "inherited required member", Source: """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

public class AdapterBase
{
    public required string Name { get; init; }
}

internal class Adapter : AdapterBase, SharpLink.Abstractions.IRpcCodecAdapter
{
    public Adapter() { }
}
""")
        };

        foreach (var scenario in unsafeScenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", scenario.Source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

            Ensure(actions.Count == 0,
                $"An adapter with a {scenario.Name} cannot promise safe parameterless construction without SetsRequiredMembers.");
        }

        using var marked = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public required string Name { get; init; }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public Adapter() { Name = string.Empty; }
}
"""));
        await marked.AssertCompilesAsync();
        var markedDiagnostic = await marked.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");
        var markedActions = await marked.GetActionsAsync(markedDiagnostic, "Adapter.cs");
        Ensure(markedActions.Count == 1
               && markedActions[0].Title == "Fix Adapter Codec adapter shape"
               && markedActions[0].EquivalenceKey == "FixAdapterShape",
            "SetsRequiredMembers makes the existing public parameterless constructor safe for adapter repair.");
        var markedChanged = await marked.ApplyAsync(markedActions[0]);
        await marked.AssertCompilesAsync(markedChanged);
    }

    [Test]
    public async Task Sharplink015ShouldRemovePolicyAcrossEditableEquivalentInterfaces()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Base.cs", """
using System.Threading;
using System.Threading.Tasks;

public interface IBaseContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> RunAsync(int value, CancellationToken cancellationToken);
}
"""),
            ("Derived.cs", """
using System.Threading;
using System.Threading.Tasks;

public interface IDerivedContract : IBaseContract
{
    [SharpLink.Sdk.NonCancellable]
    new ValueTask<int> [|RunAsync|](int value, CancellationToken cancellationToken);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK015", "Derived.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Derived.cs");
        Ensure(actions.Count == 1
               && actions[0].Title == "Remove [NonCancellable]"
               && actions[0].EquivalenceKey == "RemoveNonCancellable",
            "Editable equivalent interface policies need one synchronized removal action.");

        var changed = await workspace.ApplyAsync(actions[0]);

        var baseSource = await workspace.GetTextAsync("Base.cs", changed);
        var derivedSource = await workspace.GetTextAsync("Derived.cs", changed);
        EnsureDoesNotContain(baseSource, "NonCancellable", "base interface policy");
        EnsureDoesNotContain(derivedSource, "NonCancellable", "derived interface policy");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink015ShouldWithholdRemovalForMetadataEquivalentInterface()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Derived.cs", """
using System.Threading;
using System.Threading.Tasks;

public interface IDerivedContract : External.IBaseContract
{
    [SharpLink.Sdk.NonCancellable]
    new ValueTask<int> [|RunAsync|](int value, CancellationToken cancellationToken);
}
"""));
        workspace.AddMetadataReferenceFromSource("External.Policy.Contracts", """
namespace SharpLink.Sdk
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class NonCancellableAttribute : System.Attribute { }
}

namespace External
{
    public interface IBaseContract
    {
        [SharpLink.Sdk.NonCancellable]
        System.Threading.Tasks.ValueTask<int> RunAsync(
            int value,
            System.Threading.CancellationToken cancellationToken);
    }
}
""");
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK015", "Derived.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Derived.cs");

        Ensure(actions.Count == 0,
            "Policy removal must be hidden when an equivalent metadata declaration cannot be edited.");
    }

    [Test]
    public async Task AddCancellationTokenShouldUpdateSiblingSourceInterfacesWithoutImplementation()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Base.cs", """
using System.Threading.Tasks;

public interface IBaseContract : SharpLink.Sdk.IService
{
    ValueTask<int> RunAsync(int value);
}
"""),
            ("First.cs", """
using System.Threading.Tasks;

public interface IFirstContract : IBaseContract
{
    new ValueTask<int> [|RunAsync|](int value);
}
"""),
            ("Second.cs", """
using System.Threading.Tasks;

public interface ISecondContract : IBaseContract
{
    new ValueTask<int> RunAsync(int value);
}
"""),
            ("Combined.cs", """
[SharpLink.Sdk.RpcContract]
public interface ICombinedContract : IFirstContract, ISecondContract, SharpLink.Sdk.IService
{
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "First.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "First.cs");
        var addToken = actions.Single(static action =>
            action.EquivalenceKey == "Signature:AddCancellationToken");

        var changed = await workspace.ApplyAsync(addToken);

        foreach (var documentName in new[] { "Base.cs", "First.cs", "Second.cs" })
        {
            var source = await workspace.GetTextAsync(documentName, changed);
            EnsureContains(source,
                "RunAsync(int value, global::System.Threading.CancellationToken cancellationToken)",
                documentName + " equivalent interface method");
        }
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task PublicizationShouldIncludeConstructedContainingTypeArguments()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
internal sealed class InternalPayload { }

internal class Outer<T>
{
    public sealed class Nested { }
}

[SharpLink.Sdk.RpcContract]
internal interface [|IContract|] : SharpLink.Sdk.IService
{
    Outer<InternalPayload>.Nested Get();
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK055", "Contract.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");
        Ensure(actions.Count <= 1,
            "Constructed containing-type publicization must expose at most one deterministic action.");
        if (actions.Count == 0)
            return;

        Ensure(actions[0].Title == "Make RPC contract publicly reachable"
               && actions[0].EquivalenceKey == "MakeContractPublic",
            "The safe publicization action must retain its stable identity.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Contract.cs", changed);
        EnsureContains(source, "public sealed class InternalPayload", "constructed containing type argument");
        EnsureContains(source, "public class Outer<T>", "generic containing type");
        EnsureContains(source, "public interface IContract", "RPC contract");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task KeepControlParameterShouldRespectInterpolatedStringHandlerArgumentReferences()
    {
        var scenarios = new[]
        {
            (
                DiagnosticId: "SHARPLINK002",
                DisplayName: "CancellationToken",
                Kind: "CancellationToken",
                ParameterType: "System.Threading.CancellationToken",
                KeptName: "cancellationToken"),
            (
                DiagnosticId: "SHARPLINK007",
                DisplayName: "SharpLinkCallOptions",
                Kind: "CallOptions",
                ParameterType: "SharpLink.Sdk.SharpLinkCallOptions",
                KeptName: "options")
        };

        foreach (var scenario in scenarios)
        {
            var source = $$"""
[System.Runtime.CompilerServices.InterpolatedStringHandler]
public ref struct Handler
{
    public Handler(int literalLength, int formattedCount, {{scenario.ParameterType}} {{scenario.KeptName}}) { }
    public void AppendLiteral(string value) { }
    public void AppendFormatted<T>(T value) { }
}

public interface IContract
{
    void [|Run|](
        {{scenario.ParameterType}} {{scenario.KeptName}},
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument("{{scenario.KeptName}}")] Handler handler,
        {{scenario.ParameterType}} duplicate);
}
""";
            using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(scenario.DiagnosticId, "Contract.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Count == 1
                   && actions[0].Title == $"Keep {scenario.DisplayName} '{scenario.KeptName}'"
                   && actions[0].EquivalenceKey == $"Signature:Keep:{scenario.Kind}:0",
                $"Only the control parameter referenced by InterpolatedStringHandlerArgument is safe to keep for {scenario.DiagnosticId}.");
            var changed = await workspace.ApplyAsync(actions[0]);
            var changedSource = await workspace.GetTextAsync("Contract.cs", changed);
            EnsureDoesNotContain(changedSource, scenario.ParameterType + " duplicate", scenario.DiagnosticId + " method");
            EnsureContains(changedSource,
                $"InterpolatedStringHandlerArgument(\"{scenario.KeptName}\")",
                scenario.DiagnosticId + " handler binding");
            await workspace.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task Sharplink043ShouldWithholdForClassPrimaryConstructor()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter(int value) : SharpLink.Abstractions.IRpcCodecAdapter
{
    public int Value { get; } = value;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.Count == 0,
            "A class primary constructor prevents synthesizing a parameterless constructor without a this(...) initializer.");
    }

    private static Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax GetInvocation(
        string source,
        string methodName)
        => CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax member &&
                                  member.Name.Identifier.ValueText == methodName);

    private static Microsoft.CodeAnalysis.SeparatedSyntaxList<
        Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax> GetInvocationArguments(
        string source,
        string methodName)
        => GetInvocation(source, methodName).ArgumentList.Arguments;
}
