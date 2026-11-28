using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyPublicGeneratedAndHandwrittenCallersSharePreparation(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(typeof(INStudyUnarySiteProbe).Assembly, out var manifest);
        Ensure(loaded.Succeeded, "the genuine generated manifest loads");
        var contract = manifest!.Contracts.Single(c => c.ContractType == typeof(INStudyUnarySiteProbe));
        var source = new FixedGeneratedManifestSource([manifest]);
        var observer = new NStudyUnarySiteObserver(contract.ContractId);
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider).AddInterceptor(observer);
        var memory = transport == "shared-memory" ? $"sharplink-public-proxy-{Guid.NewGuid():N}" : null;
        if (memory is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memory);
        var port = memory is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var built = NewNStudyClient(source, port, memory);
        ISharpLinkClient client = built;
        var handle = client.CreateProxy<INStudyUnarySiteProbe>(new RpcProxyOptions { Metadata = Metadata("public-proxy") });
        var service = handle.Service;
        var local = memory is null
            ? client.GetLocalContract<INStudyUnarySiteProbe>()
            : client.GetLocalContract(typeof(INStudyUnarySiteProbe).Assembly, contract.ContractId);
        var manual = client.CreateContractChannel(local, new RpcProxyOptions { Metadata = Metadata("handwritten-default") });
        var method = contract.Methods.Single().Invocation;
        var requestCodec = new NStudyManualIntRequestCodec();
        var responseCodec = RpcGeneratedCodecResolver.GetProvider(manual.RuntimeContext,
            typeof(INStudyUnarySiteProbe).Assembly).GetCodec<int>();
        Ensure(client.State == SharpLinkConnectionState.Created && built.ReadyConnectionCount == 0 &&
               built.PendingCallCount == 0 && observer.Calls.IsEmpty && ReferenceEquals(client.RuntimeContext, manual.RuntimeContext),
            "both factories are local and the handwritten caller keeps the original runtime context");
        trace.Record("PublicFactoriesAreLocalAndShareTheRuntimeContext");

        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        var session = NStudyClientSession(built);
        var peer = NStudyServerConnection(server);
        Ensure(session.NStudyBindOperationForDiagnostics == 0 && peer.NStudyBindings.Snapshot() == (0, 0, 0) &&
               peer.AuthenticationContext?.Subject == "n-validation" &&
               (memory is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
            "ordinary connect leaves the real authenticated physical Session unbound");
        trace.Record("ConnectDoesNotPrepareEitherPublicCaller");

        if (memory is null)
            await handle.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        else
            await manual.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var binding = session.CaptureNStudyBinding(contract.ContractId);
        Ensure(session.NStudyBindOperationForDiagnostics == 1 && peer.NStudyBindings.Snapshot() == (1, 0, 0) &&
               observer.Calls.IsEmpty && ReferenceEquals(handle.Service, service),
            "public Prepare installs exactly one route without calling business or replacing the proxy");
        trace.Record("EitherPublicCallerCanPrepareWithoutBusinessWork");

        Ensure(await service.ReportAsync(42, ct) == 142, "the pre-created service uses that actual route");
        var ready = memory is null ? manual.PrepareAsync(TimeSpan.FromSeconds(5), ct) : handle.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        Ensure(ready.IsCompletedSuccessfully, "the other public caller reuses the same preparation synchronously");
        await ready;
        Ensure(await manual.InvokeUnaryAsync(method, 43, requestCodec, responseCodec, null, ct) == 143 &&
               await manual.InvokeUnaryAsync(method, 44, requestCodec, responseCodec, Metadata("handwritten-per-call"), ct) == 144 &&
               requestCodec.Writes == 2 && observer.Calls.Select(call => call.Label)
                   .SequenceEqual(["public-proxy", "handwritten-default", "handwritten-per-call"]) &&
               observer.Calls.All(call => call.Method == method && call.HasDeadline) &&
               session.NStudyBindOperationForDiagnostics == 1 && session.CaptureNStudyBinding(contract.ContractId) == binding,
            "one prepared route serves actual generated and handwritten calls with original metadata, method facts and deadline");
        trace.Record("GeneratedAndHandwrittenUseOneExactRouteWithTheirOwnMetadata");
    }
}
