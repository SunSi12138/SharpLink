using static SharpLink.CodeFixes.Tests.CodeFixTestWorkspace;

namespace SharpLink.CodeFixes.Tests;

public sealed class TwentyFifthCodexReviewRegressionTests
{
    [Test]
    public async Task ConstructorSelectionShouldRequireAnApplicableMarkerAttribute()
    {
        var invalidMarkers = new[]
        {
            """
using System;
namespace Microsoft.Extensions.DependencyInjection
{
    internal sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}
""",
            """
using System;
namespace Microsoft.Extensions.DependencyInjection
{
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute
    {
        private ActivatorUtilitiesConstructorAttribute() { }
    }
}
""",
            """
using System;
namespace Microsoft.Extensions.DependencyInjection
{
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute
    {
        public ActivatorUtilitiesConstructorAttribute(int value) { }
    }
}
""",
            """
using System;
namespace Microsoft.Extensions.DependencyInjection
{
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute
    {
        [Obsolete("Removed marker constructor", true)]
        public ActivatorUtilitiesConstructorAttribute() { }
    }
}
""",
            """
using System;
namespace Microsoft.Extensions.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ActivatorUtilitiesConstructorAttribute : Attribute { }
}
""",
            """
namespace Microsoft.Extensions.DependencyInjection
{
    public sealed class ActivatorUtilitiesConstructorAttribute { }
}
"""
        };

        for (var index = 0; index < invalidMarkers.Length; index++)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService]
public sealed class [|Service|]
{
    public Service(int value) { }
    public Service(string value) { }
}
"""));
            workspace.AddMetadataReferenceFromSource("InvalidMarker" + index, invalidMarkers[index]);
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync("SHARPLINK019", "Service.cs");
            var actions = await workspace.GetActionsAsync(diagnostic, "Service.cs");

            Ensure(actions.Count == 0,
                $"Invalid marker fixture {index} must not enable constructor-selection actions.");
        }
    }

    [Test]
    public async Task RestoreMemberIdShouldPreserveExistingArgumentStructureAndTrivia()
    {
        var arguments = new[]
        {
            "/* stable wire note */ 99",
            "id: /* stable wire note */ 99"
        };

        foreach (var argument in arguments)
        {
            using var workspace = CodeFixTestWorkspace.Create(("Payload.cs", $$"""
public sealed class Payload
{
    [SharpLink.Sdk.RpcMember({{argument}})]
    public int [|Value|] { get; set; }
}
"""));
            await workspace.AssertCompilesAsync();
            var diagnostic = await workspace.CreateDiagnosticAsync(
                "SHARPLINK028",
                "Payload.cs",
                new Dictionary<string, string?> { ["SharpLink.PreviousMemberId"] = "7" });
            var action = (await workspace.GetActionsAsync(diagnostic, "Payload.cs"))
                .Single(static item => item.EquivalenceKey == "RestoreMemberId");

            var changed = await workspace.ApplyAsync(action);
            var source = await workspace.GetTextAsync("Payload.cs", changed);

            EnsureContains(source, "/* stable wire note */ 7", "RpcMember argument trivia");
            if (argument.StartsWith("id:", StringComparison.Ordinal))
                EnsureContains(source, "RpcMember(id:", "RpcMember named argument structure");
            EnsureDoesNotContain(source, "99", "obsolete RpcMember ID");
            await workspace.AssertCompilesAsync(changed);
        }
    }

    [Test]
    public async Task AdjacentAttributeValueRepairsShouldPreserveArgumentTrivia()
    {
        using (var union = CodeFixTestWorkspace.Create(("Union.cs", """
public sealed class OldCase : IResult { }
public sealed class NewCase : IResult { }

[[|SharpLink.Sdk.RpcUnionCase|](/* stable tag */ 9, typeof(/* stable case */ NewCase))]
public interface IResult { }
""")))
        {
            await union.AssertCompilesAsync();
            var diagnostic = await union.CreateDiagnosticAsync(
                "SHARPLINK033",
                "Union.cs",
                new Dictionary<string, string?>
                {
                    ["SharpLink.PreviousUnionTag"] = "7",
                    ["SharpLink.PreviousUnionType"] = "OldCase"
                });
            var action = (await union.GetActionsAsync(diagnostic, "Union.cs"))
                .Single(static item => item.EquivalenceKey == "RestoreUnionTag");
            var changed = await union.ApplyAsync(action);
            var source = await union.GetTextAsync("Union.cs", changed);

            EnsureContains(source, "/* stable tag */ 7", "union tag trivia");
            EnsureContains(source, "typeof(/* stable case */ global::OldCase)", "union case trivia");
            await union.AssertCompilesAsync(changed);
        }

        using var lifetime = CodeFixTestWorkspace.Create(("Service.cs", """
[SharpLink.Sdk.RpcService(
    Lifetime = /* deployment policy */ (SharpLink.Sdk.SharpLinkServiceLifetime)99)]
public sealed class [|Service|] { }
"""));
        await lifetime.AssertCompilesAsync();
        var lifetimeDiagnostic = await lifetime.CreateDiagnosticAsync("SHARPLINK020", "Service.cs");
        var lifetimeAction = (await lifetime.GetActionsAsync(lifetimeDiagnostic, "Service.cs"))
            .Single(static item => item.EquivalenceKey == "SetLifetime:Call");
        var lifetimeChanged = await lifetime.ApplyAsync(lifetimeAction);
        var lifetimeSource = await lifetime.GetTextAsync("Service.cs", lifetimeChanged);

        EnsureContains(lifetimeSource, "Lifetime = /* deployment policy */", "service lifetime trivia");
        EnsureContains(lifetimeSource, "SharpLinkServiceLifetime.Call", "restored service lifetime");
        await lifetime.AssertCompilesAsync(lifetimeChanged);
    }
}
