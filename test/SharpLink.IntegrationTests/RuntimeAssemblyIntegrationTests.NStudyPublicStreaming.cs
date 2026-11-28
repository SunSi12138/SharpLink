using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private static NStudyPublicStreaming<TContract> CreateNStudyPublicStreaming<TContract>(
        SharpLinkClient client, Assembly assembly, string generatedMetadata) where TContract : IService
    {
        ISharpLinkClient app = client;
        var manifest = GetDynamicModule(client, assembly).Manifest;
        var contract = manifest.Contracts.Single(item => item.ContractType == typeof(TContract));
        var local = app.GetLocalContract(assembly, contract.ContractId);
        var manual = app.CreateContractChannel(local);
        var proxy = app.CreateProxy<TContract>(new RpcProxyOptions { Metadata = Metadata(generatedMetadata) });
        var codecs = RpcGeneratedCodecResolver.GetProvider(manual.RuntimeContext, assembly);

        async ValueTask<NStudyAck> Prepare(TimeSpan timeout, CancellationToken ct)
        {
            await proxy.PrepareAsync(timeout, ct);
            var shared = manual.PrepareAsync(timeout, ct);
            Ensure(shared.IsCompletedSuccessfully, "handwritten preparation reuses the generated entry's exact binding");
            await shared;
            return NStudyClientSession(client).CaptureNStudyRoute(contract.ContractId).Acknowledgement;
        }

        return new(proxy.Service, manual, codecs, contract, Prepare);
    }

    private sealed record NStudyPublicStreaming<TContract>(TContract Service, IRpcContractChannel Channel,
        IRpcCodecProvider Codecs, SharpLinkGeneratedContractDescriptor Contract,
        Func<TimeSpan, CancellationToken, ValueTask<NStudyAck>> Prepare) where TContract : IService
    {
        internal RpcMethodDescriptor Method(string name) => Contract.Methods.Single(item => item.Name == name).Invocation;
    }
}
