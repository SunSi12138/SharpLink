using System.Reflection;

namespace SharpLink.Server;

/// <summary>Cold compatibility checks for the deliberately immutable Contract/Codec boundary.</summary>
internal static class ServerImplementationReplacementValidation
{
    internal static void Validate(
        IReadOnlyList<SharpLinkDynamicModule> oldModules,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> incoming,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> retained)
    {
        var replacements = new Dictionary<Assembly, Assembly>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < oldModules.Count; index++)
        {
            EnsureImplementationOnly(oldModules[index].Manifest);
            EnsureImplementationOnly(incoming[index]);
            replacements.Add(oldModules[index].Assembly, incoming[index].OwnerAssembly);
        }
        var target = retained.Concat(incoming).ToDictionary(static manifest => manifest.OwnerAssembly);
        var contractOwners = retained.SelectMany(manifest => manifest.Contracts.Select(contract =>
            (Contract: contract, Manifest: manifest))).ToDictionary(static entry => entry.Contract.ContractId);
        for (var index = 0; index < oldModules.Count; index++)
        {
            var previous = oldModules[index].Manifest;
            var next = incoming[index];
            var previousServices = previous.Services.ToDictionary(static service => service.ContractId);
            if (previousServices.Count == 0 || previousServices.Count != next.Services.Count)
                throw new InvalidOperationException("Each replacement must preserve its nonempty service route set.");
            foreach (var service in next.Services)
            {
                if (!previousServices.TryGetValue(service.ContractId, out var oldService) ||
                    !ReferenceEquals(oldService.ContractType, service.ContractType) ||
                    !contractOwners.TryGetValue(service.ContractId, out var owner) ||
                    !ReferenceEquals(owner.Contract.ContractType, service.ContractType) ||
                    !ReferenceEquals(owner.Manifest.OwnerAssembly, service.ContractType.Assembly) ||
                    owner.Manifest.RpcAssemblyHash.IsEmpty)
                {
                    throw new InvalidOperationException(
                        $"Service '{service.ImplementationName}' must retain its exact registered Contract Type and nonempty owning RPC hash.");
                }
                // The implementation fingerprint intentionally need not match. Compatibility belongs
                // to the exact retained Contract owner, not the service assembly's unrelated hash.
                SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(owner.Manifest);
                SharpLinkGeneratedManifestStructureValidator.Validate(owner.Manifest);
            }
            var expected = BoundDependencies(previous).Select(dependency =>
                replacements.GetValueOrDefault(dependency, dependency)).ToHashSet();
            if (!expected.SetEquals(BoundDependencies(next)))
                throw new InvalidOperationException("Replacement changed the exact generated dependency closure.");
        }
        foreach (var manifest in target.Values)
        {
            foreach (var dependency in BoundDependencies(manifest))
            {
                if (!target.ContainsKey(dependency))
                    throw new InvalidOperationException(
                        $"Generated dependency of '{manifest.OwnerAssembly.FullName}' is absent from the exact target catalog.");
            }
        }
        // Cycles have no valid dependent-first release order; the generic legacy helper tolerates
        // them, but this new transaction rejects rather than silently falling back to an order.
        _ = DependantsFirst(oldModules.Select(static module => module.Manifest).ToArray());
        _ = DependantsFirst(incoming);
    }

    internal static void EnsureImplementationOnly(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(manifest);
        SharpLinkGeneratedManifestStructureValidator.Validate(manifest);
        if (manifest.Contracts.Count != 0 || manifest.Codecs.Count != 0 || manifest.ContractCodecs.Count != 0)
            throw new InvalidOperationException(
                "Compatible implementation maintenance cannot replace a Contract or Codec owner.");
    }

    internal static int[] DependantsFirst(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
    {
        var dependencies = manifests.Select(BoundDependencies).ToArray();
        var remaining = Enumerable.Range(0, manifests.Count).ToHashSet();
        var result = new int[manifests.Count];
        for (var output = 0; output < result.Length; output++)
        {
            var selected = -1;
            foreach (var index in remaining)
            {
                if (remaining.Any(other => other != index &&
                    dependencies[other].Contains(manifests[index].OwnerAssembly)))
                    continue;
                selected = index;
                break;
            }
            if (selected < 0)
                throw new InvalidOperationException("The implementation replacement contains a generated dependency cycle.");
            result[output] = selected;
            remaining.Remove(selected);
        }
        return result;
    }

    private static HashSet<Assembly> BoundDependencies(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        var result = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
        foreach (var identity in manifest.Dependencies.Concat(manifest.ContractDependencies))
        {
            var dependency = SharpLinkGeneratedDependencyBinding.Resolve(manifest.OwnerAssembly, identity)
                ?? throw new InvalidOperationException($"Generated dependency '{identity}' cannot be bound exactly.");
            if (!ReferenceEquals(dependency, manifest.OwnerAssembly))
                result.Add(dependency);
        }
        if (manifest is ISharpLinkReferencedCodecDependencyManifest referenced)
        {
            foreach (var dependency in referenced.ReferencedCodecDependencies)
            {
                if (!ReferenceEquals(dependency.TargetType.Assembly, manifest.OwnerAssembly))
                    result.Add(dependency.TargetType.Assembly);
            }
        }
        return result;
    }
}
