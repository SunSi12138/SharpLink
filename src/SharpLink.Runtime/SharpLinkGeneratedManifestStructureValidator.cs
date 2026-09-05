using System.Reflection;

namespace SharpLink.Runtime;

internal static class SharpLinkGeneratedManifestStructureValidator
{
    internal static void Validate(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var ownerAssembly = manifest.OwnerAssembly ??
            throw new InvalidOperationException("Generated manifest has no owner assembly.");
        if (manifest.RpcAssemblyHash.IsEmpty)
        {
            throw new InvalidOperationException(
                $"Generated manifest '{ownerAssembly.FullName}' has no deterministic RPC assembly identity.");
        }
        var contracts = manifest.Contracts ??
            throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a null Contract table.");
        var codecs = manifest.Codecs ??
            throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a null global Codec table.");
        var contractCodecs = manifest.ContractCodecs ??
            throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a null Contract Codec table.");
        var dependencies = manifest.Dependencies ??
            throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a null dependency table.");
        var contractDependencies = manifest.ContractDependencies ??
            throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a null Contract dependency table.");

        var ownedContracts = new HashSet<Type>();
        for (var index = 0; index < contracts.Count; index++)
        {
            var contract = contracts[index] ??
                throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a null Contract descriptor at index {index}.");
            var contractType = contract.ContractType ??
                throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a Contract descriptor without a Contract Type at index {index}.");
            if (!ReferenceEquals(contractType.Assembly, ownerAssembly))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains Contract '{contractType.FullName}' that is not owned by the manifest assembly.");
            }
            if (!ownedContracts.Add(contractType))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains duplicate Contract Type '{contractType.FullName}'.");
            }
        }

        ValidateFactories(ownerAssembly, "global", codecs);
        ValidateFactories(ownerAssembly, "Contract assembly", contractCodecs);
        ValidateDependencies(ownerAssembly, "global", dependencies);
        ValidateDependencies(ownerAssembly, "Contract assembly", contractDependencies);
    }

    private static void ValidateFactories(
        Assembly ownerAssembly,
        string scope,
        IReadOnlyList<IRpcGeneratedCodecFactory> factories)
    {
        var targets = new HashSet<Type>();
        for (var index = 0; index < factories.Count; index++)
        {
            var factory = factories[index] ??
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains a null Codec factory at index {index} in the {scope} graph.");
            var targetType = factory.TargetType ??
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains a Codec factory without a target Type in the {scope} graph.");
            if (factory.CodecHash.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains no deterministic CodecHash for '{targetType.FullName}' in the {scope} graph.");
            }
            if (!targets.Add(targetType))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains duplicate Codec target '{targetType.FullName}' in the {scope} graph.");
            }

            var hasAdapterId = factory.AdapterId is not null;
            var hasAdapter = factory.Adapter is not null;
            if (hasAdapterId != hasAdapter ||
                (hasAdapterId && string.IsNullOrWhiteSpace(factory.AdapterId)))
            {
                throw new InvalidOperationException(
                    $"Codec factory for '{targetType.FullName}' in the {scope} graph has inconsistent adapter metadata.");
            }
        }
    }

    private static void ValidateDependencies(
        Assembly ownerAssembly,
        string scope,
        IReadOnlyList<string> dependencies)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < dependencies.Count; index++)
        {
            var dependency = dependencies[index];
            if (string.IsNullOrWhiteSpace(dependency) || !identities.Add(dependency))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains an empty or duplicate dependency at index {index} in the {scope} dependency table.");
            }

            try
            {
                var identity = new AssemblyName(dependency);
                if (string.IsNullOrWhiteSpace(identity.Name))
                    throw new ArgumentException("Assembly identity has no simple name.", nameof(dependency));
            }
            catch (Exception exception) when (exception is ArgumentException or FileLoadException)
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains invalid dependency identity '{dependency}' in the {scope} dependency table.",
                    exception);
            }
        }
    }
}
