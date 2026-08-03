using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ThirdCodexReviewRegressionTests
{
    [Test]
    public async Task Sharplink009ShouldWithholdSealingForNewVirtualMembers()
    {
        var scenarios = new[]
        {
            (Name: "method", Member: "public virtual int Compute() => 42;"),
            (Name: "property", Member: "public virtual int Value { get; } = 42;")
        };

        foreach (var scenario in scenarios)
        {
            var source = $$"""
[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]
{
    {{scenario.Member}}
}
""";
            using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(
                "SHARPLINK009",
                "Payload.cs",
                new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });

            var actions = await workspace.GetActionsAsync(diagnostic, "Payload.cs");

            Ensure(actions.Count == 0,
                $"Sealing a DTO that declares a new virtual {scenario.Name} would make the source invalid.");
        }
    }

    [Test]
    public async Task KeepControlParameterShouldPreserveNestedRpcInvocationArgumentMappings()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken firstToken, int value, CancellationToken secondToken);
}
"""),
            ("Implementation.cs", """
using System.Threading;

public sealed class Contract : IContract
{
    public int Run(CancellationToken firstToken, int value, CancellationToken secondToken) => value;
}
"""),
            ("Caller.cs", """
using System.Threading;

public static class Caller
{
    public static int Call(
        IContract contract,
        CancellationToken first,
        CancellationToken second,
        CancellationToken third,
        CancellationToken fourth)
        => contract.Run(first, contract.Run(second, 42, third), fourth);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK002", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static item => (item.Title, item.EquivalenceKey)).SequenceEqual(
                [
                    ("Keep CancellationToken 'firstToken'", "Signature:Keep:CancellationToken:0"),
                    ("Keep CancellationToken 'secondToken'", "Signature:Keep:CancellationToken:2")
                ]),
            "Nested calls must retain the two deterministic keep-token choices.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var contract = await workspace.GetTextAsync("Contract.cs", changed);
        var implementation = await workspace.GetTextAsync("Implementation.cs", changed);
        var caller = await workspace.GetTextAsync("Caller.cs", changed);
        EnsureContains(contract, "Run(CancellationToken firstToken, int value)", "contract declaration");
        EnsureContains(implementation, "Run(CancellationToken firstToken, int value)", "implementation declaration");
        EnsureContains(caller,
            "contract.Run(first, contract.Run(second, 42))",
            "nested edited RPC invocations");
        EnsureDoesNotContain(caller,
            "contract.Run(first, contract.Run(second, 42, third), fourth)",
            "nested edited RPC invocations");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task PublicReachabilityFixesShouldPublicizeInternalSourceBaseTypes()
    {
        using (var contract = CodeFixTestWorkspace.Create(("Contract.cs", """
internal interface IBaseContract { }

[SharpLink.Sdk.RpcContract]
internal interface [|IContract|] : IBaseContract, SharpLink.Sdk.IService { }
""")))
        {
            await contract.AssertCompilesAsync();
            var diagnostic = await contract.CreateDiagnosticAsync("SHARPLINK055", "Contract.cs");

            var actions = await contract.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Count == 1
                   && actions[0].Title == "Make RPC contract publicly reachable"
                   && actions[0].EquivalenceKey == "MakeContractPublic",
                "SHARPLINK055 may repair reachability only by publicizing the source base interface too.");
            var changed = await contract.ApplyAsync(actions[0]);
            var source = await contract.GetTextAsync("Contract.cs", changed);
            EnsureContains(source, "public interface IBaseContract", "contract base interface");
            EnsureContains(source, "public interface IContract", "RPC contract");
            await contract.AssertCompilesAsync(changed);
        }

        using var service = CodeFixTestWorkspace.Create(("Service.cs", """
internal class BaseService
{
    public BaseService() { }
}

[SharpLink.Sdk.RpcService]
internal sealed class [|Service|] : BaseService
{
    public Service() { }
}
"""));
        await service.AssertCompilesAsync();
        var serviceDiagnostic = await service.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

        var serviceActions = await service.GetActionsAsync(serviceDiagnostic, "Service.cs");

        Ensure(serviceActions.Count == 1
               && serviceActions[0].Title == "Make RPC service publicly reachable"
               && serviceActions[0].EquivalenceKey == "MakeServicePublic",
            "SHARPLINK018 may repair reachability only by publicizing the source base class too.");
        var serviceChanged = await service.ApplyAsync(serviceActions[0]);
        var serviceSource = await service.GetTextAsync("Service.cs", serviceChanged);
        EnsureContains(serviceSource, "public class BaseService", "service base class");
        EnsureContains(serviceSource, "public sealed class Service", "RPC service");
        await service.AssertCompilesAsync(serviceChanged);
    }

    [Test]
    public async Task LegacyAbstractionsPolicyAttributesShouldBeRemoved()
    {
        using (var nonCancellable = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;
using System.Threading.Tasks;

public interface IContract
{
    [System.Obsolete, SharpLink.Abstractions.NonCancellable]
    ValueTask<int> [|RunAsync|](CancellationToken cancellationToken);
}
""")))
        {
            await AssertLegacyRemovalAsync(
                nonCancellable,
                "SHARPLINK015",
                "Remove [NonCancellable]",
                "RemoveNonCancellable",
                "NonCancellable");
        }

        using (var oneway = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

public interface IContract
{
    [System.Obsolete, SharpLink.Abstractions.Oneway]
    ValueTask<int> [|RunAsync|]();
}
""")))
        {
            await AssertLegacyRemovalAsync(
                oneway,
                "SHARPLINK056",
                "Remove [Oneway]",
                "RemoveOneway",
                "Oneway");
        }

        using var timeout = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

