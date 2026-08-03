using SharpLink.Generator;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ThirtyFirstCodexReviewRegressionTests
{
    [Test]
    public async Task KeepFixesShouldValidateConstructedInheritedMethod()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IBase<T>
{
    ValueTask<int> Run(T token, CancellationToken other);
    ValueTask<int> Run(CancellationToken other);
}

[RpcContract]
public interface IContract : IService, IBase<CancellationToken> { }
"""));
        await workspace.AssertCompilesAsync();
        var compilation = await workspace.Solution.GetProject(workspace.ProjectId)!.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RpcGenerator());
        var diagnostic = driver.RunGenerators(compilation).GetRunResult().Diagnostics
            .Single(static item => item.Id == "SHARPLINK002");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.All(static item =>
                item.EquivalenceKey?.StartsWith("Signature:Keep:", StringComparison.Ordinal) != true),
            "Keep fixes must be withheld when the constructed signature would collide with an overload.");
    }

    [Test]
    public async Task ServiceLifetimeFixesShouldResolveInheritedProperty()
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
        workspace.AddMetadataReferenceFromSource("InheritedLegacyLifetime", """
using System;

namespace SharpLink.Abstractions
{
    public enum LegacyLifetime { Singleton = 0, Connection = 1, Call = 2 }

    public abstract class RpcServiceBaseAttribute : Attribute
    {
        public LegacyLifetime Lifetime { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcServiceAttribute : RpcServiceBaseAttribute { }
}
""", alias: "Legacy");
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK020", "Service.cs");
        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Select(static item => item.EquivalenceKey).SequenceEqual(
                ["SetLifetime:Singleton", "SetLifetime:Connection", "SetLifetime:Call"]),
            $"Inherited Lifetime properties must expose the three valid enum repairs. Actual: " +
            string.Join(", ", actions.Select(static item => item.EquivalenceKey)));
        var changed = await workspace.ApplyAsync(
            actions.Single(static item => item.EquivalenceKey == "SetLifetime:Connection"));
        var source = await workspace.GetTextAsync("Service.cs", changed);
        EnsureContains(source, "LegacyLifetime.Connection", "inherited lifetime repair");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task DefaultTimeoutShouldPreserveNamedSettings()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
extern alias Legacy;
using System.Threading.Tasks;

public interface IContract
{
    [Legacy::SharpLink.Abstractions.Timeout(-1, Mode = 2)]
    ValueTask<int> [|RunAsync|]();
}
"""));
        workspace.AddMetadataReferenceFromSource("ConfiguredLegacyTimeout", """
using System;

namespace SharpLink.Abstractions
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TimeoutAttribute : Attribute
    {
        public TimeoutAttribute() { }
        public TimeoutAttribute(int seconds) { }
        public int Mode { get; set; }
    }
}
""", alias: "Legacy");
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK050", "Contract.cs");
        var action = (await workspace.GetActionsAsync(diagnostic, "Contract.cs"))
            .Single(static item => item.EquivalenceKey == "UseDefaultTimeout");

        var changed = await workspace.ApplyAsync(action);
        var source = await workspace.GetTextAsync("Contract.cs", changed);

        EnsureContains(source, "Timeout(Mode = 2)", "named timeout setting");
        EnsureDoesNotContain(source, "Timeout(-1", "invalid constructor argument");
        await workspace.AssertCompilesAsync(changed);
    }
}
