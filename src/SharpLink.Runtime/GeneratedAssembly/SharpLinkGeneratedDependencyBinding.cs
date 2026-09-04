using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.Runtime;

internal static class SharpLinkGeneratedDependencyBinding
{
    internal static Assembly? Resolve(Assembly ownerAssembly, string dependencyIdentity)
    {
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        if (string.IsNullOrWhiteSpace(dependencyIdentity))
            return null;
        if (string.Equals(ownerAssembly.FullName, dependencyIdentity, StringComparison.Ordinal))
            return ownerAssembly;

        AssemblyName requested;
        try
        {
            requested = new AssemblyName(dependencyIdentity);
        }
        catch (Exception exception) when (exception is ArgumentException or FileLoadException)
        {
            return null;
        }

        // The generated manifest already records the compile-time dependency identity. Resolve that
        // identity through the owner's load context so the result stays bound to the exact runtime
        // assembly generation without depending on Assembly.GetReferencedAssemblies(), whose metadata
        // view is not preserved by trimming/NativeAOT. Delegate loaded-assembly reuse to the ALC binder,
        // then verify every identity component that the dependency string actually specified: a custom
        // ALC can return an already-loaded same-name assembly even when its version is incompatible.
        var loadContext = AssemblyLoadContext.GetLoadContext(ownerAssembly);
        if (loadContext is null)
            return null;

        try
        {
            var resolved = loadContext.LoadFromAssemblyName(requested);
            return MatchesRequestedIdentity(requested, resolved.GetName(), dependencyIdentity)
                ? resolved
                : null;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private static bool MatchesRequestedIdentity(
        AssemblyName requested,
        AssemblyName resolved,
        string dependencyIdentity)
    {
        if (!string.Equals(requested.Name, resolved.Name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (requested.Version is not null && requested.Version != resolved.Version)
            return false;
        if (dependencyIdentity.Contains("Culture=", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                requested.CultureName ?? string.Empty,
                resolved.CultureName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (dependencyIdentity.Contains("PublicKeyToken=", StringComparison.OrdinalIgnoreCase) &&
            !PublicKeyTokensEqual(requested.GetPublicKeyToken(), resolved.GetPublicKeyToken()))
        {
            return false;
        }
        return true;
    }

    private static bool PublicKeyTokensEqual(byte[]? left, byte[]? right)
    {
        left ??= [];
        right ??= [];
        return left.AsSpan().SequenceEqual(right);
    }

    internal static bool Matches(
        Assembly ownerAssembly,
        string dependencyIdentity,
        Assembly candidateAssembly)
        => ReferenceEquals(Resolve(ownerAssembly, dependencyIdentity), candidateAssembly);

    internal static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        foreach (var dependency in manifest.Dependencies)
        {
            if (Matches(manifest.OwnerAssembly, dependency, ownerAssembly))
                return true;
        }
        foreach (var dependency in manifest.ContractDependencies)
        {
            if (Matches(manifest.OwnerAssembly, dependency, ownerAssembly))
                return true;
        }
        if (manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest ||
            dependencyManifest.ReferencedCodecDependencies is not { } referencedDependencies)
        {
            return false;
        }
        for (var index = 0; index < referencedDependencies.Count; index++)
        {
            var dependency = referencedDependencies[index];
            if (dependency is not null &&
                dependency.TargetType is { } targetType &&
                ReferenceEquals(targetType.Assembly, ownerAssembly))
            {
                return true;
            }
        }
        return false;
    }

    internal static int[] GetDependantsFirstOrder(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var remaining = new bool[manifests.Count];
        Array.Fill(remaining, true);
        var order = new int[manifests.Count];
        for (var outputIndex = 0; outputIndex < order.Length; outputIndex++)
        {
            var selected = -1;
            for (var candidate = 0; candidate < manifests.Count; candidate++)
            {
                if (!remaining[candidate])
                    continue;
                var candidateAssembly = manifests[candidate].OwnerAssembly;
                var hasRemainingDependant = false;
                for (var dependant = 0; dependant < manifests.Count; dependant++)
                {
                    if (dependant == candidate || !remaining[dependant])
                        continue;
                    if (ManifestDependsOn(manifests[dependant], candidateAssembly))
                    {
                        hasRemainingDependant = true;
                        break;
                    }
                }
                if (!hasRemainingDependant)
                {
                    selected = candidate;
                    break;
                }
            }
            if (selected < 0)
            {
                for (var candidate = manifests.Count - 1; candidate >= 0; candidate--)
                {
                    if (remaining[candidate])
                    {
                        selected = candidate;
                        break;
                    }
                }
            }
            order[outputIndex] = selected;
            remaining[selected] = false;
        }
        return order;
    }
}
