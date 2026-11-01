using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.Abstractions;

/// <summary>Maps one contract-owning assembly to a multi-cluster slot.</summary>
public sealed record SharpLinkGeneratedClusterAssemblyRoute(
    SharpLinkClusterKey Cluster,
    Assembly ContractAssembly,
    string ContractAssemblyIdentity);

/// <summary>Provides source-generated static contract routes owned by an application assembly.</summary>
public interface ISharpLinkGeneratedClusterRouteManifest
{
    /// <summary>Gets the host assembly that declared the route attributes.</summary>
    Assembly OwnerAssembly { get; }

    /// <summary>Gets deterministically ordered routes declared by the host assembly.</summary>
    IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; }
}

/// <summary>
/// Stores bounded weak references to generated cluster route manifests without keeping collectible
/// AssemblyLoadContexts alive.
/// </summary>
public static class SharpLinkGeneratedClusterRouteCatalog
{
    internal const int SnapshotCapacity = 16_384;
    private const int MaximumEntries = SnapshotCapacity;
    private static readonly Lock Gate = new();
    private static readonly List<WeakReference<ISharpLinkGeneratedClusterRouteManifest>> Entries = [];

    /// <summary>Adds a generated route manifest without taking process-lifetime ownership of it.</summary>
    public static void Register(ISharpLinkGeneratedClusterRouteManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var loadContext = AssemblyLoadContext.GetLoadContext(manifest.OwnerAssembly);
        lock (Gate)
        {
            for (var index = Entries.Count - 1; index >= 0; index--)
            {
                if (!Entries[index].TryGetTarget(out var existing))
                {
                    Entries.RemoveAt(index);
                    continue;
                }

                if (ReferenceEquals(existing, manifest))
                    return;
            }

            if (Entries.Count >= MaximumEntries)
            {
                throw new InvalidOperationException(
                    $"The generated cluster route catalog reached its safety limit of {MaximumEntries} live entries.");
            }

            var weakManifest = new WeakReference<ISharpLinkGeneratedClusterRouteManifest>(manifest);
            Entries.Add(weakManifest);
            if (loadContext?.IsCollectible == true)
                loadContext.Unloading += _ => Remove(weakManifest);
        }
    }

    /// <summary>Creates a strong point-in-time route-manifest snapshot for one coordinator.</summary>
    public static IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> CreateSnapshot()
    {
        // Application-directed utility; Runtime ingestion uses its separately reserved adapter.
        var workspace = new ISharpLinkGeneratedClusterRouteManifest[SnapshotCapacity];
        try
        {
            var count = CopyLiveSnapshot(workspace);
            var result = new ISharpLinkGeneratedClusterRouteManifest[count];
            Array.Copy(workspace, result, count);
            return Array.AsReadOnly(result);
        }
        finally { Array.Clear(workspace); }
    }

    internal static int CopyLiveSnapshot(Span<ISharpLinkGeneratedClusterRouteManifest> destination)
    {
        if (destination.Length < SnapshotCapacity)
            throw new ArgumentException("Catalog snapshot destination is smaller than the fixed capacity.", nameof(destination));
        lock (Gate)
        {
            var count = 0;
            for (var index = Entries.Count - 1; index >= 0; index--)
            {
                if (Entries[index].TryGetTarget(out var manifest))
                    destination[count++] = manifest;
                else
                    Entries.RemoveAt(index);
            }
            return count;
        }
    }

    private static void Remove(WeakReference<ISharpLinkGeneratedClusterRouteManifest> manifest)
    {
        lock (Gate)
        {
            Entries.Remove(manifest);
            for (var index = Entries.Count - 1; index >= 0; index--)
            {
                if (!Entries[index].TryGetTarget(out _))
                    Entries.RemoveAt(index);
            }
        }
    }
}
