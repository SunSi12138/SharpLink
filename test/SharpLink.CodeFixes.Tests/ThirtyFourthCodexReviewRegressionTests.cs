using SharpLink.Generator;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ThirtyFourthCodexReviewRegressionTests
{
    [Test]
    public async Task AdapterShapeShouldValidatePublicRequiredMemberConstructor()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
using System.Diagnostics.CodeAnalysis;

[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public required int Value { get; init; }

    [SetsRequiredMembers]
    private Adapter() { Value = 1; }

    public Adapter(int value = 0) { Value = value; }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.All(static item => item.EquivalenceKey != "FixAdapterShape"),
            "The required-member check must validate the public optional constructor selected by generated code.");
    }

    [Test]
    public async Task GeneratorShouldRejectCallableConstructorWithoutSetsRequiredMembers()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
using System;

[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(
    typeof(Adapter), "required.adapter/v1", "required-wire/v1")]

public sealed class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public required int Value { get; init; }

    public Adapter(int value = 0) { Value = value; }
}

namespace SharpLink.Sdk
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RpcCodecAdapterRegistrationAttribute : Attribute
    {
        public RpcCodecAdapterRegistrationAttribute(Type adapterType, string adapterId, string wireFormatId) { }
    }
}
"""));
        await workspace.AssertCompilesAsync();
        var compilation = await workspace.Solution.GetProject(workspace.ProjectId)!.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RpcGenerator());
        var diagnostics = driver.RunGenerators(compilation).GetRunResult().Diagnostics;

        Ensure(diagnostics.Count(static item => item.Id == "SHARPLINK043") == 1,
            "Generator validation must reject a callable adapter constructor that does not set required members.");
    }

    [Test]
    public async Task MakeDtoAccessibleShouldSupportFileLocalType()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", """
using SharpLink.Sdk;

[RpcSerializable]
file sealed class [|Payload|]
{
    public int Value { get; set; }
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

        EnsureContains(source, "public sealed class Payload", "publicized file-local DTO");
        EnsureDoesNotContain(source, "file sealed class", "file-local modifier");
        await workspace.AssertCompilesAsync(changed);
        var compilation = await changed.GetProject(workspace.ProjectId)!.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RpcGenerator());
        var diagnostics = driver.RunGenerators(compilation).GetRunResult().Diagnostics;
        Ensure(diagnostics.All(static item => item.Id != "SHARPLINK009"),
            "The publicized DTO must be accepted by the generator.");
    }

    [Test]
    public async Task ServiceLifetimeFixShouldUseBoundHiddenFieldType()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
extern alias Legacy;

[Legacy::SharpLink.Abstractions.RpcService(
    Lifetime = (Legacy::SharpLink.Abstractions.DerivedLifetime)99)]
public sealed class [|Service|]
{
    public Service() { }
}
"""));
        workspace.AddMetadataReferenceFromSource("HiddenLegacyLifetime", """
using System;

namespace SharpLink.Abstractions
{
    public enum BaseLifetime { Singleton = 0, Connection = 1, Call = 2 }
    public enum DerivedLifetime { Singleton = 0, Connection = 1, Call = 2 }

    public abstract class RpcServiceBaseAttribute : Attribute
    {
        public BaseLifetime Lifetime { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcServiceAttribute : RpcServiceBaseAttribute
    {
        public new DerivedLifetime Lifetime;
    }
}
""", alias: "Legacy");
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK020", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Select(static item => item.EquivalenceKey).SequenceEqual(
                ["SetLifetime:Singleton", "SetLifetime:Connection", "SetLifetime:Call"]),
            "The bound hidden Lifetime field must expose its enum repairs.");
        var changed = await workspace.ApplyAsync(
            actions.Single(static item => item.EquivalenceKey == "SetLifetime:Call"));
        var source = await workspace.GetTextAsync("Service.cs", changed);

        EnsureContains(source, "DerivedLifetime.Call", "bound hidden lifetime field");
        EnsureDoesNotContain(source, "BaseLifetime.Call", "hidden base lifetime property");
        await workspace.AssertCompilesAsync(changed);
    }
}
