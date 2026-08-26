using System.Reflection;

namespace SharpLink.Runtime;

internal static class SharpLinkGeneratedManifestStructureValidator
{
    internal static void ValidateContractCodecSets(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var ownerAssembly = manifest.OwnerAssembly ??
            throw new InvalidOperationException("Generated manifest has no owner assembly.");
        var contracts = manifest.Contracts ??
            throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a null Contract table.");
        var sets = manifest.ContractCodecSets ??
            throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a null Contract Codec set table.");

        // The default-empty interface member remains a compatibility surface for hand-written
        // manifests that do not participate in API-5 Contract Codec ownership. Only manifests that
        // actually publish Contract Codec sets opt into the exact per-Contract ownership table.
        if (sets.Count == 0)
            return;

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

        var seenContracts = new HashSet<Type>();
        for (var setIndex = 0; setIndex < sets.Count; setIndex++)
        {
            var set = sets[setIndex] ??
                throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a null Contract Codec set at index {setIndex}.");
            var contractType = set.ContractType ??
                throw new InvalidOperationException($"Generated manifest '{ownerAssembly.FullName}' has a Contract Codec set without a Contract Type at index {setIndex}.");
            if (!ownedContracts.Contains(contractType))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains a Contract Codec set for foreign or undeclared Contract '{contractType.FullName}'.");
            }
            if (!seenContracts.Add(contractType))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains duplicate Contract Codec sets for '{contractType.FullName}'.");
            }
            if (set.Codecs is null)
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains a null Codec table for Contract '{contractType.FullName}'.");
            }
            if (set.Dependencies is null)
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains a null dependency table for Contract '{contractType.FullName}'.");
            }

            ValidateFactories(ownerAssembly, contractType, set.Codecs);
            ValidateDependencies(ownerAssembly, contractType, set.Dependencies);
        }

        if (seenContracts.Count != ownedContracts.Count)
        {
            var missing = ownedContracts
                .Where(contractType => !seenContracts.Contains(contractType))
                .OrderBy(static contractType => contractType.FullName, StringComparer.Ordinal)
                .Select(static contractType => contractType.FullName ?? contractType.Name);
            throw new InvalidOperationException(
                $"Generated manifest '{ownerAssembly.FullName}' does not contain exactly one Contract Codec set for every Contract. Missing: {string.Join(", ", missing)}.");
        }
    }

    private static void ValidateFactories(
        Assembly ownerAssembly,
        Type contractType,
        IReadOnlyList<IRpcGeneratedCodecFactory> factories)
    {
        var targets = new HashSet<Type>();
        for (var index = 0; index < factories.Count; index++)
        {
            var factory = factories[index] ??
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains a null Codec factory at index {index} for Contract '{contractType.FullName}'.");
            var targetType = factory.TargetType ??
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains a Codec factory without a target Type for Contract '{contractType.FullName}'.");
            if (string.IsNullOrWhiteSpace(factory.SchemaId) || string.IsNullOrWhiteSpace(factory.WireFormatId))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains incomplete Codec identity for '{targetType.FullName}' in Contract '{contractType.FullName}'.");
            }
            if (!targets.Add(targetType))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains duplicate Codec target '{targetType.FullName}' in Contract '{contractType.FullName}'.");
            }

            switch (factory.Kind)
            {
                case RpcGeneratedCodecFactoryKind.Native:
                    if (factory.AdapterId is not null || factory.Adapter is not null ||
                        !string.Equals(factory.WireFormatId, "sharplink-native/v1", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Native Codec factory for '{targetType.FullName}' in Contract '{contractType.FullName}' has invalid adapter or wire-format metadata.");
                    }
                    break;
                case RpcGeneratedCodecFactoryKind.Direct:
                    if (factory.AdapterId is not null || factory.Adapter is not null)
                    {
                        throw new InvalidOperationException(
                            $"Direct Codec factory for '{targetType.FullName}' in Contract '{contractType.FullName}' cannot declare adapter metadata.");
                    }
                    break;
                case RpcGeneratedCodecFactoryKind.Adapter:
                    if (string.IsNullOrWhiteSpace(factory.AdapterId) || factory.Adapter is null)
                    {
                        throw new InvalidOperationException(
                            $"Adapter Codec factory for '{targetType.FullName}' in Contract '{contractType.FullName}' has incomplete adapter metadata.");
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Codec factory for '{targetType.FullName}' in Contract '{contractType.FullName}' has unknown factory kind '{factory.Kind}'.");
            }
        }
    }

    private static void ValidateDependencies(
        Assembly ownerAssembly,
        Type contractType,
        IReadOnlyList<string> dependencies)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < dependencies.Count; index++)
        {
            var dependency = dependencies[index];
            if (string.IsNullOrWhiteSpace(dependency) || !identities.Add(dependency))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{ownerAssembly.FullName}' contains an empty or duplicate dependency at index {index} for Contract '{contractType.FullName}'.");
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
                    $"Generated manifest '{ownerAssembly.FullName}' contains invalid dependency identity '{dependency}' for Contract '{contractType.FullName}'.",
                    exception);
            }
        }
    }
}
