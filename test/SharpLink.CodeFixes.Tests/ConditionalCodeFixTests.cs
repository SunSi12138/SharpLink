using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ConditionalCodeFixTests
{
    [Test]
    public async Task Sharplink009UnsealedDtoWithoutDerivedTypesShouldOfferAddSealed()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Dto.cs", """
[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]
{
    public int Value { get; set; }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK009",
            "Dto.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });

        var actions = await workspace.GetActionsAsync(diagnostic, "Dto.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Seal DTO for generated Codec",
            "A source DTO without derived types should offer Add sealed.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Dto.cs", changed);
        EnsureContains(source, "public sealed class Payload", "DTO declaration");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink009DtoWithSourceDerivedTypeShouldOfferNoSealingAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Dto.cs", """
[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]
{
}

public sealed class DerivedPayload : Payload
{
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK009",
            "Dto.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });

        var actions = await workspace.GetActionsAsync(diagnostic, "Dto.cs");

        Ensure(actions.Count == 0, "Sealing must be absent when a source-derived type would be invalidated.");
    }

    [Test]
    public async Task Sharplink016SingleSourceIServiceInterfaceShouldAnnotateThatInterface()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
public interface IContract : SharpLink.Sdk.IService
{
}
"""),
            ("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract
{
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Annotate IContract with [RpcContract]",
            "Exactly one source IService interface should be selected deterministically.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var contract = await workspace.GetTextAsync("Contract.cs", changed);
        var service = await workspace.GetTextAsync("Service.cs", changed);
        EnsureContains(contract, "[global::SharpLink.Sdk.RpcContract]", "contract interface");
        EnsureDoesNotContain(service, "RpcContract", "service implementation");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink016MultipleSourceCandidatesShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
public interface IFirst : SharpLink.Sdk.IService { }
public interface ISecond : SharpLink.Sdk.IService { }

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IFirst, ISecond
{
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 0, "SHARPLINK016 must not choose among multiple candidate interfaces.");
    }

    [Test]
    public async Task Sharplink018AccessibilityOnlyShouldMakeServicePublic()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
internal sealed class [|Service|]
{
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Make RPC service publicly reachable",
            "An accessibility-only service should offer the public-reachability edit.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        EnsureContains(source, "public sealed class Service", "service declaration");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink018OpenGenericServiceShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]<T>
{
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 0, "Open generic services must remain diagnostic-only.");
    }

    [Test]
    public async Task Sharplink019SingleNonPublicConstructorShouldMakeItPublic()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    private Service(int value) { }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Make Service constructor public",
            "A unique otherwise-valid non-public constructor should be made public.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        EnsureContains(source, "public Service(int value)", "service constructor");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink019AmbiguousConstructorsWithoutSelectionAttributeShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service(int value) { }
    public Service(string value) { }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 0,
            "Ambiguous constructors must not offer selection when ActivatorUtilitiesConstructorAttribute is unavailable.");
    }

    [Test]
    public async Task Sharplink033UnresolvedPreviousTypeShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Union.cs", """
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
                ["SharpLink.PreviousUnionType"] = "Deleted.Namespace.OldCase"
            });

        var actions = await workspace.GetActionsAsync(diagnostic, "Union.cs");

        Ensure(actions.Count == 0,
            "SHARPLINK033 must not advertise restoration when the published case type no longer resolves.");
    }

    [Test]
    public async Task Sharplink037SingleSourceImplementationShouldAddRpcService()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService
{
}
"""),
            ("Service.cs", """
public sealed class Service : IContract
{
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Add [RpcService] to Service",
            "Exactly one source implementation should receive RpcService.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        EnsureContains(source, "[global::SharpLink.Sdk.RpcService]", "service implementation");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink037MultipleImplementationsShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }
public sealed class FirstService : IContract { }
public sealed class SecondService : IContract { }
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 0, "SHARPLINK037 must not choose among multiple implementations.");
    }

    [Test]
    public async Task Sharplink043ModifierAndConstructorDefectsShouldOfferAdapterShapeFix()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    private Adapter() { }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Fix Adapter Codec adapter shape",
            "A source adapter with only modifier/constructor defects should offer one shape fix.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Adapter.cs", changed);
        EnsureContains(source, "public sealed class Adapter", "adapter declaration");
        EnsureContains(source, "public Adapter()", "adapter constructor");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink043OpenGenericAdapterShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter<>))]

public sealed class Adapter<T> : SharpLink.Abstractions.IRpcCodecAdapter
{
    public Adapter() { }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.Count == 0, "Open generic adapters have defects beyond modifiers/constructor shape.");
    }

    [Test]
    public async Task Sharplink053OrdinaryStaticMethodShouldBecomeInstanceMethod()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
public sealed class Contract
{
    public static int [|Run|](int value) => value + 1;
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK053", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Make RPC method an instance method",
            "An unreferenced ordinary source static method should offer the instance edit.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Contract.cs", changed);
        EnsureContains(source, "public int Run(int value)", "RPC method");
        EnsureDoesNotContain(source, "static int Run", "RPC method");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink053OperatorShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
public readonly struct Contract
{
    public static Contract operator [|+|](Contract left, Contract right) => left;
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK053", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 0, "Operators/conversions must not receive the ordinary-method edit.");
    }

    [Test]
    public async Task Sharplink009AccessibilityOnlyShouldMakeDtoAndContainerPublic()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Dto.cs", """
using SharpLink.Sdk;

internal class Container
{
    [RpcSerializable]
    private sealed class [|Payload|]
    {
        public int Value { get; set; }
    }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK009",
            "Dto.cs",
            new Dictionary<string, string?> { ["SharpLink.FixKind"] = "MakeDtoAccessible" });

        var actions = await workspace.GetActionsAsync(diagnostic, "Dto.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Make DTO publicly reachable",
            "The accessibility-only DTO case should expose one public-reachability action.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Dto.cs", changed);
        EnsureContains(source, "public class Container", "DTO container");
        EnsureContains(source, "public sealed class Payload", "DTO");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink018SafeAbstractServiceShouldOfferConcreteEdit()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
using SharpLink.Sdk;

[RpcService]
public abstract class [|Service|]
{
    public int Value => 42;
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Make RPC service concrete",
            "An otherwise-concrete abstract service should offer removal of abstract.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Service.cs", changed);
        EnsureContains(source, "public class Service", "RPC service");
        EnsureDoesNotContain(source, "abstract class Service", "RPC service");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink019AmbiguousConstructorsWithSelectionAttributeShouldOfferEachValidChoice()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
using System;
using SharpLink.Sdk;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}

[RpcService]
public sealed class [|Service|]
{
    public Service() { }
    public Service(string name) { }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 2,
            $"Each valid public constructor should be selectable. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        foreach (var action in actions)
        {
            using var independent = CodeFixTestWorkspace.Create(("Service.cs", """
using System;
using SharpLink.Sdk;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}

[RpcService]
public sealed class [|Service|]
{
    public Service() { }
    public Service(string name) { }
}
"""));
            var independentDiagnostic = await independent.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
            var independentActions = await independent.GetActionsAsync(independentDiagnostic, "Service.cs");
            var selected = independentActions.Single(item => item.EquivalenceKey == action.EquivalenceKey);
            var changed = await independent.ApplyAsync(selected);
            var source = await independent.GetTextAsync("Service.cs", changed);
            Ensure(source.Split("ActivatorUtilitiesConstructor", StringSplitOptions.None).Length == 3,
                "Exactly the attribute declaration and one selected constructor annotation should remain.");
            await independent.AssertCompilesAsync(changed);
        }
    }
}
