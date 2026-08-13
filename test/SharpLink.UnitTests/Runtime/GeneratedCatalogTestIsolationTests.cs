using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public sealed class GeneratedCatalogTestIsolationTests
{
    [Test]
    // The test deliberately mutates both process-wide weak catalogs to prove exact restoration.
    [NotInParallel("generated-catalog")]
    public void IdentityRemovalShouldPreserveOtherEntriesAndTreatMissingEntriesAsNoOp()
    {
        var assemblySnapshotBefore = RollbackTestIsolation.AssemblyManifestSnapshot;
        var routeSnapshotBefore = RollbackTestIsolation.RouteManifestSnapshot;
        var removedAssemblyManifest = new TestAssemblyManifest("remove");
        var retainedAssemblyManifest = new TestAssemblyManifest("retain");
        var removedRouteManifest = new TestRouteManifest("remove");
        var retainedRouteManifest = new TestRouteManifest("retain");
        try
        {
            SharpLinkGeneratedAssemblyCatalog.Register(removedAssemblyManifest);
            SharpLinkGeneratedAssemblyCatalog.Register(retainedAssemblyManifest);
            SharpLinkGeneratedClusterRouteCatalog.Register(removedRouteManifest);
            SharpLinkGeneratedClusterRouteCatalog.Register(retainedRouteManifest);

            Ensure(RollbackTestIsolation.RemoveManifestFromCatalog(removedAssemblyManifest),
                "the exact assembly-manifest identity must be removed");
            var assemblyCountAfterRemoval = RollbackTestIsolation.AssemblyManifestCount;
            Ensure(!RollbackTestIsolation.ContainsManifest(removedAssemblyManifest) &&
                   RollbackTestIsolation.ContainsManifest(retainedAssemblyManifest),
                "assembly-manifest removal must preserve every other live identity");
            EnsureContainsEveryIdentity(
                assemblySnapshotBefore,
                RollbackTestIsolation.AssemblyManifestSnapshot,
                "assembly catalog baseline after exact removal");
            Ensure(!RollbackTestIsolation.RemoveManifestFromCatalog(removedAssemblyManifest) &&
                   RollbackTestIsolation.AssemblyManifestCount == assemblyCountAfterRemoval,
                "removing an absent assembly-manifest identity must be a no-op");

            Ensure(RollbackTestIsolation.RemoveManifestFromCatalog(removedRouteManifest),
                "the exact route-manifest identity must be removed");
            var routeCountAfterRemoval = RollbackTestIsolation.RouteManifestCount;
            Ensure(!RollbackTestIsolation.ContainsManifest(removedRouteManifest) &&
                   RollbackTestIsolation.ContainsManifest(retainedRouteManifest),
                "route-manifest removal must preserve every other live identity");
            EnsureContainsEveryIdentity(
                routeSnapshotBefore,
                RollbackTestIsolation.RouteManifestSnapshot,
                "route catalog baseline after exact removal");
            Ensure(!RollbackTestIsolation.RemoveManifestFromCatalog(removedRouteManifest) &&
                   RollbackTestIsolation.RouteManifestCount == routeCountAfterRemoval,
                "removing an absent route-manifest identity must be a no-op");
        }
        finally
        {
            _ = RollbackTestIsolation.RemoveManifestFromCatalog(removedAssemblyManifest);
            _ = RollbackTestIsolation.RemoveManifestFromCatalog(retainedAssemblyManifest);
            _ = RollbackTestIsolation.RemoveManifestFromCatalog(removedRouteManifest);
            _ = RollbackTestIsolation.RemoveManifestFromCatalog(retainedRouteManifest);
        }

        EnsureSameIdentitySet(
            assemblySnapshotBefore,
            RollbackTestIsolation.AssemblyManifestSnapshot,
            "assembly catalog after identity-specific cleanup");
        EnsureSameIdentitySet(
            routeSnapshotBefore,
            RollbackTestIsolation.RouteManifestSnapshot,
            "route catalog after identity-specific cleanup");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void EnsureContainsEveryIdentity<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string scenario)
        where T : class
    {
        for (var index = 0; index < expected.Count; index++)
        {
            var identity = expected[index];
            Ensure(actual.Any(candidate => ReferenceEquals(candidate, identity)),
                $"{scenario} must preserve baseline identity {index}");
        }
    }

    private static void EnsureSameIdentitySet<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string scenario)
        where T : class
    {
        Ensure(actual.Count == expected.Count,
            $"{scenario} must restore the exact live-entry count");
        EnsureContainsEveryIdentity(expected, actual, scenario);
    }

    private sealed class TestAssemblyManifest(string descriptor) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(GeneratedCatalogTestIsolationTests).Assembly;
        public string CompileTimeDescriptor => descriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class TestRouteManifest(string cluster) : ISharpLinkGeneratedClusterRouteManifest
    {
        public Assembly OwnerAssembly => typeof(GeneratedCatalogTestIsolationTests).Assembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; } =
        [
            new SharpLinkGeneratedClusterAssemblyRoute(
                cluster,
                typeof(GeneratedCatalogTestIsolationTests).Assembly,
                typeof(GeneratedCatalogTestIsolationTests).Assembly.FullName!)
        ];
    }
}
