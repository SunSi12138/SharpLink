using System.Linq;
using System.Reflection;

namespace SharpLink.UnitTests.Abstractions;

public class AssemblyRegistrySurfaceTests
{
    private static readonly string[] RegistryOperationNames =
    [
        nameof(ISharpLinkAssemblyRegistry.RegisterAssembly),
        nameof(ISharpLinkAssemblyRegistry.ReplaceAssemblyAsync),
        nameof(ISharpLinkAssemblyRegistry.UnregisterAssemblyAsync)
    ];

    [Test]
    public async Task ClientAndServerShouldConsumeOneAssemblyRegistryContract()
    {
        var registry = typeof(ISharpLinkAssemblyRegistry);

        await Assert.That(registry.IsAssignableFrom(typeof(ISharpLinkClient))).IsTrue();
        await Assert.That(registry.IsAssignableFrom(typeof(ISharpLinkServer))).IsTrue();

        var declaredRegistryOperations = registry
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        await Assert.That(declaredRegistryOperations).IsEquivalentTo(RegistryOperationNames);

        foreach (var rootInterface in new[] { typeof(ISharpLinkClient), typeof(ISharpLinkServer) })
        {
            var duplicatedOperations = rootInterface
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => RegistryOperationNames.Contains(method.Name, StringComparer.Ordinal))
                .ToArray();
            await Assert.That(duplicatedOperations.Length).IsEqualTo(0);
        }
    }
}
