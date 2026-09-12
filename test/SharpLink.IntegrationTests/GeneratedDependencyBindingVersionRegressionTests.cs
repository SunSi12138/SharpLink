using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.IntegrationTests;

public sealed class GeneratedDependencyBindingVersionRegressionTests
{
    [Test]
    [NotInParallel]
    public void LoadedLowerVersionMustNotSatisfyHigherGeneratedDependencyIdentity()
    {
        var directory = GetProjectOutputDirectory("SharpLink.ModuleDependencyConsumer");
        var loadContext = new DirectoryLoadContext("generated-dependency-version-binding", directory);
        try
        {
            var provider = loadContext.LoadFromAssemblyPath(
                Path.Combine(directory, "SharpLink.ReferencedCodecProvider.dll"));
            var owner = loadContext.LoadFromAssemblyPath(
                Path.Combine(directory, "SharpLink.ModuleDependencyConsumer.dll"));

            var requested = new AssemblyName(provider.FullName!);
            var loadedVersion = requested.Version ?? new Version(0, 0, 0, 0);
            requested.Version = new Version(
                checked(loadedVersion.Major + 1),
                loadedVersion.Minor,
                Math.Max(loadedVersion.Build, 0),
                Math.Max(loadedVersion.Revision, 0));

            var resolved = SharpLinkGeneratedDependencyBinding.Resolve(owner, requested.FullName!);
            if (resolved is not null)
            {
                throw new Exception(
                    $"Generated dependency '{requested.FullName}' must not bind to already-loaded lower/incompatible assembly '{resolved.FullName}'.");
            }
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static string GetProjectOutputDirectory(string projectName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
        return Path.Combine(
            directory.FullName,
            "test",
            projectName,
            "bin",
            "Release",
            "net10.0");
    }

    private sealed class DirectoryLoadContext(string name, string directory)
        : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = Default.Assemblies.FirstOrDefault(candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
            if (shared is not null)
                return shared;
            var path = Path.Combine(directory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
