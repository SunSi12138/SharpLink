using SharpLink.Generator;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ThirtiethCodexReviewRegressionTests
{
    [Test]
    public async Task ServiceLifetimeFixesShouldRequireNamedEnumMembers()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
extern alias Legacy;

[Legacy::SharpLink.Abstractions.RpcService(
    Lifetime = (Legacy::SharpLink.Abstractions.LegacyLifetime)99)]
public sealed class [|Service|]
{
    public Service() { }
}
"""));
        workspace.AddMetadataReferenceFromSource("CustomLegacyLifetime", """
using System;

namespace SharpLink.Abstractions
{
    public enum LegacyLifetime { Default = 0, Session = 1, Request = 2 }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcServiceAttribute : Attribute
    {
        public LegacyLifetime Lifetime { get; set; }
    }
}
""", alias: "Legacy");
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK020", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.All(static action =>
                action.EquivalenceKey?.StartsWith("SetLifetime:", StringComparison.Ordinal) != true),
            "Lifetime fixes must not reference enum member names that do not exist.");
    }

    [Test]
    public async Task ReorderShouldUseConstructedInheritedMethodIdentity()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;
using SharpLink.Sdk;

public interface IBase<T>
{
    int Run(T token, SharpLinkCallOptions options);
}

[RpcContract]
public interface IContract : IService, IBase<CancellationToken> { }
"""));
        await workspace.AssertCompilesAsync();
        var compilation = await workspace.Solution.GetProject(workspace.ProjectId)!.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RpcGenerator());
        var diagnostic = driver.RunGenerators(compilation).GetRunResult().Diagnostics
            .Single(static item => item.Id == "SHARPLINK008");
        var action = (await workspace.GetActionsAsync(diagnostic, "Contract.cs"))
            .Single(static item => item.EquivalenceKey == "Signature:ReorderControlParameters");
        var changed = await workspace.ApplyAsync(action);

        EnsureContains(await workspace.GetTextAsync("Contract.cs", changed),
            "Run(SharpLinkCallOptions options, T token)", "constructed inherited method declaration");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task AdapterShapeShouldPreserveEscapedConstructorName()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(@class))]

internal class @class : SharpLink.Abstractions.IRpcCodecAdapter
{
    public @class(int value) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");
        var action = (await workspace.GetActionsAsync(diagnostic, "Adapter.cs"))
            .Single(static item => item.EquivalenceKey == "FixAdapterShape");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Adapter.cs", changed);

        EnsureContains(source, "public @class()", "escaped adapter constructor");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task AdapterShapeShouldReusePublicOptionalConstructor()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public Adapter(int value = 42) { Value = value; }

    public int Value { get; }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");
        var action = (await workspace.GetActionsAsync(diagnostic, "Adapter.cs"))
            .Single(static item => item.EquivalenceKey == "FixAdapterShape");
        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Adapter.cs", changed);

        EnsureContains(source, "public Adapter(int value = 42)", "optional adapter constructor");
        EnsureDoesNotContain(source, "public Adapter()", "synthetic adapter constructor");
        await workspace.AssertCompilesAsync(changed);
    }
}
