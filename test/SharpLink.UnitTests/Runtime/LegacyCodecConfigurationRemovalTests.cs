using System.Linq;
using System.Reflection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

public sealed class LegacyCodecConfigurationRemovalTests
{
    [Test]
    public void LegacyStandaloneAndEndpointCodecMethodsMustNotBePublicIn3x()
    {
        var runtimeMethods = typeof(SharpLinkRuntimeContextBuilder).GetMethods(
            BindingFlags.Public | BindingFlags.Instance);
        Ensure(!runtimeMethods.Any(static method => method.Name is "AddCodec" or "UseCodecResolver"),
            "3.0 must not expose the retired standalone Codec registration or runtime resolver");
        var serverMethods = typeof(SharpLinkServerBuilder).GetMethods(
            BindingFlags.Public | BindingFlags.Instance);
        Ensure(!serverMethods.Any(static method => method.Name == "UseSerializer"),
            "3.0 must not expose a Server serializer override that is ignored by generated RPC");
    }

    [Test]
    public void CodecProviderMustNotRetainStandaloneFallbackState()
    {
        var fields = typeof(RpcCodecProvider).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Ensure(!fields.Any(static field => field.Name is "_resolver" or "_explicitCodecTypes"),
            "3.0 provider must not retain dynamic resolver state or explicit override precedence");
    }

    [Test]
    public void UnknownReferenceTypeWithoutGeneratedCodecShouldFailClosed()
    {
        using var runtime = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        try
        {
            _ = runtime.Codecs.GetCodec<UnregisteredPayload>();
            throw new Exception("Unregistered reference payload unexpectedly received a runtime Codec.");
        }
        catch (NotSupportedException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class UnregisteredPayload;
}
