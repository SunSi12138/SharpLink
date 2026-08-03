using SharpLink.Generator;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class ThirtySecondCodexReviewRegressionTests
{
    [Test]
    public async Task AddFixShouldValidateConstructedInheritedMethod()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IBase<T>
{
    ValueTask<int> Run(T value);
    ValueTask<int> Run(string value, CancellationToken cancellationToken);
}

[RpcContract]
public interface IContract : IService, IBase<string> { }
"""));
        await workspace.AssertCompilesAsync();
        var compilation = await workspace.Solution.GetProject(workspace.ProjectId)!.GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation was unavailable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RpcGenerator());
        var diagnostic = driver.RunGenerators(compilation).GetRunResult().Diagnostics
            .Single(static item => item.Id == "SHARPLINK004");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.All(static item => item.EquivalenceKey != "Signature:AddCancellationToken"),
            "Add must be withheld when the constructed signature would collide with an overload.");
    }

    [Test]
    public async Task GeneratorShouldRejectErrorObsoleteOptionalAdapterConstructor()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
using System;

[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(
    typeof(Adapter), "obsolete.adapter/v1", "obsolete-wire/v1")]

public sealed class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    [Obsolete("Removed constructor", true)]
    public Adapter(int value = 0) { }
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

        var count = diagnostics.Count(static item => item.Id == "SHARPLINK043");
        Ensure(count == 1,
            $"An error-obsolete optional constructor must keep the adapter registration invalid. Actual: {count}.");
    }
}
