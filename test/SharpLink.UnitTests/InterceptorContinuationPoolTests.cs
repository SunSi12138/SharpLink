using System.Reflection;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests;

public sealed class InterceptorContinuationPoolTests
{
    [Test]
    public void ClientPipelineShouldUsePublishedGenerationWithoutLegacyContinuationState()
    {
        Ensure(
            typeof(SharpLinkClient).GetNestedType("ClientInterceptorGeneration", BindingFlags.NonPublic) is not null,
            "cannot find Client interceptor generation");
        Ensure(
            !ContainsNestedType(typeof(SharpLinkClient), "ClientContinuationState"),
            "Client pipeline should not retain the legacy per-RPC continuation state");
    }

    [Test]
    public void ServerPipelineShouldUsePublishedGenerationWithoutLegacyContinuationState()
    {
        Ensure(
            typeof(SharpLinkServer).GetNestedType("ServerInterceptorGeneration", BindingFlags.NonPublic) is not null,
            "cannot find Server interceptor generation");
        Ensure(
            !ContainsNestedType(typeof(SharpLinkServer), "ServerContinuationState"),
            "Server pipeline should not retain the legacy per-RPC continuation state");
    }

    private static bool ContainsNestedType(Type rootType, string nestedTypeName)
    {
        foreach (var nestedType in rootType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (nestedType.Name == nestedTypeName || ContainsNestedType(nestedType, nestedTypeName))
                return true;
        }

        return false;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
