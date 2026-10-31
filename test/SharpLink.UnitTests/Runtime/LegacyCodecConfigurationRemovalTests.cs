using System.Linq;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

public sealed class LegacyCodecConfigurationRemovalTests
{
    [Test]
    public void LegacyStandaloneAndEndpointCodecMethodsMustNotBePublicIn3x()
    {
        const BindingFlags instanceMethods =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var runtimeMethods = typeof(SharpLinkRuntimeContextBuilder).GetMethods(instanceMethods);
        Ensure(!runtimeMethods.Any(static method => method.Name is "AddCodec" or "UseCodecResolver"),
            "3.0 must not restore standalone Codec registration or runtime fallback resolver methods");

        var clientMethods = typeof(SharpClientBuilder).GetMethods(instanceMethods);
        Ensure(!clientMethods.Any(static method => method.Name is "UseCodec" or "UseSerializer"),
            "3.0 Client Builder must not reintroduce runtime Codec/Serializer injection");

        var serverMethods = typeof(SharpLinkServerBuilder).GetMethods(instanceMethods);
        Ensure(!serverMethods.Any(static method => method.Name is "UseCodec" or "UseSerializer"),
            "3.0 Server Builder must not reintroduce a wire Codec/Serializer override");
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
