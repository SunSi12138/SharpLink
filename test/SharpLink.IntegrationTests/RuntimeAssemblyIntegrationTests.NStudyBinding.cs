using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private static readonly byte[] NStudyAuthenticationPayload = "local-n-validation"u8.ToArray();

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyGeneratedCallsFollowExactBindingsAcrossNormalReplacement(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        using var contracts = PluginBundle.Load($"n-study-{transport}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(
            typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var contractLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && contractLoad.Succeeded, "actual generated manifests are available");
        var src = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = manifest!.Contracts.Single(c => c.ContractType == contracts.ContractType).ContractId;
        var identity = new NStudyContractInputs(aCid, manifest.RpcAssemblyHash, bCid, staticManifest.RpcAssemblyHash);

        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(src, provider);
        string? memoryName = transport == "shared-memory" ? $"sharplink-n-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        var retiredAlc = await GiveNStudyServiceAReplacementAsync(
            builder, src, contracts, identity, port, memoryName, trace, ct);
        // Keep both active peers running until old implementation ALC collection is observed.
        await using (retiredAlc)
        {
            for (var i = 0; retiredAlc.OldContext.IsAlive && i < 24; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (retiredAlc.OldContext.IsAlive)
                    await Task.Delay(25, ct);
            }
            Ensure(!retiredAlc.OldContext.IsAlive, "retained old token entries do not own the old implementation");
            Ensure(await retiredAlc.First.Get<IShutdownCleanupProbe>().TouchAsync(8, ct) == 108 &&
                   await retiredAlc.Second.Get<IShutdownCleanupProbe>().TouchAsync(8, ct) == 108,
                "original Sessions keep serving B after old implementation collects");
            trace.Record("OldAlcCollectedWhileBothConnectionsServeB");
        }
    }

    private static SharpLinkServerBuilder NewNStudyServer(IGeneratedManifestSource source, IServiceProvider provider)
        => SharpLinkServerBuilder.Create().UseGeneratedManifestSource(source).UseServiceProvider(provider)
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(static (request, _) => ValueTask.FromResult(
                request.Payload.Span.SequenceEqual(NStudyAuthenticationPayload)
                    ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext("n-validation"))
                    : SharpLinkAuthenticationResult.Reject())))
            .RequireAuthentication()
            .ReplaceService<IShutdownCleanupProbe>(new ShutdownCleanupProbe());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<NStudyLivePeers> GiveNStudyServiceAReplacementAsync(
        SharpLinkServerBuilder builder, IGeneratedManifestSource src,
        PluginBundle contracts, NStudyContractInputs identity, int port, string? memoryName,
        NStudyTrace trace, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetDirectoryName(contracts.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var oldLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var newLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var weak = new WeakReference(oldLoader);
        var server = (SharpLinkServer)builder.Build();
        var first = NewNStudyClient(src, port, memoryName);
        var second = NewNStudyClient(src, port, memoryName);
        var transfer = false;
        Type? oldType = null;
        try
        {
            var oldAssembly = oldLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            oldType = oldAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var newType = newAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            Ensure(server.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   first.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   second.RegisterAssembly(contracts.ContractAssembly).Succeeded,
                "all endpoints own their exact dynamic contract");
            var originalReg = server.RegisterAssembly(oldAssembly);
            Ensure(originalReg.Succeeded, $"R1 implementation registered: {originalReg.Error}");
            await server.StartAsync(ct);
            await first.ConnectAsync(ct);
            await second.ConnectAsync(ct);
            var s1 = NStudyClientSession(first);
            var s2 = NStudyClientSession(second);
            Ensure(!ReferenceEquals(s1, s2) && (memoryName is null
                ? s1.RemoteEndPoint is IPEndPoint && s2.RemoteEndPoint is IPEndPoint
                : s1.Input is SharedMemoryPipeReader && s2.Input is SharedMemoryPipeReader),
                "two independently connected physical peers use the selected real transport");
            var original = server.ReadNStudyTarget(oldAssembly, identity.A);
            var b1 = await first.PrepareNStudyContractAsync(identity.B, identity.BHash, default, ct);
            var b2 = await second.PrepareNStudyContractAsync(identity.B, identity.BHash, default, ct);
            var a1 = await first.PrepareNStudyContractAsync(identity.A, identity.AHash, original, ct);
            var a2 = await second.PrepareNStudyContractAsync(identity.A, identity.AHash, original, ct);
            var peers = ((ServerConnectionRegistry)typeof(SharpLinkServer).GetField("_connectionRegistry",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!).SnapshotActive();
            Ensure(peers.Length == 2 && peers.All(p => p.AuthenticationContext?.Subject == "n-validation") &&
                   peers.Select(p => p.NStudyPhysicalIdentity).ToHashSet()
                    .SetEquals([a1.Authority.PhysicalSession, a2.Authority.PhysicalSession]) &&
                   a1.Authority.PhysicalSession != a2.Authority.PhysicalSession &&
                   a1.Authority.Server == a2.Authority.Server && a1.Authority.RegistryEpoch == a2.Authority.RegistryEpoch &&
                   a1.Authority == b1.Authority && a2.Authority == b2.Authority &&
                   a1.Target == original && a2.Target == original &&
                   a1.BindingId == a2.BindingId && b1.BindingId == b2.BindingId &&
                   a1.BindingId != b1.BindingId && s1.CaptureNStudyBinding(identity.A) == a1.BindingId &&
                   s2.CaptureNStudyBinding(identity.A) == a2.BindingId,
                "matching slot numbers are installed independently and in their actual Session authority");
            var proxy1 = GetProxy(first, contracts.ContractType);
            var proxy2 = GetProxy(second, contracts.ContractType);
            Ensure(await InvokeValueTaskAsync<int>(proxy1, contracts.ContractType, "UnaryAsync", 6, ct) == 7 &&
                   await InvokeValueTaskAsync<int>(proxy2, contracts.ContractType, "UnaryAsync", 8, ct) == 9,
                "actual generated Unary uses both initial binds");
            await InvokeValueTaskAsync(proxy1, contracts.ContractType, "NotifyAsync", 2, ct);
            await InvokeValueTaskAsync(proxy2, contracts.ContractType, "NotifyAsync", 3, ct);
            await WaitForNStudyAsync(() => NStudyCount(oldType!, "Notifications") == 5, ct);
            trace.Record("BothPhysicalSessionsBindAndUseR1", a1, a2);

            var originalCall = InvokeValueTaskAsync<int>(proxy1, contracts.ContractType, "BlockAsync", ct).AsTask();
            await ((Task)oldType.GetProperty("BlockStarted")!.GetValue(null)!).WaitAsync(ct);
            var oldModule = GetDynamicModule(server, oldAssembly);
            var replacing = server.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(15), ct).AsTask();
            Ensure(!replacing.IsCompleted && oldModule.State == SharpLinkDynamicModuleState.Draining &&
                   oldModule.RemainingCalls == 1 && NStudyCount(oldType, "Disposed") == 0,
                "normal replacement does not retire the admitted R1 invocation");
            var next = server.ReadNStudyTarget(newAssembly, identity.A);
            Ensure(next.Registration != original.Registration && next.Publication > original.Publication,
                "same contract hash does not equate two service registrations");

            var next1 = await first.PrepareNStudyContractAsync(identity.A, identity.AHash, next, ct);
            Ensure(next1.Authority == a1.Authority && next1.Target == next && next1.BindingId != a1.BindingId &&
                   s2.CaptureNStudyBinding(identity.A) == a2.BindingId &&
                   s1.CaptureNStudyBinding(identity.B) == b1.BindingId &&
                   s2.CaptureNStudyBinding(identity.B) == b2.BindingId,
                "preparing new R2 changes only the requested contract on that Session");
            Ensure(await InvokeValueTaskAsync<int>(proxy1, contracts.ContractType, "UnaryAsync", 10, ct) == 11 &&
                   await first.Get<IShutdownCleanupProbe>().TouchAsync(2, ct) == 102 &&
                   await second.Get<IShutdownCleanupProbe>().TouchAsync(2, ct) == 102 &&
                   NStudyCount(newType, "Created") == 1 && !originalCall.IsCompleted,
                "first session and unrelated B proceed while the original service is still live");
            trace.Record("FirstSessionUsesR2WhileSecondKeepsItsOwnBinding", next1, a2);

            var next2 = await second.PrepareNStudyContractAsync(identity.A, identity.AHash, next, ct);
            Ensure(next2.Authority == a2.Authority && next2.Target == next && next2.BindingId != a2.BindingId,
                "other peer obtains its own exact successful R2 Bind");
            Ensure(await InvokeValueTaskAsync<int>(proxy2, contracts.ContractType, "UnaryAsync", 20, ct) == 21,
                "new generated Unary on the second Session reaches R2");
            await InvokeValueTaskAsync(proxy1, contracts.ContractType, "NotifyAsync", 11, ct);
            await InvokeValueTaskAsync(proxy2, contracts.ContractType, "NotifyAsync", 13, ct);
            await WaitForNStudyAsync(() => NStudyCount(newType, "Notifications") == 24, ct);
            Ensure(NStudyCount(oldType, "Notifications") == 5 &&
                   ReferenceEquals(s1, NStudyClientSession(first)) && ReferenceEquals(s2, NStudyClientSession(second)),
                "new requests reach R2 without creating a new connection or migrating previous business");
            trace.Record("BothOriginalSessionsUseNewR2", next1, next2);

            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
            Ensure(await originalCall.WaitAsync(ct) == 42, "admitted invocation finishes in the old R1 handler");
            EnsureReplacementReleased(await replacing.WaitAsync(ct), "old N replacement");
            Ensure(NStudyCount(oldType, "Disposed") == 1 && oldModule.State == SharpLinkDynamicModuleState.Released,
                "retired implementation is released once after the physical invocation");
            trace.Record("OriginalR1CompletedAndRetired");
            transfer = true;
            return new NStudyLivePeers(server, first, second, newLoader, weak);
        }
        finally
        {
            oldType?.GetMethod("ReleaseBlock")!.Invoke(null, null);
            oldType = null;
            oldLoader.Unload();
            oldLoader = null!;
            if (!transfer)
                await new NStudyLivePeers(server, first, second, newLoader, weak).DisposeAsync();
        }
    }

    private static SharpLinkClient NewNStudyClient(IGeneratedManifestSource source, int port, string? memoryName)
    {
        var builder = SharpClientBuilder.Create().UseGeneratedManifestSource(source).DisableRequestTimeout()
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(static _ =>
                ValueTask.FromResult<ReadOnlyMemory<byte>>(NStudyAuthenticationPayload)));
        if (memoryName is null)
            builder.UseTcp(IPAddress.Loopback.ToString(), port);
        else
            builder.UseSharedMemory(memoryName);
        return (SharpLinkClient)builder.Build();
    }

    private static RpcSession NStudyClientSession(SharpLinkClient client)
        => ((ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections", BindingFlags.Instance |
            BindingFlags.NonPublic)!.GetValue(client)!).Single().Session;

    private static int NStudyCount(Type type, string name) => (int)type.GetProperty(name)!.GetValue(null)!;

    private static async Task WaitForNStudyAsync(Func<bool> ready, CancellationToken ct)
    {
        while (!ready())
            await Task.Delay(5, ct);
    }

    private readonly record struct NStudyContractInputs(long A, RpcHash128 AHash, long B, RpcHash128 BHash);

    private sealed class NStudyLivePeers(SharpLinkServer server, SharpLinkClient first, SharpLinkClient second,
        SharedContractServiceLoadContext loader, WeakReference oldContext) : IAsyncDisposable
    {
        internal SharpLinkClient First { get; } = first;
        internal SharpLinkClient Second { get; } = second;
        internal WeakReference OldContext { get; } = oldContext;

        public async ValueTask DisposeAsync()
        {
            try
            {
                try { await First.DisposeAsync(); }
                finally { await Second.DisposeAsync(); }
            }
            finally
            {
                try { await server.DisposeAsync(); }
                finally { loader.Unload(); }
            }
        }
    }

    private sealed class NStudyTrace(string transport)
    {
        private int _seq;
        internal void Record(string edge, NStudyAck? first = null, NStudyAck? second = null) =>
            Console.WriteLine("NSTUDY " + JsonSerializer.Serialize(new
            {
                seq = ++_seq, transport, edge,
                first = Route(first), second = Route(second)
            }));

        private static object? Route(NStudyAck? ack) => ack is { } value
            ? new { session = value.Authority.PhysicalSession, value.BindingId,
                value.Target.Registration, value.Target.Publication }
            : null;
    }
}
