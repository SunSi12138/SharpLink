using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue899AliasOnlyReferencedManifestsMustBeBootstrappedWithExternAlias()
    {
        var infrastructure = CreateManifestInfrastructureReference();
        var producer = CreateGeneratedManifestReference(
            "AliasedProducer", "AliasedManifest", "HiddenService", infrastructure);
        var aliasOnly = ((PortableExecutableReference)producer)
            .WithAliases(ImmutableArray.Create("ownerAlias"));
        const string consumer = """
extern alias ownerAlias;
public sealed class Consumer
{
    public System.Type Manifest => typeof(ownerAlias::SharpLink.Generated.AliasedManifest);
}
""";
        var generated = GetReferencedManifestBootstrap(
            RunGeneratorAndGetSources(consumer, infrastructure, aliasOnly));
        Ensure(generated.Contains("extern alias ownerAlias;", StringComparison.Ordinal) &&
               generated.Contains("ownerAlias::SharpLink.Generated.AliasedManifest.Register();", StringComparison.Ordinal),
            "bootstrap must use an accessible alias-qualified producer manifest");
        Ensure(!generated.Contains("global::SharpLink.Generated.AliasedManifest.Register()", StringComparison.Ordinal),
            "alias-only assembly is not reachable via global");
        EnsureGeneratorOutputCompiles(consumer, infrastructure, aliasOnly);
        EnsureGeneratorOutputCompiles("public sealed class Consumer { }", infrastructure, aliasOnly);

        var globalAndAlias = ((PortableExecutableReference)producer)
            .WithAliases(ImmutableArray.Create("global", "ownerAlias"));
        var ordinary = GetReferencedManifestBootstrap(
            RunGeneratorAndGetSources("public sealed class Consumer { }", infrastructure, globalAndAlias));
        Ensure(ordinary.Contains("global::SharpLink.Generated.AliasedManifest.Register();", StringComparison.Ordinal) &&
               !ordinary.Contains("extern alias", StringComparison.Ordinal),
            "global alias should preserve existing stable bootstrap output");
        EnsureGeneratorOutputCompiles("public sealed class Consumer { }", infrastructure, globalAndAlias);
        return Task.CompletedTask;
    }

    [Test]
    public Task Issue899MultipleAliasesAndInstanceFallbackMustStayDeterministic()
    {
        var infrastructure = CreateManifestInfrastructureReference();
        var instanceProducer = CreateMetadataReference("InstanceOwner", """
using SharpLink.Abstractions;
[assembly: SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(SharpLink.Generated.InstanceManifest), 5, 2,
    "3.0.0-test", "sharplink-3.0-api5-complete-method-facts-v1")]
namespace SharpLink.Generated
{
    public sealed class InstanceManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public static readonly InstanceManifest Instance = new();
    }
}
""", infrastructure);
        var aliases = ((PortableExecutableReference)instanceProducer)
            .WithAliases(ImmutableArray.Create("zetaAlias", "alphaAlias"));
        var first = GetReferencedManifestBootstrap(
            RunGeneratorAndGetSources("public sealed class Consumer { }", infrastructure, aliases));
        var second = GetReferencedManifestBootstrap(
            RunGeneratorAndGetSources("public sealed class Consumer { }", infrastructure,
                ((PortableExecutableReference)instanceProducer)
                    .WithAliases(ImmutableArray.Create("alphaAlias", "zetaAlias"))));
        Ensure(first == second, "bootstrap output must not depend on alias ordering");
        Ensure(first.Contains("extern alias alphaAlias;", StringComparison.Ordinal) &&
               first.Contains("alphaAlias::SharpLink.Generated.InstanceManifest.Instance", StringComparison.Ordinal),
            "Instance fallback must use stable accessible alias");
        Ensure(!first.Contains("extern alias zetaAlias;", StringComparison.Ordinal),
            "unselected alias should not generate an unnecessary directive");
        EnsureGeneratorOutputCompiles("public sealed class Consumer { }", infrastructure, aliases);
        return Task.CompletedTask;
    }
}
