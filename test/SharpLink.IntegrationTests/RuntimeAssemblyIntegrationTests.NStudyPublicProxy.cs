using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyPublicProxyPreparesTheServiceItCreatedBeforeConnecting(string transport)
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
        Ensure(client.State == SharpLinkConnectionState.Created && built.ReadyConnectionCount == 0 &&
               built.PendingCallCount == 0 && observer.Calls.IsEmpty,
            "the public factory returns a local service before connecting or sending any requests");
        trace.Record("PublicFactoryReturnsLocalServiceBeforeConnection");

        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        var session = NStudyClientSession(built);
        var peer = NStudyServerConnection(server);
        Ensure(session.NStudyBindOperationForDiagnostics == 0 && peer.NStudyBindings.Snapshot() == (0, 0, 0) &&
               peer.AuthenticationContext?.Subject == "n-validation" &&
               (memory is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
            "ordinary connect leaves the real authenticated physical Session unbound");
        trace.Record("ConnectDoesNotPrepareThePublicProxy");

        await handle.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var binding = session.CaptureNStudyBinding(contract.ContractId);
        Ensure(session.NStudyBindOperationForDiagnostics == 1 && peer.NStudyBindings.Snapshot() == (1, 0, 0) &&
               observer.Calls.IsEmpty && ReferenceEquals(handle.Service, service),
            "public Prepare installs exactly one route without calling business or replacing the proxy");
        trace.Record("PublicPrepareBindsExactlyOnceWithoutSendingBusiness");

        Ensure(await service.ReportAsync(42, ct) == 142, "the pre-created service uses that actual route");
        var ready = handle.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        Ensure(ready.IsCompletedSuccessfully, "already prepared public handle stays synchronous");
        await ready;
        Ensure(await handle.Service.ReportAsync(43, ct) == 143 &&
               observer.Calls.Select(call => call.Label).SequenceEqual(["public-proxy", "public-proxy"]) &&
               session.NStudyBindOperationForDiagnostics == 1 && session.CaptureNStudyBinding(contract.ContractId) == binding,
            "the same prepared service reuses its exact binding and captures the original public metadata");
        trace.Record("PublicServiceReusesItsBindingAndPreservesMetadata");
    }
}
