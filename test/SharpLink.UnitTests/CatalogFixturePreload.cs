using System.Runtime.CompilerServices;

namespace SharpLink.UnitTests;

/// <summary>
/// Loads the generated contract fixture assemblies before any test executes. Their generated
/// manifests register into the global catalogs from a [ModuleInitializer], so loading them lazily
/// mid-suite would race the weak-catalog tests that snapshot and restore catalog counts. Forcing
/// the load here makes those registrations stable and visible to every before/after snapshot.
/// </summary>
internal static class GeneratedFixturePreload
{
    [ModuleInitializer]
    internal static void PreloadGeneratedFixtureAssemblies()
    {
        RuntimeHelpers.RunModuleConstructor(
            typeof(SharpLink.MultiClusterTest.Contracts.IOrdersContract).Assembly.ManifestModule.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(
            typeof(SharpLink.StaticCodecOwnerTest.Contracts.IContractA).Assembly.ManifestModule.ModuleHandle);
    }
}
