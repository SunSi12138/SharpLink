using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class SecondCodexReviewRegressionTests
{
    [Test]
    public async Task Sharplink008ShouldWithholdReorderAcrossOptionalOrParamsTail()
    {
        var scenarios = new[]
        {
            (Name: "optional parameter", Source: """
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken cancellationToken, int value = 42);
}
"""),
            (Name: "params parameter", Source: """
using System.Threading;

public interface IContract : SharpLink.Sdk.IService
{
    int [|Run|](CancellationToken cancellationToken, params int[] values);
}
""")
        };

        foreach (var scenario in scenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", scenario.Source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK008", "Contract.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Count == 0,
                $"Reordering a control parameter after a {scenario.Name} would create an invalid signature.");
        }
    }

    [Test]
    public async Task Sharplink032ShouldValidateConstantsAgainstRestoredUnderlyingType()
    {
        using (var outOfRange = CodeFixTestWorkspace.Create(("Status.cs", """
public enum [|Status|] : int
{
    TooLarge = 300
}
""")))
        {
            await outOfRange.AssertCompilesAsync();
            var diagnostic = await outOfRange.CreateDiagnosticAsync(
                "SHARPLINK032",
                "Status.cs",
                new Dictionary<string, string?>
                {
                    ["SharpLink.PreviousEnumUnderlyingType"] = "System.Byte"
                });

            var actions = await outOfRange.GetActionsAsync(diagnostic, "Status.cs");

            Ensure(actions.Count == 0,
                "Restoring byte must be withheld when an enum constant is outside the byte range.");
        }

        using var boundary = CodeFixTestWorkspace.Create(("Status.cs", """
public enum [|Status|] : int
{
    Maximum = 255
}
"""));
        await boundary.AssertCompilesAsync();
        var boundaryDiagnostic = await boundary.CreateDiagnosticAsync(
            "SHARPLINK032",
            "Status.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousEnumUnderlyingType"] = "System.Byte"
            });

        var boundaryActions = await boundary.GetActionsAsync(boundaryDiagnostic, "Status.cs");

        Ensure(boundaryActions.Count == 1
               && boundaryActions[0].Title == "Restore published enum underlying type System.Byte"
               && boundaryActions[0].EquivalenceKey == "RestoreEnumType",
            "The byte maximum value must remain safely restorable.");
        var changed = await boundary.ApplyAsync(boundaryActions[0]);
        var source = await boundary.GetTextAsync("Status.cs", changed);
        EnsureContains(source, "enum Status : byte", "boundary enum declaration");
        await boundary.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink019ShouldNotExposeSecondConstructorWhenPublicUnsupportedConstructorExists()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service(ref int value) { }
    private Service(string value) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

        Ensure(actions.Count == 0,
            "Making the supported private constructor public would leave two public constructors and still not yield a valid activation shape.");
    }

    [Test]
    public async Task Sharplink043ShouldWithholdSealingForNewVirtualMember()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

public class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public Adapter() { }
    public virtual int Encode() => 42;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.Count == 0,
            "A newly declared virtual member cannot remain virtual after its declaring adapter is sealed.");
    }

    [Test]
    public async Task Sharplink043ShouldLeaveStaticConstructorAndExposeInstanceConstructor()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

internal class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    static Adapter() { }
    private Adapter() { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Fix Adapter Codec adapter shape"
               && actions[0].EquivalenceKey == "FixAdapterShape",
            "The adapter remains repairable when its static constructor precedes its private instance constructor.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Adapter.cs", changed);
        var constructors = CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>()
            .ToArray();
        var staticConstructor = constructors.Single(static item =>
            item.Modifiers.Any(SyntaxKind.StaticKeyword));
        var instanceConstructor = constructors.Single(static item =>
            !item.Modifiers.Any(SyntaxKind.StaticKeyword));
        Ensure(staticConstructor.Modifiers.Count == 1
               && staticConstructor.Modifiers[0].IsKind(SyntaxKind.StaticKeyword),
            $"The static constructor must remain unchanged. Actual source: {source}");
        Ensure(instanceConstructor.Modifiers.Count == 1
               && instanceConstructor.Modifiers[0].IsKind(SyntaxKind.PublicKeyword),
            $"Only the instance parameterless constructor must become public. Actual source: {source}");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task SignatureEditShouldBeWithheldForMetadataOnlyInterfaceObligation()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

