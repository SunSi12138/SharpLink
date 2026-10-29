using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private const string PhysicalProbeService = "SharpLink.DynamicPlugin.PhysicalOwnershipProbeService";
    private static readonly TimeSpan PhysicalPhaseTimeout = TimeSpan.FromSeconds(5);

    [Test]
    [NotInParallel]
    [Arguments("deadline")]
    [Arguments("caller-cancellation")]
    [Arguments("module-retirement")]
    public async Task DynamicStreamTerminalMustRetainPhysicalOwnersThroughSequentialDisposal(string terminal)
    {
        var (harness, clientClock, serverClock) = await CreatePhysicalOwnershipHarnessAsync();
        await using var harnessLifetime = harness;
        var weakContext = await ExercisePhysicalOwnershipAndUnloadAsync(harness, clientClock, serverClock, terminal);
        for (var attempt = 0; attempt < 20 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        Ensure(!weakContext.IsAlive,
            "physical cleanup must release its collectible ALC while the client and server remain alive");
        Ensure(harness.Client.State == SharpLinkConnectionState.Ready,
            "physical cleanup and collection must not require session teardown");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ExercisePhysicalOwnershipAndUnloadAsync(
        DynamicHarness harness, PhysicalOwnershipClock clientClock, PhysicalOwnershipClock serverClock, string terminal)
    {
        using var plugin = PluginBundle.Load($"physical-stream-{terminal}");
        plugin.InvokeServiceStatic(PhysicalProbeService, "Reset");
        await RegisterAllAsync(harness, plugin);
        var contract = plugin.GetContractType("SharpLink.DynamicPlugin.IPhysicalOwnershipProbe");
        await WaitForRemoteContractManifestAsync(harness.Client, contract);
        var proxy = GetProxy(harness.Client, contract);
        var module = GetDynamicModule(harness.Server, plugin.ServiceAssembly);
        using var callerCancellation = new CancellationTokenSource();
        var method = terminal == "module-retirement" ? "CooperativeStreamAsync" : "DeadlineStreamAsync";
        await using var reader = InvokeStream(proxy, contract, method, callerCancellation.Token)
            .GetAsyncEnumerator();
        Task<bool>? pendingMove = null;
        try
        {
            Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(PhysicalPhaseTimeout) && reader.Current == 1,
                "physical ownership stream receives its initial element");
            await plugin.GetServiceStaticTask(PhysicalProbeService, "WorkStarted").WaitAsync(PhysicalPhaseTimeout);
            var next = pendingMove = reader.MoveNextAsync().AsTask();
            SharpLinkAssemblyUnregisterResult retirement = default;
            if (terminal == "module-retirement")
                retirement = await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.Zero);
            else if (terminal == "caller-cancellation")
                await callerCancellation.CancelAsync();

            if (terminal == "deadline")
            {
                serverClock.Advance(TimeSpan.FromSeconds(1));
                await WaitUntilAsync(() => plugin.GetServiceStaticInt(PhysicalProbeService, "WorkCancelled") == 1);
                clientClock.Advance(TimeSpan.FromSeconds(1));
            }

            Exception? failure = null;
            try { await next.WaitAsync(PhysicalPhaseTimeout); }
            catch (Exception exception) { failure = exception; }
            Ensure(terminal switch
            {
                "deadline" => failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "module-retirement" => failure is SharpLinkException { Code: SharpLinkErrorCode.Unavailable },
                _ => failure is OperationCanceledException
            }, $"the {terminal} terminal is observable before physical cleanup, actual {failure}");

            if (terminal != "module-retirement")
                retirement = await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.Zero);
            Ensure(!retirement.ReferencesReleased && retirement.RemainingCalls == 1 && retirement.RemainingStreams == 1,
                "retirement must report the still-owned generated operation, not false reclamation");
            Ensure(module.RemainingCalls == 1 && module.RemainingStreams == 1,
                "the original module lease remains held after public terminal completion");
            Ensure(plugin.GetServiceStaticInt(PhysicalProbeService, "ServiceDisposed") == 0,
                "the per-call service scope remains alive while generated work is outstanding");
            if (terminal != "module-retirement")
                Ensure(!plugin.GetServiceStaticTask(PhysicalProbeService, "DisposeStarted").IsCompleted,
                    "DisposeAsync must not overlap an outstanding MoveNextAsync");

            plugin.InvokeServiceStatic(PhysicalProbeService, "ReleaseWork");
            await plugin.GetServiceStaticTask(PhysicalProbeService, "WorkFinished").WaitAsync(PhysicalPhaseTimeout);
            await plugin.GetServiceStaticTask(PhysicalProbeService, "DisposeStarted").WaitAsync(PhysicalPhaseTimeout);
            Ensure(plugin.GetServiceStaticInt(PhysicalProbeService, "ConcurrentDispose") == 0,
                "generated disposal starts only after physical MoveNext completion");
            Ensure(module.RemainingCalls == 1 && module.RemainingStreams == 1 &&
                   plugin.GetServiceStaticInt(PhysicalProbeService, "ServiceDisposed") == 0,
                "asynchronous disposal itself retains the module and per-call service scope");
            Ensure(((SharpLinkServer)harness.Server).ActiveCallCountForDiagnostics == 1,
                "the existing server request permit remains a physical owner through disposal");

            plugin.InvokeServiceStatic(PhysicalProbeService, "ReleaseDispose");
            await plugin.GetServiceStaticTask(PhysicalProbeService, "DisposeFinished").WaitAsync(PhysicalPhaseTimeout);
            await WaitUntilAsync(() => module.State == SharpLinkDynamicModuleState.Released);
            Ensure(plugin.GetServiceStaticInt(PhysicalProbeService, "ServiceDisposed") == 1,
                "the per-call service is disposed exactly once after generated cleanup");
            Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0,
                "physical completion releases the existing module counters");
            await WaitUntilAsync(() => ((SharpLinkServer)harness.Server).ActiveCallCountForDiagnostics == 0);
            Ensure((await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2)))
                .ReferencesReleased, "server contract release after physical service completion");
            Ensure((await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2)))
                .ReferencesReleased, "client contract release after terminal consumption");
        }
        finally
        {
            plugin.InvokeServiceStatic(PhysicalProbeService, "ReleaseAll");
            clientClock.Advance(TimeSpan.FromSeconds(10));
            await callerCancellation.CancelAsync();
            if (pendingMove is not null)
            {
                try { await pendingMove.WaitAsync(PhysicalPhaseTimeout); }
                catch (Exception) { /* Preserve the body assertion after completing the reader. */ }
            }
        }
        return plugin.Unload();
    }

    private static async Task<(DynamicHarness Harness, PhysicalOwnershipClock ClientClock, PhysicalOwnershipClock ServerClock)> CreatePhysicalOwnershipHarnessAsync()
    {
        var serverCancellation = new CancellationTokenSource();
        var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var serverClock = new PhysicalOwnershipClock();
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback)
            .UseTimeProvider(serverClock)
            .UseServiceProvider(provider)
            .UseHeartbeat(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        var server = builder.Build();
        var serverTask = server.RunUntilStoppedAsync(serverCancellation.Token).AsTask();
        var clientClock = new PhysicalOwnershipClock();
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseHeartbeat(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30))
            .UseTimeProvider(clientClock)
            .UseRpcSessionFlush(1, TimeSpan.FromMilliseconds(1))
            .Build();
        await client.ConnectAsync();
        return (new DynamicHarness(server, client, port, serverCancellation, serverTask, provider), clientClock, serverClock);
    }

    // Both monotonic clocks remain frozen until the producer has entered. Advance the server
    // first, observe its cancellation, then advance the client to consume its normal deadline
    // terminal. This orders the contenders without a wall-clock race or changing the wire budget.
    private sealed class PhysicalOwnershipClock : TimeProvider
    {
        private long _timestamp = 1;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed)
            => Interlocked.Add(ref _timestamp, (long)(elapsed.TotalSeconds * TimestampFrequency));
    }
}
