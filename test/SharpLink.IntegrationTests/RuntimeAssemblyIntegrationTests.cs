using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task MultiClusterDynamicRegistrationShouldRouteToOneExplicitSlot()
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
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
        RegisterMultiClusterPlugin(harness, client, plugin);

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
        await using var registrationSource = SharpClientBuilder.Create()
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
        await using var registrationSource = SharpClientBuilder.Create()
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
        await using var registrationSource = SharpClientBuilder.Create()
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
        await using var registrationSource = SharpClientBuilder.Create()
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
        RegisterMultiClusterPlugin(harness, client, oldPlugin);

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

    [Test]
    [NotInParallel]
    public async Task DynamicServiceRegistrationShouldRejectMissingProviderDependenciesTransactionally()
    {
        await using var harness = await DynamicHarness.CreateAsync(registerDynamicServiceDependencies: false);
        using var plugin = PluginBundle.Load("dynamic-missing-provider-dependency");
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "client contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "server contract registration");

        var result = harness.Server.RegisterAssembly(plugin.ServiceAssembly);
        Ensure(!result.Succeeded, "missing provider dependency rejects dynamic service assembly");
        Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
            "missing provider dependency error code");
        Ensure(result.Error?.Message.Contains(typeof(TimeProvider).FullName!, StringComparison.Ordinal) == true,
            "missing provider dependency diagnostic");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "failed service registration publishes no server state");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "client contract release after failed service registration");
    }

    [Test]
    [NotInParallel]
    public async Task RuntimeAssembliesShouldRegisterTransactionallyAndSupportEveryCallShape()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var nullClient = harness.Client.RegisterAssembly(null!);
        var nullServer = harness.Server.RegisterAssembly(null!);
        Ensure(nullClient.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidArgument,
            "null client registration is a structured error");
        Ensure(nullServer.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidArgument,
            "null server registration is a structured error");
        var missing = harness.Client.RegisterAssembly(typeof(string).Assembly);
        Ensure(!missing.Succeeded, "assembly without manifest must be rejected");
        Ensure(missing.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingManifest,
            "missing manifest error code");

        using var plugin = PluginBundle.Load("dynamic-call-shapes");
        plugin.ResetServiceState();

        var missingDependency = harness.Server.RegisterAssembly(plugin.ServiceAssembly);
        Ensure(!missingDependency.Succeeded, "service cannot precede its contract");
        Ensure(missingDependency.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
            "missing dependency error code");

        var clientContract = harness.Client.RegisterAssembly(plugin.ContractAssembly);
        Ensure(clientContract.Succeeded, $"client contract registration: {clientContract.Error}");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "server contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ServiceAssembly).Succeeded, "server service registration");
        Ensure(harness.Client.RegisterAssembly(plugin.ServiceAssembly).Succeeded,
            "client accepts identical service-assembly DTO codecs");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.Zero)).ReferencesReleased,
            "removing duplicate client codecs preserves the contract-owned codecs");

        var duplicate = harness.Server.RegisterAssembly(plugin.ServiceAssembly);
        Ensure(!duplicate.Succeeded, "same Assembly object cannot be registered twice");
        Ensure(duplicate.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
            "duplicate assembly error code");
        Ensure(duplicate.Error?.IncomingAssembly?.Contains("SharpLink.DynamicPlugin.Services", StringComparison.Ordinal) == true,
            "duplicate diagnostics contain full assembly identity");
        Ensure(duplicate.Error?.IncomingLoadContext?.Contains("dynamic-call-shapes", StringComparison.Ordinal) == true,
            "duplicate diagnostics contain ALC identity");

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var unary = await InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UnaryAsync", 7, CancellationToken.None);
        Ensure(unary == 8, "dynamic unary");
        var payloadType = plugin.GetContractType("SharpLink.DynamicPlugin.DynamicPayload");
        var payload = Activator.CreateInstance(payloadType)!;
        payloadType.GetProperty("Value")!.SetValue(payload, 5);
        payloadType.GetProperty("Label")!.SetValue(payload, "codec");
        payloadType.GetProperty("Parent")!.SetValue(payload, payload);
        var values = (System.Collections.IList)payloadType.GetProperty("Values")!.GetValue(payload)!;
        values.Add(1);
        values.Add(2);
        values.Add(3);
        var payloadResult = await InvokeValueTaskAsync<int>(
            proxy, plugin.ContractType, "UsePayloadAsync", payload, CancellationToken.None);
        Ensure(payloadResult == 16, "SharpPack dynamic nested/circular/collection payload");

        await InvokeValueTaskAsync(proxy, plugin.ContractType, "NotifyAsync", 3, CancellationToken.None);
        await WaitUntilAsync(() => plugin.GetStaticInt("Notifications") == 3);

        var clientStream = await InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "ClientStreamAsync",
            Values(1, 2, 3),
            CancellationToken.None);
        Ensure(clientStream == 6, "dynamic client stream");

        var serverStream = InvokeStream(proxy, plugin.ContractType, "ServerStreamAsync", 3, CancellationToken.None);
        Ensure((await CollectAsync(serverStream)).SequenceEqual([0, 1, 2]), "dynamic server stream");

        var duplex = InvokeStream(
            proxy,
            plugin.ContractType,
            "DuplexAsync",
            Values(2, 4, 6),
            CancellationToken.None);
        Ensure((await CollectAsync(duplex)).SequenceEqual([4, 8, 12]), "dynamic duplex stream");

        try
        {
            _ = await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.Zero);
            throw new Exception("assert failed: contract unload must be blocked by its service dependency");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("depends on", StringComparison.Ordinal),
                "dependency blocker diagnostic");
        }

        var serviceRelease = await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(serviceRelease.ReferencesReleased, "service references released");
        Ensure(plugin.GetStaticInt("Disposed") == 1, "dynamic singleton disposed exactly once");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "server contract references released");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "client contract references released");

        try
        {
            _ = await InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UnaryAsync", 1, CancellationToken.None);
            throw new Exception("assert failed: old proxy must fail locally after unregister");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "old proxy draining error code");
            Ensure(exception.Message.Contains("module is draining", StringComparison.Ordinal),
                "old proxy draining diagnostic");
        }
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task DormantDynamicStreamsShouldNotHoldModuleLeases()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-dormant-streams");
        RegisterAll(harness, plugin);

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var serverStream = InvokeStream(
            proxy, plugin.ContractType, "ServerStreamAsync", 1, CancellationToken.None);
        var duplexStream = InvokeStream(
            proxy, plugin.ContractType, "DuplexAsync", Values(1), CancellationToken.None);

        var released = await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly, TimeSpan.Zero);
        Ensure(released.ReferencesReleased, "unstarted streams do not hold client module leases");
        Ensure(released.RemainingCalls == 0 && released.RemainingStreams == 0,
            "unstarted streams leave no client module counters");

        await EnsureDrainingStreamAsync(serverStream, "dormant server stream");
        await EnsureDrainingStreamAsync(duplexStream, "dormant duplex stream");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "dormant stream service release");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "dormant stream server contract release");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task ServerStreamConsumerExitShouldReleaseDynamicModuleLeasesAndAllCounters()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-server-stream-consumer-exit");
        plugin.ResetServiceState();
        RegisterAll(harness, plugin);

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var clientModule = GetDynamicModule(harness.Client, plugin.ContractAssembly);
        var serverModule = GetDynamicModule(harness.Server, plugin.ServiceAssembly);
        await using (var enumerator = InvokeStream(
                proxy,
                plugin.ContractType,
                "ServerStreamAsync",
                int.MaxValue,
                CancellationToken.None)
            .GetAsyncEnumerator())
        {
            Ensure(await enumerator.MoveNextAsync(),
                "P2-T06 dynamic stream publishes one item before consumer exit");
            Ensure(enumerator.Current == 0,
                "P2-T06 dynamic stream first item preserves the expected route payload");
            Ensure(clientModule.RemainingCalls == 1 && clientModule.RemainingStreams == 1,
                "P2-T06 active stream holds one client contract module lease");
            Ensure(serverModule.RemainingCalls == 1 && serverModule.RemainingStreams == 1,
                "P2-T06 active stream holds one server service module lease");
        }

        Ensure(clientModule.RemainingCalls == 0 && clientModule.RemainingStreams == 0,
            "P2-T06 consumer exit synchronously releases the client module lease");
        var serverServiceRelease = harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(5)).AsTask();
        await serverModule.WaitForDrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(serverModule.RemainingCalls == 0 && serverModule.RemainingStreams == 0,
            "P2-T06 consumer cancellation naturally releases the server module before grace expires");
        var serverService = await serverServiceRelease.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(serverService.ReferencesReleased &&
               serverService.RemainingCalls == 0 &&
               serverService.RemainingStreams == 0,
            "P2-T06 consumer exit releases the server service module lease");
        Ensure(plugin.GetStaticInt("Disposed") == 1,
            "P2-T06 dynamic singleton is disposed exactly once");

        var clientContract = await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(clientContract.ReferencesReleased &&
               clientContract.RemainingCalls == 0 &&
               clientContract.RemainingStreams == 0,
            "P2-T06 consumer exit releases the client contract module lease");

        var serverContract = await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(serverContract.ReferencesReleased &&
               serverContract.RemainingCalls == 0 &&
               serverContract.RemainingStreams == 0,
            "P2-T06 server contract releases after the stream dispatcher exits");
        EnsureClientAndServerCountersAreZero(harness, "P2-T06 dynamic stream");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task EarlyServerResponseShouldRetainOnlyTheActiveClientStreamProducer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-early-client-stream-response");
        RegisterAll(harness, plugin);
        plugin.ResetServiceState();

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var response = InvokeValueTaskAsync<int>(
                proxy,
                plugin.ContractType,
                "RejectClientStreamAsync",
                BlockingValues(producerStarted, producerRelease.Task),
                CancellationToken.None);
            await producerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await plugin.GetStaticTask("RejectResponseStarted").WaitAsync(TimeSpan.FromSeconds(2));
            plugin.ReleaseRejectResponse();
            Ensure(await response == -1, "server may return without consuming the request stream");

            var serverService = await harness.Server.UnregisterAssemblyAsync(
                plugin.ServiceAssembly,
                TimeSpan.FromSeconds(2));
            Ensure(serverService.ReferencesReleased,
                "server request dispatchers are retired before the service module lease is released");
            Ensure(serverService.RemainingStreams == 0,
                "early server completion leaves no service-module stream lease");

            var clientContract = await harness.Client.UnregisterAssemblyAsync(
                plugin.ContractAssembly,
                TimeSpan.FromMilliseconds(20));
            Ensure(!clientContract.ReferencesReleased,
                "client contract remains leased by the background request-stream producer");
            Ensure(clientContract.RemainingStreams > 0,
                "the active request-stream producer is reported as a remaining stream");
        }
        finally
        {
            plugin.ReleaseRejectResponse();
            producerRelease.TrySetResult();
        }

        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "client contract releases after its producer exits");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "server contract releases after request-dispatcher cleanup");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task DisposalFailuresShouldNotSkipRemainingModuleCleanup()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-disposal-failure");
        RegisterAll(harness, plugin);

        const string firstContractName = "SharpLink.DynamicPlugin.IFirstThrowingDisposalService";
        const string secondContractName = "SharpLink.DynamicPlugin.ISecondThrowingDisposalService";
        const string firstServiceName = "SharpLink.DynamicPlugin.FirstThrowingDisposalService";
        const string secondServiceName = "SharpLink.DynamicPlugin.SecondThrowingDisposalService";
        var firstContract = plugin.GetContractType(firstContractName);
        var secondContract = plugin.GetContractType(secondContractName);
        object? firstProxy = GetProxy(harness.Client, firstContract);
        object? secondProxy = GetProxy(harness.Client, secondContract);
        plugin.InvokeServiceStatic(firstServiceName, "Reset");
        plugin.InvokeServiceStatic(secondServiceName, "Reset");
        Ensure(await InvokeValueTaskAsync<int>(
            firstProxy, firstContract, "TouchAsync", 1, CancellationToken.None) == 11,
            "first throwing service activation");
        Ensure(await InvokeValueTaskAsync<int>(
            secondProxy, secondContract, "TouchAsync", 2, CancellationToken.None) == 22,
            "second throwing service activation");
        plugin.InvokeServiceStatic(firstServiceName, "EnableDisposeFailure");
        plugin.InvokeServiceStatic(secondServiceName, "EnableDisposeFailure");

        try
        {
            _ = await harness.Server.UnregisterAssemblyAsync(
                plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
            throw new Exception("assert failed: service disposal failure must be reported");
        }
        catch (Exception exception)
        {
            Ensure(ContainsMessage(exception, "First dynamic disposal failure."),
                "first disposal failure is preserved");
            Ensure(ContainsMessage(exception, "Second dynamic disposal failure."),
                "second disposal failure is preserved");
        }

        Ensure(plugin.GetServiceStaticInt(firstServiceName, "Disposed") == 1,
            "first throwing service disposed once");
        Ensure(plugin.GetServiceStaticInt(secondServiceName, "Disposed") == 1,
            "second throwing service disposed despite the first failure");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "contract releases after disposal failure");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "client releases after disposal failure");
        firstProxy = null;
        secondProxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task ServerStopShouldFinishStaticCleanupAfterDynamicDisposalFailure()
    {
        ShutdownCleanupProbe.Reset();
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-stop-disposal-failure");
        RegisterAll(harness, plugin);

        const string firstContractName = "SharpLink.DynamicPlugin.IFirstThrowingDisposalService";
        const string secondContractName = "SharpLink.DynamicPlugin.ISecondThrowingDisposalService";
        const string firstServiceName = "SharpLink.DynamicPlugin.FirstThrowingDisposalService";
        const string secondServiceName = "SharpLink.DynamicPlugin.SecondThrowingDisposalService";
        var firstContract = plugin.GetContractType(firstContractName);
        var secondContract = plugin.GetContractType(secondContractName);
        object? firstProxy = GetProxy(harness.Client, firstContract);
        object? secondProxy = GetProxy(harness.Client, secondContract);
        var staticProxy = harness.Client.Get<IShutdownCleanupProbe>();
        plugin.InvokeServiceStatic(firstServiceName, "Reset");
        plugin.InvokeServiceStatic(secondServiceName, "Reset");

        Ensure(await InvokeValueTaskAsync<int>(
            firstProxy, firstContract, "TouchAsync", 1, CancellationToken.None) == 11,
            "first dynamic shutdown service activation");
        Ensure(await InvokeValueTaskAsync<int>(
            secondProxy, secondContract, "TouchAsync", 2, CancellationToken.None) == 22,
            "second dynamic shutdown service activation");
        Ensure(await staticProxy.TouchAsync(3, CancellationToken.None) == 103,
            "static shutdown service activation");
        plugin.InvokeServiceStatic(firstServiceName, "EnableDisposeFailure");

        await harness.Client.StopAsync();
        Exception? stopFailure = null;
        try
        {
            await harness.Server.StopAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception)
        {
            stopFailure = exception;
            harness.ExpectServerStopFailure("First dynamic disposal failure.");
        }

        Ensure(stopFailure is not null && ContainsMessage(stopFailure, "First dynamic disposal failure."),
            "Server Stop must surface the dynamic disposal failure");
        Ensure(plugin.GetServiceStaticInt(firstServiceName, "Disposed") == 1,
            "throwing dynamic service disposed once during stop");
        Ensure(plugin.GetServiceStaticInt(secondServiceName, "Disposed") == 1,
            "remaining dynamic service disposed after the first failure");
        Ensure(ShutdownCleanupProbe.Disposed == 1,
            "static service cleanup continues after dynamic module disposal fails");
        firstProxy = null;
        secondProxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task FailedConnectionActivationShouldBeEvictedAndRetried()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-flaky-connection");
        RegisterAll(harness, plugin);

        const string contractName = "SharpLink.DynamicPlugin.IFlakyConnectionService";
        const string serviceName = "SharpLink.DynamicPlugin.FlakyConnectionService";
        var contract = plugin.GetContractType(contractName);
        object? proxy = GetProxy(harness.Client, contract);
        plugin.InvokeServiceStatic(serviceName, "Reset");

        try
        {
            _ = await InvokeValueTaskAsync<int>(
                proxy, contract, "TouchAsync", 1, CancellationToken.None);
            throw new Exception("assert failed: first connection activation must fail");
        }
        catch (SharpLinkException)
        {
        }

        Ensure(await InvokeValueTaskAsync<int>(
            proxy, contract, "TouchAsync", 2, CancellationToken.None) == 32,
            "same connection retries a transient service activation failure");
        Ensure(plugin.GetServiceStaticInt(serviceName, "Activations") == 2,
            "connection activation retried exactly once");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "flaky connection service release");
        Ensure(plugin.GetServiceStaticInt(serviceName, "Disposed") == 1,
            "successfully activated connection service disposed once");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "flaky server contract release");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "flaky client contract release");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task ModuleUnregisterShouldJoinRetiredConnectionServiceCleanup()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-retired-connection");
        RegisterAll(harness, plugin);

        const string contractName = "SharpLink.DynamicPlugin.IRetiredConnectionService";
        const string serviceName = "SharpLink.DynamicPlugin.RetiredConnectionService";
        var contract = plugin.GetContractType(contractName);
        object? proxy = GetProxy(harness.Client, contract);
        plugin.InvokeServiceStatic(serviceName, "Reset");
        Ensure(await InvokeValueTaskAsync<int>(
            proxy, contract, "TouchAsync", 2, CancellationToken.None) == 42,
            "retired connection service activation");

        await harness.Client.StopAsync();
        await plugin.GetServiceStaticTask(serviceName, "DisposeStarted")
            .WaitAsync(TimeSpan.FromSeconds(2));
        var unregister = harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2)).AsTask();
        await Task.Delay(20);
        Ensure(!unregister.IsCompleted,
            "module unregister joins cleanup owned by a disconnected connection");

        plugin.InvokeServiceStatic(serviceName, "ReleaseDispose");
        Ensure((await unregister).ReferencesReleased,
            "retired connection service references released after shared cleanup");
        Ensure(plugin.GetServiceStaticInt(serviceName, "Disposed") == 1,
            "retired connection service disposed exactly once");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "retired server contract release");
        Ensure(!(await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "client stop already released the retired client contract module");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task UnregisterTimeoutShouldKeepRouteOwnedUntilIgnoredCallActuallyEnds()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-draining");
        plugin.ResetServiceState();
        RegisterAll(harness, plugin);

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var blocked = InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "BlockIgnoringCancellationAsync",
            callerCancellation.Token).AsTask();
        await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        var timedOut = await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromMilliseconds(20));
        Ensure(!timedOut.ReferencesReleased, "ignored cancellation keeps framework references");
        Ensure(timedOut.RemainingCalls == 1, "one call remains after drain timeout");

        try
        {
            _ = await InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UnaryAsync", 1, CancellationToken.None);
            throw new Exception("assert failed: draining route must reject new work");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "draining server route error code");
            Ensure(exception.Message.Contains("module is draining", StringComparison.Ordinal),
                "draining server route diagnostic");
        }

        plugin.ReleaseBlock();
        try
        {
            _ = await blocked.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception exception) when (exception is OperationCanceledException or SharpLinkException)
        {
        }
        await WaitUntilAsync(() => plugin.GetStaticInt("Disposed") == 1);

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "contract releases after background drain");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "client contract releases");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task NonCooperativeSynchronousCallShouldObserveModuleDrainBeforeResponding()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-synchronous-drain");
        plugin.ResetServiceState();
        RegisterAll(harness, plugin);

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var blocked = InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "BlockSynchronously").AsTask();
        await plugin.GetStaticTask("SynchronousBlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        var timedOut = await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromMilliseconds(20));
        Ensure(!timedOut.ReferencesReleased && timedOut.RemainingCalls == 1,
            "non-cooperative synchronous call keeps its module lease through timeout");

        plugin.ReleaseSynchronousBlock();
        try
        {
            _ = await blocked.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception("assert failed: drained synchronous call must not return success");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable,
                "drained synchronous call error code");
            Ensure(exception.Message.Contains("module is draining", StringComparison.Ordinal),
                "drained synchronous call diagnostic");
        }
        finally
        {
            plugin.ReleaseSynchronousBlock();
        }

        await WaitUntilAsync(() => plugin.GetStaticInt("Disposed") == 1);
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "synchronous drain server contract release");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "synchronous drain client contract release");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task UnregisterTimeoutShouldCancelCooperativeCallAndNotifyItsClient()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-cooperative-drain");
        plugin.ResetServiceState();
        RegisterAll(harness, plugin);

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var blocked = InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "BlockAsync",
            CancellationToken.None).AsTask();
        await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        var released = await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromMilliseconds(20));
        Ensure(released.ReferencesReleased, "cooperative call releases during targeted cancellation");

        try
        {
            _ = await blocked.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception("assert failed: canceled module call must notify its client");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable,
                "cooperative module cancellation error code");
            Ensure(exception.Message.Contains("module is draining", StringComparison.Ordinal),
                "cooperative module cancellation diagnostic");
        }

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "server contract release after cooperative drain");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "client contract release after cooperative drain");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task SameNamedAssembliesInDifferentCollectibleContextsShouldReportConflictWithoutAliasing()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var first = PluginBundle.Load("same-name-first", loadService: false);
        using var second = PluginBundle.Load("same-name-second", loadService: false);

        var firstRegistration = harness.Client.RegisterAssembly(first.ContractAssembly);
        Ensure(firstRegistration.Succeeded, $"first same-name assembly registers: {firstRegistration.Error}");
        var conflict = harness.Client.RegisterAssembly(second.ContractAssembly);
        Ensure(!conflict.Succeeded, "second same-name route conflicts");
        Ensure(conflict.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
            "same-name route conflict code");
        Ensure(conflict.Error?.IncomingLoadContext?.Contains("same-name-second", StringComparison.Ordinal) == true,
            "incoming ALC diagnostic");
        Ensure(conflict.Error?.ExistingLoadContext?.Contains("same-name-first", StringComparison.Ordinal) == true,
            "existing ALC diagnostic");
        Ensure(conflict.Error?.ContractId is not null &&
               conflict.Error?.IncomingFingerprint?.Length == 64 &&
               conflict.Error?.ExistingFingerprint?.Length == 64,
            "route conflict contains ID and full fingerprints");

        Ensure((await harness.Client.UnregisterAssemblyAsync(
            first.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "first same-name assembly releases");
    }

    [Test]
    [NotInParallel]
    public async Task CancelledUnregisterWaitsShouldNotCancelClientOrServerBackgroundDrain()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-cancelled-unregister-waits");
        plugin.ResetServiceState();
        RegisterAll(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var blocked = InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "BlockIgnoringCancellationAsync",
            CancellationToken.None).AsTask();
        await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        using var clientCancellation = new CancellationTokenSource();
        using var serverCancellation = new CancellationTokenSource();
        var clientWait = harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2),
            clientCancellation.Token).AsTask();
        var serverWait = harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2),
            serverCancellation.Token).AsTask();
        clientCancellation.Cancel();
        serverCancellation.Cancel();
        await EnsureCancelledAsync(clientWait, "client unregister wait");
        await EnsureCancelledAsync(serverWait, "server unregister wait");

        plugin.ReleaseBlock();
        Ensure(await blocked.WaitAsync(TimeSpan.FromSeconds(2)) == 43,
            "the admitted call completes while both background drains continue");
        await WaitUntilAsync(() => plugin.GetStaticInt("Disposed") == 1);

        SharpLinkAssemblyRegistrationResult clientRegistration = default;
        await WaitUntilAsync(() =>
            (clientRegistration = harness.Client.RegisterAssembly(plugin.ContractAssembly)).Succeeded);
        Ensure(clientRegistration.Succeeded,
            "client background drain removes the cancelled waiter's old registration");
        SharpLinkAssemblyRegistrationResult serverRegistration = default;
        await WaitUntilAsync(() =>
            (serverRegistration = harness.Server.RegisterAssembly(plugin.ServiceAssembly)).Succeeded);
        Ensure(serverRegistration.Succeeded,
            "server background drain removes the cancelled waiter's old registration");
        object? reRegisteredProxy = GetProxy(harness.Client, plugin.ContractType);
        Ensure(await InvokeValueTaskAsync<int>(
                reRegisteredProxy,
                plugin.ContractType,
                "UnaryAsync",
                5,
                CancellationToken.None) == 6,
            "re-registered client and server routes serve a new call");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "re-registered server service release");
        Ensure(plugin.GetStaticInt("Disposed") == 2,
            "each server service registration is disposed exactly once");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "server contract release");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "re-registered client contract release");
        proxy = null;
        reRegisteredProxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentUnregisterCallersShouldShareOneDrainOperation()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-shared-unregister");
        plugin.ResetServiceState();
        RegisterAll(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var blocked = InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "BlockIgnoringCancellationAsync",
            CancellationToken.None).AsTask();
        await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        var first = harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2)).AsTask();
        var second = harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2)).AsTask();
        Ensure(ReferenceEquals(first, second), "concurrent callers observe the same operation task");
        await Task.Delay(20);
        Ensure(!first.IsCompleted, "shared unregister waits for the active call");

        plugin.ReleaseBlock();
        Ensure(await blocked.WaitAsync(TimeSpan.FromSeconds(2)) == 43, "active call completes during grace");
        var firstResult = await first;
        var secondResult = await second;
        Ensure(firstResult.ReferencesReleased && secondResult.ReferencesReleased,
            "both callers observe successful release");
        Ensure(plugin.GetStaticInt("Disposed") == 1, "shared unregister disposes singleton once");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "server contract release");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "client contract release");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task ReplacementShouldPublishNewRoutesWhileOldUnaryDrainsAndThenReleaseItsAlc()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var oldPlugin = PluginBundle.Load("replace-old");
        using var newPlugin = PluginBundle.Load("replace-new");
        oldPlugin.ResetServiceState();
        newPlugin.ResetServiceState();
        RegisterAll(harness, oldPlugin);

        object? oldProxy = GetProxy(harness.Client, oldPlugin.ContractType);
        var oldCall = InvokeValueTaskAsync<int>(
            oldProxy,
            oldPlugin.ContractType,
            "BlockIgnoringCancellationAsync",
            CancellationToken.None).AsTask();
        await oldPlugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        var serverContract = await harness.Server.ReplaceAssemblyAsync(
            oldPlugin.ContractAssembly,
            newPlugin.ContractAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(serverContract.Succeeded && serverContract.ReferencesReleased,
            "contract-only replacement drains immediately");

        var serverServiceTask = harness.Server.ReplaceAssemblyAsync(
            oldPlugin.ServiceAssembly,
            newPlugin.ServiceAssembly,
            TimeSpan.FromSeconds(5)).AsTask();
        var clientContractTask = harness.Client.ReplaceAssemblyAsync(
            oldPlugin.ContractAssembly,
            newPlugin.ContractAssembly,
            TimeSpan.FromSeconds(5)).AsTask();

        object? newProxy = GetProxy(harness.Client, newPlugin.ContractType);
        await InvokeValueTaskAsync(
            newProxy,
            newPlugin.ContractType,
            "NotifyAsync",
            17,
            CancellationToken.None);
        await WaitUntilAsync(() => newPlugin.GetStaticInt("Notifications") == 17);
        Ensure(oldPlugin.GetStaticInt("Notifications") == 0,
            "post-switch request enters only the new service registration");
        Ensure(!serverServiceTask.IsCompleted && !clientContractTask.IsCompleted,
            "old server and client registrations remain alive while their admitted call is active");

        oldPlugin.ReleaseBlock();
        Ensure(await oldCall.WaitAsync(TimeSpan.FromSeconds(2)) == 43,
            "admitted old unary completes on its original registration");
        var serverService = await serverServiceTask;
        Ensure(serverService.Succeeded && serverService.ReferencesReleased,
            "old service registration releases after the last call");
        var clientContract = await clientContractTask;
        Ensure(clientContract.Succeeded && clientContract.ReferencesReleased,
            "old client registration releases after the last call");
        Ensure(oldPlugin.GetStaticInt("Disposed") == 1, "old singleton is observed and disposed once");
        Ensure(newPlugin.GetStaticInt("Disposed") == 0, "new singleton remains active");

        oldProxy = null;
        var weakOldContext = oldPlugin.Unload();
        for (var attempt = 0; attempt < 20 && weakOldContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        Ensure(!weakOldContext.IsAlive, "replacement cleanup releases the old collectible ALC");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            newPlugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "new service release");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            newPlugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "new server contract release");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            newPlugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "new client contract release");
        newProxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task OneHundredDynamicModuleReplacementsShouldPublishNewRouteWhileOldUnaryDrainsWithoutLeaks()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var first = PluginBundle.Load("replace-race-first");
        using var second = PluginBundle.Load("replace-race-second");
        RegisterAll(harness, first);

        var current = first;
        var next = second;
        for (var iteration = 1; iteration <= 100; iteration++)
        {
            current.ResetServiceState();
            next.ResetServiceState();
            object? oldProxy = GetProxy(harness.Client, current.ContractType);
            var oldCall = InvokeValueTaskAsync<int>(
                oldProxy,
                current.ContractType,
                "BlockIgnoringCancellationAsync",
                CancellationToken.None).AsTask();
            await current.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

            object? newProxy = null;
            try
            {
                var serverContract = await harness.Server.ReplaceAssemblyAsync(
                    current.ContractAssembly,
                    next.ContractAssembly,
                    TimeSpan.FromSeconds(2));
                EnsureReplacementReleased(serverContract,
                    $"P2-T07 iteration {iteration}: server contract");

                var serverServiceTask = harness.Server.ReplaceAssemblyAsync(
                    current.ServiceAssembly,
                    next.ServiceAssembly,
                    TimeSpan.FromSeconds(5)).AsTask();
                var clientContractTask = harness.Client.ReplaceAssemblyAsync(
                    current.ContractAssembly,
                    next.ContractAssembly,
                    TimeSpan.FromSeconds(5)).AsTask();

                newProxy = GetProxy(harness.Client, next.ContractType);
                Ensure(await InvokeValueTaskAsync<int>(
                        newProxy,
                        next.ContractType,
                        "UnaryAsync",
                        iteration,
                        CancellationToken.None) == iteration + 1,
                    $"P2-T07 iteration {iteration}: the newly published route serves immediately");
                Ensure(next.GetStaticInt("Created") == 1,
                    $"P2-T07 iteration {iteration}: only the next service generation is activated");
                Ensure(!serverServiceTask.IsCompleted && !clientContractTask.IsCompleted,
                    $"P2-T07 iteration {iteration}: old registrations drain behind their admitted call");

                current.ReleaseBlock();
                Ensure(await oldCall.WaitAsync(TimeSpan.FromSeconds(2)) == 43,
                    $"P2-T07 iteration {iteration}: old unary completes on its original generation");
                EnsureReplacementReleased(await serverServiceTask,
                    $"P2-T07 iteration {iteration}: server service");
                EnsureReplacementReleased(await clientContractTask,
                    $"P2-T07 iteration {iteration}: client contract");
                Ensure(current.GetStaticInt("Disposed") == 1,
                    $"P2-T07 iteration {iteration}: old service generation is disposed exactly once");
                Ensure(next.GetStaticInt("Disposed") == 0,
                    $"P2-T07 iteration {iteration}: new service generation remains active");
                EnsureClientAndServerCountersAreZero(harness,
                    $"P2-T07 iteration {iteration}");
            }
            finally
            {
                current.ReleaseBlock();
                oldProxy = null;
                newProxy = null;
            }

            (current, next) = (next, current);
        }

        var finalService = await harness.Server.UnregisterAssemblyAsync(
            current.ServiceAssembly,
            TimeSpan.FromSeconds(2));
        var finalServerContract = await harness.Server.UnregisterAssemblyAsync(
            current.ContractAssembly,
            TimeSpan.FromSeconds(2));
        var finalClientContract = await harness.Client.UnregisterAssemblyAsync(
            current.ContractAssembly,
            TimeSpan.FromSeconds(2));
        EnsureUnregisterReleased(finalService, "P2-T07 final server service");
        EnsureUnregisterReleased(finalServerContract, "P2-T07 final server contract");
        EnsureUnregisterReleased(finalClientContract, "P2-T07 final client contract");
        EnsureClientAndServerCountersAreZero(harness, "P2-T07 final cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task ReplacementValidationFailureShouldLeaveTheOldSnapshotServing()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("replace-validation");
        RegisterAll(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);

        var result = await harness.Client.ReplaceAssemblyAsync(
            plugin.ContractAssembly,
            typeof(string).Assembly,
            TimeSpan.Zero);
        Ensure(!result.Succeeded, "invalid replacement is rejected");
        Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingManifest,
            "invalid replacement reports the manifest failure");
        Ensure(await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UnaryAsync", 9, CancellationToken.None) == 10,
            "old proxy remains active after preparation failure");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "service release after failed replacement");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "server contract release after failed replacement");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "client contract release after failed replacement");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task ReplacementTimeoutShouldCancelThenDeferOldServiceCleanup()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var oldPlugin = PluginBundle.Load("replace-timeout-old");
        using var newPlugin = PluginBundle.Load("replace-timeout-new");
        oldPlugin.ResetServiceState();
        newPlugin.ResetServiceState();
        RegisterAll(harness, oldPlugin);
        object? proxy = GetProxy(harness.Client, oldPlugin.ContractType);
        var blocked = InvokeValueTaskAsync<int>(
            proxy,
            oldPlugin.ContractType,
            "BlockIgnoringCancellationAsync",
            CancellationToken.None).AsTask();
        await oldPlugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        Ensure((await harness.Server.ReplaceAssemblyAsync(
            oldPlugin.ContractAssembly,
            newPlugin.ContractAssembly,
            TimeSpan.Zero)).ReferencesReleased, "timeout test contract replacement");
        var timedOut = await harness.Server.ReplaceAssemblyAsync(
            oldPlugin.ServiceAssembly,
            newPlugin.ServiceAssembly,
            TimeSpan.FromMilliseconds(20));
        Ensure(timedOut.Succeeded && !timedOut.ReferencesReleased,
            "published replacement returns at its graceful bound");
        Ensure(timedOut.RemainingCalls > 0,
            "bounded replacement reports the non-cooperative old call");
        Ensure(oldPlugin.GetStaticInt("Disposed") == 0,
            "old service is not disposed while user code is still active");

        var clientReplacement = await harness.Client.ReplaceAssemblyAsync(
            oldPlugin.ContractAssembly,
            newPlugin.ContractAssembly,
            TimeSpan.FromMilliseconds(20));
        Ensure(clientReplacement.Succeeded,
            "client replacement publishes even when the remote old call was already canceled");
        object? newProxy = GetProxy(harness.Client, newPlugin.ContractType);
        Ensure(await InvokeValueTaskAsync<int>(
                newProxy, newPlugin.ContractType, "UnaryAsync", 4, CancellationToken.None) == 5,
            "new server route accepts requests immediately after timeout");
        oldPlugin.ReleaseBlock();
        try
        {
            _ = await blocked.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable,
                "timed-out client observes targeted old-module cancellation");
        }
        catch (OperationCanceledException)
        {
            // The old client module's forced token may win the response race.
        }
        await WaitUntilAsync(() => oldPlugin.GetStaticInt("Disposed") == 1);

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            newPlugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "timeout replacement new service release");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            newPlugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "timeout replacement new contract release");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            newPlugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "timeout replacement client contract release");
        proxy = null;
        newProxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task OneHundredClientReplacementsShouldLeaveOneReusableRegistration()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var first = PluginBundle.Load("replace-cycle-first", loadService: false);
        using var second = PluginBundle.Load("replace-cycle-second", loadService: false);
        Ensure(harness.Client.RegisterAssembly(first.ContractAssembly).Succeeded,
            "initial replacement-cycle registration");

        var current = first.ContractAssembly;
        var next = second.ContractAssembly;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var result = await harness.Client.ReplaceAssemblyAsync(current, next, TimeSpan.Zero);
            Ensure(result.Succeeded && result.ReferencesReleased,
                $"replacement cycle {iteration} releases the prior registration");
            (current, next) = (next, current);
        }

        Ensure((await harness.Client.UnregisterAssemblyAsync(current, TimeSpan.Zero)).ReferencesReleased,
            "the only remaining registration releases after 100 replacements");
        Ensure(harness.Client.RegisterAssembly(current).Succeeded,
            "registry remains reusable after replacement cycles");
        Ensure((await harness.Client.UnregisterAssemblyAsync(current, TimeSpan.Zero)).ReferencesReleased,
            "reused replacement registration releases");
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentRegistrationShouldPublishExactlyOneCompleteSnapshot()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-concurrent-register", loadService: false);
        var registrations = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(
            () => harness.Client.RegisterAssembly(plugin.ContractAssembly))));
        Ensure(registrations.Count(static result => result.Succeeded) == 1,
            "exactly one concurrent registration commits");
        Ensure(registrations.Where(static result => !result.Succeeded).All(static result =>
                result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly),
            "all losing registrations are structured duplicates");
        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        Ensure(proxy is not null, "published snapshot contains the whole proxy descriptor");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.Zero)).ReferencesReleased, "concurrent registration snapshot releases");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task CollectibleContextShouldUnloadAfterFrameworkReferencesAreReleased()
    {
        var tracked = await LoadInvokeUnregisterAndUnloadAsync();
        for (var attempt = 0; attempt < 20 && tracked.AnyAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        Ensure(!tracked.AnyAlive,
            $"collectible plugin state must not be rooted by SharpLink; alive: {tracked.AliveNames}");
    }

    [Test]
    [Arguments("normal")]
    [Arguments("cancellation-before-first")]
    [Arguments("cancellation-mid-stream")]
    [Arguments("consumer-break")]
    [Arguments("service-exception")]
    [NotInParallel]
    public async Task Api4DynamicStreamExitShouldReleaseItsCollectibleContext(string exitMode)
    {
        var weakContext = await ExecuteDynamicStreamExitAndUnloadAsync(exitMode);
        for (var attempt = 0; attempt < 20 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        Ensure(!weakContext.IsAlive,
            $"API 4 dynamic stream '{exitMode}' must not retain its collectible ALC");
    }

    [Test]
    [NotInParallel]
    public async Task RejectedApi4DynamicRegistrationShouldReleaseItsCollectibleContext()
    {
        var weakContext = await RejectConflictingApi4AssemblyAndUnloadAsync();
        for (var attempt = 0; attempt < 20 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        Ensure(!weakContext.IsAlive,
            "rejected API 4 registration must not retain its collectible ALC");
    }

    [Test]
    [NotInParallel]
    public async Task TenThousandRegisterUnregisterCyclesShouldLeaveRegistryReusable()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-ten-thousand", loadService: false);
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var registered = harness.Client.RegisterAssembly(plugin.ContractAssembly);
            Ensure(registered.Succeeded, $"registration cycle {iteration}: {registered.Error}");
            var released = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.Zero);
            Ensure(released.ReferencesReleased, $"unregister cycle {iteration}");
        }
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "registry remains reusable after 10,000 cycles");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.Zero)).ReferencesReleased, "final cycle releases");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<TrackedWeakReferences> LoadInvokeUnregisterAndUnloadAsync()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var plugin = PluginBundle.Load("dynamic-unload");
        RegisterAll(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        Ensure(InvokeProbeBlocking(proxy, plugin.ContractType) == 10, "ALC probe call");
        var payloadType = plugin.GetContractType("SharpLink.DynamicPlugin.DynamicPayload");
        var payload = Activator.CreateInstance(payloadType)!;
        payloadType.GetProperty("Value")!.SetValue(payload, 5);
        payloadType.GetProperty("Label")!.SetValue(payload, "alc-sharppack");
        payloadType.GetProperty("Parent")!.SetValue(payload, payload);
        ((System.Collections.IList)payloadType.GetProperty("Values")!.GetValue(payload)!).Add(7);
        Ensure(await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UsePayloadAsync", payload, CancellationToken.None) == 25,
            "ALC SharpPack payload call");
        var tracked = CapturePluginRuntimeObjects(harness.Client, plugin.ContractAssembly);
        tracked.Add("ContractAssembly", plugin.ContractAssembly);
        tracked.Add("ServiceAssembly", plugin.ServiceAssembly);
        tracked.Add("ContractType", plugin.ContractType);
        tracked.Add("PayloadType", payloadType);
        payload = null;
        payloadType = null!;
        proxy = null;
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "ALC service release");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "ALC server contract release");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "ALC client contract release");
        tracked.Add("AssemblyLoadContext", plugin.Unload());
        return tracked;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ExecuteDynamicStreamExitAndUnloadAsync(string exitMode)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var plugin = PluginBundle.Load($"api4-stream-exit-{exitMode}");
        RegisterAll(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);

        if (string.Equals(exitMode, "normal", StringComparison.Ordinal))
        {
            Ensure((await CollectAsync(InvokeStream(
                    proxy,
                    plugin.ContractType,
                    "ServerStreamAsync",
                    3,
                    CancellationToken.None))).SequenceEqual([0, 1, 2]),
                "normal API 4 dynamic stream completes");
        }
        else if (string.Equals(exitMode, "service-exception", StringComparison.Ordinal))
        {
            try
            {
                _ = await CollectAsync(InvokeStream(
                    proxy,
                    plugin.ContractType,
                    "ThrowingServerStreamAsync",
                    CancellationToken.None));
                throw new Exception("assert failed: dynamic service stream must fail");
            }
            catch (SharpLinkException exception)
            {
                Ensure(exception.Code == SharpLinkErrorCode.Internal,
                    "dynamic service stream exception maps to Internal");
            }
        }
        else if (string.Equals(exitMode, "cancellation-before-first", StringComparison.Ordinal))
        {
            using var cancellation = new CancellationTokenSource();
            await using var enumerator = InvokeStream(
                    proxy,
                    plugin.ContractType,
                    "ServerStreamAsync",
                    int.MaxValue,
                    cancellation.Token)
                .GetAsyncEnumerator();
            cancellation.Cancel();
            var cancelled = false;
            try
            {
                _ = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            Ensure(cancelled,
                "API 4 dynamic stream cancellation before the first item reaches the caller");
        }
        else
        {
            using var cancellation = new CancellationTokenSource();
            var token = string.Equals(exitMode, "cancellation-mid-stream", StringComparison.Ordinal)
                ? cancellation.Token
                : CancellationToken.None;
            await using var enumerator = InvokeStream(
                    proxy,
                    plugin.ContractType,
                    "ServerStreamAsync",
                    int.MaxValue,
                    token)
                .GetAsyncEnumerator();
            Ensure(await enumerator.MoveNextAsync(),
                $"API 4 dynamic stream '{exitMode}' starts before exit");
            if (string.Equals(exitMode, "cancellation-mid-stream", StringComparison.Ordinal))
            {
                cancellation.Cancel();
                var cancelled = false;
                try
                {
                    _ = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                Ensure(cancelled,
                    "API 4 dynamic stream cancellation after the first item reaches the caller");
            }
            else
                Ensure(string.Equals(exitMode, "consumer-break", StringComparison.Ordinal),
                    $"unknown dynamic stream exit mode '{exitMode}'");
        }

        if (!string.Equals(exitMode, "cancellation-before-first", StringComparison.Ordinal))
        {
            await plugin.GetStaticTask("ServerStreamDisposed").WaitAsync(TimeSpan.FromSeconds(2));
        }
        proxy = null;
        var service = await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(service.ReferencesReleased,
            $"API 4 dynamic stream '{exitMode}' releases its service module before dependants");
        var serverContract = await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2));
        var clientContract = await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(serverContract.ReferencesReleased && clientContract.ReferencesReleased,
            $"API 4 dynamic stream '{exitMode}' releases all module references");
        EnsureClientAndServerCountersAreZero(harness, $"API 4 dynamic stream '{exitMode}'");
        return plugin.Unload();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RejectConflictingApi4AssemblyAndUnloadAsync()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var accepted = PluginBundle.Load("api4-registration-accepted", loadService: false);
        var rejected = PluginBundle.Load("api4-registration-rejected", loadService: false);
        Ensure(harness.Client.RegisterAssembly(accepted.ContractAssembly).Succeeded,
            "first API 4 dynamic contract registers");
        var conflict = harness.Client.RegisterAssembly(rejected.ContractAssembly);
        Ensure(!conflict.Succeeded &&
               conflict.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
            "conflicting API 4 dynamic contract is rejected before publication");
        var weakContext = rejected.Unload();
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            accepted.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "accepted API 4 contract releases after conflict verification");
        return weakContext;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RegisterRemoveAndUnloadMultiClusterPluginAsync()
    {
        var plugin = PluginBundle.Load("multi-cluster-runtime-remove", loadService: false);
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster(
                "plugins",
                child => child.UseTcp(IPAddress.Loopback.ToString(), 1),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        Ensure(client.RegisterAssembly("plugins", plugin.ContractAssembly).Succeeded,
            "runtime-remove setup registration");
        object? proxy = GetMultiClusterProxy(client, plugin.ContractType);
        Ensure(proxy is not null, "runtime-remove setup proxy");

        var removal = await client.RemoveClusterAsync("plugins", TimeSpan.FromSeconds(2));
        Ensure(removal is { Succeeded: true, ReferencesReleased: true, ForcedStop: false },
            "runtime-remove child cleanup must complete before unloading the plugin context");
        proxy = null;
        return plugin.Unload();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TrackedWeakReferences CapturePluginRuntimeObjects(
        ISharpLinkClient client,
        Assembly contractAssembly)
    {
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var tracked = new TrackedWeakReferences();
        var runtimeContext = (IRpcRuntimeContext)client.GetType()
            .GetProperty("RuntimeContext", instanceFlags)!
            .GetValue(client)!;
        var registrations = runtimeContext.GetType()
            .GetField("_manifestRegistrations", instanceFlags)!
            .GetValue(runtimeContext) as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("Runtime Manifest registrations were not enumerable.");
        foreach (var registration in registrations)
        {
            var registrationType = registration!.GetType();
            var manifest = (ISharpLinkGeneratedAssemblyManifest)registrationType
                .GetProperty("Manifest", instanceFlags)!
                .GetValue(registration)!;
            if (!ReferenceEquals(manifest.OwnerAssembly, contractAssembly))
                continue;

            tracked.Add("Manifest", manifest);
            tracked.Add("ManifestType", manifest.GetType());
            var scopes = (Array)registrationType.GetField("_scopes", instanceFlags)!.GetValue(registration)!;
            foreach (var scope in scopes)
            {
                tracked.Add("AdapterScope", scope!);
                var serializerContext = scope!.GetType().GetField("_context", instanceFlags)!.GetValue(scope);
                if (serializerContext is not null)
                    tracked.Add("SharpPackSerializerContext", serializerContext);
            }

            var codecs = registrationType.GetProperty("Codecs", instanceFlags)!.GetValue(registration)
                as System.Collections.IEnumerable
                ?? throw new InvalidOperationException("Manifest Codec registrations were not enumerable.");
            foreach (var pair in codecs)
            {
                var pairType = pair!.GetType();
                tracked.Add("CodecTargetType", pairType.GetProperty("Key")!.GetValue(pair)!);
                var codecRegistration = pairType.GetProperty("Value")!.GetValue(pair)!;
                var codecRegistrationType = codecRegistration.GetType();
                tracked.Add("GeneratedCodecFactory",
                    codecRegistrationType.GetProperty("Factory", instanceFlags)!.GetValue(codecRegistration)!);
                var preparedCodec = codecRegistrationType
                    .GetField("_preparedCodec", instanceFlags)!
                    .GetValue(codecRegistration);
                if (preparedCodec is not null)
                    tracked.Add("PreparedCodec", preparedCodec);
            }
        }
        Ensure(tracked.Count >= 7, "plugin runtime Adapter state was captured before unregister");
        return tracked;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeProbeBlocking(object proxy, Type contractType)
        => InvokeValueTaskAsync<int>(
                proxy,
                contractType,
                "UnaryAsync",
                9,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    private static void RegisterAll(DynamicHarness harness, PluginBundle plugin)
    {
        var client = harness.Client.RegisterAssembly(plugin.ContractAssembly);
        Ensure(client.Succeeded, $"client contract registration: {client.Error}");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "server contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ServiceAssembly).Succeeded, "server service registration");
    }

    private static void EnsureReplacementReleased(
        SharpLinkAssemblyReplacementResult result,
        string name)
        => Ensure(result.Succeeded &&
                  result.ReferencesReleased &&
                  result.RemainingCalls == 0 &&
                  result.RemainingStreams == 0,
            $"{name} publishes atomically and releases every old module counter: {result.Error}");

    private static void EnsureUnregisterReleased(
        SharpLinkAssemblyUnregisterResult result,
        string name)
        => Ensure(result.ReferencesReleased &&
                  result.RemainingCalls == 0 &&
                  result.RemainingStreams == 0,
            $"{name} releases every module counter");

    private static void EnsureClientAndServerCountersAreZero(
        DynamicHarness harness,
        string name)
    {
        var client = (SharpLinkClient)harness.Client;
        var serverActiveCalls = (int)(harness.Server.GetType().GetField(
                "_globalActiveCalls",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(harness.Server) ?? -1);
        Ensure(client.PendingCallCount == 0 &&
               client.ActiveClientCallCount == 0 &&
               client.ActiveClientStreamCount == 0 &&
               serverActiveCalls == 0,
            $"{name} leaves client pending/call/stream and server call counters at zero");
    }

    private static SharpLinkDynamicModule GetDynamicModule(object owner, Assembly assembly)
    {
        var modules = owner.GetType().GetField(
                "_dynamicModules",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            as System.Collections.IDictionary
            ?? throw new InvalidOperationException("Dynamic module registry was not found.");
        return modules[assembly] as SharpLinkDynamicModule
            ?? throw new InvalidOperationException(
                $"Dynamic module was not found for '{assembly.FullName}'.");
    }

    private static object GetProxy(ISharpLinkClient client, Type contractType)
    {
        var get = typeof(ISharpLinkClient).GetMethod(nameof(ISharpLinkClient.Get))!
            .MakeGenericMethod(contractType);
        return get.Invoke(client, null) ?? throw new InvalidOperationException("Dynamic proxy factory returned null.");
    }

    private static ValueTask<T> InvokeValueTaskAsync<T>(
        object proxy,
        Type contractType,
        string methodName,
        params object?[] arguments)
        => (ValueTask<T>)(contractType.GetMethod(methodName)!.Invoke(proxy, arguments) ??
            throw new InvalidOperationException($"{methodName} returned null."));

    private static ValueTask InvokeValueTaskAsync(
        object proxy,
        Type contractType,
        string methodName,
        params object?[] arguments)
        => (ValueTask)(contractType.GetMethod(methodName)!.Invoke(proxy, arguments) ??
            throw new InvalidOperationException($"{methodName} returned null."));

    private static IAsyncEnumerable<int> InvokeStream(
        object proxy,
        Type contractType,
        string methodName,
        params object?[] arguments)
        => (IAsyncEnumerable<int>)(contractType.GetMethod(methodName)!.Invoke(proxy, arguments) ??
            throw new InvalidOperationException($"{methodName} returned null."));

    private static async IAsyncEnumerable<int> Values(params int[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            yield return values[index];
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> BlockingValues(
        TaskCompletionSource started,
        Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        started.TrySetResult();
        await release.ConfigureAwait(false);
        yield return 1;
    }

    private static async Task<int[]> CollectAsync(IAsyncEnumerable<int> values)
    {
        var result = new List<int>();
        await foreach (var value in values.ConfigureAwait(false))
            result.Add(value);
        return [.. result];
    }

    private static async Task EnsureDrainingStreamAsync(IAsyncEnumerable<int> stream, string name)
    {
        try
        {
            _ = await CollectAsync(stream);
            throw new Exception($"assert failed: {name} must fail after module release");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, $"{name} draining error code");
            Ensure(exception.Message.Contains("module is draining", StringComparison.Ordinal),
                $"{name} draining diagnostic");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3d);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("Condition was not reached within three seconds.");
            await Task.Delay(10);
        }
    }

    private static async Task<ISharpLinkMultiClusterClient> CreateDynamicMultiClusterClientAsync(int port)
    {
        var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("plugins", child => child.UseTcp(IPAddress.Loopback.ToString(), port),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();
        return client;
    }

    private static void RegisterMultiClusterPlugin(
        DynamicHarness harness,
        ISharpLinkMultiClusterClient client,
        PluginBundle plugin)
    {
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "multi-cluster server contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ServiceAssembly).Succeeded,
            "multi-cluster server service registration");
        Ensure(client.RegisterAssembly("plugins", plugin.ContractAssembly).Succeeded,
            "multi-cluster client contract registration");
    }

    private static async Task UnregisterMultiClusterPluginAsync(DynamicHarness harness, PluginBundle plugin)
    {
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "multi-cluster server service release");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "multi-cluster server contract release");
    }

    private static async Task EnsureCancelledAsync(Task task, string name)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new Exception($"assert failed: {name} should observe caller cancellation");
    }

    private static async Task<SharpLinkAssemblyUnregisterResult> UnregisterWhenReplacementPublishesAsync(
        ISharpLinkMultiClusterClient client,
        Assembly assembly)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3d);
        while (true)
        {
            try
            {
                return await client.UnregisterAssemblyAsync("plugins", assembly, TimeSpan.FromSeconds(2));
            }
            catch (InvalidOperationException) when (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(10);
            }
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                if (ContainsMessage(inner, message))
                    return true;
            return false;
        }
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private sealed class TrackedWeakReferences
    {
        private readonly List<(string Name, WeakReference Reference)> _items = [];

        internal int Count => _items.Count;
        internal bool AnyAlive => _items.Any(static item => item.Reference.IsAlive);
        internal string AliveNames => string.Join(", ", _items
            .Where(static item => item.Reference.IsAlive)
            .Select(static item => item.Name)
            .Distinct(StringComparer.Ordinal));

        internal void Add(string name, object value)
            => _items.Add((name, value as WeakReference ?? new WeakReference(value, trackResurrection: false)));
    }

    private static object? GetMultiClusterProxy(ISharpLinkMultiClusterClient client, Type contractType)
        => typeof(ISharpLinkMultiClusterClient).GetMethod(nameof(ISharpLinkMultiClusterClient.Get))!
            .MakeGenericMethod(contractType)
            .Invoke(client, null);

    private sealed class PluginBundle : IDisposable
    {
        private PluginLoadContext? _context;

        private PluginBundle(
            PluginLoadContext context,
            Assembly contractAssembly,
            Assembly? serviceAssembly,
            Type contractType,
            Type? serviceType)
        {
            _context = context;
            ContractAssembly = contractAssembly;
            ServiceAssembly = serviceAssembly ?? contractAssembly;
            ContractType = contractType;
            ServiceType = serviceType;
        }

        internal Assembly ContractAssembly { get; private set; }
        internal Assembly ServiceAssembly { get; private set; }
        internal Type ContractType { get; private set; }
        private Type? ServiceType { get; set; }

        internal static PluginBundle Load(string contextName, bool loadService = true)
        {
            var directory = GetPluginOutputDirectory();
            var context = new PluginLoadContext(contextName, directory);
            var contract = context.LoadFromAssemblyPath(
                Path.Combine(directory, "SharpLink.DynamicPlugin.Contracts.dll"));
            Assembly? service = null;
            Type? serviceType = null;
            if (loadService)
            {
                service = context.LoadFromAssemblyPath(
                    Path.Combine(directory, "SharpLink.DynamicPlugin.Services.dll"));
                serviceType = service.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            }
            return new PluginBundle(
                context,
                contract,
                service,
                contract.GetType("SharpLink.DynamicPlugin.IDynamicPluginService", throwOnError: true)!,
                serviceType);
        }

        internal void ResetServiceState() => InvokeStatic("Reset");

        internal void ReleaseBlock() => InvokeStatic("ReleaseBlock");

        internal void ReleaseSynchronousBlock() => InvokeStatic("ReleaseSynchronousBlock");

        internal void ReleaseRejectResponse() => InvokeStatic("ReleaseRejectResponse");

        internal int GetStaticInt(string propertyName)
            => (int)(ServiceType!.GetProperty(propertyName)!.GetValue(null) ?? -1);

        internal Task GetStaticTask(string propertyName)
            => (Task)(ServiceType!.GetProperty(propertyName)!.GetValue(null) ??
                throw new InvalidOperationException($"Static task '{propertyName}' was null."));

        internal Type GetContractType(string typeName)
            => ContractAssembly.GetType(typeName, throwOnError: true)!;

        internal int GetServiceStaticInt(string typeName, string propertyName)
            => (int)(GetServiceType(typeName).GetProperty(propertyName)!.GetValue(null) ?? -1);

        internal Task GetServiceStaticTask(string typeName, string propertyName)
            => (Task)(GetServiceType(typeName).GetProperty(propertyName)!.GetValue(null) ??
                throw new InvalidOperationException($"Static task '{propertyName}' was null."));

        internal void InvokeServiceStatic(string typeName, string methodName)
            => GetServiceType(typeName).GetMethod(methodName)!.Invoke(null, null);

        private void InvokeStatic(string methodName)
            => ServiceType!.GetMethod(methodName)!.Invoke(null, null);

        private Type GetServiceType(string typeName)
            => ServiceAssembly.GetType(typeName, throwOnError: true)!;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal WeakReference Unload()
        {
            var context = _context ?? throw new ObjectDisposedException(nameof(PluginBundle));
            var weak = new WeakReference(context, trackResurrection: false);
            ContractAssembly = null!;
            ServiceAssembly = null!;
            ContractType = null!;
            ServiceType = null;
            _context = null;
            context.Unload();
            return weak;
        }

        public void Dispose()
        {
            if (_context is not null)
                _ = Unload();
        }

        private static string GetPluginOutputDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
                directory = directory.Parent;
            if (directory is null)
                throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
            return Path.Combine(
                directory.FullName,
                "test",
                "SharpLink.DynamicServices",
                "bin",
                "Release",
                "net10.0");
        }
    }

    private sealed class PluginLoadContext(string name, string directory)
        : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = Default.Assemblies.FirstOrDefault(candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
            if (shared is not null)
                return shared;
            var path = Path.Combine(directory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class ControlledDynamicAssemblyClient : ISharpLinkClient, IDynamicAssemblyRegistrationInspector
    {
        private readonly Lock _gate = new();
        private readonly HashSet<Assembly> _registeredAssemblies = new(ReferenceEqualityComparer.Instance);
        private readonly SharpLinkAssemblyRegistrationResult _registrationResult;
        private int _unregisterCalls;
        private int _rejectNextUnregister;
        private int _blockNextUnregisterRejection;
        private int _publishReplacementThenFailCleanup;

        internal ControlledDynamicAssemblyClient(SharpLinkAssemblyRegistrationResult registrationResult)
        {
            _registrationResult = registrationResult;
        }

        internal TaskCompletionSource<bool> FirstUnregisterStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> RejectedUnregisterStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<SharpLinkAssemblyUnregisterResult> FirstUnregisterCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<SharpLinkAssemblyUnregisterResult> RejectedUnregisterCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkConnectionState State => SharpLinkConnectionState.Ready;

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
        {
            lock (_gate)
                _registeredAssemblies.Add(assembly);
            return _registrationResult;
        }

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
        {
            _ = assembly;
            _ = gracefulTimeout;
            _ = cancellationToken;
            if (Interlocked.Exchange(ref _rejectNextUnregister, 0) != 0)
            {
                return ValueTask.FromException<SharpLinkAssemblyUnregisterResult>(
                    new InvalidOperationException("controlled child unregister rejected"));
            }
            if (Interlocked.Exchange(ref _blockNextUnregisterRejection, 0) != 0)
            {
                RejectedUnregisterStarted.TrySetResult(true);
                return new ValueTask<SharpLinkAssemblyUnregisterResult>(RejectedUnregisterCompletion.Task);
            }
            if (Interlocked.Increment(ref _unregisterCalls) == 1)
            {
                FirstUnregisterStarted.TrySetResult(true);
                return new ValueTask<SharpLinkAssemblyUnregisterResult>(FirstUnregisterCompletion.Task);
            }
            return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
        }

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
        {
            _ = gracefulTimeout;
            _ = cancellationToken;
            if (Interlocked.Exchange(ref _publishReplacementThenFailCleanup, 0) == 0)
                throw new NotSupportedException();
            lock (_gate)
            {
                _registeredAssemblies.Remove(oldAssembly);
                _registeredAssemblies.Add(newAssembly);
            }
            return ValueTask.FromException<SharpLinkAssemblyReplacementResult>(
                new InvalidOperationException("controlled replacement cleanup failure"));
        }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        public TContract Get<TContract>() where TContract : IService
            => default!;



        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService


            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public bool IsDynamicAssemblyRegistered(Assembly assembly)
        {
            lock (_gate)
                return _registeredAssemblies.Contains(assembly);
        }

        internal void CompleteTimedOutUnregister()
            => FirstUnregisterCompletion.TrySetResult(new SharpLinkAssemblyUnregisterResult
            {
                ReferencesReleased = false,
                RemainingCalls = 1
            });

        internal void ReleaseAssembly(Assembly assembly)
        {
            lock (_gate)
                _registeredAssemblies.Remove(assembly);
        }

        internal void RejectNextUnregister() => Volatile.Write(ref _rejectNextUnregister, 1);

        internal void BlockAndRejectNextUnregister() => Volatile.Write(ref _blockNextUnregisterRejection, 1);

        internal void PublishReplacementThenFailCleanup()
            => Volatile.Write(ref _publishReplacementThenFailCleanup, 1);

        internal void CompleteRejectedUnregister()
            => RejectedUnregisterCompletion.TrySetException(
                new InvalidOperationException("controlled child unregister rejected"));

        internal int UnregisterCalls => Volatile.Read(ref _unregisterCalls);
    }

    private sealed class BlockingConnectClient : ISharpLinkClient
    {
        private readonly bool _releaseWhenStopped;
        private readonly bool _ignoreCancellation;
        private readonly TaskCompletionSource _connectRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = (int)SharpLinkConnectionState.Created;

        internal BlockingConnectClient(bool releaseWhenStopped = true, bool ignoreCancellation = false)
        {
            _releaseWhenStopped = releaseWhenStopped;
            _ignoreCancellation = ignoreCancellation;
        }

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkConnectionState State => (SharpLinkConnectionState)Volatile.Read(ref _state);

        public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            if (_ignoreCancellation)
                await _connectRelease.Task.ConfigureAwait(false);
            else
                await _connectRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, (int)SharpLinkConnectionState.Ready);
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (_releaseWhenStopped)
                _connectRelease.TrySetResult();
            Volatile.Write(ref _state, (int)SharpLinkConnectionState.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        public TContract Get<TContract>() where TContract : IService => throw new NotSupportedException();



        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService


            => throw new NotSupportedException();

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => StopAsync();

        internal void ReleaseConnect() => _connectRelease.TrySetResult();
    }

    private sealed class DynamicHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private readonly ServiceProvider _serviceProvider;
        private string? _expectedServerStopFailure;

        private DynamicHarness(
            ISharpLinkServer server,
            ISharpLinkClient client,
            int port,
            CancellationTokenSource serverCancellation,
            Task serverTask,
            ServiceProvider serviceProvider)
        {
            Server = server;
            Client = client;
            Port = port;
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
            _serviceProvider = serviceProvider;
        }

        internal ISharpLinkServer Server { get; }
        internal ISharpLinkClient Client { get; }
        internal int Port { get; }

        internal void ExpectServerStopFailure(string message)
            => _expectedServerStopFailure = message;

        internal static async Task<DynamicHarness> CreateAsync(
            bool registerDynamicServiceDependencies = true)
        {
            var serverCancellation = new CancellationTokenSource();
            var services = new ServiceCollection();
            if (registerDynamicServiceDependencies)
                services.AddSingleton(TimeProvider.System);
            var serviceProvider = services.BuildServiceProvider();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseServiceProvider(serviceProvider);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = server.RunAsync(serverCancellation.Token).AsTask();
            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .Build();
            await client.ConnectAsync();
            return new DynamicHarness(server, client, port, serverCancellation, serverTask, serviceProvider);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync();
            try
            {
                await Server.StopAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (
                _expectedServerStopFailure is { } message && ContainsMessage(exception, message))
            {
            }
            await _serverCancellation.CancelAsync();
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException ||
                _expectedServerStopFailure is { } message && ContainsMessage(exception, message))
            {
            }
            _serverCancellation.Dispose();
            await _serviceProvider.DisposeAsync();
        }
    }
}

[RpcContract]
public interface IShutdownCleanupProbe : IService
{
    ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ShutdownCleanupProbe : IShutdownCleanupProbe, IAsyncDisposable
{
    private static int _disposed;

    internal static int Disposed => Volatile.Read(ref _disposed);

    internal static void Reset() => Volatile.Write(ref _disposed, 0);

    public ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return ValueTask.FromResult(value + 100);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }
}
