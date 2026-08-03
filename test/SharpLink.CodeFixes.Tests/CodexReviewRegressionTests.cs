using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class CodexReviewRegressionTests
{
    [Test]
    public async Task RegisterCodeFixesShouldProcessEveryDiagnosticAtTheSameSpan()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
[SharpLink.Sdk.RpcContract]
public interface [|IContract|]
{
}
"""));
        var addService = await workspace.CreateDiagnosticAsync("SHARPLINK006", "Contract.cs");
        var makePublic = await workspace.CreateDiagnosticAsync("SHARPLINK055", "Contract.cs");

        var actions = await workspace.GetActionsAsync([addService, makePublic], "Contract.cs");

        Ensure(actions.Select(static action => action.Title).SequenceEqual(
                ["Add IService to RPC contract", "Make RPC contract publicly reachable"],
                StringComparer.Ordinal),
            $"Every same-span diagnostic must register its action. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
        Ensure(actions.Select(static action => action.EquivalenceKey).SequenceEqual(
                ["AddIService", "MakeContractPublic"],
                StringComparer.Ordinal),
            "Same-span diagnostics must retain their independent stable equivalence keys.");
    }

    [Test]
    public async Task NonCancellableShouldRemainAvailableWhenMethodGroupSuppressesSignatureEdit()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System;
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value);
}

public static class DelegateConsumer
{
    public static Func<int, ValueTask<int>> Capture(IContract contract) => contract.RunAsync;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Annotate with [NonCancellable]",
            $"Method-group use must suppress only Add CancellationToken. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
        Ensure(actions[0].EquivalenceKey == "AddNonCancellable",
            "The remaining NonCancellable action must retain its stable key.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Contract.cs", changed);
        EnsureContains(source, "[global::SharpLink.Sdk.NonCancellable]", "method-group-safe alternative");
        EnsureContains(source, "contract.RunAsync", "existing method-group conversion");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task AddCancellationTokenShouldBeWithheldAfterOptionalParameters()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Contract.cs", """
using System.Threading.Tasks;

public interface IContract : SharpLink.Sdk.IService
{
    ValueTask<int> [|RunAsync|](int value = 42);
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK004", "Contract.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Contract.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Annotate with [NonCancellable]",
            $"A required CancellationToken after an optional parameter would create CS1737 and must be withheld. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Contract.cs", changed);
        EnsureContains(source, "RunAsync(int value = 42)", "optional RPC signature");
        EnsureDoesNotContain(source, "CancellationToken", "optional RPC signature");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink018ShouldRemoveAbstractFromEveryPartialDeclaration()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Service.Part1.cs", """
[SharpLink.Sdk.RpcService]
public abstract partial class [|Service|]
{
}
"""),
            ("Service.Part2.cs", """
public abstract partial class Service
{
    public int Value => 42;
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK018", "Service.Part1.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Service.Part1.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Make RPC service concrete",
            "A partial abstract service with no abstract members should offer one solution-wide concrete edit.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var first = await workspace.GetTextAsync("Service.Part1.cs", changed);
        var second = await workspace.GetTextAsync("Service.Part2.cs", changed);
        EnsureContains(first, "public partial class Service", "first partial declaration");
        EnsureContains(second, "public partial class Service", "second partial declaration");
        EnsureDoesNotContain(first, "abstract", "first partial declaration");
        EnsureDoesNotContain(second, "abstract", "second partial declaration");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink028MultiVariableFieldShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Dto.cs", """
public sealed class Payload
{
    [SharpLink.Sdk.RpcMember(99)]
    public int [|First|], Second;
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK028",
            "Dto.cs",
            new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "7" });

        var actions = await workspace.GetActionsAsync(diagnostic, "Dto.cs");

        Ensure(actions.Count == 0,
            "A field-level RpcMember attribute would assign the same wire ID to every variable; the unsafe action must be withheld.");
    }

    [Test]
    public async Task Sharplink033ShouldResolveAndRestoreClosedGenericPreviousUnionType()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Union.cs", """
namespace Cases
{
    public sealed class GenericCase<T> : IResult { }
    public sealed class CurrentCase : IResult { }

