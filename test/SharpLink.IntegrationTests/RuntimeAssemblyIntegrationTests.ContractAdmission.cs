namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task WaitingGeneratedProxyShouldResumeAfterRealTcpManifestPublication()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("contract-admission-wait");
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded, "local contract registration");
        var proxy = CreateWaitingProxy(harness.Client, plugin.ContractType);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var call = InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UnaryAsync", 6, timeout.Token).AsTask();
        Ensure(!call.IsCompleted && ((SharpLinkClient)harness.Client).ContractWaiterCountForTesting == 1,
            "the generated call waits while the TCP session has no callable contract");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "server contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ServiceAssembly).Succeeded, "server service registration");
        Ensure(await call.WaitAsync(timeout.Token) == 7, "original call resumes when the manifest becomes compatible");
        Ensure(((SharpLinkClient)harness.Client).ContractWaiterCountForTesting == 0, "TCP admission removes its waiter");
    }

    [Test]
    [NotInParallel]
    public async Task RetiringLocalRegistrationShouldTerminateWaitingGeneratedCallAndUnload()
    {
        var tracked = await RetireWaitingProxyAndUnloadAsync();
        for (var attempt = 0; attempt < 20 && tracked.AnyAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }
        Ensure(!tracked.AnyAlive, $"completed admission must not root a collectible registration: {tracked.AliveNames}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<TrackedWeakReferences> RetireWaitingProxyAndUnloadAsync()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("contract-admission-retire", loadService: false);
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded, "local contract registration");
        object? proxy = CreateWaitingProxy(harness.Client, plugin.ContractType);
        var tracked = new TrackedWeakReferences();
        tracked.Add("WaitingProxy", proxy);
        tracked.Add("ContractType", plugin.ContractType);
        tracked.Add("ContractAssembly", plugin.ContractAssembly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<int>? call = InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UnaryAsync", 6, timeout.Token).AsTask();
        Ensure(((SharpLinkClient)harness.Client).ContractWaiterCountForTesting == 1, "generated call enters pre-send wait");
        var unregister = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2), timeout.Token);
        Ensure(unregister.ReferencesReleased && unregister.RemainingCalls == 0, "retirement must wake waiting before draining leases");
        try
        {
            _ = await call.WaitAsync(timeout.Token);
            throw new Exception("retired waiting call must fail");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "local retirement terminates admission");
        }
        Ensure(((SharpLinkClient)harness.Client).ContractWaiterCountForTesting == 0, "retirement removes the waiter");
        EnsureClientAndServerCountersAreZero(harness, "retired contract admission");
        call = null;
        proxy = null;
        tracked.Add("AssemblyLoadContext", plugin.Unload());
        return tracked;
    }

    private static object CreateWaitingProxy(ISharpLinkClient client, Type contractType)
        => typeof(ISharpLinkClient).GetMethod(nameof(ISharpLinkClient.CreateProxy))!.MakeGenericMethod(contractType)
            .Invoke(client, [new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible }])
            ?? throw new InvalidOperationException("Dynamic waiting proxy factory returned null.");
    [Test]
    [NotInParallel]
    public async Task StaleCompatibleClientManifestMustNotDispatchIntoReplacementService()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var previous = PluginBundle.Load("contract-admission-stale");
        using var replacement = PluginBundle.Load("contract-admission-current", replacement: true);
        replacement.ResetServiceState();
        Ensure(harness.Client.RegisterAssembly(previous.ContractAssembly).Succeeded, "local contract registration");
        Ensure(harness.Server.RegisterAssembly(previous.ContractAssembly).Succeeded, "server contract registration");
        var registration = harness.Server.RegisterAssembly(previous.ServiceAssembly);
        var previousRequirement = new ContractRequirement(GetReceiptForContract(previous, registration), ContractRegistrationMatch.Compatible);
        await harness.Client.WaitForContractAsync(previousRequirement, new ContractWaitOptions { Timeout = TimeSpan.FromSeconds(10) });
        var held = GetProxy(harness.Client, previous.ContractType);
        var broadcastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = () =>
        {
            broadcastStarted.TrySetResult();
            return releaseBroadcast.Task;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Ensure((await harness.Server.UnregisterAssemblyAsync(
                previous.ServiceAssembly, TimeSpan.FromSeconds(2), timeout.Token)).ReferencesReleased,
                "the previous server service is released");
            Ensure((await harness.Server.ReplaceAssemblyAsync(previous.ContractAssembly,
                replacement.ContractAssembly, TimeSpan.FromSeconds(2), timeout.Token)).Succeeded,
                "the server replaces its contract while the client manifest is deliberately stale");
            Ensure(harness.Server.RegisterAssembly(replacement.ServiceAssembly).Succeeded,
                "replacement service registration");
            await broadcastStarted.Task.WaitAsync(timeout.Token);
            Ensure(harness.Client.GetContractReadiness(previousRequirement).CompatibleEndpoints == 1,
                "the held client session still observes the previous compatible identity");

            try
            {
                _ = await InvokeValueTaskAsync<int>(held, previous.ContractType,
                    "UnaryAsync", 6, timeout.Token);
                throw new Exception("the server must reject a stale contract identity");
            }
            catch (SharpLinkException exception)
            {
                Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition &&
                       exception is not SharpLinkContractAdmissionException,
                    "the selected server registration must reject the stale wire identity");
            }
            Ensure(replacement.GetStaticInt("Created") == 0,
                "stale wire identity must fail before replacement service activation");
        }
        finally
        {
            releaseBroadcast.TrySetResult();
            ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = null;
        }
    }

    [Test]
    [NotInParallel]
    public async Task SameHashRegistrationReceiptMustWaitForItsOwnDelayedTcpPublication()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("contract-admission-receipt");
        plugin.ResetServiceState();
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded, "local contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "server contract registration");
        var initial = harness.Server.RegisterAssembly(plugin.ServiceAssembly);
        var initialReceipt = GetReceiptForContract(plugin, initial);
        var oldRequirement = new ContractRequirement(initialReceipt, ContractRegistrationMatch.ExactRegistration);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await harness.Client.WaitForContractAsync(oldRequirement, new ContractWaitOptions(), timeout.Token);
        var oldProxy = CreateReceiptProxy(harness.Client, plugin.ContractType, oldRequirement, RpcAvailability.FailFast);
        var broadcastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = () =>
        {
            broadcastStarted.TrySetResult();
            return releaseBroadcast.Task;
        };
        try
        {
            Ensure((await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly,
                TimeSpan.FromSeconds(2), timeout.Token)).ReferencesReleased, "old implementation retires");
            var current = harness.Server.RegisterAssembly(plugin.ServiceAssembly);
            var receipt = GetReceiptForContract(plugin, current);
            Ensure(receipt.ServerInstanceId == initialReceipt.ServerInstanceId &&
                   receipt.RegistryEpoch == initialReceipt.RegistryEpoch && receipt.Generation > initialReceipt.Generation &&
                   receipt.RpcAssemblyHash == initialReceipt.RpcAssemblyHash && receipt.RegistrationId != initialReceipt.RegistrationId,
                "same-hash replacement preserves its timeline while changing registration identity and generation");
            await broadcastStarted.Task.WaitAsync(timeout.Token);
            var requirement = new ContractRequirement(receipt, ContractRegistrationMatch.ExactRegistration);
            Ensure(harness.Client.GetContractReadiness(oldRequirement).CompatibleEndpoints == 1 &&
                   harness.Client.GetContractReadiness(requirement).CompatibleEndpoints == 0,
                "the delayed data manifest still advertises only the old exact registration");

            try
            {
                _ = await InvokeValueTaskAsync<int>(oldProxy, plugin.ContractType, "UnaryAsync", 4, timeout.Token);
                throw new Exception("old exact registration must not dispatch into its same-hash replacement");
            }
            catch (SharpLinkException exception)
            {
                Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition &&
                       exception is not SharpLinkContractAdmissionException,
                    "the server must reject the stale exact registration carried on the wire");
            }
            Ensure(plugin.GetStaticInt("Created") == 0,
                "receipt mismatch must reject before replacement activation");
            var waitingProxy = CreateReceiptProxy(harness.Client, plugin.ContractType, requirement, RpcAvailability.WaitForCompatible);
            var call = InvokeValueTaskAsync<int>(waitingProxy, plugin.ContractType, "UnaryAsync", 4, timeout.Token).AsTask();
            Ensure(!call.IsCompleted && ((SharpLinkClient)harness.Client).ContractWaiterCountForTesting == 1,
                "the committed receipt must wait for its publication on the actual data session");
            releaseBroadcast.TrySetResult();
            Ensure(await call.WaitAsync(timeout.Token) == 5,
                "the exact-receipt call resumes after its data manifest is published");
        }
        finally
        {
            releaseBroadcast.TrySetResult();
            ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = null;
        }
    }

    private static ContractRegistrationReceipt GetReceiptForContract(
        PluginBundle plugin, SharpLinkAssemblyRegistrationResult result)
    {
        Ensure(result.Succeeded, $"service registration must commit: {result.Error}");
        var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(candidate =>
            ReferenceEquals(candidate.OwnerAssembly, plugin.ServiceAssembly));
        var exported = manifest.Services.Select(static service => service.ContractId).Order().ToArray();
        var observed = result.Receipts.Select(static receipt => receipt.ContractId).Order().ToArray();
        Ensure(exported.Length > 0 && observed.SequenceEqual(exported),
            "registration must return the full exported service receipt set, without omissions or duplicates");
        var contractId = manifest.Services.Single(service => service.ContractType == plugin.ContractType).ContractId;
        return result.Receipts.Single(receipt => receipt.ContractId == contractId);
    }

    private static object CreateReceiptProxy(ISharpLinkClient client, Type contractType,
        ContractRequirement requirement, RpcAvailability availability)
        => typeof(ISharpLinkClient).GetMethod(nameof(ISharpLinkClient.CreateProxy))!
            .MakeGenericMethod(contractType)
            .Invoke(client, [new RpcProxyOptions { Availability = availability, Requirement = requirement }])
            ?? throw new InvalidOperationException("Dynamic receipt proxy factory returned null.");

}
