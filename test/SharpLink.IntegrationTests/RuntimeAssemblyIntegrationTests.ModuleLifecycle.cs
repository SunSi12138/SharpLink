using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
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
        await RegisterAllAsync(harness, plugin);

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
        await RegisterAllAsync(harness, plugin);

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
        await RegisterAllAsync(harness, plugin);
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
        await RegisterAllAsync(harness, plugin);

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
        await RegisterAllAsync(harness, plugin);

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
        await RegisterAllAsync(harness, plugin);

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
        await RegisterAllAsync(harness, plugin);

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
        await RegisterAllAsync(harness, plugin);

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
        await RegisterAllAsync(harness, plugin);

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
        await RegisterAllAsync(harness, plugin);

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
}