public interface IContract
{
    [System.Obsolete, [|SharpLink.Abstractions.Timeout|](-1)]
    ValueTask<int> RunAsync();
}
"""));
        await timeout.AssertCompilesAsync();
        var timeoutDiagnostic = await timeout.CreateDiagnosticAsync("SHARPLINK050", "Contract.cs");
        var timeoutActions = await timeout.GetActionsAsync(timeoutDiagnostic, "Contract.cs");
        Ensure(timeoutActions.Select(static item => (item.Title, item.EquivalenceKey)).SequenceEqual(
                [
                    ("Use generated default timeout", "UseDefaultTimeout"),
                    ("Remove [Timeout]", "RemoveTimeout")
                ]),
            "Legacy Timeout must retain the two deterministic repair choices.");
        var timeoutChanged = await timeout.ApplyAsync(
            timeoutActions.Single(static item => item.EquivalenceKey == "RemoveTimeout"));
        var timeoutSource = await timeout.GetTextAsync("Contract.cs", timeoutChanged);
        EnsureDoesNotContain(timeoutSource, "Timeout", "legacy timeout method");
        EnsureContains(timeoutSource, "System.Obsolete", "legacy timeout method");
        await timeout.AssertCompilesAsync(timeoutChanged);
    }

    [Test]
    public async Task Sharplink019ShouldSelectSoleSupportedPublicConstructor()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service(ref int value) { }
    public Service(string value) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Select constructor Service(string)"
               && actions[0].EquivalenceKey == "SelectConstructor:Service.Service(string)",
            $"Exactly the sole supported public constructor must be selectable. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        var constructors = GetServiceConstructors(source);
        var unsupported = constructors.Single(static constructor =>
            constructor.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.RefKeyword));
        var supported = constructors.Single(static constructor =>
            !constructor.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.RefKeyword));
        Ensure(unsupported.AttributeLists.Count == 0,
            "The unsupported ref constructor must remain unselected.");
        Ensure(supported.AttributeLists.SelectMany(static list => list.Attributes)
            .Any(static attribute => attribute.Name.ToString().Contains(
                "ActivatorUtilitiesConstructor", StringComparison.Ordinal)),
            "The sole supported public constructor must receive ActivatorUtilitiesConstructor.");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RecordServiceShouldSupportSharplink018ShapeRepair()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
internal abstract record class [|Service|]
{
    public int Value => 42;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Make RPC service concrete and publicly reachable"
               && actions[0].EquivalenceKey == "MakeServiceConcreteAndPublic",
            $"An otherwise-concrete inaccessible abstract record service needs the combined shape repair. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        EnsureContains(source, "public record class Service", "record service declaration");
        EnsureDoesNotContain(source, "abstract record class Service", "record service declaration");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RecordServiceShouldSupportSharplink019ConstructorRepair()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed record class [|Service|]
{
    private Service(int value) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Make Service constructor public"
               && actions[0].EquivalenceKey == "MakeConstructorPublic",
            "A record service with one supported non-public constructor must be repairable.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        EnsureContains(source, "public Service(int value)", "record service constructor");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RecordServiceShouldSupportSharplink020LifetimeRepair()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService(Lifetime = (SharpLink.Sdk.SharpLinkServiceLifetime)99)]
public sealed record class [|Service|]
{
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK020", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Select(static item => item.EquivalenceKey).SequenceEqual(
                ["SetLifetime:Singleton", "SetLifetime:Connection", "SetLifetime:Call"],
                StringComparer.Ordinal),
            "A record service must receive the same three explicit lifetime repairs as a class service.");
        var changed = await workspace.ApplyAsync(
            actions.Single(static item => item.EquivalenceKey == "SetLifetime:Call"));
        var source = await workspace.GetTextAsync("Service.cs", changed);
        EnsureContains(source,
            "RpcService(Lifetime = global::SharpLink.Sdk.SharpLinkServiceLifetime.Call)",
            "record service attribute");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task NonCancellableShouldApplyAcrossInheritedSourceInterfaceDeclarations()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Base.cs", """
