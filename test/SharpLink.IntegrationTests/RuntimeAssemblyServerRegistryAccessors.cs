using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private static SharpLinkDynamicModule GetDynamicModule(ISharpLinkServer owner, Assembly assembly)
        => ServerRegistryTestAccessor.DynamicModule(owner, assembly);
}
