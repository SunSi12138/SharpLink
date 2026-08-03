using System.Text;
using Microsoft.CodeAnalysis.Diagnostics;
using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentyFirstGenerationPreflightTests
{
    [Test]
    public async Task SealDtoShouldRequireAUsableGeneratorConstructionPlan()
    {
        var invalidSources = new[]
        {
            """
using System;

[Obsolete("Removed DTO", true)]
[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]
{
    public int Value { get; set; }
}
""",
            """
[SharpLink.Sdk.RpcSerializable]
public class [|Payload|]
{
    public int Value { get; }

    private Payload(int value) => Value = value;
}
"""
        };

        foreach (var source in invalidSources)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(
                "SHARPLINK009",
                "Payload.cs",
                new Dictionary<string, string?> { ["SharpLink.FixKind"] = "SealDto" });

            var actions = await workspace.GetActionsAsync(diagnostic, "Payload.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "SealDto"),
                "SealDto must not expose the next generator error.");
        }
    }

    [Test]
    public async Task ContractRepairsShouldRequireGeneratablePayloadCodecs()
    {
        using (var addIService = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

[SharpLink.Sdk.RpcContract]
public interface [|IContract|]
{
    [SharpLink.Sdk.NonCancellable]
    Task<object> Ping();
}
""")))
        {
            await addIService.AssertCompilesAsync();
            var diagnostic = await addIService.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");
            var actions = await addIService.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "AddIService"),
                "AddIService must be withheld when the resulting contract payload has no Codec.");
        }

        using (var annotate = CodeFixTestWorkspace.Create(("Service.cs", """
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    Task<object> Ping();
}

[SharpLink.Sdk.RpcService]
public sealed class [|Service|] : IContract
{
    public Task<object> Ping() => Task.FromResult<object>(42);
}
""")))
        {
            await annotate.AssertCompilesAsync();
            var diagnostic = await annotate.CreateDiagnosticAsync("SHARPLINK016", "Service.cs");
            var actions = await annotate.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "AnnotateRpcContract"),
                "AnnotateRpcContract must be withheld when it would activate an unsupported payload.");
        }

        using var supported = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

[SharpLink.Sdk.RpcContract]
public interface [|IContract|]
{
    [SharpLink.Sdk.NonCancellable]
    Task<int> Ping();
}
"""));
        await supported.AssertCompilesAsync();
        var supportedDiagnostic = await supported.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");
        var supportedActions = await supported.GetActionsAsync(supportedDiagnostic, "Contract.cs");

        Ensure(supportedActions.Any(static action => action.EquivalenceKey == "AddIService"),
            "A contract with a built-in payload must retain AddIService.");
    }

    [Test]
    public async Task AddIServiceShouldRejectObsoleteAndMultiplyOwnedContracts()
    {
        using (var obsolete = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;

[Obsolete("Removed contract", true)]
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] { }
""")))
        {
            await obsolete.AssertCompilesAsync();
            var diagnostic = await obsolete.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");
            var actions = await obsolete.GetActionsAsync(diagnostic, "Contract.cs");
            Ensure(actions.All(static action => action.EquivalenceKey != "AddIService"),
                "AddIService must be withheld for an error-obsolete contract.");
        }

        using var shared = CodeFixTestWorkspace.Create(("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] { }

[SharpLink.Sdk.RpcService]
public sealed class FirstService : IContract { }

[SharpLink.Sdk.RpcService]
public sealed class SecondService : IContract { }
"""));
        await shared.AssertCompilesAsync();
        var sharedDiagnostic = await shared.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");
        var sharedActions = await shared.GetActionsAsync(sharedDiagnostic, "Contract.cs");

        Ensure(sharedActions.All(static action => action.EquivalenceKey != "AddIService"),
            "AddIService must not activate a contract already owned by multiple RPC services.");
    }

    [Test]
    public async Task GeneratedPartialTargetsShouldWithholdContractAndServiceShapeEdits()
    {
        using (var contract = CodeFixTestWorkspace.Create(("Contract.cs", """
public sealed class GenerateContractPart { }

[SharpLink.Sdk.RpcContract]
public partial interface [|IContract|] { }
""")))
        {
            AddGeneratedSource(contract, """
public partial interface IContract { }
""");
            await contract.AssertCompilesAsync();
            var diagnostic = await contract.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");
            var actions = await contract.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "AddIService"),
                "AddIService must not edit a contract that also has a generated partial declaration.");
        }

        using var service = CodeFixTestWorkspace.Create(("Service.cs", """
public sealed class GenerateServicePart { }

[SharpLink.Sdk.RpcService]
public abstract partial class [|Service|]
{
    public Service() { }
}
"""));
        AddGeneratedSource(service, """
public abstract partial class Service { }
""");
        await service.AssertCompilesAsync();
        var serviceDiagnostic = await service.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");
        var serviceActions = await service.GetActionsAsync(serviceDiagnostic, "Service.cs");

        Ensure(serviceActions.All(static action =>
                action.EquivalenceKey is not ("MakeServiceConcrete" or "MakeServiceConcreteAndPublic")),
            "Concrete-service edits must be withheld for generated partial declarations.");

        using var internalService = CodeFixTestWorkspace.Create(("Service.cs", """
public sealed class GenerateInternalServicePart { }

[SharpLink.Sdk.RpcService]
internal abstract partial class [|InternalService|]
{
    public InternalService() { }
}
"""));
        AddGeneratedSource(internalService, """
internal abstract partial class InternalService { }
""");
        await internalService.AssertCompilesAsync();
        var internalDiagnostic = await internalService.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");
        var internalActions = await internalService.GetActionsAsync(internalDiagnostic, "Service.cs");

        Ensure(internalActions.All(static action =>
                action.EquivalenceKey is not ("MakeServiceConcrete" or "MakeServiceConcreteAndPublic")),
            "Combined public/concrete edits must be withheld for generated partial declarations.");
    }

    [Test]
    public async Task ErrorObsoleteContainingTypesShouldBlockGeneratedCodeRepairs()
    {
        var scenarios = new[]
        {
            (
                DiagnosticId: "SHARPLINK009",
                Source: """
using System;

[Obsolete("Removed container", true)]
public static class Container
{
    [SharpLink.Sdk.RpcSerializable]
    public class [|Payload|] { public int Value { get; set; } }
}
""",
                Properties: (IReadOnlyDictionary<string, string?>?)new Dictionary<string, string?>
                {
                    ["SharpLink.FixKind"] = "SealDto"
                }),
            (
                DiagnosticId: "SHARPLINK006",
                Source: """
using System;

[Obsolete("Removed container", true)]
public static class Container
{
    [SharpLink.Sdk.RpcContract]
    public interface [|IContract|] { }
}
""",
                Properties: (IReadOnlyDictionary<string, string?>?)null),
            (
                DiagnosticId: "SHARPLINK020",
                Source: """
using System;

[Obsolete("Removed container", true)]
public static class Container
{
    [SharpLink.Sdk.RpcService]
    public sealed class [|Service|] { public Service() { } }
}
""",
                Properties: (IReadOnlyDictionary<string, string?>?)null)
        };

        foreach (var scenario in scenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Target.cs", scenario.Source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(
                scenario.DiagnosticId,
                "Target.cs",
                scenario.Properties);
            var actions = await workspace.GetActionsAsync(diagnostic, "Target.cs");

            Ensure(actions.Count == 0,
                $"{scenario.DiagnosticId} must honor error-obsolete containing types.");
        }

        using var union = CodeFixTestWorkspace.Create(("Union.cs", """
using System;

[Obsolete("Removed container", true)]
public static class RemovedContainer
{
    public sealed class OldCase : IResult { }
    public sealed class NewCase : IResult { }

    [[|SharpLink.Sdk.RpcUnionCase|](9, typeof(NewCase))]
    public interface IResult { }
}
"""));
        await union.AssertCompilesAsync();
        var unionDiagnostic = await union.CreateDiagnosticAsync(
            "SHARPLINK033",
            "Union.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousUnionTag"] = "7",
                ["SharpLink.PreviousUnionType"] = "RemovedContainer.OldCase"
            });
        var unionActions = await union.GetActionsAsync(unionDiagnostic, "Union.cs");

        Ensure(unionActions.Count == 0,
            "Union restoration must honor an error-obsolete case containing type.");
    }

    private static void AddGeneratedSource(CodeFixTestWorkspace workspace, string source)
        => SetSolution(
            workspace,
            workspace.Solution.AddAnalyzerReference(
                workspace.ProjectId,
                new TestGeneratorReference(new FixedSourceGenerator(source))));

    private static void SetSolution(CodeFixTestWorkspace workspace, Solution solution)
    {
        var solutionProperty = typeof(CodeFixTestWorkspace).GetProperty(
            nameof(CodeFixTestWorkspace.Solution),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Test workspace Solution property was unavailable.");
        solutionProperty.SetValue(workspace, solution);
    }

    private sealed class TestGeneratorReference(ISourceGenerator generator) : AnalyzerReference
    {
        public override string? FullPath => null;

        public override string Display => "TwentyFirstGeneratedPartial";

        public override object Id { get; } = new();

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];

        public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
            => language == LanguageNames.CSharp ? [generator] : [];

        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => [generator];
    }

#pragma warning disable RS1042 // This test-only generator intentionally runs in the host test process.
    private sealed class FixedSourceGenerator(string source) : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context) { }

        public void Execute(GeneratorExecutionContext context)
            => context.AddSource("TwentyFirstGeneratedPart.g.cs", SourceText.From(source, Encoding.UTF8));
    }
#pragma warning restore RS1042
}
