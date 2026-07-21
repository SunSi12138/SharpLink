using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.Abstractions;

/// <summary>Identifies the single source-generated SharpLink manifest in an assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class SharpLinkGeneratedAssemblyManifestAttribute : Attribute
{
    /// <summary>Creates a manifest locator.</summary>
    /// <param name="manifestType">A generated manifest type with a public parameterless constructor.</param>
    public SharpLinkGeneratedAssemblyManifestAttribute(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
        Type manifestType)
    {
        ManifestType = manifestType ?? throw new ArgumentNullException(nameof(manifestType));
    }

    /// <summary>Gets the generated manifest implementation type.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type ManifestType { get; }
}

/// <summary>Describes one generated RPC method for compatibility and conflict validation.</summary>
public sealed record SharpLinkGeneratedMethodDescriptor(
    string Name,
    long MethodId,
    RpcMethodKind Kind,
    bool SupportsCancellation,
    string RequestSchema,
    string ResponseSchema,
    string Fingerprint);

/// <summary>Describes the contract-owned generated artifacts in one assembly.</summary>
public sealed record SharpLinkGeneratedContractDescriptor(
    Type ContractType,
    string ContractName,
    long ContractId,
    string Fingerprint,
    IReadOnlyList<SharpLinkGeneratedMethodDescriptor> Methods,
    Func<IRpcChannel, object> ProxyFactory,
    Func<IRpcStub> StubFactory);

/// <summary>Describes one service-owned generated activator.</summary>
public sealed record SharpLinkGeneratedServiceDescriptor(
    Type ContractType,
    Type ImplementationType,
    string ContractName,
    string ImplementationName,
    long ContractId,
    string Fingerprint,
    SharpLinkServiceLifetime Lifetime,
    IReadOnlyList<Type> Dependencies,
    Func<IServiceProvider, object> Activator);

/// <summary>Provides all artifacts generated for one owner assembly.</summary>
public interface ISharpLinkGeneratedAssemblyManifest
{
    /// <summary>Gets the manifest API version understood by the runtime.</summary>
    int ApiVersion { get; }

    /// <summary>Gets the on-wire protocol version described by this manifest.</summary>
    int ProtocolVersion { get; }

    /// <summary>Gets the source-generator version.</summary>
    string GeneratorVersion { get; }

    /// <summary>Gets the assembly that owns this manifest.</summary>
    Assembly OwnerAssembly { get; }

    /// <summary>Gets the canonical compile-time descriptor used by downstream analyzers.</summary>
    string CompileTimeDescriptor { get; }

    /// <summary>Gets contract-owned proxy and stub descriptors.</summary>
    IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }

    /// <summary>Gets service-owned activator descriptors.</summary>
    IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; }

    /// <summary>Gets generated Codec factories owned by this assembly.</summary>
    IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }

    /// <summary>Gets the identities of generated assemblies that this manifest depends on.</summary>
    IReadOnlyList<string> Dependencies { get; }
}

/// <summary>Defines generated manifest compatibility constants for SharpLink 0.7.x.</summary>
public static class SharpLinkGeneratedManifestVersions
{
    /// <summary>The current generated manifest API version.</summary>
    public const int Api = 2;

    /// <summary>The unchanged SharpLink wire protocol version.</summary>
    public const int Protocol = 2;
}

/// <summary>
/// Stores bounded weak references to generated manifests. Each generated assembly anchors
/// its own manifest; this catalog therefore does not keep a collectible load context alive.
/// </summary>
public static class SharpLinkGeneratedAssemblyCatalog
{
    private const int MaximumEntries = 16_384;
    private static readonly Lock Gate = new();
    private static readonly List<WeakReference<ISharpLinkGeneratedAssemblyManifest>> Entries = [];

    /// <summary>Adds a generated manifest without taking process-lifetime ownership of it.</summary>
    public static void Register(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
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
                    $"The generated manifest catalog reached its safety limit of {MaximumEntries} live entries.");
            }

            var weakManifest = new WeakReference<ISharpLinkGeneratedAssemblyManifest>(manifest);
            Entries.Add(weakManifest);
            var loadContext = AssemblyLoadContext.GetLoadContext(manifest.OwnerAssembly);
            if (loadContext?.IsCollectible == true)
            {
                loadContext.Unloading += _ => Remove(weakManifest);
            }
        }
    }

    /// <summary>Creates a strong, point-in-time snapshot for one client or server.</summary>
    public static IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateSnapshot()
    {
        lock (Gate)
        {
            var snapshot = new List<ISharpLinkGeneratedAssemblyManifest>(Entries.Count);
            for (var index = Entries.Count - 1; index >= 0; index--)
            {
                if (Entries[index].TryGetTarget(out var manifest))
                    snapshot.Add(manifest);
                else
                    Entries.RemoveAt(index);
            }
            return snapshot;
        }
    }

    private static void Remove(WeakReference<ISharpLinkGeneratedAssemblyManifest> manifest)
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