public sealed class Contract : External.IExternalContract
{
    public ValueTask<int> [|RunAsync|](int value) => ValueTask.FromResult(value);
}
"""));
        workspace.AddMetadataReferenceFromSource("External.Contracts", """
namespace External
{
    public interface IExternalContract
    {
        System.Threading.Tasks.ValueTask<int> RunAsync(int value);
    }
}
""");
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Select(static item => (item.Title, item.EquivalenceKey)).SequenceEqual(
                [("Annotate with [NonCancellable]", "AddNonCancellable")]),
            $"A metadata-only interface obligation must suppress only signature edits. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink037ShouldWithholdAnnotateOnlyRestoreForIneffectivelyPublicService()
    {
        var scenarios = new[]
        {
            (Name: "internal implementation", Source: """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }

internal sealed class Service : IContract
{
    public Service() { }
}
"""),
            (Name: "public implementation in internal container", Source: """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|] : SharpLink.Sdk.IService { }

internal static class Container
{
    public sealed class Service : IContract
    {
        public Service() { }
    }
}
""")
        };

        foreach (var scenario in scenarios)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", scenario.Source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK037", "Contract.cs");

            var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

            Ensure(actions.Count == 0,
                $"An annotate-only route restoration cannot make an {scenario.Name} publicly activatable.");
        }
    }

    [Test]
    public async Task Sharplink033ShouldWithholdInvalidResolvedPreviousCaseTypes()
    {
        var scenarios = new[]
        {
            (Name: "abstract", PreviousType: "AbstractCase", Declaration: "public abstract class AbstractCase : IResult { }"),
            (Name: "open generic", PreviousType: "OpenCase<>", Declaration: "public sealed class OpenCase<T> : IResult { }"),
            (Name: "unassignable", PreviousType: "UnrelatedCase", Declaration: "public sealed class UnrelatedCase { }")
        };

        foreach (var scenario in scenarios)
        {
            var source = $$"""
{{scenario.Declaration}}
public sealed class CurrentCase : IResult { }

[[|SharpLink.Sdk.RpcUnionCase|](9, typeof(CurrentCase))]
public interface IResult { }
""";
            using var workspace = CodeFixTestWorkspace.Create(("Union.cs", source));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(
                "SHARPLINK033",
                "Union.cs",
                new Dictionary<string, string?>
                {
                    ["SharpLink.PreviousUnionTag"] = "7",
                    ["SharpLink.PreviousUnionType"] = scenario.PreviousType
                });

            var actions = await workspace.GetActionsAsync(diagnostic, "Union.cs");

            Ensure(actions.Count == 0,
                $"A resolved {scenario.Name} previous case type is not a closed, concrete union case.");
        }
    }

    [Test]
    public async Task Sharplink028FixAllShouldRestoreDifferentIdsWithSharedEquivalenceKey()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("First.cs", """
public sealed class FirstPayload
{
    [SharpLink.Sdk.RpcMember(99)]
    public int [|Value|] { get; set; }
}
"""),
            ("Second.cs", """
