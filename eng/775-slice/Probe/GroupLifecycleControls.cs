using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.GroupPlugin;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.BindingSlice;

internal static partial class Program
{
    private static string GroupPath(bool changed = false) => changed
        ? Path.GetFullPath("eng/775-slice/GroupPluginChanged/bin/Release/net10.0/SharpLink.GroupPlugin.dll")
        : typeof(IHotRpc).Assembly.Location;
    private static void GCheck(bool condition, string prefix, string name)
    {
        if (!condition && name.Contains("collectible", StringComparison.Ordinal)) CaptureAlcFailure();
        Check(condition, prefix + " / " + name);
    }

    private static async Task RunGroupAsync(string transport, CancellationToken token)
    {
        await ReplacementGroupAsync(transport, false, token);
        await ReplacementGroupAsync(transport, true, token);
        await LateAckGroupAsync(transport, token);
        await CodecGroupAsync(transport, token);
        await QueuedGroupAsync(transport, token);
        await ReuseGroupAsync(transport, token);
    }

    private static async Task ReplacementGroupAsync(string transport, bool changed, CancellationToken token)
    {
        var prefix = changed ? "changed hash" : "same hash";
        await using var f = new GroupFixture(transport);
        var oldSignals = new GroupSignals(); var nextSignals = new GroupSignals();
        var old = new LoadedGroup(GroupPath(), 1, oldSignals);
        var next = new LoadedGroup(GroupPath(changed), 2, nextSignals);
        var releaseCut = new ManualResetEventSlim();
        try
        {
            GCheck(f.Server.RegisterAssembly(old.Assembly).Succeeded, prefix, "lifecycle: runtime group registration succeeds");
            var module = f.Module(old);
            GCheck(f.Registry.ServicesStorage.Values.Count(service => ReferenceEquals(service.Module, module)) == 2,
                prefix, "lifecycle: one module owns two service registrations");
            GCheck((old.Manifest.RpcAssemblyHash != next.Manifest.RpcAssemblyHash) == changed, prefix,
                "lifecycle: replacement hash relationship is explicit");
            await f.ConnectAsync(token);
            var oldBindings = await f.BindAllAsync(token);
            GCheck(module.RemainingCalls == 0 && oldSignals.Count("get") == 0 && oldSignals.Count("second") == 0,
                prefix, "lifecycle: four Session bindings are not call leases or activations");
            var unrelated = f.Client.CreateProxy<ISliceRpc>();
            await unrelated.PrepareAsync(TimeSpan.FromSeconds(5), token);
            GCheck(await unrelated.Service.PingAsync() == 42, prefix, "lifecycle: unrelated static route is ready");
            GCheck(await f.EchoAsync(1, oldBindings[1][0], token) == 1017 && oldSignals.Count("decode") == 1,
                prefix, "lifecycle: dynamic request uses original generation custom Codec");
            oldSignals.ResetCleanup();
            old.Set("Body", (Func<Task>)(() => oldSignals.ReleaseBody.Task));
            old.Set("Cleanup", (Func<Task>)(() => oldSignals.ReleaseCleanup.Task));
            using var caller = new CancellationTokenSource();
            var held = f.CallAsync(0, oldBindings[0][0], nameof(IHotRpc.HoldAsync), caller.Token).AsTask();
            await oldSignals.BodyEntered.Task.WaitAsync(token);
            GCheck(module.RemainingCalls == 1, prefix, "lifecycle: pre-cut request owns exact module protection");
            var cut = Signal();
            var observedInvalidation = new[] { Signal(), Signal() };
            for (var i = 0; i < 2; i++)
            {
                var index = i;
                f.Connections[i].StaticBindings.InvalidationObservedTestHook = () => observedInvalidation[index].TrySetResult();
            }
            f.Server.AfterBindingGroupCutTestHook = () =>
            {
                cut.TrySetResult();
                if (!releaseCut.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Group cut not released.");
            };
            var replacing = Task.Run(async () => await f.Server.ReplaceAssemblyAsync(old.Assembly, next.Assembly,
                TimeSpan.FromSeconds(30), token));
            await cut.Task.WaitAsync(token);
            GCheck(module.State == SharpLinkDynamicModuleState.Draining &&
                f.Registry.ServicesStorage.Values.Count(service => ReferenceEquals(service.Module, module)) == 2,
                prefix, "lifecycle: whole old gate closes before complete registry publication");
            var refused = 0;
            for (var i = 0; i < 2; i++)
                for (var c = 0; c < 2; c++)
                {
                    try { await f.CallAsync(i, oldBindings[i][c], c == 0 ? nameof(IHotRpc.GetAsync) : nameof(ISecondHotRpc.GetSecondAsync), token); }
                    catch (SharpLinkException e) when (e.Code == SharpLinkErrorCode.Unavailable) { refused++; }
                }
            GCheck(refused == 4 && oldSignals.Count("get") == 0 && oldSignals.Count("second") == 0,
                prefix, "lifecycle: post-cut old tokens reject across all services and Sessions");
            await ExpectCode(() => f.EchoAsync(1, oldBindings[1][0], token).AsTask(), SharpLinkErrorCode.Unavailable,
                prefix + " / lifecycle: post-cut payload rejects before original or new Codec");
            GCheck(oldSignals.Count("decode") == 1 && nextSignals.Count("decode") == 0,
                prefix, "lifecycle: rejected payload performs zero business decode");
            caller.Cancel();
            await Expect<OperationCanceledException>(() => held, prefix + " / lifecycle: caller cancellation is logical before physical exit");
            GCheck(!replacing.IsCompleted && module.RemainingCalls == 1, prefix,
                "lifecycle: canceled caller cannot release held old code");
            releaseCut.Set();
            await Task.WhenAll(observedInvalidation.Select(signal => signal.Task)).WaitAsync(token);
            f.Server.AfterBindingGroupCutTestHook = null;
            var nextModule = f.Module(next);
            GCheck(f.Registry.ServicesStorage.Values.Count(service => ReferenceEquals(service.Module, nextModule)) == 2 &&
                f.Registry.ServicesStorage.Values.All(service => !ReferenceEquals(service.Module, module)),
                prefix, "lifecycle: full new group publishes while old physical work drains");
            GCheck(await unrelated.Service.PingAsync() == 42, prefix, "lifecycle: unrelated route survives retirement invalidation");

            var clientLocal = new LoadedGroup(GroupPath(changed), 0, new GroupSignals());
            var client = f.CreateClient(clientLocal.Manifest);
            try
            {
                var contract = clientLocal.Manifest.Contracts.Single(c => c.ContractType.Name == nameof(IHotRpc));
                var method = contract.Methods.Single(m => m.Name == nameof(IHotRpc.GetAsync));
                var channel = client.CreateContractChannel(client.GetLocalContract(clientLocal.Assembly, contract.ContractId));
                var preparing = channel.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask();
                await client.ConnectAsync(token); await preparing.WaitAsync(token);
                var descriptor = new RpcMethodDescriptor(contract.ContractId, method.MethodId, RpcMethodKind.Unary, true, false, false, null);
                GCheck(await channel.InvokeUnaryAsync(descriptor, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                    client.RuntimeContext.Codecs.GetCodec<int>(), null, token) == 2,
                    prefix, "lifecycle: matching local generation invokes newly bound replacement");
            }
            finally { await client.StopAsync(token); clientLocal.Drop(); }

            oldSignals.ReleaseBody.TrySetResult();
            await oldSignals.CleanupEntered.Task.WaitAsync(token);
            GCheck(!replacing.IsCompleted && module.RemainingCalls == 1 && module.State != SharpLinkDynamicModuleState.Released,
                prefix, "lifecycle: asynchronous service disposal still owns old protection");
            oldSignals.ReleaseCleanup.TrySetResult();
            var replaced = await replacing.WaitAsync(token);
            GCheck(replaced.Succeeded && module.State == SharpLinkDynamicModuleState.Released && module.RemainingCalls == 0 &&
                module.BindingPhysicalReleased.Task.IsCompletedSuccessfully,
                prefix, "lifecycle: replacement reports release only after old physical cleanup");
            var before = nextSignals.Count("get");
            try { await f.CallAsync(0, oldBindings[0][0], nameof(IHotRpc.GetAsync), token); }
            catch (SharpLinkException e) when (e.Code == SharpLinkErrorCode.Unavailable) { }
            GCheck(nextSignals.Count("get") == before, prefix, "lifecycle: retired token never retargets new generation");
            GCheck(!Collected(old.Weak), prefix, "lifecycle: deliberate caller ALC root is detected");
            old.Drop();
            GCheck(Collected(old.Weak), prefix, "lifecycle: idle bindings snapshots and caches release old ALC");
            var removal = await UnregisterAndDropGroupAsync(f, next, token);
            GCheck(removal.ReferencesReleased && nextModule.State == SharpLinkDynamicModuleState.Released, prefix,
                "lifecycle: unregister uses the same group retirement owner");
            GCheck(Collected(next.Weak), prefix, "lifecycle: unregistered replacement ALC is collectible");
            await f.Client.StopAsync(token); await f.Server.StopAsync(TimeSpan.FromSeconds(5), token);
            GCheck(f.Client.BindingBudget.Bindings == 0 && f.Client.BindingBudget.Operations == 0 &&
                f.Server.BindingBudget.Bindings == 0 && f.Server.BindingBudget.Operations == 0,
                prefix, "lifecycle: terminal owners return aggregate quotas");
        }
        finally
        {
            releaseCut.Set(); oldSignals.ReleaseBody.TrySetResult(); oldSignals.ReleaseCleanup.TrySetResult();
            f.Server.AfterBindingGroupCutTestHook = null;
        }
    }

    private static async Task LateAckGroupAsync(string transport, CancellationToken token)
    {
        const string prefix = "late Ack";
        await using var f = new GroupFixture(transport);
        var old = new LoadedGroup(GroupPath(), 1, new GroupSignals());
        var next = new LoadedGroup(GroupPath(), 2, new GroupSignals());
        if (!f.Server.RegisterAssembly(old.Assembly).Succeeded) throw new InvalidOperationException("Registration failed.");
        await f.ConnectAsync(token);
        var entered = Signal(); var release = Signal(); var invalidated = Signal();
        var seen = 0;
        f.Server.BeforeBindingAckTestHook = async (_, _, _, ct) =>
        {
            if (Interlocked.Increment(ref seen) != 1) return;
            entered.TrySetResult(); await release.Task.WaitAsync(ct);
        };
        var connection = f.Connections[0];
        connection.StaticBindings.InvalidationObservedTestHook = () => invalidated.TrySetResult();
        try
        {
            var binding = connection.StaticBindings.BindAsync(f.Contract().ContractId, f.LocalManifest.RpcAssemblyHash);
            await entered.Task.WaitAsync(token);
            var replaced = await f.Server.ReplaceAssemblyAsync(old.Assembly, next.Assembly, TimeSpan.FromSeconds(5), token);
            await invalidated.Task.WaitAsync(token);
            GCheck(replaced.Succeeded && !binding.IsCompleted, prefix, "lifecycle: retirement invalidation precedes held BindAck");
            old.Drop();
            GCheck(Collected(old.Weak), prefix, "lifecycle: pending scalar Ack does not root retired ALC");
            release.TrySetResult();
            await ExpectCode(() => binding, SharpLinkErrorCode.Unavailable, prefix + " / lifecycle: delayed Ack cannot reopen invalidated binding");
            GCheck(connection.StaticBindings.CompletedCount == 0 && connection.StaticBindings.PendingCount == 0,
                prefix, "lifecycle: delayed Ack correlation and reservation are reclaimed");
            var fresh = await connection.StaticBindings.BindAsync(f.Contract().ContractId, f.LocalManifest.RpcAssemblyHash).WaitAsync(token);
            GCheck((uint)(fresh.Token >> 32) == 2 && await f.CallAsync(0, fresh, nameof(IHotRpc.GetAsync), token) == 2,
                prefix, "lifecycle: rebind uses a new incarnation and exact replacement");
        }
        finally { release.TrySetResult(); f.Server.BeforeBindingAckTestHook = null; }
        await f.Server.UnregisterAssemblyAsync(next.Assembly, TimeSpan.FromSeconds(5), token);
        next.Drop();
    }

    private static async Task CodecGroupAsync(string transport, CancellationToken token)
    {
        const string prefix = "physical decode";
        await using var f = new GroupFixture(transport);
        var signals = new GroupSignals();
        var old = new LoadedGroup(GroupPath(), 1, signals);
        var next = new LoadedGroup(GroupPath(), 2, new GroupSignals());
        if (!f.Server.RegisterAssembly(old.Assembly).Succeeded) throw new InvalidOperationException("Registration failed.");
        await f.ConnectAsync(token);
        var bindings = await f.BindAllAsync(token);
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        old.Set("Decode", (Action)(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Codec not released.");
        }));
        try
        {
            var echo = f.EchoAsync(0, bindings[0][0], token).AsTask();
            await entered.Task.WaitAsync(token);
            var module = f.Module(old);
            GCheck(module.RemainingCalls == 1, prefix, "lifecycle: module protection precedes custom business decode");
            var replacing = f.Server.ReplaceAssemblyAsync(old.Assembly, next.Assembly, TimeSpan.FromSeconds(30), token).AsTask();
            GCheck(!replacing.IsCompleted && module.State == SharpLinkDynamicModuleState.Draining && module.RemainingCalls == 1,
                prefix, "lifecycle: retirement cannot reclaim an executing Codec");
            release.Set();
            GCheck(await echo.WaitAsync(token) == 1017, prefix, "lifecycle: pre-cut decode and invocation retain original registration and Codec");
            await replacing.WaitAsync(token);
            old.Drop();
            GCheck(Collected(old.Weak), prefix, "lifecycle: physical Codec exit releases collectible generation");
        }
        finally { release.Set(); }
        await f.Server.UnregisterAssemblyAsync(next.Assembly, TimeSpan.FromSeconds(5), token);
        next.Drop();
    }

