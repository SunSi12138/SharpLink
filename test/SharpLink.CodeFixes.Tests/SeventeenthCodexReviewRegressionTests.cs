using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class SeventeenthCodexReviewRegressionTests
{
    [Test]
    public async Task ServicePublicizationShouldRequireValidActivationShape()
    {
        using (var invalid = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
internal class [|Service|]
{
    protected Service(int value) { }
}
""")))
        {
            await invalid.AssertCompilesAsync();
            var diagnostic = await invalid.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");

            var actions = await invalid.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "MakeServicePublic"),
                "MakeServicePublic must be withheld when publicizing the service would expose an invalid activation shape.");
        }

        using var valid = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
internal sealed class [|Service|]
{
    public Service(int value) { }
}
"""));
        await valid.AssertCompilesAsync();
        var validDiagnostic = await valid.CreateDiagnosticAsync("SHARPLINK018", "Service.cs");
        var validActions = await valid.GetActionsAsync(validDiagnostic, "Service.cs");
        var validAction = validActions.Single(static action => action.EquivalenceKey == "MakeServicePublic");

        var changed = await valid.ApplyAsync(validAction);
        var source = await valid.GetTextAsync("Service.cs", changed);

        EnsureContains(source, "public sealed class Service", "service with a valid activation shape");
        await valid.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task RestoreMemberIdShouldUpdateAttributeOnPartialPropertyImplementation()
    {
        using var workspace = CodeFixTestWorkspace.Create(
            ("Payload.Definition.cs", """
public sealed partial class Payload
{
    public partial int [|Value|] { get; set; }
}
"""),
            ("Payload.Implementation.cs", """
public sealed partial class Payload
{
    private int _value;

    [SharpLink.Sdk.RpcMember(99)]
    public partial int Value
    {
        get => _value;
        set => _value = value;
    }
}
"""));
        await workspace.AssertCompilesAsync();
        var diagnostic = await workspace.CreateDiagnosticAsync(
            "SHARPLINK028",
            "Payload.Definition.cs",
            new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "7" });
        var actions = await workspace.GetActionsAsync(diagnostic, "Payload.Definition.cs");
        var action = actions.Single(static item => item.EquivalenceKey == "RestoreMemberId");

        var changed = await workspace.ApplyAsync(action);
        var definition = await workspace.GetTextAsync("Payload.Definition.cs", changed);
        var implementation = await workspace.GetTextAsync("Payload.Implementation.cs", changed);

        EnsureDoesNotContain(definition, "RpcMember", "partial property definition");
        EnsureContains(implementation, "RpcMember(7)", "partial property implementation");
        EnsureDoesNotContain(implementation, "RpcMember(99)", "partial property implementation");
        Ensure(CountOccurrences(definition + implementation, "RpcMember") == 1,
            "The partial property must retain exactly one non-repeatable RpcMember attribute.");
        await workspace.AssertCompilesAsync(changed);
    }

    [Test]
    public async Task ConstructorRepairsShouldRecursivelyRejectRefLikeDependencies()
    {
        const string sharedTypes = """
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}

public interface IBox<T> where T : allows ref struct { }
""";

        using (var selection = CodeFixTestWorkspace.Create(("Service.cs", sharedTypes + """

[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service(IBox<Span<int>> dependency) { }
    public Service(string dependency) { }
}
""")))
        {
            await selection.AssertCompilesAsync();
            var diagnostic = await selection.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

            var actions = await selection.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.Select(static action => (action.Title, action.EquivalenceKey)).SequenceEqual(
                    [("Select constructor Service(string)", "SelectConstructor:Service.Service(string)")]),
                $"Only the constructor without a nested ref-like dependency may be selected. Actual: {string.Join(", ", actions.Select(static action => action.Title))}");
            var changed = await selection.ApplyAsync(actions[0]);
            var source = await selection.GetTextAsync("Service.cs", changed);
            Ensure(CountOccurrences(source, "ActivatorUtilitiesConstructor") == 2,
                "The fixture declaration and selected constructor must be the only marker-name occurrences.");
            await selection.AssertCompilesAsync(changed);
        }

        using (var exposure = CodeFixTestWorkspace.Create(("Service.cs", sharedTypes + """

[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    private Service(IBox<Span<int>> dependency) { }
}
""")))
        {
            await exposure.AssertCompilesAsync();
            var diagnostic = await exposure.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");

            var actions = await exposure.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.All(static action => action.EquivalenceKey != "MakeConstructorPublic"),
                "MakeConstructorPublic must be withheld for a nested ref-like dependency.");
        }

        using var ordinary = CodeFixTestWorkspace.Create(("Service.cs", sharedTypes + """

[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    private Service(IBox<string> dependency) { }
}
"""));
        await ordinary.AssertCompilesAsync();
        var ordinaryDiagnostic = await ordinary.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
        var ordinaryActions = await ordinary.GetActionsAsync(ordinaryDiagnostic, "Service.cs");
        var ordinaryAction = ordinaryActions.Single(static action =>
            action.EquivalenceKey == "MakeConstructorPublic");

        var ordinaryChanged = await ordinary.ApplyAsync(ordinaryAction);
        var ordinarySource = await ordinary.GetTextAsync("Service.cs", ordinaryChanged);

        EnsureContains(ordinarySource, "public Service(IBox<string> dependency)",
            "ordinary generic constructor dependency");
        await ordinary.AssertCompilesAsync(ordinaryChanged);
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0;
             index += fragment.Length)
        {
            count++;
        }
        return count;
    }
}
