using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task MultiClusterDynamicRegistrationShouldRouteToOneExplicitSlot()
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .AddCluster("plugins", child => child.UseTcp(IPAddress.Loopback.ToString(), 1),
                slot => slot.AllowDynamicContracts = true)
            .AddCluster("other", child => child.UseTcp(IPAddress.Loopback.ToString(), 2),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        using var plugin = PluginBundle.Load("multi-cluster-dynamic-registration", loadService: false);

        var first = client.RegisterAssembly("plugins", plugin.ContractAssembly);
        Ensure(first.Succeeded, $"multi-cluster plugin registration: {first.Error}");

        var proxy = GetMultiClusterProxy(client, plugin.ContractType);
        Ensure(proxy is not null, "multi-cluster Get should create the dynamically routed proxy");

        var second = client.RegisterAssembly("other", plugin.ContractAssembly);
        Ensure(!second.Succeeded, "contract-owning assembly must not register in a second cluster");
        Ensure(second.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
            "second cluster should return a structured contract conflict");

        var drained = await client.UnregisterAssemblyAsync(
            "plugins", plugin.ContractAssembly, TimeSpan.FromSeconds(2));
        Ensure(drained.ReferencesReleased, "multi-cluster plugin unregister should release the child module");
    }

    [Test]
    [NotInParallel]
    public async Task MultiClusterRemoveShouldReleaseCollectibleContractContext()
    {
        var weakContext = await RegisterRemoveAndUnloadMultiClusterPluginAsync();
        for (var attempt = 0; attempt < 20 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }

        Ensure(!weakContext.IsAlive,
            "runtime slot removal must release coordinator and child references to the collectible ALC");
    }

    [Test]
    [NotInParallel]
    public async Task MultiClusterSharedConnectShouldSurviveFirstWaiterCancellation()
    {
        var child = new BlockingConnectClient();
        var slot = new SharpLinkClusterSlot("plugins", child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new[] { slot }.ToFrozenDictionary(static candidate => candidate.Key),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);

        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = client.ConnectAsync(cancellation.Token).AsTask();
        await child.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var survivingWaiter = client.ConnectAsync().AsTask();

        cancellation.Cancel();
        await EnsureCancelledAsync(cancelledWaiter, "first shared connect waiter");
        Ensure(!survivingWaiter.IsCompleted,
            "another connect waiter remains attached to the shared operation");

        child.ReleaseConnect();
        await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.State == SharpLinkMultiClusterState.Ready,
            "shared connect reaches ready after the first caller cancels its wait");
    }

    [Test]
    [NotInParallel]
    public async Task MultiClusterStopShouldWinWhenChildConnectCompletesAfterShutdown()
    {
        var child = new BlockingConnectClient(releaseWhenStopped: false, ignoreCancellation: true);
        var slot = new SharpLinkClusterSlot("plugins", child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new[] { slot }.ToFrozenDictionary(static candidate => candidate.Key),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);

        var connecting = client.ConnectAsync().AsTask();
        await child.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.StopAsync();

        child.ReleaseConnect();
        await EnsureCancelledAsync(connecting.WaitAsync(TimeSpan.FromSeconds(2)),
            "connect that was cancelled by coordinator shutdown");
        Ensure(client.State == SharpLinkMultiClusterState.Stopped,
            "a post-stop child connect completion must not overwrite the coordinator terminal state");

        try
        {
            await client.ConnectAsync();
            throw new Exception("assert failed: stopped coordinator must reject later connect attempts");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Test]
    [NotInParallel]
    public async Task MultiClusterCancelledUnregisterShouldStillReleaseCoordinatorRegistration()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        await using var client = await CreateDynamicMultiClusterClientAsync(harness.Port);
        using var plugin = PluginBundle.Load("multi-cluster-cancelled-unregister");
        plugin.ResetServiceState();
        await RegisterMultiClusterPluginAsync(harness, client, plugin);

        var proxy = GetMultiClusterProxy(client, plugin.ContractType)
            ?? throw new InvalidOperationException("Multi-cluster proxy factory returned null.");
        var activeCall = InvokeValueTaskAsync<int>(
            proxy, plugin.ContractType, "BlockAsync", CancellationToken.None).AsTask();
        await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        using var cancellation = new CancellationTokenSource();
        var unregister = client.UnregisterAssemblyAsync(
            "plugins", plugin.ContractAssembly, TimeSpan.FromSeconds(2), cancellation.Token).AsTask();
        cancellation.Cancel();
        await EnsureCancelledAsync(unregister, "multi-cluster unregister wait");

        plugin.ReleaseBlock();
        Ensure(await activeCall.WaitAsync(TimeSpan.FromSeconds(2)) == 42,
            "the admitted call should complete before the child unregister releases its module");
        await WaitUntilAsync(() => client.RegisterAssembly("plugins", plugin.ContractAssembly).Succeeded);

        Ensure((await client.UnregisterAssemblyAsync(
            "plugins", plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "the re-registered coordinator module should release");
        await UnregisterMultiClusterPluginAsync(harness, plugin);
    }

    [Test]
    [NotInParallel]
    public async Task MultiClusterDeferredUnregisterShouldRemoveARegistrationReleasedByItsChild()
    {
        using var plugin = PluginBundle.Load("multi-cluster-deferred-unregister", loadService: false);
        await using var registrationSource = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), 1)
            .Build();
        var registrationResult = registrationSource.RegisterAssembly(plugin.ContractAssembly);
        Ensure(registrationResult.Succeeded, "controlled child registration result");

        var child = new ControlledDynamicAssemblyClient(registrationResult);
        var slot = new SharpLinkClusterSlot("plugins", child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new[] { slot }.ToFrozenDictionary(static candidate => candidate.Key),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        Ensure(client.RegisterAssembly("plugins", plugin.ContractAssembly).Succeeded,
            "multi-cluster controlled registration");

        var unregister = client.UnregisterAssemblyAsync(
            "plugins", plugin.ContractAssembly, TimeSpan.Zero).AsTask();
        await child.FirstUnregisterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        child.CompleteTimedOutUnregister();
        Ensure(!(await unregister).ReferencesReleased,
            "the child unregister should defer coordinator cleanup");

        child.ReleaseAssembly(plugin.ContractAssembly);
        await WaitUntilAsync(() => client.RegisterAssembly("plugins", plugin.ContractAssembly).Succeeded);
        Ensure(child.UnregisterCalls == 1,
            "deferred coordinator cleanup should poll child registration without starting another unregister");
    }

    [Test]
    [NotInParallel]
    public async Task MultiClusterRejectedUnregisterShouldRestoreCoordinatorRoute()
    {
        using var plugin = PluginBundle.Load("multi-cluster-rejected-unregister", loadService: false);
        await using var registrationSource = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), 1)
            .Build();
        var registrationResult = registrationSource.RegisterAssembly(plugin.ContractAssembly);
        Ensure(registrationResult.Succeeded, "controlled child registration result");

        var child = new ControlledDynamicAssemblyClient(registrationResult);
        var slot = new SharpLinkClusterSlot("plugins", child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new[] { slot }.ToFrozenDictionary(static candidate => candidate.Key),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        Ensure(client.RegisterAssembly("plugins", plugin.ContractAssembly).Succeeded,
            "multi-cluster controlled registration");

        child.RejectNextUnregister();
        try
        {
            _ = await client.UnregisterAssemblyAsync(
                "plugins", plugin.ContractAssembly, TimeSpan.Zero);
            throw new Exception("assert failed: child unregister rejection must reach the caller");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("rejected", StringComparison.Ordinal),
                "child unregister rejection is preserved");
        }

        _ = GetMultiClusterProxy(client, plugin.ContractType);
        Ensure(child.IsDynamicAssemblyRegistered(plugin.ContractAssembly),
            "child retains the rejected dynamic assembly");
    }

    [Test]
    [NotInParallel]
    public async Task MultiClusterRejectedUnregisterShouldReserveContractIdsUntilRoutesAreRestored()
    {
        using var originalPlugin = PluginBundle.Load("multi-cluster-rejected-unregister-original", loadService: false);
        using var reloadedPlugin = PluginBundle.Load("multi-cluster-rejected-unregister-reloaded", loadService: false);
        await using var registrationSource = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), 1)
            .Build();
        var registrationResult = registrationSource.RegisterAssembly(originalPlugin.ContractAssembly);
        Ensure(registrationResult.Succeeded, "controlled child registration result");

        var originalChild = new ControlledDynamicAssemblyClient(registrationResult);
        var reloadedChild = new ControlledDynamicAssemblyClient(registrationResult);
        var originalSlot = new SharpLinkClusterSlot("original", originalChild, AllowDynamicContracts: true);
        var reloadedSlot = new SharpLinkClusterSlot("reloaded", reloadedChild, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new[] { originalSlot, reloadedSlot }.ToFrozenDictionary(static candidate => candidate.Key),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        Ensure(client.RegisterAssembly("original", originalPlugin.ContractAssembly).Succeeded,
            "initial contract registration");

        originalChild.BlockAndRejectNextUnregister();
        var unregister = client.UnregisterAssemblyAsync(
            "original", originalPlugin.ContractAssembly, TimeSpan.Zero).AsTask();
        await originalChild.RejectedUnregisterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var conflictingRegistration = client.RegisterAssembly("reloaded", reloadedPlugin.ContractAssembly);
        Ensure(!conflictingRegistration.Succeeded,
            "an active unregister must continue reserving its ContractIds");
        Ensure(conflictingRegistration.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
            "the ContractId reservation should return a structured conflict");

        originalChild.CompleteRejectedUnregister();
        try
        {
            await unregister;
            throw new Exception("assert failed: the controlled child rejection must reach the caller");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("rejected", StringComparison.Ordinal),
                "the controlled child rejection is preserved");
        }
        _ = GetMultiClusterProxy(client, originalPlugin.ContractType);
    }

    [Test]
    [NotInParallel]
    public async Task MultiClusterReplacementCleanupFailureShouldReconcilePublishedChildRoutes()
    {
        using var oldPlugin = PluginBundle.Load(
            "multi-cluster-replacement-cleanup-failure-old", loadService: false);
        using var newPlugin = PluginBundle.Load(
            "multi-cluster-replacement-cleanup-failure-new", loadService: false);
        await using var registrationSource = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), 1)
            .Build();
        var registrationResult = registrationSource.RegisterAssembly(oldPlugin.ContractAssembly);
        Ensure(registrationResult.Succeeded, "controlled child registration result");

        var child = new ControlledDynamicAssemblyClient(registrationResult);
        var slot = new SharpLinkClusterSlot("plugins", child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new[] { slot }.ToFrozenDictionary(static candidate => candidate.Key),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        Ensure(client.RegisterAssembly("plugins", oldPlugin.ContractAssembly).Succeeded,
            "multi-cluster controlled registration");
        child.PublishReplacementThenFailCleanup();

        try
        {
            _ = await client.ReplaceAssemblyAsync(
                "plugins",
                oldPlugin.ContractAssembly,
                newPlugin.ContractAssembly,
                TimeSpan.Zero);
            throw new Exception("assert failed: child cleanup failure must reach the caller");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("replacement cleanup failure", StringComparison.Ordinal),
                "child cleanup failure is preserved");
        }

        Ensure(child.IsDynamicAssemblyRegistered(newPlugin.ContractAssembly) &&
               !child.IsDynamicAssemblyRegistered(oldPlugin.ContractAssembly),
            "the child has already committed the replacement generation");
        _ = GetMultiClusterProxy(client, newPlugin.ContractType);
    }

    [Test]
    [NotInParallel]
    public async Task MultiClusterReplacementShouldPublishCoordinatorRoutesBeforeOldDrainAndAfterCallerCancellation()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        await using var client = await CreateDynamicMultiClusterClientAsync(harness.Port);
        using var oldPlugin = PluginBundle.Load("multi-cluster-cancelled-replacement-old");
        using var newPlugin = PluginBundle.Load("multi-cluster-cancelled-replacement-new");
        oldPlugin.ResetServiceState();
        await RegisterMultiClusterPluginAsync(harness, client, oldPlugin);

        var proxy = GetMultiClusterProxy(client, oldPlugin.ContractType)
            ?? throw new InvalidOperationException("Multi-cluster proxy factory returned null.");
        var activeCall = InvokeValueTaskAsync<int>(
            proxy, oldPlugin.ContractType, "BlockAsync", CancellationToken.None).AsTask();
        await oldPlugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        using var cancellation = new CancellationTokenSource();
        var replacement = client.ReplaceAssemblyAsync(
            "plugins",
            oldPlugin.ContractAssembly,
            newPlugin.ContractAssembly,
            TimeSpan.FromSeconds(2),
            cancellation.Token).AsTask();

        var newProxy = GetMultiClusterProxy(client, newPlugin.ContractType)
            ?? throw new InvalidOperationException("Multi-cluster replacement proxy factory returned null.");
        Ensure(await InvokeValueTaskAsync<int>(
                newProxy, newPlugin.ContractType, "UnaryAsync", 1, CancellationToken.None) == 2,
            "replacement routes should publish while the old call is draining");

        cancellation.Cancel();
        await EnsureCancelledAsync(replacement, "multi-cluster replacement wait");

        oldPlugin.ReleaseBlock();
        Ensure(await activeCall.WaitAsync(TimeSpan.FromSeconds(2)) == 42,
            "the admitted old call should complete before replacement cleanup");
        var released = await UnregisterWhenReplacementPublishesAsync(client, newPlugin.ContractAssembly);
        Ensure(released.ReferencesReleased,
            "the replacement assembly should become the coordinator registration after caller cancellation");

        await UnregisterMultiClusterPluginAsync(harness, oldPlugin);
    }
}
