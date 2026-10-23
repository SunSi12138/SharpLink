using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("unary", true)]
    [Arguments("no-return", true)]
    [Arguments("client-stream", true)]
    [Arguments("server-stream", true)]
    [Arguments("duplex", true)]
    [Arguments("one-way", true)]
    [Arguments("one-way-stream", true)]
    [Arguments("unary", false)]
    [Arguments("no-return", false)]
    [Arguments("client-stream", false)]
    [Arguments("server-stream", false)]
    [Arguments("duplex", false)]
    [Arguments("one-way", false)]
    [Arguments("one-way-stream", false)]
    public async Task QueuedSelectionRetirementShouldPreserveEveryRpcShape(string shape, bool replace)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load($"queue-selection-{shape}-{replace}");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var server = (SharpLinkServer)harness.Server;
        var holder = await HoldQueuedAdmissionAsync(server);
        var admission = server.CurrentAdmissionProgramForTests!;
        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);
        Task? call = null;
        try
        {
            call = StartQueuedShape(proxy, plugin.ContractType, shape, CancellationToken.None);
            await WaitUntilAsync(() => admission.Kernel.QueuedCalls == 1);
            var oldModule = GetDynamicModule(server, plugin.ServiceAssembly);
            Ensure(oldModule.RemainingCalls == 0, "a waiter does not extend module invocation lifetime");
            if (replace)
            {
                var result = await server.ReplaceAssemblyAsync(
                    plugin.ServiceAssembly, replacementAssembly, TimeSpan.Zero);
                Ensure(result.Succeeded && result.ReferencesReleased,
                    "compatible same-contract replacement retires the queued selection");
            }
            else
            {
                var result = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.Zero);
                Ensure(result.ReferencesReleased, "unregister retires the queued selection");
            }
            Ensure(!oldModule.ForcedCancellation.IsCancellationRequested,
                "ordinary zero-invocation retirement does not cancel all old calls");
            holder.Lease!.Dispose();
            if (shape.StartsWith("one-way", StringComparison.Ordinal))
                await call.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await EnsureQueuedSelectionUnavailableAsync(call);
            await WaitUntilAsync(() => admission.Kernel.QueuedCalls == 0 && admission.Kernel.ActivePermits == 0);
            Ensure(plugin.GetStaticInt("Created") == 0, "retired R1 never enters user code");
            Ensure((int)replacementType.GetProperty("Created")!.GetValue(null)! == 0,
                "queued R1 never invokes R2, for any RPC shape");
            if (!replace)
                Ensure(server.RegisterAssembly(replacementAssembly).Succeeded, "replacement probe registers");
            Ensure(await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UnaryAsync", 41, CancellationToken.None) == 42,
                "post-retirement traffic uses R2 on the same connection");
            await WaitUntilAsync(() => admission.Kernel.ActivePermits == 0);
            EnsureClientAndServerCountersAreZero(harness, $"queued {shape} retirement");
        }
        finally
        {
            holder.Lease!.Dispose();
            if (call is not null)
                await ObserveQueuedTerminalAsync(call);
            _ = await server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            _ = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            replacementContext.Unload();
        }
    }

    [Test]
    [NotInParallel]
    [Arguments("unary")]
    [Arguments("client-stream")]
    [Arguments("server-stream")]
    [Arguments("duplex")]
    [Arguments("one-way-stream")]
    public async Task QueuedSelectionCancellationShouldNotInvokeOrLeak(string shape)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load($"queue-cancel-{shape}");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var server = (SharpLinkServer)harness.Server;
        var holder = await HoldQueuedAdmissionAsync(server);
        var admission = server.CurrentAdmissionProgramForTests!;
        using var cancellation = new CancellationTokenSource();
        var call = StartQueuedShape(proxy, plugin.ContractType, shape, cancellation.Token);
        try
        {
            await WaitUntilAsync(() => admission.Kernel.QueuedCalls == 1);
            cancellation.Cancel();
            await ObserveQueuedTerminalAsync(call);
            // OneWay has no response waiter after its request is sent; disconnect rather than
            // pretending that a completed send remains remotely cancellable.
            if (shape == "one-way-stream")
                await harness.Client.StopAsync();
            await WaitUntilAsync(() => admission.Kernel.QueuedCalls == 0);
            Ensure(plugin.GetStaticInt("Created") == 0, "cancelled queue never invokes user code");
            holder.Lease!.Dispose();
            await WaitUntilAsync(() => admission.Kernel.ActivePermits == 0);
        }
        finally
        {
            holder.Lease!.Dispose();
            await ObserveQueuedTerminalAsync(call);
        }
    }

    [Test]
    [NotInParallel]
    public async Task QueuedSelectionShouldNotRootRetiredCollectibleService()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("queue-collectible-contract", loadService: false);
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded, "collectible client contract registers");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "collectible server contract registers");
        var server = (SharpLinkServer)harness.Server;
        var holder = await HoldQueuedAdmissionAsync(server);
        var admission = server.CurrentAdmissionProgramForTests!;
        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var servicePath = Path.Combine(Path.GetDirectoryName(plugin.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(servicePath);
        Task? queued = null;
        try
        {
            var retired = await QueueAndRetireCollectibleServiceAsync(
                server, harness.Client, plugin.ContractType, servicePath, replacementAssembly);
            queued = retired.Queued;
            for (var attempt = 0; attempt < 30 && retired.Context.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(20);
            }
            Ensure(admission.Kernel.QueuedCalls == 1,
                "collectibility is tested while admission still holds the old queued request");
            Ensure(!retired.Context.IsAlive,
                "a suspended admission waiter must not root a ReferencesReleased service ALC");
            holder.Lease!.Dispose();
            await EnsureQueuedSelectionUnavailableAsync(queued);
            Ensure(await InvokeValueTaskAsync<int>(
                retired.Proxy, plugin.ContractType, "UnaryAsync", 41, CancellationToken.None) == 42,
                "collected R1 rejects without consuming R2 or closing the connection");
        }
        finally
        {
            holder.Lease!.Dispose();
            if (queued is not null)
                await ObserveQueuedTerminalAsync(queued);
            _ = await server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            _ = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            replacementContext.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Context, Task Queued, object Proxy)> QueueAndRetireCollectibleServiceAsync(
        SharpLinkServer server, ISharpLinkClient client, Type contractType, string servicePath, Assembly replacement)
    {
        var context = new SharedContractServiceLoadContext(contractType.Assembly);
        var assembly = context.LoadFromAssemblyPath(servicePath);
        Ensure(server.RegisterAssembly(assembly).Succeeded, "isolated original service registers");
        await WaitForRemoteContractManifestAsync(client, contractType);
        var proxy = GetProxy(client, contractType);
        var queued = InvokeValueTaskAsync<int>(proxy, contractType, "UnaryAsync", 1, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => server.CurrentAdmissionProgramForTests!.Kernel.QueuedCalls == 1);
        var result = await server.ReplaceAssemblyAsync(assembly, replacement, TimeSpan.Zero);
        Ensure(result.Succeeded && result.ReferencesReleased,
            "old implementation references are released while its admission request remains queued");
        var weak = new WeakReference(context, trackResurrection: false);
        context.Unload();
        return (weak, queued, proxy);
    }

    private static async Task<AdmissionDecision> HoldQueuedAdmissionAsync(SharpLinkServer server)
    {
        server.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(1);
            options.MaxQueuedCalls = 1;
            options.MaxQueuedBytes = 64 * 1024;
            options.MaxQueueDelay = TimeSpan.FromSeconds(30);
            options.QueueOneWayCalls = true;
        });
        var holder = await server.CurrentAdmissionProgramForTests!.Controller.AcquireAsync(
            new SharpLinkAdmissionContext(101, 202, RpcMethodKind.Unary, "test-holder", null, null),
            1, false, default, CancellationToken.None);
        Ensure(holder.IsAcquired, "the deterministic external holder owns the only admission permit");
        return holder;
    }

    private static Task StartQueuedShape(object proxy, Type contract, string shape, CancellationToken cancellation)
        => shape switch
        {
            "unary" => InvokeValueTaskAsync<int>(proxy, contract, "UnaryAsync", 1, cancellation).AsTask(),
            "no-return" => InvokeValueTaskAsync(proxy, contract, "NoReturnAsync", 1, cancellation).AsTask(),
            "client-stream" => InvokeValueTaskAsync<int>(proxy, contract, "ClientStreamAsync", Values(1, 2), cancellation).AsTask(),
            "server-stream" => CollectAsync(InvokeStream(proxy, contract, "ServerStreamAsync", 2, cancellation)),
            "duplex" => CollectAsync(InvokeStream(proxy, contract, "DuplexAsync", Values(1, 2), cancellation)),
            "one-way" => InvokeValueTaskAsync(proxy, contract, "NotifyAsync", 1, cancellation).AsTask(),
            "one-way-stream" => InvokeValueTaskAsync(proxy, contract, "NotifyStreamAsync", Values(1, 2), cancellation).AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

    private static async Task EnsureQueuedSelectionUnavailableAsync(Task call)
    {
        try
        {
            await call.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception("assert failed: retired queued selection must reject, never retarget");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "retired queued selection reports Unavailable");
        }
    }

    private static async Task ObserveQueuedTerminalAsync(Task call)
    {
        try { await call.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (SharpLinkException) { }
        catch (OperationCanceledException) { }
    }
}
