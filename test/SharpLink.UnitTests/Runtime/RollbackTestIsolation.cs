using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

internal static class RollbackTestIsolation
{
    internal static bool RemoveManifestFromCatalog(ISharpLinkGeneratedAssemblyManifest manifest)
        => RemoveManifestFromCatalog(
            typeof(SharpLinkGeneratedAssemblyCatalog),
            manifest);

    internal static bool RemoveManifestFromCatalog(ISharpLinkGeneratedClusterRouteManifest manifest)
        => RemoveManifestFromCatalog(
            typeof(SharpLinkGeneratedClusterRouteCatalog),
            manifest);

    internal static bool ContainsManifest(ISharpLinkGeneratedAssemblyManifest manifest)
        => SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
            .Any(candidate => ReferenceEquals(candidate, manifest));

    internal static bool ContainsManifest(ISharpLinkGeneratedClusterRouteManifest manifest)
        => SharpLinkGeneratedClusterRouteCatalog.CreateSnapshot()
            .Any(candidate => ReferenceEquals(candidate, manifest));

    internal static int AssemblyManifestCount
        => SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Count;

    internal static int RouteManifestCount
        => SharpLinkGeneratedClusterRouteCatalog.CreateSnapshot().Count;

    internal static IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> AssemblyManifestSnapshot
        => SharpLinkGeneratedAssemblyCatalog.CreateSnapshot();

    internal static IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> RouteManifestSnapshot
        => SharpLinkGeneratedClusterRouteCatalog.CreateSnapshot();

    private static bool RemoveManifestFromCatalog<TManifest>(
        Type catalogType,
        TManifest manifest)
        where TManifest : class
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var gateField = catalogType.GetField(
            "Gate",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new Exception($"cannot find {catalogType.Name} gate");
        var entriesField = catalogType.GetField(
            "Entries",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new Exception($"cannot find {catalogType.Name} entries");
        var gate = (Lock)(gateField.GetValue(null) ??
            throw new Exception($"cannot read {catalogType.Name} gate"));
        var entries = (List<WeakReference<TManifest>>)(entriesField.GetValue(null) ??
            throw new Exception($"cannot read {catalogType.Name} entries"));
        var removed = false;
        lock (gate)
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                if (!entries[index].TryGetTarget(out var candidate))
                {
                    entries.RemoveAt(index);
                    continue;
                }

                if (!ReferenceEquals(candidate, manifest))
                    continue;

                entries.RemoveAt(index);
                removed = true;
            }
        }

        return removed;
    }
}
