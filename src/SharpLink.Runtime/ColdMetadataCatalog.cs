namespace SharpLink.Runtime;

// Layer adapter: Abstractions knows only a preallocated Span, never Runtime accounting.
internal static class ColdMetadataCatalog
{
    internal static bool ContainsManifestOwner(ColdMetadataPreparation preparation, System.Reflection.Assembly owner)
    {
        using var charge = preparation.ReserveCollection(SharpLinkGeneratedAssemblyCatalog.SnapshotCapacity);
        var workspace = new ISharpLinkGeneratedAssemblyManifest[SharpLinkGeneratedAssemblyCatalog.SnapshotCapacity];
        try
        {
            var count = SharpLinkGeneratedAssemblyCatalog.CopyLiveSnapshot(workspace);
            using var callback = preparation.EnterCallback();
            for (var index = 0; index < count; index++)
                if (ReferenceEquals(workspace[index].OwnerAssembly, owner)) return true;
            return false;
        }
        finally { Array.Clear(workspace); }
    }

    internal static IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CaptureManifests(ColdMetadataPreparation preparation)
    {
        using var charge = preparation.ReserveCollection(SharpLinkGeneratedAssemblyCatalog.SnapshotCapacity);
        var workspace = new ISharpLinkGeneratedAssemblyManifest[SharpLinkGeneratedAssemblyCatalog.SnapshotCapacity];
        try
        {
            var count = SharpLinkGeneratedAssemblyCatalog.CopyLiveSnapshot(workspace);
            preparation.ReserveCollection(count);
            var result = new ISharpLinkGeneratedAssemblyManifest[count];
            Array.Copy(workspace, result, count);
            return Array.AsReadOnly(result);
        }
        finally { Array.Clear(workspace); }
    }

    internal static IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> CaptureRoutes(ColdMetadataPreparation preparation)
    {
        using var charge = preparation.ReserveCollection(SharpLinkGeneratedClusterRouteCatalog.SnapshotCapacity);
        var workspace = new ISharpLinkGeneratedClusterRouteManifest[SharpLinkGeneratedClusterRouteCatalog.SnapshotCapacity];
        try
        {
            var count = SharpLinkGeneratedClusterRouteCatalog.CopyLiveSnapshot(workspace);
            preparation.ReserveCollection(count);
            var result = new ISharpLinkGeneratedClusterRouteManifest[count];
            Array.Copy(workspace, result, count);
            return Array.AsReadOnly(result);
        }
        finally { Array.Clear(workspace); }
    }
}
