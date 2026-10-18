using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Builder;

public sealed class Issue805SealedBuilderSurfaceTests
{
    [Test]
    public void ClientAndServerBuildersShouldExposeConfigurationButNotInheritance()
    {
        var clientType = typeof(SharpClientBuilder);
        var serverType = typeof(SharpLinkServerBuilder);
        if (!clientType.IsPublic || !clientType.IsSealed ||
            !serverType.IsPublic || !serverType.IsSealed)
        {
            throw new Exception(
                "Concrete client/server builders must remain public, but external subclassing is not supported.");
        }

        if (!typeof(ISharpLinkServerBuilder).IsAssignableFrom(serverType) ||
            !typeof(ISharpLinkServerBuilder).IsInterface)
        {
            throw new Exception("Sealing the concrete Server builder must preserve its SPI interface.");
        }

        var client = SharpClientBuilder.Create().DisableRequestTimeout().UseProtocol(static _ => { });
        var server = SharpLinkServerBuilder.Create().UseProtocol(static _ => { });
        if (client.GetType() != clientType || server.GetType() != serverType)
            throw new Exception("Public fluent configuration must retain the concrete builder type.");

        if (clientType.GetConstructor(Type.EmptyTypes) is not { IsPublic: true } ||
            serverType.GetConstructor(Type.EmptyTypes) is not { IsPublic: true })
        {
            throw new Exception("Sealing must not remove public constructor/accessor compatibility.");
        }
    }
}