using System.Threading.Tasks;

public interface IBaseContract : SharpLink.Sdk.IService
{
    ValueTask<int> RunAsync(int value);
}
"""),
            ("Derived.cs", """
using System.Threading.Tasks;

public interface IDerivedContract : IBaseContract
{
    new ValueTask<int> [|RunAsync|](int value);
}
"""),
            ("Implementation.cs", """
using System.Threading.Tasks;

public sealed class Contract : IDerivedContract
{
    public ValueTask<int> RunAsync(int value) => ValueTask.FromResult(value);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Derived.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Derived.cs");
        var nonCancellable = actions.Single(static item => item.EquivalenceKey == "AddNonCancellable");

        var changed = await workspace.ApplyAsync(nonCancellable);

        var baseContract = await workspace.GetTextAsync("Base.cs", changed);
        var derivedContract = await workspace.GetTextAsync("Derived.cs", changed);
        EnsureContains(baseContract,
            "[global::SharpLink.Sdk.NonCancellable]",
            "base interface method policy");
        EnsureContains(derivedContract,
            "[global::SharpLink.Sdk.NonCancellable]",
            "derived interface method policy");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task MethodGroupInsideUnrelatedInvocationShouldSuppressSignatureEdit()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value);
}

public static class Registration
{
    public static void Configure(IContract contract) => Register(contract.RunAsync);
    private static void Register(Func<int, ValueTask<int>> callback) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static item => (item.Title, item.EquivalenceKey)).SequenceEqual(
                [("Annotate with [NonCancellable]", "AddNonCancellable")]),
            $"The nested method-group use must suppress only the signature edit. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Contract.cs", changed);
        EnsureContains(source, "Register(contract.RunAsync)", "unrelated registration invocation");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink019ShouldWithholdPublicConstructorWithLessAccessibleParameterType()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
internal sealed class Dependency { }

[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    private Service(Dependency dependency) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 0,
            "A public constructor cannot expose an internal parameter type in its signature.");
    }

    private static async Task AssertLegacyRemovalAsync(
        CodeFixTestWorkspace workspace,
        string diagnosticId,
        string expectedTitle,
        string expectedKey,
        string removedAttribute)
    {
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(diagnosticId, "Contract.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");
        Ensure(actions.Count == 1
               && actions[0].Title == expectedTitle
               && actions[0].EquivalenceKey == expectedKey,
            $"{diagnosticId} must expose the deterministic legacy-attribute removal action.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Contract.cs", changed);
        EnsureDoesNotContain(source, removedAttribute, $"legacy {removedAttribute} method");
        EnsureContains(source, "System.Obsolete", $"legacy {removedAttribute} method");
        await workspace.AssertCompilesAsync(changed);
    }

    private static Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax[] GetServiceConstructors(
        string source)
        => CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>()
            .Where(static constructor => constructor.Identifier.ValueText == "Service")
            .ToArray();
}
