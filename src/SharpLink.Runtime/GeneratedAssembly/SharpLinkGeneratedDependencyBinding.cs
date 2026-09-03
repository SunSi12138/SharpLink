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

        AssemblyName? reference = null;
        foreach (var candidate in ownerAssembly.GetReferencedAssemblies())
        {
            if (!AssemblyName.ReferenceMatchesDefinition(candidate, requested))
                continue;
            reference = candidate;
            break;
        }
        if (reference is null)
            return null;

        var loadContext = AssemblyLoadContext.GetLoadContext(ownerAssembly);
        if (loadContext is null)
            return null;
        foreach (var loaded in loadContext.Assemblies)
        {
            if (AssemblyName.ReferenceMatchesDefinition(loaded.GetName(), reference))
                return loaded;
        }

        try
        {
            return loadContext.LoadFromAssemblyName(reference);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    internal static bool Matches(
        Assembly ownerAssembly,
        string dependencyIdentity,
        Assembly candidateAssembly)
        => ReferenceEquals(Resolve(ownerAssembly, dependencyIdentity), candidateAssembly);
}
