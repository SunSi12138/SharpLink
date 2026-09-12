using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
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
        await RegisterAllAsync(harness, plugin);
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
        await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
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
        await RegisterAllAsync(harness, plugin);
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
    public async Task ReplacementShouldRejectContractGenerationWhileDependentServiceRemainsRegistered()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var oldPlugin = PluginBundle.Load("replace-dependency-old");
        using var newPlugin = PluginBundle.Load("replace-dependency-new");
        await RegisterAllAsync(harness, oldPlugin);
        object? oldProxy = GetProxy(harness.Client, oldPlugin.ContractType);

        Ensure(string.Equals(
                oldPlugin.ContractAssembly.FullName,
                newPlugin.ContractAssembly.FullName,
                StringComparison.Ordinal),
            "the regression must exercise same-identity replacement across distinct collectible generations");
        var replacement = await harness.Server.ReplaceAssemblyAsync(
            oldPlugin.ContractAssembly,
            newPlugin.ContractAssembly,
            TimeSpan.Zero);
        Ensure(!replacement.Succeeded,
            "a Contract generation must not be replaced while a dynamic service dependant remains registered");
        Ensure(replacement.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
            "unsafe same-identity replacement must return the structured dependency error");
        Ensure(replacement.Error?.Message.Contains("depends on", StringComparison.Ordinal) == true,
            "replacement rejection should identify the retained dependant");
        Ensure(await InvokeValueTaskAsync<int>(
                oldProxy, oldPlugin.ContractType, "UnaryAsync", 9, CancellationToken.None) == 10,
            "rejected replacement must leave the old Contract/service snapshot serving normally");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            oldPlugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "dependent service release after replacement rejection");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            oldPlugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "old server Contract release after dependant removal");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            oldPlugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "old client Contract release after replacement rejection");
        oldProxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task ReplacementShouldProceedAfterDependentServiceIsRemoved()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var oldPlugin = PluginBundle.Load("replace-safe-order-old");
        using var newPlugin = PluginBundle.Load("replace-safe-order-new");
        await RegisterAllAsync(harness, oldPlugin);

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            oldPlugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "safe replacement removes the service dependant before replacing its Contract generation");
        var serverContract = await harness.Server.ReplaceAssemblyAsync(
            oldPlugin.ContractAssembly,
            newPlugin.ContractAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(serverContract.Succeeded && serverContract.ReferencesReleased,
            "server Contract replacement may proceed once no dynamic dependant retains the old generation");
        Ensure(harness.Server.RegisterAssembly(newPlugin.ServiceAssembly).Succeeded,
            "the new service generation may register after its new Contract generation is published");

        var clientContract = await harness.Client.ReplaceAssemblyAsync(
            oldPlugin.ContractAssembly,
            newPlugin.ContractAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(clientContract.Succeeded && clientContract.ReferencesReleased,
            "client Contract replacement without dependants remains supported");
        object? newProxy = GetProxy(harness.Client, newPlugin.ContractType);
        Ensure(await InvokeValueTaskAsync<int>(
                newProxy, newPlugin.ContractType, "UnaryAsync", 4, CancellationToken.None) == 5,
            "safe-order replacement publishes a usable new Contract/service generation");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            newPlugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "new service release after safe replacement");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            newPlugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "new server Contract release after safe replacement");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            newPlugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "new client Contract release after safe replacement");
        newProxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task ReplacementValidationFailureShouldLeaveTheOldSnapshotServing()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("replace-validation");
        await RegisterAllAsync(harness, plugin);
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
        Ensure(HasLocalProxyDescriptor(harness.Client, plugin.ContractType),
            "published snapshot contains the whole proxy descriptor");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.Zero)).ReferencesReleased, "concurrent registration snapshot releases");
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
}