    [[|SharpLink.Sdk.RpcUnionCase|](9, typeof(CurrentCase))]
    public interface IResult { }
}
"""));
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK033",
            "Union.cs",
            new Dictionary<string, string?>
            {
                ["SharpLink.PreviousUnionTag"] = "7",
                ["SharpLink.PreviousUnionType"] = "Cases.GenericCase<System.Int32>"
            });

        var actions = await workspace.GetActionsAsync(diagnostic, "Union.cs");

        Ensure(actions.Count == 1 &&
               actions[0].Title == "Restore tag 7 to Cases.GenericCase<System.Int32>",
            $"A resolvable closed generic case type must be restorable. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
        var changed = await workspace.ApplyAsync(actions[0]);
        var source = await workspace.GetTextAsync("Union.cs", changed);
        EnsureContains(source,
            "RpcUnionCase(7, typeof(global::Cases.GenericCase<System.Int32>))",
            "closed generic union mapping");
        EnsureContains(source,
            "RpcUnionCase(9, typeof(CurrentCase))",
            "preserved current closed generic union mapping");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink043RecordClassShouldOfferNoAdapterShapeAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

public sealed record class Adapter : SharpLink.Abstractions.IRpcCodecAdapter;
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.Count == 0,
            "The class-only adapter rewriter must not advertise a no-op action for record classes.");
    }

    [Test]
    public async Task Sharplink043SourceDerivedAdapterShouldOfferNoSealingAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

public class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public Adapter() { }
}

public sealed class DerivedAdapter : Adapter
{
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.Count == 0,
            "Sealing a source adapter with a source-derived class would break the solution and must be withheld.");
    }

    [Test]
    public async Task Sharplink043ShouldMakePrivateParameterlessConstructorPublicAcrossPartialDocuments()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Binding.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]
"""),
            ("Adapter.Part1.cs", """
internal partial class Adapter : SharpLink.Abstractions.IRpcCodecAdapter
{
}
"""),
            ("Adapter.Part2.cs", """
internal partial class Adapter
{
    private Adapter() { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Binding.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Binding.cs");

        Ensure(actions.Count == 1 && actions[0].Title == "Fix Adapter Codec adapter shape",
            "A private parameterless constructor in another partial document remains safely repairable.");
        var changed = await workspace.ApplyAsync(actions[0]);
        var first = await workspace.GetTextAsync("Adapter.Part1.cs", changed);
        var second = await workspace.GetTextAsync("Adapter.Part2.cs", changed);
        var combined = first + "\n" + second;
        EnsurePublicSealedPartialClass(first, "first adapter partial declaration");
        EnsurePublicSealedPartialClass(second, "second adapter partial declaration");
        EnsureContains(second, "public Adapter()", "existing parameterless constructor");
        Ensure(CountOccurrences(combined, "Adapter()") == 1,
            $"The fix must not add a duplicate constructor. Actual source: {combined}");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task Sharplink043BaseWithoutAccessibleParameterlessConstructorShouldOfferNoAction()
    {
        using var workspace = CodeFixTestWorkspace.Create(("Adapter.cs", """
[assembly: [|SharpLink.Sdk.RpcCodecAdapter|](typeof(Adapter))]

public abstract class AdapterBase
{
    internal AdapterBase(int value) { }
}

internal class Adapter : AdapterBase, SharpLink.Abstractions.IRpcCodecAdapter
{
    internal Adapter(int value) : base(value) { }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK043", "Adapter.cs");

        var actions = await workspace.GetActionsAsync(diagnostic, "Adapter.cs");

        Ensure(actions.Count == 0,
            "The provider must not synthesize a parameterless constructor that cannot call its base constructor.");
    }

    private static int CountOccurrences(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;

    private static void EnsurePublicSealedPartialClass(string source, string scenario)
    {
        var declaration = CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
            .Single(static item => item.Identifier.ValueText == "Adapter");
        var modifiers = declaration.Modifiers.Select(static token => token.Kind()).ToHashSet();

        Ensure(modifiers.Contains(SyntaxKind.PublicKeyword)
               && modifiers.Contains(SyntaxKind.SealedKeyword)
               && modifiers.Contains(SyntaxKind.PartialKeyword),
            $"Expected {scenario} to be public, sealed, and partial. Actual source: {source}");
    }
}
