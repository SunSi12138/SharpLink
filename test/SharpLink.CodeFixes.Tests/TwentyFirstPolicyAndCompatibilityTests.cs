using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentyFirstPolicyAndCompatibilityTests
{
    [Test]
    public async Task TimeoutFixShouldEditOnlyDeclarationsWithInvalidTimeouts()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Base.cs", """
public interface IBaseContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout(5)]
    int Run();
}
"""),
            ("Derived.cs", """
public interface IDerivedContract : IBaseContract
{
    [SharpLink.Sdk.Timeout(-1)]
    new int [|Run|]();
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK050", "Derived.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Derived.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "RemoveTimeout");

        var changed = await workspace.ApplyAsync(action);
        var baseSource = await workspace.GetTextAsync("Base.cs", changed);
        var derivedSource = await workspace.GetTextAsync("Derived.cs", changed);

        EnsureContains(baseSource, "Timeout(5)", "valid inherited timeout");
        EnsureDoesNotContain(derivedSource, "Timeout", "invalid derived timeout");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task OnewayFixShouldEditOnlyDeclarationsWithInvalidReturnTypes()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Base.cs", """
using System.Threading.Tasks;

public interface IBaseContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Oneway]
    ValueTask RunAsync();
}
"""),
            ("Derived.cs", """
using System.Threading.Tasks;

public interface IDerivedContract : IBaseContract
{
    [SharpLink.Sdk.Oneway]
    new ValueTask<int> [|RunAsync|]();
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK056", "Derived.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Derived.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "RemoveOneway");

        var changed = await workspace.ApplyAsync(action);
        var baseSource = await workspace.GetTextAsync("Base.cs", changed);
        var derivedSource = await workspace.GetTextAsync("Derived.cs", changed);

        EnsureContains(baseSource, "Oneway", "valid inherited Oneway policy");
        EnsureDoesNotContain(derivedSource, "Oneway", "invalid derived Oneway policy");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task AttributeRemovalShouldPreserveSeparatorComments()
    {
        using (var direct = CodeFixTestWorkspace.Create(("Target.cs", """
using System;

[[|SharpLink.Sdk.RpcCodecAdapter|](typeof(int)), // keep adapter explanation
 Obsolete]
public sealed class Target { }
""")))
        {
            await direct.AssertCompilesAsync();
            var diagnostic = await direct.CreateDiagnosticAsync("SHARPLINK049", "Target.cs");
            var actions = await direct.GetActionsAsync(diagnostic, "Target.cs");

            var changed = await direct.ApplyAsync(actions.Single());
            var source = await direct.GetTextAsync("Target.cs", changed);

            EnsureContains(source, "keep adapter explanation", "direct attribute-removal trivia");
            EnsureContains(source, "Obsolete", "remaining attribute");
            await direct.AssertCompilesAsync(changed);
        }

        using var synchronized = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Oneway, // keep policy explanation
     Obsolete]
    ValueTask<int> [|RunAsync|]();
}
"""));
        await synchronized.AssertCompilesAsync();
        var synchronizedDiagnostic = await synchronized.CreateDiagnosticAsync("SHARPLINK056", "Contract.cs");
        var synchronizedActions = await synchronized.GetActionsAsync(synchronizedDiagnostic, "Contract.cs");

        var synchronizedChanged = await synchronized.ApplyAsync(
            synchronizedActions.Single(static item => item.EquivalenceKey == "RemoveOneway"));
        var synchronizedSource = await synchronized.GetTextAsync("Contract.cs", synchronizedChanged);

        EnsureContains(synchronizedSource, "keep policy explanation", "solution-wide attribute-removal trivia");
        EnsureContains(synchronizedSource, "Obsolete", "remaining method attribute");
        await synchronized.AssertCompilesAsync(synchronizedChanged);
    }

    [Test]
    public async Task LegacyServiceLifetimeFixShouldUseTheMatchedAttributeEnum()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Abstractions.RpcService]
public sealed class [|Service|]
{
    public Service() { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK020", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Select(static action => action.EquivalenceKey).SequenceEqual(
                ["SetLifetime:Singleton", "SetLifetime:Connection", "SetLifetime:Call"],
                StringComparer.Ordinal),
            "Legacy RpcService must retain all lifetime repairs.");
        var changed = await workspace.ApplyAsync(
            actions.Single(static action => action.EquivalenceKey == "SetLifetime:Call"));
        var source = await workspace.GetTextAsync("Service.cs", changed);

        EnsureContains(
            source,
            "RpcService(Lifetime = global::SharpLink.Abstractions.RpcServiceLifetime.Call)",
            "legacy service lifetime");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task InternalServiceConstructorShouldAllowInternalDependencies()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
internal sealed class Dependency { }

[SharpLink.Sdk.RpcService]
internal sealed class [|Service|]
{
    private Service(Dependency dependency) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "MakeConstructorPublic");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Service.cs", changed);

        EnsureContains(source, "public Service(Dependency dependency)", "internal service constructor");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task ConstructorSelectionShouldPreserveMarkerSeparatorComments()
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
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor, // keep constructor rationale
     Obsolete("Removed constructor", true)]
    public Service() { }

    public Service(string value) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");
        var action = actions.Single(static item =>
            item.EquivalenceKey == "SelectConstructor:Service.Service(string)");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Service.cs", changed);

        EnsureContains(source, "keep constructor rationale", "constructor marker separator trivia");
        EnsureContains(source, "Obsolete(\"Removed constructor\", true)", "remaining constructor attribute");
        EnsureContains(
            source,
            "ActivatorUtilitiesConstructor] public Service(string value)",
            "newly selected constructor");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task InternalServiceConstructorShouldRejectCrossAssemblyProtectedInternalDependency()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
internal sealed class [|Service|] : External.BaseService
{
    private Service(Dependency dependency) { }
}
"""));
        workspace.AddMetadataReferenceFromSource("External.Service.Dependencies", """
namespace External
{
    public class BaseService
    {
        protected internal sealed class Dependency { }
    }
}
""");
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.All(static action => action.EquivalenceKey != "MakeConstructorPublic"),
            "Generated code in the service assembly cannot name a protected-internal dependency from another assembly.");
    }

    [Test]
    public async Task EnumUnderlyingTypeRestorationShouldPreserveBaseListTrivia()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Status.cs", """
public enum [|Status|] : /* wire-width note */ long
{
    Ready = 1
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK032",
            "Status.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousEnumUnderlyingType"] = "System.Int32"
            });
        var actions = await workspace.GetActionsAsync(diagnostic, "Status.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "RestoreEnumType");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Status.cs", changed);

        EnsureContains(source, "/* wire-width note */ int", "restored enum base-list trivia");
        await workspace.AssertCompilesAsync(changed);
    }
}
