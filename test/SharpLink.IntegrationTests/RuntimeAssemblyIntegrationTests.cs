using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed class RuntimeAssemblyIntegrationTests
{
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
        var payload = Activator.CreateInstance(payloadType, 5, "codec")!;
        var payloadResult = await InvokeValueTaskAsync<int>(
            proxy, plugin.ContractType, "UsePayloadAsync", payload, CancellationToken.None);
        Ensure(payloadResult == 10, "identical contract and service DTO codecs");

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
    public async Task EarlyServerResponseShouldRetainOnlyTheActiveClientStreamProducer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-early-client-stream-response");
        RegisterAll(harness, plugin);

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
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("dynamic disposal failure", StringComparison.Ordinal),
                "original disposal failure is preserved");
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
        var weakContext = await LoadInvokeUnregisterAndUnloadAsync();
        for (var attempt = 0; attempt < 20 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        Ensure(!weakContext.IsAlive, "collectible ALC must not be rooted by SharpLink");
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
    private static async Task<WeakReference> LoadInvokeUnregisterAndUnloadAsync()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var plugin = PluginBundle.Load("dynamic-unload");
        RegisterAll(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        Ensure(InvokeProbeBlocking(proxy, plugin.ContractType) == 10, "ALC probe call");
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
        return plugin.Unload();
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

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

    private sealed class DynamicHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private readonly ServiceProvider _serviceProvider;

        private DynamicHarness(
            ISharpLinkServer server,
            ISharpLinkClient client,
            CancellationTokenSource serverCancellation,
            Task serverTask,
            ServiceProvider serviceProvider)
        {
            Server = server;
            Client = client;
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
            _serviceProvider = serviceProvider;
        }

        internal ISharpLinkServer Server { get; }
        internal ISharpLinkClient Client { get; }

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
            return new DynamicHarness(server, client, serverCancellation, serverTask, serviceProvider);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync();
            await Server.StopAsync(TimeSpan.FromSeconds(2));
            await _serverCancellation.CancelAsync();
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
            }
            _serverCancellation.Dispose();
            await _serviceProvider.DisposeAsync();
        }
    }
}