    private static async Task QueuedGroupAsync(string transport, CancellationToken token)
    {
        const string prefix = "queued generation";
        await using var f = new GroupFixture(transport, queue: true);
        var signals = new GroupSignals();
        var old = new LoadedGroup(GroupPath(), 1, signals);
        var next = new LoadedGroup(GroupPath(), 2, new GroupSignals());
        if (!f.Server.RegisterAssembly(old.Assembly).Succeeded) throw new InvalidOperationException("Registration failed.");
        await f.ConnectAsync(token);
        var bindings = await f.BindAllAsync(token);
        old.Set("Body", (Func<Task>)(() => signals.ReleaseBody.Task));
        try
        {
            var held = f.CallAsync(0, bindings[0][0], nameof(IHotRpc.HoldAsync), token).AsTask();
            await signals.BodyEntered.Task.WaitAsync(token);
            var queued = f.CallAsync(1, bindings[1][1], nameof(ISecondHotRpc.GetSecondAsync), token).AsTask();
            await f.Queued.Task.WaitAsync(token);
            var module = f.Module(old);
            GCheck(!queued.IsCompleted && module.RemainingCalls == 2, prefix,
                "lifecycle: admission queue holds the exact pre-cut registration protection");
            var replacing = f.Server.ReplaceAssemblyAsync(old.Assembly, next.Assembly, TimeSpan.FromSeconds(30), token).AsTask();
            GCheck(!replacing.IsCompleted && module.State == SharpLinkDynamicModuleState.Draining && module.RemainingCalls == 2,
                prefix, "lifecycle: group replacement waits for queued and active old work");
            signals.ReleaseBody.TrySetResult();
            GCheck(await held.WaitAsync(token) == 1 && await queued.WaitAsync(token) == 1, prefix,
                "lifecycle: admitted continuation never reacquires or retargets after cut");
            await replacing.WaitAsync(token);
            GCheck(module.RemainingCalls == 0 && module.BindingPhysicalReleased.Task.IsCompletedSuccessfully, prefix,
                "lifecycle: queue and invocation release one protection each");
            old.Drop();
            GCheck(Collected(old.Weak), prefix, "lifecycle: resumed queue facts and weak selections release old ALC");
        }
        finally { signals.ReleaseBody.TrySetResult(); }
        await f.Server.UnregisterAssemblyAsync(next.Assembly, TimeSpan.FromSeconds(5), token);
        next.Drop();
    }

