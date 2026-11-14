using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

/// <summary>Installs a real Session-local binding for private-dispatch ownership tests.</summary>
internal static class ServerBindingTestFixture
{
    internal static ulong Install(
        ServerConnectionState connection,
        ServiceRegistration registration,
        RpcMethodDescriptor facts)
    {
        var contract = new SharpLinkGeneratedContractDescriptor(
            typeof(ITestContract), nameof(ITestContract), facts.ContractId, new string('a', 64),
            [new SharpLinkGeneratedMethodDescriptor(
                "TestMethod", facts.MethodId, facts.Kind, facts.SupportsCancellation,
                "test-request", "test-response", new string('b', 64)) { Facts = facts }],
            static (_, _) => throw new NotSupportedException(),
            _ => registration.Stub);
        var ack = connection.StaticBindings.Install(facts.ContractId, registration, contract, generation: 1);
        if (ack.Failure != StaticBindingFailure.None)
            throw new InvalidOperationException($"Cannot install dispatcher fixture binding: {ack.Failure}.");
        return ack.Token;
    }

    private interface ITestContract : IService;
}
