namespace SharpLink.Sdk;

/// <summary>Assigns all generated contracts in a marker assembly to one multi-cluster slot.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SharpLinkClusterContractAssemblyAttribute : Attribute
{
    /// <summary>Creates a static contract-assembly route declaration.</summary>
    /// <param name="cluster">The target case-sensitive cluster key.</param>
    /// <param name="assemblyMarker">A type in the contract-owning assembly.</param>
    public SharpLinkClusterContractAssemblyAttribute(string cluster, Type assemblyMarker)
    {
        Cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        AssemblyMarker = assemblyMarker ?? throw new ArgumentNullException(nameof(assemblyMarker));
    }

    /// <summary>Gets the target cluster key.</summary>
    public string Cluster { get; }

    /// <summary>Gets the type used only to locate the contract-owning assembly.</summary>
    public Type AssemblyMarker { get; }
}