    private static async Task ReuseGroupAsync(string transport, CancellationToken token)
    {
        await using var f = new GroupFixture(transport);
        var weak = await ExecuteReuseAndUnloadAsync(f, token);
        GCheck(Collected(weak), "ABA", "lifecycle: reuse and watermark state do not retain collectible code");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ExecuteReuseAndUnloadAsync(GroupFixture f, CancellationToken token)
    {
        const string prefix = "ABA";
        var plugin = new LoadedGroup(GroupPath(), 3, new GroupSignals());
        if (!f.Server.RegisterAssembly(plugin.Assembly).Succeeded) throw new InvalidOperationException("Registration failed.");
        await f.ConnectAsync(token);
        var connection = f.Connections[0];
        ulong oldToken = 0;
        for (var i = 0; i < 70; i++)
        {
            var binding = await connection.StaticBindings.BindAsync(f.Contract().ContractId, f.LocalManifest.RpcAssemblyHash).WaitAsync(token);
            if (i != 0 && ((uint)binding.Token != (uint)oldToken || binding.Token == oldToken))
                throw new InvalidOperationException("Slot was not safely reused.");
            var invalidated = Signal();
            connection.StaticBindings.InvalidationObservedTestHook = () => invalidated.TrySetResult();
            await StaticBindingWire.SendReleaseAsync(connection.Session, binding.Token, token);
            await invalidated.Task.WaitAsync(token);
            oldToken = binding.Token;
        }
        GCheck(connection.StaticBindings.CompletedCount == 0 && f.Server.BindingBudget.Bindings == 0 &&
            f.Client.BindingBudget.Bindings == 0, prefix, "lifecycle: seventy reuse cycles remain bounded without slot exhaustion");
        var current = await connection.StaticBindings.BindAsync(f.Contract().ContractId, f.LocalManifest.RpcAssemblyHash).WaitAsync(token);
        var lateRelease = Signal();
        connection.StaticBindings.InvalidationObservedTestHook = () => lateRelease.TrySetResult();
        await StaticBindingWire.SendReleaseAsync(connection.Session, oldToken, token);
        await lateRelease.Task.WaitAsync(token);
        GCheck(connection.StaticBindings.CompletedCount == 1 && await f.CallAsync(0, current, nameof(IHotRpc.GetAsync), token) == 3,
            prefix, "lifecycle: stale wire Release and Invalidate cannot evict reused incarnation");
        var stale = current with { Token = oldToken };
        await ExpectCode(() => f.CallAsync(0, stale, nameof(IHotRpc.GetAsync), token).AsTask(), SharpLinkErrorCode.Unavailable,
            prefix + " / lifecycle: stale wire Request cannot invoke reused slot");

        var staticContract = f.ProbeManifest.Contracts.Single(c => c.ContractType == typeof(ISliceRpc));
        var registration = f.Registry.ServicesStorage[staticContract.ContractId];
        var budget = new StaticBindingBudget(); var table = new StaticBindingTable(budget);
        var incarnations = Field<uint[]>(table, "_incarnations");
        Array.Fill(incarnations, uint.MaxValue);
        GCheck(table.Install(staticContract.ContractId, registration, staticContract, 0).Failure == StaticBindingFailure.Capacity &&
            table.Count == 0 && budget.Bindings == 0, prefix, "lifecycle: incarnation exhaustion never wraps or reserves quota");
        table.Close();
        await UnregisterAndDropGroupAsync(f, plugin, token);
        return plugin.Weak;
    }
}

