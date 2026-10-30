namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("deadline")]
    [Arguments("module-retirement")]
    public async Task RetiringQueuedSelectionMustNotReleaseAdmittedPhysicalCleanupOrRetarget(string terminal)
    {
        var (harness, clientClock, serverClock) = await CreatePhysicalOwnershipHarnessAsync();
        await using var harnessLifetime = harness;
        using var plugin = PluginBundle.Load($"queued-physical-{terminal}");
        plugin.InvokeServiceStatic(PhysicalProbeService, "Reset");
        await RegisterAllAsync(harness, plugin);
        var contract = plugin.GetContractType("SharpLink.DynamicPlugin.IPhysicalOwnershipProbe");
        await WaitForRemoteContractManifestAsync(harness.Client, contract);
        var proxy = GetProxy(harness.Client, contract);
        var server = (SharpLinkServer)harness.Server;
        var holder = await HoldQueuedAdmissionAsync(server);
        holder.Lease!.Dispose();
        var admission = server.CurrentAdmissionProgramForTests!;
        var module = GetDynamicModule(server, plugin.ServiceAssembly);
        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var replacementType = replacementAssembly.GetType(PhysicalProbeService, throwOnError: true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);
        var method = terminal == "deadline" ? "DeadlineStreamAsync" : "CooperativeStreamAsync";
        await using var activeReader = InvokeStream(proxy, contract, method, CancellationToken.None).GetAsyncEnumerator();
        await using var queuedReader = InvokeStream(proxy, contract, "CooperativeStreamAsync", CancellationToken.None)
            .GetAsyncEnumerator();
        Task<bool>? activeMove = null;
        Task<bool>? queuedMove = null;
        try
        {
            Ensure(await activeReader.MoveNextAsync().AsTask().WaitAsync(PhysicalPhaseTimeout),
                "the first stream must acquire the sole admission permit");
            await plugin.GetServiceStaticTask(PhysicalProbeService, "WorkStarted").WaitAsync(PhysicalPhaseTimeout);
            activeMove = activeReader.MoveNextAsync().AsTask();
            queuedMove = queuedReader.MoveNextAsync().AsTask();
            await WaitUntilAsync(() => admission.Kernel.QueuedCalls == 1);
            Ensure(admission.Kernel.ActivePermits == 1 && module.RemainingCalls == 1 && module.RemainingStreams == 1,
                "the queued selection must not acquire another module invocation lease");

            if (terminal == "deadline")
            {
                serverClock.Advance(TimeSpan.FromSeconds(1));
                await WaitUntilAsync(() => plugin.GetServiceStaticInt(PhysicalProbeService, "WorkCancelled") == 1);
                clientClock.Advance(TimeSpan.FromSeconds(1));
            }
            var replacement = await server.ReplaceAssemblyAsync(
                plugin.ServiceAssembly, replacementAssembly, TimeSpan.Zero);
            Ensure(replacement.Succeeded && !replacement.ReferencesReleased,
                "compatible replacement publishes while the original physical invocation remains owned");
            Exception? failure = null;
            try { await activeMove.WaitAsync(PhysicalPhaseTimeout); }
            catch (Exception exception) when (exception is not TimeoutException) { failure = exception; }
            Ensure(failure is SharpLinkException selectedFailure && selectedFailure.Code ==
                (terminal == "deadline" ? SharpLinkErrorCode.DeadlineExceeded : SharpLinkErrorCode.Unavailable),
                "the active call must publish its selected deadline or retirement terminal");
            // Because the old module still owns an invocation, zero-grace replacement cancels
            // its module token. The queued wait can reject immediately without acquiring the
            // permit still owned by the active call's physical cleanup.
            await EnsureQueuedSelectionUnavailableAsync(queuedMove);
            await WaitUntilAsync(() => admission.Kernel.QueuedCalls == 0);
            Ensure(admission.Kernel.ActivePermits == 1 && module.RemainingCalls == 1 && module.RemainingStreams == 1 &&
                server.ActiveCallCountForDiagnostics == 1,
                "rejecting a retired queued selection must not release the active physical invocation's ownership");

            plugin.InvokeServiceStatic(PhysicalProbeService, "ReleaseWork");
            await plugin.GetServiceStaticTask(PhysicalProbeService, "WorkFinished").WaitAsync(PhysicalPhaseTimeout);
            await plugin.GetServiceStaticTask(PhysicalProbeService, "DisposeStarted").WaitAsync(PhysicalPhaseTimeout);
            Ensure(plugin.GetServiceStaticInt(PhysicalProbeService, "ConcurrentDispose") == 0 &&
                plugin.GetServiceStaticInt(PhysicalProbeService, "ServiceDisposed") == 0 &&
                admission.Kernel.ActivePermits == 1 && admission.Kernel.QueuedCalls == 0,
                "sequential asynchronous disposal must still own the original service and admission permit");
            plugin.InvokeServiceStatic(PhysicalProbeService, "ReleaseDispose");
            await plugin.GetServiceStaticTask(PhysicalProbeService, "DisposeFinished").WaitAsync(PhysicalPhaseTimeout);
            await WaitUntilAsync(() => admission.Kernel.ActivePermits == 0 && admission.Kernel.QueuedCalls == 0);
            Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0 &&
                plugin.GetServiceStaticInt(PhysicalProbeService, "ServiceDisposed") == 1,
                "physical completion must release the original invocation and service exactly once");
            Ensure(!((Task)replacementType.GetProperty("WorkStarted")!.GetValue(null)!).IsCompleted &&
                (int)replacementType.GetProperty("ServiceDisposed")!.GetValue(null)! == 0,
                "the original queued selection must never execute the replacement service");
            var freshProxy = GetProxy(harness.Client, plugin.ContractType);
            Ensure(await InvokeValueTaskAsync<int>(freshProxy, plugin.ContractType,
                "UnaryAsync", 41, CancellationToken.None) == 42,
                "fresh traffic reaches the replacement on the surviving connection");
        }
        finally
        {
            holder.Lease!.Dispose();
            plugin.InvokeServiceStatic(PhysicalProbeService, "ReleaseAll");
            replacementType.GetMethod("ReleaseAll")!.Invoke(null, null);
            serverClock.Advance(TimeSpan.FromSeconds(10));
            clientClock.Advance(TimeSpan.FromSeconds(10));
            // Also cancel any erroneously admitted producer before observing failure-path tasks.
            _ = await server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.Zero);
            _ = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.Zero);
            if (activeMove is not null)
                await ObserveQueuedTerminalAsync(activeMove);
            if (queuedMove is not null)
                await ObserveQueuedTerminalAsync(queuedMove);
            _ = await server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            _ = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            replacementContext.Unload();
        }
    }
}