public sealed class SecondPayload
{
    [SharpLink.Sdk.RpcMember(99)]
    public int [|Value|] { get; set; }
}
"""));
        await workspace.AssertCompilesAsync();
        var firstDiagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK028",
            "First.cs",
            new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "7" });
        var secondDiagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK028",
            "Second.cs",
            new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "8" });
        var firstActions = await workspace.GetActionsAsync(firstDiagnostic, "First.cs");
        var secondActions = await workspace.GetActionsAsync(secondDiagnostic, "Second.cs");

        Ensure(firstActions.Count == 1 && firstActions[0].Title == "Preserve published member ID 7",
            "The first member restoration action must retain its diagnostic-specific title.");
        Ensure(secondActions.Count == 1 && secondActions[0].Title == "Preserve published member ID 8",
            "The second member restoration action must retain its diagnostic-specific title.");
        Ensure(firstActions[0].EquivalenceKey == "RestoreMemberId"
               && secondActions[0].EquivalenceKey == "RestoreMemberId",
            "Different published IDs must share the Fix All equivalence key.");

        var provider = CreateProvider();
        var firstDocument = workspace.GetDocument("First.cs");
        var secondDocument = workspace.GetDocument("Second.cs");
        var diagnosticProvider = new TestFixAllDiagnosticProvider(
            new Dictionary<DocumentId, ImmutableArray<Diagnostic>>
            {
                [firstDocument.Id] = [firstDiagnostic],
                [secondDocument.Id] = [secondDiagnostic]
            });
        var context = new FixAllContext(
            workspace.Solution.GetProject(workspace.ProjectId)
            ?? throw new InvalidOperationException("Test project was unavailable."),
            provider,
            FixAllScope.Project,
            "RestoreMemberId",
            ["SHARPLINK028"],
            diagnosticProvider,
            CancellationToken.None);
        var fixAllProvider = provider.GetFixAllProvider();
        Ensure(fixAllProvider is not null, "The provider must expose a Fix All provider.");

        var fixAllAction = await fixAllProvider!.GetFixAsync(context);

        Ensure(fixAllAction is not null, "SHARPLINK028 must support project Fix All across different IDs.");
        var changed = await workspace.ApplyAsync(fixAllAction!);
        var first = await workspace.GetTextAsync("First.cs", changed);
        var second = await workspace.GetTextAsync("Second.cs", changed);
        EnsureContains(first, "RpcMember(7)", "first restored member");
        EnsureContains(second, "RpcMember(8)", "second restored member");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink020ShouldUpdateRpcServiceAttributeInDifferentPartialDocument()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Service.Part1.cs", """
public sealed partial class [|Service|]
{
}
"""),
            ("Service.Part2.cs", """
[SharpLink.Sdk.RpcService]
public sealed partial class Service
{
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK020", "Service.Part1.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.Part1.cs");

        Ensure(actions.Select(static item => item.EquivalenceKey).SequenceEqual(
                ["SetLifetime:Singleton", "SetLifetime:Connection", "SetLifetime:Call"],
                StringComparer.Ordinal),
            "A partial service must retain the three explicit lifetime choices.");
        var changed = await workspace.ApplyAsync(
            actions.Single(static item => item.EquivalenceKey == "SetLifetime:Connection"));
        var first = await workspace.GetTextAsync("Service.Part1.cs", changed);
        var second = await workspace.GetTextAsync("Service.Part2.cs", changed);
        EnsureDoesNotContain(first, "RpcService", "partial declaration without the service attribute");
        EnsureContains(second,
            "RpcService(Lifetime = global::SharpLink.Sdk.SharpLinkServiceLifetime.Connection)",
            "actual partial service attribute");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink018ShouldCombineAccessibilityAndConcreteRepairAcrossPartialHierarchy()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Service.Part1.cs", """
internal partial class Container
{
    [SharpLink.Sdk.RpcService]
    internal abstract partial class [|Service|]
    {
        public int Value => 42;
    }
}
"""),
            ("Service.Part2.cs", """
internal partial class Container
{
    internal abstract partial class Service
    {
    }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK018", "Service.Part1.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.Part1.cs");

        Ensure(actions.Count == 1
               && actions[0].Title == "Make RPC service concrete and publicly reachable"
               && actions[0].EquivalenceKey == "MakeServiceConcreteAndPublic",
            $"An abstract inaccessible otherwise-concrete service needs one atomic solution action. Actual: {string.Join(", ", actions.Select(static item => item.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        var first = await workspace.GetTextAsync("Service.Part1.cs", changed);
        var second = await workspace.GetTextAsync("Service.Part2.cs", changed);
        foreach (var (name, source) in new[]
                 {
                     ("first partial document", first),
                     ("second partial document", second)
                 })
        {
            EnsureContains(source, "public partial class Container", name);
            EnsureContains(source, "public partial class Service", name);
            EnsureDoesNotContain(source, "abstract partial class Service", name);
        }
        await workspace.AssertCompilesAsync(changed);
    }
}
